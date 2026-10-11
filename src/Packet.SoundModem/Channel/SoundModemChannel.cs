using M0LTE.Radio.Audio;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Channel;

/// <summary>KISS channel-access parameters, in KISS units where noted.</summary>
public sealed class CsmaParameters
{
    /// <summary>Preamble length in milliseconds. Default 300.</summary>
    public int TxDelayMilliseconds { get; set; } = 300;

    /// <summary>p-persistence parameter, 0-255 (p = (value+1)/256). Default 63 (p=0.25).</summary>
    public int Persistence { get; set; } = 63;

    /// <summary>Slot time in milliseconds. Default 100.</summary>
    public int SlotTimeMilliseconds { get; set; } = 100;

    /// <summary>Audio kept flowing after the last frame, in milliseconds. Software modems
    /// need a non-zero tail so they do not clip their own transmissions. Default 20.</summary>
    public int TxTailMilliseconds { get; set; } = 20;
}

/// <summary>Receives the same audio the channel's modems see (see
/// <see cref="SoundModemChannel.AddReceiveTap"/>).</summary>
public delegate void ReceiveTap(ReadOnlySpan<float> samples);

/// <summary>
/// One audio channel hosting up to 16 logical modems (the QtSoundModem multiplex model,
/// addressed by KISS sub-channel): fans received audio into every modem plus the spectrum
/// source, aggregates carrier sense, and runs the transmit side - classic AX.25 §6
/// p-persistent CSMA gated on <see cref="ChannelBusyFor(int)"/>, PTT keying, and
/// device-paced audio with a drain before unkey (sample-domain TX-complete).
/// </summary>
/// <remarks>
/// <para><b>The transmit side is a scheduler, not a queue.</b> Everything that can key this
/// radio - each KISS modem, the paging endpoint, ARDOP, the CW ident - is a <i>transmitter</i>
/// with a queue of its own, and the channel serves them round robin. There is nothing special
/// about there being two, or about their being modems; the number and the kinds are open.</para>
/// <para><b>What makes that necessary is half duplex.</b> Keying up makes EVERY receiver on this
/// radio deaf, not just the one whose traffic caused it, so one transmitter's frame is airtime
/// taken from all of the others and silence imposed on all of them. Two rules follow, and both
/// are properties of the shared resource rather than of any protocol: a keyup carries one
/// transmitter's traffic (see <see cref="RunTransmitterAsync"/>), and a transmission that expects
/// an answer keeps the rest quiet until the answer has had its chance (see
/// <see cref="QuietAfterTransmit"/> and <see cref="TurnaroundHold"/>).</para>
/// </remarks>
public sealed class SoundModemChannel
{
    private readonly Dictionary<int, IModem> _modems = [];
    private readonly List<ReceiveTap> _receiveTaps = [];

    // Where each modem sits in the audio band, measured once as it is added, so that carrier
    // sense can be answered for one sub-channel rather than for the whole station. Keyed on the
    // modem instance because that is the identity a queued transmission carries. A null value is
    // a modem that would not say (see ModemPassband.Measure), and defers to everything.
    private readonly Dictionary<object, ModemPassband?> _passbands = new(ReferenceEqualityComparer.Instance);

    /// <summary>One queued transmission, with the identity that decides whose keyup it is.</summary>
    private sealed record TxItem(
        Func<int, float[]> Modulate,
        TaskCompletionSource<bool> Done,
        Action<Exception>? Rejected,
        bool OwnsTiming,
        object Source,
        TimeSpan? QuietAfter,
        Func<bool>? StopEarly = null,
        Action<int>? Written = null,
        long QueuedAt = 0,
        Action<TimeSpan>? Started = null)
    {
        /// <summary>
        /// The token registration that withdraws this item, disposed once it has gone out or been
        /// withdrawn, so a long-lived token does not accumulate registrations for finished work.
        /// </summary>
        public CancellationTokenRegistration Withdrawal { get; set; }

        /// <summary>
        /// Fires once, <see cref="TransmitInhibitTimeout"/> after this item was handed over, and
        /// gives it a definite answer if another service is still holding the channel then.
        /// Null on a channel with no <see cref="TransmitInhibit"/> and on a transmission that owns
        /// the channel's timing, neither of which can be held.
        /// </summary>
        public ITimer? HeldTooLong { get; set; }

        /// <summary>
        /// This transmitter's running wait ledger, and a copy of it as it stood when this item was
        /// queued. The difference at pickup is where this frame's own wait went.
        /// </summary>
        /// <remarks>
        /// Properties rather than more positional parameters. This record's trailing parameters
        /// are exactly the shape that accepts two arguments transposed without a word - which is
        /// the first thing anyone suspects when a duration comes out wrong - and there is no
        /// reason to lengthen the row of them for state the constructor cannot sensibly set.
        /// The item holds the ledger ITSELF rather than looking it up again at pickup, because a
        /// transmitter's ledger is dropped when its queue empties and the last frame of a keyup
        /// is picked up from a queue that has just become empty.
        /// </remarks>
        public long[]? Ledger { get; set; }

        /// <inheritdoc cref="Ledger" />
        public long[]? LedgerAtQueue { get; set; }

        /// <summary>
        /// The ledger as it stood when the transmitter took this item off the queue, which is the
        /// instant its wait ended. Copied there rather than read back later because the ledger
        /// goes on moving the moment the burst starts, and because the last item of a keyup
        /// empties its transmitter's queue and the ledger goes with it.
        /// </summary>
        public long[]? LedgerAtPickup { get; set; }

        /// <summary>
        /// Told the whole wait and where it went, immediately before the burst is written. The
        /// station's own path, beside the public <see cref="Started"/> callback: the breakdown is
        /// carried to a consumer on <see cref="FrameTransmittedWithReport"/>, which is where a
        /// station's journal, frame log and page all read it from.
        /// </summary>
        public Action<TimeSpan, TransmitWaits>? Noted { get; set; }

        public byte[]? Frame { get; init; }

        /// <summary>
        /// Set on a sub-channel frame whose modem can pack (<see cref="IFramePackingModem"/>):
        /// what the keyup needs to put this frame in one burst with the ones queued behind it.
        /// Null on everything else, which always goes out one item per burst.
        /// </summary>
        public PackedFrame? Packed { get; init; }
    }

    /// <summary>
    /// What a packable frame brings to a packed burst beyond its bytes: the modem that packs it,
    /// and the trim decision its own modulate closure would otherwise have made.
    /// </summary>
    /// <param name="Packer">The modem, which is also the frame's transmitter identity.</param>
    /// <param name="ChooseTrim">Asks <see cref="TransmitTrimHz"/> for this frame, at render time.</param>
    /// <param name="Applied">Told the trim the burst actually went out with.</param>
    private sealed record PackedFrame(IFramePackingModem Packer, Func<double> ChooseTrim, Action<double> Applied);

    /// <summary>
    /// A bound on how many queued frames one packed burst is offered, so that a queue thousands
    /// deep costs one burst's worth of sizing work rather than the whole queue's. Far more than
    /// any burst holds: MS110D at its fastest waveform carries a few hundred short frames in two
    /// minutes.
    /// </summary>
    private const int MaxPackedFramesOffered = 1024;

    // Where a waiting transmission's time goes. One slot per cause, then one per sub-channel for
    // the carrier sense that asserted, then one for the radio's own station-wide answer - all in
    // a single long[] per transmitter so that a frame can take a copy at enqueue and subtract it
    // at pickup. Ticks of the TimeProvider's own timestamp, converted only at the end.
    //
    // A flat array rather than a class of named fields because the hot path is "add this many
    // ticks to these slots", which is an indexed add with no allocation and no branching per
    // field; the names live in WaitSlot, one place, and the public shape is TransmitWaits.
    private enum WaitSlot
    {
        Unattributed = 0,
        ChannelBusy = 1,
        Backoff = 2,
        TurnaroundHold = 3,
        OurTransmission = 4,
        OurTurn = 5,
        TransmitInhibit = 6,
    }

    private const int WaitSlotCount = 7;
    private const int BusySubChannelBase = WaitSlotCount;   // + sub-channel 0..15
    private const int BusyRadioSlot = BusySubChannelBase + 16;
    private const int LedgerLength = BusyRadioSlot + 1;
    private const int RadioBusyBit = 1 << 16;               // matches TransmitWaits.RadioBit

    // The running ledger per transmitter, alongside its queue. Kept in step with _txQueues: a
    // transmitter with nothing queued has nothing waiting, and its next frame starts a fresh
    // ledger from zero. Items that are already queued hold the array itself, so dropping it here
    // cannot strand a frame's figures.
    private readonly Dictionary<object, long[]> _waitLedgers = new(ReferenceEqualityComparer.Instance);
    private WaitSlot _waitSlot = WaitSlot.Unattributed;
    private object? _waitFor;
    private int _waitAsserted;
    private long _waitMark;

    // A queue PER TRANSMITTER rather than one for the channel. With a single queue a deferred
    // frame at the head blocks everything behind it, including the link that is not deferred -
    // so the moment any transmission can be held back, per-source queues stop being a nicety.
    private readonly object _txGate = new();
    private readonly Dictionary<object, Queue<TxItem>> _txQueues = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _txOrder = [];
    private TaskCompletionSource _txSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The turnaround hold: after a transmission that expects an answer, the other transmitters
    // stay off the air until that answer has had its chance. See TurnaroundHold.
    private object? _quietOwner;
    private long _quietFrom;
    private long _quietOwnerSince;
    private TimeSpan _quietWindow;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly SpectrumSource? _spectrum;
    private readonly BurstSnrMonitor _burstSnr;
    private readonly FrameLevelMonitor _frameLevel;
    private readonly Action<int, ReadOnlyMemory<byte>>? _constellationSink;
    private readonly IChannelBusySource? _busySource;
    private readonly FmShapeBusyDetector? _audioFallback;
    private volatile bool _transmitting;

    // The announcement of the last frame the transmitter put on the air, which the next one's
    // announcement waits for. See SendAndAnnounceAsync.
    private Task _lastAnnouncement = Task.CompletedTask;

    /// <summary>For tests: the place in the announcement order that the next frame to go out will
    /// wait for. Completed when every frame sent so far has been announced or given up its
    /// place.</summary>
    internal Task LastAnnouncement => Volatile.Read(ref _lastAnnouncement);

    /// <summary>Creates a channel.</summary>
    /// <param name="sampleRate">DSP sample rate all modems and TX audio run at.</param>
    /// <param name="time">Clock for CSMA waits (injectable per repo discipline).</param>
    /// <param name="spectrumSink">Optional waterfall line sink (see
    /// <see cref="SpectrumSource"/>).</param>
    /// <param name="constellationSink">Optional per-symbol constellation-frame sink
    /// (sub-channel, frame). Wired to any PSK modem added to the channel - see
    /// <see cref="ConstellationSource"/>; a no-op for the non-PSK modes.</param>
    /// <param name="randomSeed">Seed for the p-persistence roll (tests); null = random.</param>
    /// <param name="channelBusySource">Something outside the audio path that knows whether the
    /// channel is occupied - a radio's squelch and signal-strength meter, read over its control
    /// link. Null leaves the station on the audio-derived answer it has always had. See
    /// <see cref="CarrierSenseRule"/> for what difference having one makes, and why.
    /// <para><b>Passed in, never read from a static.</b> A host that registered a radio with
    /// <see cref="ChannelBusySources.Host"/> hands <see cref="ChannelBusySources.Resolve"/> in
    /// here. Reading the static from this constructor was tried and taken back out: a channel is
    /// built in a dozen places, several of them concurrently in one process, and a mutable global
    /// that silently changes what a station will transmit over is the wrong thing for any of them
    /// to depend on by accident.</para></param>
    /// <param name="audioFallback">Whether, with no source of its own, this channel should read
    /// carrier sense off the shape of the received audio (<see cref="FmShapeBusyDetector"/>).
    /// For a station on an open-squelch FM radio with no control cable. Off by default here
    /// because it is a station's policy rather than a library's: the daemon turns it on unless
    /// the operator says otherwise, and a consumer that knows it is on SSB or a wired loop should
    /// leave it off. It is ignored below
    /// <see cref="FmShapeBusyDetector.MinimumSampleRate"/>, where it is measured not to work, and
    /// it is ignored entirely when a real source is supplied.</param>
    public SoundModemChannel(
        int sampleRate,
        TimeProvider? time = null,
        Action<ReadOnlyMemory<byte>>? spectrumSink = null,
        Action<int, ReadOnlyMemory<byte>>? constellationSink = null,
        int? randomSeed = null,
        IChannelBusySource? channelBusySource = null,
        bool audioFallback = false)
    {
        SampleRate = sampleRate;
        _time = time ?? TimeProvider.System;
        _random = randomSeed is int seed ? new Random(seed) : new Random();
        if (spectrumSink is not null)
        {
            _spectrum = new SpectrumSource(sampleRate, spectrumSink);
        }

        _burstSnr = new BurstSnrMonitor(sampleRate);
        _frameLevel = new FrameLevelMonitor(sampleRate);
        TransmitLease = new TransmitLease(_time);
        TransmitLease.TakenInternal += RefuseOutsideLease;
        TransmitLease.EndingInternal += (holder, how) =>
        {
            // A lease that ran out was not given back: its holder stopped renewing, most likely
            // because it died, and whatever it had queued is a broadcast nobody is running any
            // more. It must not go out into normal service, so it goes with the lease. A release
            // keeps its queue unless it asks otherwise (DropQueued).
            if (how == TransmitLeaseChange.Expired)
            {
                DropQueued(holder, ExpiredDropReason);
            }
        };
        _constellationSink = constellationSink;
        // Read once, here, and never again: a station's receive path does not change under it
        // while it runs.
        _busySource = channelBusySource;

        // Only where there is nothing better to ask, and only where it is measured to work. It
        // decides for itself whether the path is one it understands and answers null on anything
        // else, so a station on SSB or a wired loop keeps the energy detector either way.
        if (_busySource is null && audioFallback && sampleRate >= FmShapeBusyDetector.MinimumSampleRate)
        {
            _audioFallback = new FmShapeBusyDetector(sampleRate);
            _busySource = _audioFallback;
        }
    }

    /// <summary>The channel's DSP sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Channel-access tunables (KISS parameter commands update these).</summary>
    public CsmaParameters Csma { get; } = new();

    /// <summary>
    /// Gives one sub-channel this channel's transmitter for a while, refusing everyone else's
    /// transmissions until it ends; nobody holds it unless asked. See <see cref="Channel.TransmitLease"/>.
    /// </summary>
    /// <remarks>
    /// Each modem's frames belong to its own sub-channel, and are attributed to it as the modem is
    /// added. A transmission queued through the delegate overload belongs to nobody unless its
    /// source has been <see cref="TransmitLease.Attribute">attributed</see>, so while a lease is
    /// held it is refused. Receive is untouched.
    /// </remarks>
    public TransmitLease TransmitLease { get; }

    /// <summary>Raised for every received frame a modem passes up, with the sub-channel that
    /// decoded it. Called from the receive-processing thread. This is the <b>host</b> path: it
    /// comes from each modem's constructor frame sink, and it is what a KISS client is sent.
    /// Not every frame the station reads arrives here - see
    /// <see cref="FrameReceivedWithQuality"/>.</summary>
    public event Action<int, byte[]>? FrameReceived;

    /// <summary>Per-frame receive diagnostics (sub-channel, frame, quality) for every frame the
    /// station decoded - FEC corrections, CRC state, winning decoder branch. See
    /// <see cref="Modems.FrameQuality"/>.</summary>
    /// <remarks>
    /// The <b>monitor</b> path, from <see cref="IModem.FrameDecoded"/>: display, frame log,
    /// journal and survey hang off this. It is a superset of <see cref="FrameReceived"/>, not a
    /// companion to it - a frame marked <see cref="Modems.FrameQuality.MonitorOnly"/> is raised
    /// here and never there, which is how a station shows an operator traffic it is deliberately
    /// not handing to its host.
    /// </remarks>
    public event Action<int, byte[], Modems.FrameQuality>? FrameReceivedWithQuality;

    /// <summary>Raised when a queued frame is dropped (sub-channel, frame, reason): its
    /// modem refused to modulate it (e.g. a frame beyond the mode's size bound), or the
    /// sub-channel has no modem at all. The frame's
    /// <see cref="EnqueueTransmit(int, byte[])"/> task faults with the same exception;
    /// the transmitter keeps running.</summary>
    public event Action<int, byte[], Exception>? TransmitRejected;

    /// <summary>
    /// True while the channel is occupied: we are transmitting, or the station's carrier sense
    /// says somebody else is.
    /// </summary>
    /// <remarks>
    /// <para>This is what the CSMA below will not transmit over, and it is the station's one
    /// answer rather than a modem's. <b>With a <see cref="BusySource"/> that has an opinion</b>,
    /// that opinion decides, ored with any modem's packet carrier detect; every modem's in-band
    /// energy detector is dropped, because on an FM path it is not merely redundant but
    /// anti-correlated. <b>Without one</b>, or with one that does not know, it is the OR across
    /// every modem's own audio answer, which is what this has always been. The measured argument
    /// for both halves is on <see cref="CarrierSenseRule.Occupied"/>.</para>
    /// <para><b>Our own transmission always counts</b>, first and unconditionally. It is not a
    /// measurement of the channel and no source is asked about it: a station's receiver is muted
    /// while it transmits, so nothing outside can see it.</para>
    /// <para><b>This is no longer what gates a transmission</b>, and has not been since
    /// packet-net/pdn-soundmodem#526: a frame is gated on
    /// <see cref="ChannelBusyFor(int)"/>, the same question asked about its own sub-channel.
    /// What stays here is the station-wide answer - is anything at all going on out there - which
    /// is what the waterfall, the station page and the diagnostics are asking.</para>
    /// </remarks>
    public bool ChannelBusy =>
        _transmitting
        || CarrierSenseRule.Occupied(
            _busySource?.Busy,
            anyCarrierDetect: _modems.Values.Any(m => m.CarrierDetect),
            anyAudioBusy: _modems.Values.Any(m => m.ChannelBusy));

    /// <summary>
    /// True while the channel is occupied <em>as far as one sub-channel is concerned</em>: we are
    /// transmitting, the station's radio says the channel is busy, or a modem sharing this one's
    /// passband hears something. This is what the CSMA below will not transmit over.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is not <see cref="ChannelBusy"/>.</b> Whether we can hear is a fact about
    /// the station's one receiver. Whether transmitting would collide is a fact about one
    /// sub-channel's passband, and the two used to share an answer. On a station whose modems sit
    /// on different RF frequencies inside one slice, that meant a frame for 7051.6 kHz was held
    /// because something was active on 7050.3 kHz, 1.3 kHz away, which it could not have
    /// interfered with.</para>
    /// <para><b>What that cost, measured.</b> On 2026-09-21 GB7RDG-2 could not answer a connect
    /// request for 3 minutes 48 seconds. It ran four modems then, and replaying the off-air
    /// capture of those 228 s through this repo's own <see cref="Modems.EnergyBusyDetector"/>
    /// they were busy 36.1, 81.1, 35.2 and 35.2 % of the time and the OR across them was busy
    /// 96.4 %. That left 8.2 s of clear air and exactly one gap long enough to start a 15 byte
    /// frame, where the modem with the traffic, busy 81.1 % on its own, had fourteen. It is
    /// arithmetic rather than a broken detector: four roughly independent channels at those
    /// rates union to 94.9 % by pure probability, against 96.4 % measured, so every modem added
    /// to a station multiplies the deferral whether or not its channel has anything to do with
    /// the traffic (packet-net/pdn-soundmodem#526).</para>
    /// <para><b>Overlap, not sub-channel equality.</b> Two afsk300 modems 133 Hz apart really do
    /// share a passband and must still defer to each other; the test is each modem's measured
    /// occupied band, widened by a guard sized from how far off tune the stations we actually
    /// work sit. See <see cref="ModemPassband"/>, which has the distribution that sized it.</para>
    /// <para><b>What did not become per-sub-channel.</b> The <see cref="BusySource"/> is a report
    /// on the receiver rather than on a waveform, so when it has an opinion it still decides for
    /// the whole station, exactly as before - the narrowing is to the audio fallback only, and
    /// <see cref="CarrierSenseRule.Occupied"/> is untouched. Our own transmission is still
    /// absolute, because the station is half duplex and keying makes every receiver on it deaf.
    /// So is the turnaround hold, which is a different mechanism and already per-source.</para>
    /// <para>A sub-channel with no modem on it gets the station-wide answer; there is no passband
    /// to narrow to.</para>
    /// </remarks>
    public bool ChannelBusyFor(int subChannel) =>
        _modems.TryGetValue(subChannel, out IModem? modem) ? ChannelBusyFor(modem) : ChannelBusy;

    /// <summary>
    /// The same question for whoever queued a transmission, which is how the transmitter asks it.
    /// </summary>
    /// <remarks>
    /// A source that is not a modem - POCSAG paging, the CW ident, the operator's test
    /// transmission - gets the station-wide answer. Nothing here knows what part of the band such
    /// a transmitter occupies, and the conservative choice for an unknown passband is the one
    /// every transmitter had before this existed.
    /// </remarks>
    private bool ChannelBusyFor(object? source) => ChannelBusyFor(source, out _);

    /// <summary>
    /// The same answer, and who gave it: a bit per sub-channel that asserted carrier sense, with
    /// <see cref="RadioBusyBit"/> when the station's radio answered for the whole station.
    /// </summary>
    /// <remarks>
    /// <para>For the wait ledger, and only for it - the decision is identical either way. Which
    /// sub-channel held a frame is the question that says whether the per-sub-channel rule of
    /// packet-net/pdn-soundmodem#526 is doing its job, and a station that is deferring to a modem
    /// 1.3 kHz away it could not collide with has no other way of showing it.</para>
    /// <para>Both halves are evaluated exactly as before, the audio answer included even when the
    /// radio has an opinion and decides. They are reads of a detector's current state and the
    /// result does not depend on them, but the transmit gate is not the place to start changing
    /// how often a detector is asked.</para>
    /// </remarks>
    private bool ChannelBusyFor(object? source, out int asserted)
    {
        asserted = 0;
        if (_transmitting)
        {
            // Our own keyup, asked of no source and answered before anything is read - the same
            // short circuit this always had. The transmitter never sees it: it only asks between
            // keyups.
            return true;
        }

        int detected = 0;
        foreach ((int sub, IModem modem) in _modems)
        {
            if (modem.CarrierDetect)
            {
                detected |= 1 << sub;
            }
        }

        int audio = 0;
        bool anyAudioBusy = AnyAudioBusyFor(source, ref audio);

        if (_busySource?.Busy is bool known)
        {
            asserted = (known ? RadioBusyBit : 0) | detected;
            return known || detected != 0;
        }

        asserted = anyAudioBusy ? audio : 0;
        return anyAudioBusy;
    }

    /// <summary>
    /// Whether any modem sharing <paramref name="source"/>'s passband hears something.
    /// </summary>
    /// <remarks>
    /// A modem always shares its own passband, so a transmission still defers to its own
    /// sub-channel's detector. An unmeasured passband, at either end of the comparison, could be
    /// anywhere and so counts as overlapping: carrier sense that does not know must defer.
    /// </remarks>
    /// <param name="source">The transmitter asking, whose passband narrows the answer.</param>
    /// <param name="asserted">
    /// Collects a bit per sub-channel whose modem is the reason for the answer, for the wait
    /// ledger. Every overlapping modem that hears something is recorded, not just the first,
    /// which is the only difference from the answer this always gave: a frame held by two
    /// sub-channels at once is a different diagnosis from one held by either of them.
    /// </param>
    private bool AnyAudioBusyFor(object? source, ref int asserted)
    {
        if (source is not IModem asking
            || !_passbands.TryGetValue(asking, out ModemPassband? measured)
            || measured is not ModemPassband mine)
        {
            foreach ((int sub, IModem modem) in _modems)
            {
                if (modem.ChannelBusy)
                {
                    asserted |= 1 << sub;
                }
            }

            return asserted != 0;
        }

        foreach ((int sub, IModem other) in _modems)
        {
            if (!other.ChannelBusy)
            {
                continue;
            }

            if (!_passbands.TryGetValue(other, out ModemPassband? band)
                || band is not ModemPassband theirs
                || mine.Overlaps(theirs))
            {
                asserted |= 1 << sub;
            }
        }

        return asserted != 0;
    }

    /// <summary>
    /// Where this channel's carrier sense comes from, or null if it has nothing but the audio.
    /// For diagnostics and for a station to report what it is running.
    /// </summary>
    public IChannelBusySource? BusySource => _busySource;

    /// <summary>True while any modem's packet DCD is asserted.</summary>
    public bool CarrierDetect => _modems.Values.Any(m => m.CarrierDetect);

    /// <summary>The modems keyed by sub-channel.</summary>
    public IReadOnlyDictionary<int, IModem> Modems => _modems;

    /// <summary>Adds a modem on a KISS sub-channel (0-15).</summary>
    public void AddModem(int subChannel, Func<Action<byte[]>, IModem> factory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(subChannel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subChannel, 15);
        IModem modem = factory(frame => FrameReceived?.Invoke(subChannel, frame));

        // Enriched here, at the one point every modem's quality passes through, so the frame
        // log, the journal line, the KISS quality frame and the waterfall all carry the SAME
        // burst-SNR figure - the lesson of the branch-index offsets, applied before the second
        // number exists rather than after it bites.
        modem.FrameDecoded += (frame, quality) =>
        {
            // Anything decoded on this modem renews its hold, because it means the exchange this
            // station is holding the channel for is alive. Deliberately an over-approximation:
            // the channel does not parse addresses, so somebody else's frame on the same
            // sub-channel renews it too. That costs the others at most one more window each, and
            // a carrier we can decode is one CSMA would have deferred to anyway.
            NoteHeard(modem);

            // Two measurements of the same burst, taken here for the same reason: how strong it
            // was against the band's noise floor, and how loud it was against the converter's
            // full scale. The second is not the first - a station can be the loudest thing on a
            // quiet band and still be 30 dB under where the capture gain should put it - and it
            // is the one an operator setting that gain needs per frame (issue #426).
            //
            // The span comes from the modem, taken from its own per-sample count while it was
            // decoding, and is consumed by the asking. Nothing out here could work it out: this
            // runs inside the modem's decode of one block, and on a station reading 100 ms
            // blocks a whole qpsk3600 frame fits inside one with room to spare. A modem that
            // does not report a span (FreeDV and MS110D decode from native frames, and the
            // ARDOP bridge is not a modem at all) carries no level rather than a guessed one.
            //
            // And the verdict on that reading is taken here too, from the deciding modem's own
            // limits (IFrameSpanSource.FrameLevels), so that "this frame was too loud" is a
            // property of the decode rather than a rule each consumer reapplies. It used to be
            // reapplied at the edge, by mode name, which is how every C4FSK frame came to take
            // the wrong pair of thresholds for a whole release.
            (double? peakDbFs, bool? clipped, Audio.FrameLevel? level, bool? worthShowing) =
                modem is IFrameSpanSource source
                && source.TryTakeFrameSpan(out long spanFrom, out long spanTo)
                    ? Judge(source, _frameLevel.Measure(spanFrom, spanTo, source.FrameSpanMarginSamples))
                    : (null, null, null, null);
            FrameReceivedWithQuality?.Invoke(
                subChannel,
                frame,
                quality with
                {
                    SnrDb = _burstSnr.MeasureBurst(subChannel),
                    PeakDbFs = peakDbFs,
                    Clipped = clipped,
                    Level = level,
                    PeakWorthShowing = worthShowing,
                });
        };
        if (_constellationSink is { } sink && modem is IConstellationSource psk)
        {
            var constellation = new ConstellationSource(frame => sink(subChannel, frame));
            constellation.Attach(psk);
        }

        _burstSnr.AddModem(subChannel, modem);
        _modems.Add(subChannel, modem);

        // The modem is the identity its frames are queued under, so this is what lets them
        // through while their own sub-channel holds the transmit lease.
        TransmitLease.Attribute(modem, subChannel);

        // Measured here, and only here: a modem has just been built, nothing is feeding it audio
        // and nothing is asking it to modulate, which is the one moment its own transmit path is
        // certainly free. The alternative, measuring lazily on the first transmission, would put
        // a probe frame through a live modem from the transmitter loop. See ChannelBusyFor.
        _passbands[modem] = ModemPassband.Measure(modem, SampleRate);
    }

    /// <summary>
    /// One reading, the verdict the deciding modem's own limits put on it, and whether those
    /// limits make the figure itself worth an operator's attention.
    /// </summary>
    /// <remarks>
    /// Both answers come from the same object at the same moment, because both are properties of
    /// the slicer that decoded the frame and nothing downstream can ask it: a page, a frame log
    /// and a monitor at the other end of an uplink see a row and not a demodulator.
    /// </remarks>
    private static (double? PeakDbFs, bool? Clipped, Audio.FrameLevel? Level, bool? WorthShowing)
        Judge(IFrameSpanSource source, (double? PeakDbFs, bool? Clipped) reading) =>
        (reading.PeakDbFs, reading.Clipped,
            source.FrameLevels.Classify(reading.PeakDbFs, reading.Clipped),
            source.FrameLevels.PeakWorthShowing);

    /// <summary>Adds a non-KISS receive listener - a service decoder (e.g. POCSAG
    /// paging) that shares the channel's audio without occupying a KISS sub-channel.
    /// Called with the same half-duplex-gated samples the modems get.</summary>
    public void AddReceiveTap(ReceiveTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        _receiveTaps.Add(tap);
    }

    /// <summary>
    /// Offers one block of audio exactly as the sound card delivered it, at the card's own rate
    /// and before any resampling, so that a decoded frame can say whether the converter clipped
    /// while it was arriving (<see cref="Modems.FrameQuality.Clipped"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Clipping is the one reading that cannot be taken downstream of the decimator.</b>
    /// On a 48 kHz card the audio the modems hear has been through a decimating FIR whose ripple
    /// moves peaks either way, so a signal with real headroom at the converter can leave it at
    /// full scale and one that genuinely railed can leave it lower - on the bench CM108 the
    /// difference was about 1.3 dB (radio1, 2026-09-06). Only the card's own samples can answer
    /// it, and only the host has them.</para>
    /// <para>Call it with the block the device returned, immediately before the
    /// <see cref="ProcessReceive"/> of what that block becomes, and not while the station is
    /// keyed. Optional: a channel nobody hands card samples to reports clipping as null - not
    /// measured - rather than as false. Called on the receive thread, so it returns promptly and
    /// allocates nothing.</para>
    /// </remarks>
    /// <param name="samples">The block as the device delivered it, at the card's own rate.</param>
    public void NoteCardClipping(ReadOnlySpan<float> samples) => _frameLevel.NoteCardClipping(samples);

    /// <summary>Feeds received audio to every modem and the spectrum source. Skipped
    /// while transmitting (half duplex).</summary>
    public void ProcessReceive(ReadOnlySpan<float> samples)
    {
        _spectrum?.Process(samples);
        if (_transmitting)
        {
            KeyedReceiveBlock?.Invoke(samples.Length);
            return;
        }

        // Below the half-duplex gate on purpose: our own transmission is not a signal we
        // heard, and feeding it here would attribute a huge SNR to whatever decodes next. The
        // audio carrier sense is here for the same reason, and it makes its own gated-input
        // check belt and braces rather than load-bearing.
        _burstSnr.Process(samples);
        _frameLevel.Process(samples);
        _audioFallback?.Process(samples);
        foreach (IModem modem in _modems.Values)
        {
            modem.Process(samples);
        }

        foreach (ReceiveTap tap in _receiveTaps)
        {
            tap(samples);
        }
    }

    /// <summary>Queues a frame for transmission on a sub-channel. The returned task
    /// completes when the frame's audio has been handed to the device, at most one card buffer
    /// ahead of the air, with the keyup it belongs to still holding the channel behind it
    /// (ACKMODE's answer). Redundant queued frames complete with the frame that represents them,
    /// without raising transmission events of their own. See <see cref="RunTransmitterAsync"/>
    /// for why that is not "played out".</summary>
    public Task EnqueueTransmit(int subChannel, byte[] frame)
    {
        if (!_modems.TryGetValue(subChannel, out IModem? modem))
        {
            // A sub-channel nothing transmits on - a typo'd nibble, or the ARDOP entry, which
            // is a receive tap rather than a modem. This used to fault a task most callers
            // discard: no DROPPED line, no observed exception, the host's traffic simply
            // vanished with nothing to distinguish it from a dead band. Announced like every
            // other refused frame, and observed here because a fire-and-forget caller cannot.
            var refusal = new ArgumentException($"no modem on sub-channel {subChannel}");
            TransmitRejected?.Invoke(subChannel, frame, refusal);
            Task faulted = Task.FromException(refusal);
            _ = faulted.Exception;
            return faulted;
        }

        return SendAndAnnounceAsync(subChannel, frame, modem);
    }

    /// <summary>
    /// Sends the frame and then announces it, so that awaiting the transmission is enough to know
    /// the announcement has happened.
    /// </summary>
    /// <remarks>
    /// Announced after the send rather than when it was queued, because a frame can wait behind
    /// CSMA or an ARQ session for seconds and a log line claiming a transmission that has not
    /// happened yet is worse than none. Sequenced by <c>await</c> rather than a continuation: a
    /// continuation lets the caller's own await resume first, so anything checking the event
    /// immediately after awaiting the send is racing it - which it will lose on a loaded machine,
    /// intermittently, in someone else's CI. A rejection throws out of the await, so a frame is
    /// announced by exactly one of this and <see cref="TransmitRejected"/>, never both.
    /// </remarks>
    /// <summary>
    /// Asked, per frame, how many Hz to nudge the transmit centre for it; null (the default)
    /// always transmits on the nominal centre.
    /// </summary>
    /// <remarks>
    /// For answering a station whose rig is off frequency on the frequency its receiver is
    /// actually listening on. The policy lives with the caller rather than here: only the caller
    /// knows whether a frame is addressed to one station or broadcast to the whole channel, and
    /// a trim aimed at one station's oscillator is aimed away from everybody else's. A modem
    /// that cannot be trimmed ignores this, so the hook is always safe to install.
    /// </remarks>
    public Func<int, byte[], double>? TransmitTrimHz { get; set; }

    /// <summary>Matches <c>FrequencyShiftedModem</c>: enough taps that the Hilbert transform's
    /// low-frequency edge is well below anything a packet mode occupies.</summary>
    private const int TrimHilbertTaps = 639;

    /// <summary>Hard ceiling on <see cref="TransmitTrimHz"/>, in Hz.</summary>
    public const double MaxTransmitTrimHz = 500;

    /// <summary>
    /// Applies the transmit trim to a rendered burst, by translating the whole thing.
    /// </summary>
    /// <remarks>
    /// <para>Done here rather than inside the modem because most modems are never wrapped in a
    /// frequency shifter: the AFSK and PSK families carry a settable centre natively and
    /// generate their carrier at it, so there is no shift stage to lean on, and those are
    /// exactly the modes that talk to the stations this is for. Translating the finished burst
    /// works for every mode on the same code path, at the cost of one Hilbert pass per
    /// transmission - which is nothing beside the airtime that follows it.</para>
    /// <para>A fresh shifter per burst, and a group delay of zeros flushed through it, for the
    /// same reason the modem's own shifter does that: the FIR delays everything by (taps-1)/2
    /// samples, and without the pad that much of the end of the burst never comes out.</para>
    /// </remarks>
    /// <summary>
    /// What <see cref="TransmitTrimHz"/> asked for, reduced to what will actually be done.
    /// </summary>
    /// <remarks>
    /// Clamped here rather than at the point of use so that everything downstream - the burst,
    /// the event, the frame log, the panel - reports the same number, and that number is the one
    /// that went on air. The clamp is a backstop on the caller, not a tuning knob: correcting for
    /// another station's oscillator is a few tens of Hz, and anything approaching this ceiling is
    /// a bug upstream that a transmitter is the wrong place to discover.
    /// </remarks>
    private static double ResolveTrim(double? requestedHz)
    {
        double hz = requestedHz ?? 0;
        return double.IsNaN(hz) ? 0 : Math.Clamp(hz, -MaxTransmitTrimHz, MaxTransmitTrimHz);
    }

    private float[] ApplyTransmitTrim(float[] burst, double trimHz)
    {
        if (trimHz == 0 || burst.Length == 0)
        {
            return burst;
        }

        const int groupDelay = (TrimHilbertTaps - 1) / 2;
        var shifter = new FrequencyShifter(SampleRate, trimHz, TrimHilbertTaps);
        var shifted = new float[burst.Length + groupDelay];
        shifter.Process(burst, shifted.AsSpan(0, burst.Length));
        shifter.Process(new float[groupDelay], shifted.AsSpan(burst.Length));
        return shifted;
    }

    private async Task SendAndAnnounceAsync(int subChannel, byte[] frame, IModem modem)
    {
        frame = (byte[])frame.Clone();
        double applied = 0;
        TimeSpan heldFor = TimeSpan.Zero;
        TransmitWaits waits = default;

        // This frame's place in the order the announcements go out in: the announcement of the
        // frame that went on air before it, and this one's own, which the frame behind it waits
        // for in turn. See the comment above the announcement below.
        Task previous = Task.CompletedTask;
        TaskCompletionSource? announced = null;

        // A modem that can pack is offered the frame with the decisions its own closure below
        // makes, so a keyup can put it in one burst with the frames queued behind it. Whether it
        // actually does is asked when the burst is rendered: the modem's Packing can be null, and
        // usually is, in which case the closure is what renders it exactly as before.
        PackedFrame? packed = modem is IFramePackingModem packer
            ? new PackedFrame(
                packer,
                () => ResolveTrim(TransmitTrimHz?.Invoke(subChannel, frame)),
                hz => applied = hz)
            : null;
        try
        {
            bool transmitted = await EnqueueTransmitCore(
                    // Inside the modulate callback, so the trim is chosen when the burst is actually
                    // rendered rather than when it was queued - a frame can wait behind CSMA for
                    // seconds, and the estimate may have moved on by then.
                    txDelay =>
                    {
                        applied = ResolveTrim(TransmitTrimHz?.Invoke(subChannel, frame));
                        return ApplyTransmitTrim(modem.Modulate(frame, txDelay), applied);
                    },
                    rejection => TransmitRejected?.Invoke(subChannel, frame, rejection),
                    ownsChannelTiming: false,
                    // The modem is the keyup's identity: this sub-channel's frames run back-to-back
                    // under one PTT, another sub-channel's do not.
                    source: modem,
                    quietAfter: QuietAfterTransmit?.Invoke(subChannel, frame),
                    withdraw: default,
                    stopEarly: null,
                    written: null,
                    started: null,
                    noted: (held, where) =>
                    {
                        heldFor = held;
                        waits = where;

                        // On the transmitter's own thread, immediately before this frame's write, so
                        // in the order the frames go on the air.
                        announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        previous = Interlocked.Exchange(ref _lastAnnouncement, announced.Task);
                    },
                    frame: frame,
                    packed: packed)
                .ConfigureAwait(false);

            if (!transmitted)
            {
                return;
            }

            // Announced in the order the frames went out, one at a time. The transmitter completes
            // each frame's task in that order, but each continuation then runs whenever the thread
            // pool gets to it, and the frames of one keyup complete close together. A pool thread
            // runs the work it queued itself newest first, so on a busy pool a three-frame keyup
            // could be announced 3, 2, 1, with the handlers running at the same time on threads of
            // their own: the frame log and the console wrote the rows in that order, each row's
            // figures right and the rows in the wrong places. Comparing held times across the rows
            // of one keyup is the whole use of them. The channel's own wait-attribution tests
            // failed on exactly this under load (#537). Waiting for the previous announcement costs
            // a caller nothing it was not already waiting for: its frame went out after that one,
            // and what it waits on is that frame's log lines.
            await previous.ConfigureAwait(false);
            FrameTransmitted?.Invoke(subChannel, frame);
            FrameTransmittedWithTrim?.Invoke(subChannel, frame, applied);
            FrameTransmittedWithReport?.Invoke(
                subChannel, frame, new TransmitReport(applied, heldFor) { Waits = waits });
        }
        finally
        {
            // Whatever happened to this frame - announced, refused after all, faulted with the
            // device - the one behind it must not wait for an announcement that is never coming.
            // But not before the one in front of it has been made either: a frame that failed
            // after taking its place has not waited for its predecessor, and releasing the next
            // frame at once would let that one be announced ahead of an earlier frame whose
            // handlers had not run yet. So its place completes when its predecessor's does, and
            // this caller is not held for it: its own frame is not being announced.
            if (announced is not null)
            {
                if (previous.IsCompleted)
                {
                    announced.TrySetResult();
                }
                else
                {
                    _ = previous.ContinueWith(
                        static (_, place) => ((TaskCompletionSource)place!).TrySetResult(),
                        announced,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
    }

    /// <summary>
    /// Raised alongside <see cref="FrameTransmitted"/>, carrying how far the burst was shifted
    /// off the nominal centre to suit the station it was addressed to; 0 when it went out
    /// straight.
    /// </summary>
    /// <remarks>
    /// Separate from the receive side's offset on purpose. A received frame's offset is a
    /// <em>measurement</em> of somebody else's transmitter; this is a <em>command</em> to our
    /// own, known exactly rather than estimated. Reporting them as one number would make a
    /// station's average offset meaningless, mixing what they did with what we did about it.
    /// </remarks>
    public event Action<int, byte[], double>? FrameTransmittedWithTrim;

    /// <summary>
    /// Raised alongside <see cref="FrameTransmitted"/> with everything the channel knows about
    /// what it did with one frame: the trim it applied, and how long the frame waited for the channel.
    /// </summary>
    /// <remarks>
    /// <para>A third event rather than a wider <see cref="FrameTransmittedWithTrim"/>, because
    /// this class ships as a NuGet package and the existing two are somebody else's compile.
    /// Both of the older ones are still raised, in the order they were added; a station's own
    /// consumers read this one.</para>
    /// <para>The station's journal, frame log and page all take <see cref="TransmitReport.HeldFor"/>
    /// from here, so the three say the same number about the same frame rather than each timing
    /// the transmission from wherever it happened to be told about it.</para>
    /// </remarks>
    public event Action<int, byte[], TransmitReport>? FrameTransmittedWithReport;

    /// <summary>
    /// Raised once a KISS-addressed frame has been transmitted - after the audio has gone to the
    /// device, not when the frame was queued.
    /// </summary>
    /// <remarks>
    /// The receive side has had <see cref="FrameReceived"/> since the beginning and the transmit
    /// side had only <see cref="TransmitRejected"/>, so a station's journal recorded every frame
    /// it failed to send and none that it sent. Service transmitters that are not KISS modems
    /// (paging, ARDOP) go through the delegate overload and are not announced here - they are not
    /// frames on a sub-channel.
    /// </remarks>
    public event Action<int, byte[]>? FrameTransmitted;

    /// <summary>Queues an arbitrary transmission - the channel-access path (CSMA, PTT,
    /// pacing, TX-complete) for service transmitters that are not KISS-addressed modems
    /// (e.g. POCSAG paging). The delegate receives the TXDELAY budget in milliseconds
    /// (full on the keyup's first transmission, a token 30 ms after) and returns the
    /// audio at the channel rate.</summary>
    /// <param name="modulate">Renders the transmission; an <see cref="ArgumentException"/>
    /// thrown here drops the item and faults the returned task, as for frames.</param>
    /// <param name="rejected">Optional observer for such a rejection.</param>
    /// <param name="ownsChannelTiming">
    /// True for a transmitter that owns the channel's timing rather than sharing it - an ARDOP
    /// ARQ session, whose turnarounds are what <see cref="TransmitInhibit"/> protects. Such a
    /// transmission skips <b>both</b> the inhibit and the p-persistence roll: it is not one of
    /// the stations contending for the channel, it is the one running it, and deferring would
    /// mean deferring partly to its own signal - a shifted ARDOP centre sits inside a packet
    /// modem's passband and trips its busy detector. Everything else leaves this false.
    /// </param>
    /// <param name="source">
    /// Who is transmitting - the identity a keyup is held for. Consecutive transmissions carrying
    /// the same <paramref name="source"/> share one keyup and the token preamble that goes with
    /// it; a different one ends the keyup and contends for its own. Null means "no identity", and
    /// is treated as unique per transmission, so an unidentified caller never rides on somebody
    /// else's keyup. See <see cref="RunTransmitterAsync"/>.
    /// </param>
    /// <param name="quietAfter">
    /// How long to keep the OTHER transmitters off the air once this one has finished, so that a
    /// reply to it is not transmitted over. Null (the default) never holds. See
    /// <see cref="QuietAfterTransmit"/> for the frame-addressed form and for why the decision
    /// belongs to the caller.
    /// </param>
    /// <param name="withdraw">
    /// Cancelling this takes the transmission back off the queue, so it never keys the radio and
    /// the returned task is cancelled. For a caller that can change its mind while a transmission
    /// waits out a busy channel - the operator's test transmission - where emptying the burst
    /// would not be enough, the transmitter having already keyed by the time it asks for audio.
    /// Silent once the transmission is under way; see <c>Withdraw</c>. The default cannot be
    /// cancelled and costs nothing.
    /// </param>
    /// <param name="stopEarly">
    /// Polled between blocks of this item's own audio, once it is on its way to the device: true
    /// ends the write early, fading the block in progress to silence rather than cutting it dead.
    /// Null (every caller but the operator's test transmission) writes the whole burst as one
    /// call, exactly as before - the blocks share one continuous write with nothing drained
    /// between them either way, so this is never what re-primes the PCM; see
    /// <see cref="RunTransmitterAsync"/>.
    /// </param>
    /// <param name="written">
    /// Told, once, how many samples of this item actually reached the device - the whole burst
    /// unless <paramref name="stopEarly"/> cut it short. A caller that renders its whole burst up
    /// front (the operator's test tone does, for a continuous tone) cannot otherwise tell a stop
    /// from a completion: what it rendered and what was actually written stop being the same
    /// number the moment a write can end early.
    /// </param>
    /// <param name="started">
    /// Told, once, how long the channel held this transmission: the interval between this call
    /// and the transmitter picking the frame up. Called on the transmitter's thread immediately
    /// before the burst is written, and only for a frame that is actually going out - a frame the
    /// modem refuses never reports one. A handler that throws loses the figure and nothing else.
    /// </param>
    public Task EnqueueTransmit(
        Func<int, float[]> modulate, Action<Exception>? rejected = null, bool ownsChannelTiming = false,
        object? source = null, TimeSpan? quietAfter = null, CancellationToken withdraw = default,
        Func<bool>? stopEarly = null, Action<int>? written = null, Action<TimeSpan>? started = null) =>
        EnqueueTransmitCore(
            modulate, rejected, ownsChannelTiming, source, quietAfter, withdraw, stopEarly, written,
            started, noted: null);

    /// <summary>
    /// The whole of <see cref="EnqueueTransmit(Func{int, float[]}, Action{Exception}, bool, object,
    /// TimeSpan?, CancellationToken, Func{bool}, Action{int}, Action{TimeSpan})"/>, plus the
    /// station's own richer report of the wait.
    /// </summary>
    /// <remarks>
    /// Private, and the public overload delegates to it unchanged, so the published signature does
    /// not gain a parameter. This assembly ships as a NuGet package and that signature is
    /// somebody else's compile; the breakdown reaches a station through
    /// <see cref="FrameTransmittedWithReport"/>, which is where its journal, frame log and page
    /// read everything else about a transmission from.
    /// </remarks>
    private Task<bool> EnqueueTransmitCore(
        Func<int, float[]> modulate, Action<Exception>? rejected, bool ownsChannelTiming,
        object? source, TimeSpan? quietAfter, CancellationToken withdraw,
        Func<bool>? stopEarly, Action<int>? written, Action<TimeSpan>? started,
        Action<TimeSpan, TransmitWaits>? noted, byte[]? frame = null, PackedFrame? packed = null)
    {
        ArgumentNullException.ThrowIfNull(modulate);
        if (ReceiveOnlyReason is string receiveOnly)
        {
            var refusal = new InvalidOperationException(receiveOnly);
            rejected?.Invoke(refusal);
            Task<bool> faulted = Task.FromException<bool>(refusal);
            _ = faulted.Exception; // observed here: a fire-and-forget caller cannot, and on a
                                   // receive-only channel this happens to every frame.
            return faulted;
        }

        // Stamped here rather than in EnqueueNow, so that the wait a caller is told about is the
        // whole wait it actually had, including any time another service held the channel.
        long queuedAt = _time.GetTimestamp();
        object identity = source ?? new object();

        // Queued here, from this call, whatever the channel is doing. Nothing between this call
        // and the queue may await, because the queue's order is the order frames go on the air
        // and anything awaited puts the scheduler in charge of it. TransmitInhibit used to be
        // waited out HERE, one poll per frame, so several frames of one link could be queued in
        // the order their polls happened to wake rather than the order the host wrote them - on
        // AX.25 that is I-frames on the air with N(S) out of sequence. The hold is now waited out
        // by the transmitter, which has one queue to draw from and cannot reorder it.
        return EnqueueNow(
            modulate, rejected, ownsChannelTiming, identity, quietAfter, withdraw, stopEarly, written,
            queuedAt, started, noted, frame, packed);
    }

    /// <summary>
    /// Asked, per frame, how long this station should stay off the air after sending it so that
    /// the reply is not transmitted over by one of the channel's OTHER modems. Null (the default
    /// hook, and a null result) never holds, which is the behaviour of a channel that says
    /// nothing about reply timing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is a hook.</b> Carrier sense cannot solve this: at the moment we roll
    /// p-persistence the reply has not started, so there is nothing to detect and deferring is
    /// down to luck. The only tool is prediction, and predicting means knowing whether a frame is
    /// the kind that gets an answer - which is a property of the PROTOCOL the frame belongs to,
    /// not of the modem carrying it. So the modem asks and does not guess. The AX.25 answer lives
    /// in <see cref="Modems.Ax25ReplyExpectation"/>, and anything else this channel ever carries
    /// brings its own or leaves this null and keeps today's behaviour exactly. Same shape and
    /// same reasoning as <see cref="TransmitTrimHz"/>.</para>
    /// <para>The window itself is a calculation, not a fitted constant: the peer needs its own
    /// TXDELAY to key up and a few p-persistence slots to win the channel, both of which are
    /// wall-clock KISS parameters this channel already holds. It is very nearly independent of
    /// baud rate, because what is being waited out is the far end keying up rather than anything
    /// being sent. See <see cref="TurnaroundHold"/>.</para>
    /// </remarks>
    public Func<int, byte[], TimeSpan?>? QuietAfterTransmit { get; set; }

    /// <summary>
    /// The turnaround window <see cref="QuietAfterTransmit"/> implementations should use: long
    /// enough for the far end to key up and win the channel, and no longer.
    /// </summary>
    /// <remarks>
    /// <para>Calculated, not fitted. What is being waited out is the far end KEYING UP, which is
    /// its own TXDELAY (it runs the same convention, so ours is the fair estimate of theirs),
    /// plus its rig and decode latency, for which a second TXDELAY is the honest allowance, plus
    /// one slot of contention. Note what that makes it nearly independent of: baud rate. Nothing
    /// here is a symbol count - TXDELAY and slot time are wall-clock KISS parameters - so a
    /// 9600 Bd link waits about as long as a 300 Bd one, which is the opposite of what the
    /// intuition says.</para>
    /// <para>On the shipped defaults (300 ms, 100 ms) that is 700 ms. Against 4,997 real
    /// turnarounds in GB7RDG-2's frame log it covers 80 % of them, and the curve is a knee:
    /// another 600 ms buys only 11 more points (1.30 s covers 91 %).</para>
    /// <para>Slot time deliberately carries little weight, because a host may set it to zero and
    /// one does: LinBPQ configures this very station with TXDELAY 300 ms, persistence 50 and
    /// <b>slot time 0</b>, which turns the p-persistence backoff into a spin and leaves this hold
    /// as the only thing keeping the station off the air after its own transmission. A window
    /// built mostly out of slot time would have quietly become 300 ms there.</para>
    /// </remarks>
    public TimeSpan TurnaroundHold =>
        TimeSpan.FromMilliseconds((2 * Csma.TxDelayMilliseconds) + Csma.SlotTimeMilliseconds);

    /// <summary>
    /// A ceiling on how long one transmitter can keep the hold by being answered. Not a fairness
    /// knob for the ordinary case - an exchange that keeps getting replies is exactly what the
    /// hold is for. This is the backstop for the pathological one, where a busy frequency renews
    /// the hold indefinitely and every other transmitter on the channel is muted for good.
    /// </summary>
    public TimeSpan MaxTurnaroundHold { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set to say the channel has no transmitter at all, with the reason an operator or a host
    /// should be told. Every transmission is then refused the moment it is queued, carrying this
    /// as its message.
    /// </summary>
    /// <remarks>
    /// This is not <see cref="TransmitInhibit"/>, which holds a transmission back until the
    /// channel frees up. Nothing here ever frees up - a web receiver has no transmitter - so
    /// queueing would only turn "cannot" into a 30-second wait ending in the wrong explanation.
    /// It also bypasses <c>ownsChannelTiming</c>: ARDOP owning the timing of a channel it cannot
    /// key changes nothing about whether the frame goes out.
    /// </remarks>
    public string? ReceiveOnlyReason { get; set; }

    /// <summary>
    /// Raised with each block of audio as it is handed to the sound device - the station's own
    /// transmission, at the channel rate.
    /// </summary>
    /// <remarks>
    /// For a display that wants to draw what we put on the air. Receive processing is gated off
    /// while transmitting (half duplex), so without this a waterfall simply stops for the length
    /// of every keyup and its time axis quietly stops meaning anything.
    ///
    /// Raised at the moment the samples are written, which is slightly ahead of them leaving the
    /// device - there is a buffer and a drain behind this. Close enough for a display; not a
    /// timing reference.
    /// </remarks>
    public event Action<ReadOnlyMemory<float>>? TransmittedAudio;

    /// <summary>
    /// Raised with true when the transmitter takes the channel and false when it gives it back.
    /// </summary>
    /// <remarks>
    /// For a display. Receive processing stops the moment this goes true, but the first
    /// transmitted audio does not exist until the frame has been modulated and handed to the
    /// device - so anything drawing the channel needs to know a keyup has begun rather than
    /// inferring it from audio that has not arrived yet, or it simply stops for that gap.
    /// </remarks>
    public event Action<bool>? TransmittingChanged;

    /// <summary>
    /// Raised with the sample count of a receive block that arrived while the channel was
    /// transmitting - the block <see cref="ProcessReceive"/> skipped its modems and its receive
    /// taps for (half duplex).
    /// </summary>
    /// <remarks>
    /// A tap sees nothing at all for the length of a keyup, which is fine for anything that only
    /// cares about decoded signal, but a consumer whose own output has to carry a sample clock
    /// that never stops - a stream another program reads as though it were a live sound card -
    /// needs to know a keyup block was skipped, not silently miss it. This event is raised from
    /// inside <see cref="ProcessReceive"/> at the exact point a block would otherwise be dropped
    /// on the floor, with the length of that block, so such a consumer can emit a silent block
    /// of the same length, marked as transmitted, in its place: the reader's clock keeps
    /// advancing and knows which part of it was us rather than the band.
    /// </remarks>
    public event Action<int>? KeyedReceiveBlock;

    /// <summary>
    /// Raised by <see cref="NoteReceiveAudioLost"/>: the input lost audio before the next block
    /// <see cref="ProcessReceive"/> is given.
    /// </summary>
    /// <remarks>
    /// The channel itself does nothing with it; it is for a consumer that hands the receive
    /// audio on as a continuous stream and has to tell its reader not to treat the two sides of
    /// the hole as one signal. Raised on whatever thread called <see cref="NoteReceiveAudioLost"/>,
    /// which is the receive thread, so a handler must return promptly and must not allocate.
    /// </remarks>
    public event Action? ReceiveAudioLost;

    /// <summary>
    /// Says that the input lost audio here: an overrun on a sound card, a packet the radio's
    /// stream never delivered, an input that stalled and came back. The next block passed to
    /// <see cref="ProcessReceive"/> is the first one after the hole.
    /// </summary>
    /// <remarks>
    /// Only a flag, not a sample count: none of the inputs that can lose audio say how much they
    /// lost. The host calls it on the receive thread, immediately before the
    /// <see cref="ProcessReceive"/> of the block that follows the loss; the channel knows nothing
    /// about which device lost what.
    /// </remarks>
    public void NoteReceiveAudioLost() => ReceiveAudioLost?.Invoke();

    /// <summary>
    /// A PTT keyup or unkey failed. Raised instead of letting the exception kill the
    /// transmitter loop, which is what happened before this existed: the loop had no catch,
    /// the daemon swallowed the task's fault, and one throwing Key() left a receive-only
    /// station with no log line - for every PTT type, not just a Flex. On a keyup failure
    /// every queued frame is faulted (a definite answer for ACKMODE hosts and the reject
    /// path, matching TransmitInhibitTimeout's philosophy) and the loop carries on: the
    /// next enqueue tries again, so a transient contention or an unplugged serial lead
    /// costs frames, never the transmitter.
    /// </summary>
    public event Action<Exception>? PttFailed;

    /// <summary>
    /// Consulted before a shared transmission is put on the air; while it returns true those
    /// transmissions wait. Set by a host that has to keep a stretch of the channel clear - an
    /// ARDOP ARQ session, whose timing an AX.25 frame landing mid-turnaround would break. Null
    /// (the default) means nothing is holding the channel.
    /// </summary>
    /// <remarks>
    /// A held frame waits IN ITS TRANSMITTER'S QUEUE rather than in front of it: it is queued by
    /// the call that handed it over, in that call's order, and the transmitter is what waits.
    /// That is what keeps a link's frames in sequence across a hold - see
    /// <see cref="InhibitHolds"/> for what the other arrangement cost. A transmission that owns
    /// the channel's timing is never held, since the holder is usually the one making it.
    /// </remarks>
    public Func<bool>? TransmitInhibit { get; set; }

    /// <summary>
    /// How long a transmission waits on <see cref="TransmitInhibit"/> before being rejected.
    /// A held frame cannot wait indefinitely: an AX.25 host will have retried long before an
    /// ARQ session ends, so a definite answer beats a transmission that eventually escapes
    /// minutes late as a duplicate.
    /// </summary>
    /// <remarks>
    /// Measured from the moment the frame was handed over and armed per frame at that moment
    /// (<see cref="ExpireHeldTransmission"/>), so the answer is a property of the frame and does
    /// not depend on which transmitter the round robin is looking at, or on a transmitter running
    /// at all. The enqueue task faults with the same exception it always did and
    /// <c>TransmitRejected</c> is raised with it, so an ACKMODE host is told the frame is gone
    /// rather than left waiting.
    /// </remarks>
    public TimeSpan TransmitInhibitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Coarse on purpose: this gates against sessions lasting minutes.</summary>
    private static readonly TimeSpan InhibitPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Whether another service is holding the channel against this transmitter's next frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Asked here rather than at the enqueue, and that is the whole point.</b> Each frame
    /// used to wait out <see cref="TransmitInhibit"/> on a poll of its own before it was queued,
    /// so when the hold lifted the frames of one link were queued in whatever order their polls
    /// happened to wake: ordering by the thread pool. For AX.25 that is I-frames on the air with
    /// N(S) out of sequence, which costs the peer a REJ or SREJ recovery and can drop the link.
    /// Asked once, by the one loop that transmits, there is nothing to reorder - the frames are
    /// already in the queue in the order the host wrote them, and the queue is the only thing
    /// that decides what goes out next.</para>
    /// <para>A frame that owns the channel's timing is never held. ARDOP is the usual holder, and
    /// holding its own bursts behind its own hold would deadlock an ARQ session.</para>
    /// </remarks>
    private bool InhibitHolds(object source)
    {
        // A lease is a claim on the transmitter that outranks the inhibit: everything still
        // queued belongs to the holder, and the service that set the inhibit cannot transmit
        // until the lease ends anyway.
        if (TransmitInhibit?.Invoke() != true || TransmitLease.HolderNow() is not null)
        {
            return false;
        }

        // Nothing queued for this transmitter is nothing to hold, and a transmission that owns
        // the channel's timing is never held.
        return PeekFrom(source) is { OwnsTiming: false };
    }

    private Task<bool> EnqueueNow(
        Func<int, float[]> modulate, Action<Exception>? rejected, bool ownsChannelTiming, object source,
        TimeSpan? quietAfter, CancellationToken withdraw, Func<bool>? stopEarly, Action<int>? written,
        long queuedAt, Action<TimeSpan>? started, Action<TimeSpan, TransmitWaits>? noted, byte[]? frame,
        PackedFrame? packed)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new TxItem(
            modulate, done, rejected, ownsChannelTiming, source, quietAfter, stopEarly, written,
            queuedAt, started)
        {
            Noted = noted,
            Frame = frame,
            Packed = packed,
        };
        Exception? leasedOut = null;
        lock (_txGate)
        {
            // Asked under the queue lock, which is also where a lease being taken sweeps the
            // queues (RefuseOutsideLease), so a transmission either sees the lease here or is
            // queued before the sweep and refused by it: it never slips in between.
            if (TransmitLease.HolderNow() is int holder && !TransmitLease.IsAttributed(source, holder))
            {
                leasedOut = TransmitLease.Refusal(holder);
            }
            else if (IsExpiredHoldersModemLocked(source))
            {
                leasedOut = new InvalidOperationException(ExpiredDropReason);
            }
            else
            {
                if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue))
                {
                    queue = new Queue<TxItem>();
                    _txQueues[source] = queue;
                    _txOrder.Add(source);
                }

                // Brought up to this instant BEFORE the copy below, so that the stretch the
                // transmitter is in the middle of is booked against the frames that were already
                // waiting and none of it lands on this one, which has only just arrived.
                CreditWaitLocked(_time.GetTimestamp());
                if (!_waitLedgers.TryGetValue(source, out long[]? ledger))
                {
                    ledger = new long[LedgerLength];
                    _waitLedgers[source] = ledger;
                }

                item.Ledger = ledger;
                item.LedgerAtQueue = (long[])ledger.Clone();
                queue.Enqueue(item);
                WakeLocked();
            }
        }

        if (leasedOut is not null)
        {
            // Refused, never queued: a queue would release a burst of stale frames when the lease
            // ends, where AX.25 simply retries. Announced before the task faults, as every other
            // refusal is, and observed here because a fire-and-forget caller cannot.
            rejected?.Invoke(leasedOut);
            done.TrySetException(leasedOut);
            _ = done.Task.Exception;
            return done.Task;
        }

        if (withdraw.CanBeCanceled)
        {
            // Registered after the enqueue, and Withdraw is a no-op on an item that is no longer
            // in the queue, so a token that fires between the two is not a lost transmission.
            item.Withdrawal = withdraw.Register(() => Withdraw(item));
        }

        if (!ownsChannelTiming && TransmitInhibit is not null)
        {
            // Armed here, per frame, from the moment the host handed it over. It used to be the
            // elapsed check inside each frame's own inhibit poll, and it has to keep working the
            // same way: a channel whose transmitter is not running still owes an ACKMODE host an
            // answer, and the answer must not depend on where the round robin happens to be. Also
            // registered after the enqueue, for the same reason as the withdrawal above.
            item.HeldTooLong = _time.CreateTimer(
                _ => ExpireHeldTransmission(item), null, TransmitInhibitTimeout, Timeout.InfiniteTimeSpan);
            bool stillQueued;
            lock (_txGate)
            {
                stillQueued = _txQueues.TryGetValue(source, out Queue<TxItem>? queue)
                    && queue.Contains(item);
            }

            if (!stillQueued)
            {
                Finish(item);
            }
        }

        return done.Task;
    }

    /// <summary>
    /// Takes a queued transmission back off the queue, so it never reaches the transmitter and
    /// never causes a keyup.
    /// </summary>
    /// <remarks>
    /// <para>The whole point is the keyup. A caller that has given up - an operator cancelling a
    /// test transmission that has been waiting for a busy channel - must not have the radio key
    /// up minutes later on their behalf, unannounced. Emptying the burst is not enough: the
    /// transmitter has already keyed by the time it asks for the audio.</para>
    /// <para>Silent when the item has already been taken for a keyup that is under way. Nothing
    /// can be done about that one - the audio may already be in the sound card - so the task
    /// completes normally as the transmission it was, and it is the caller's business to render
    /// nothing if it no longer wants to be heard.</para>
    /// </remarks>
    private void Withdraw(TxItem item)
    {
        bool removed;
        lock (_txGate)
        {
            removed = RemoveLocked(item);
        }

        if (removed)
        {
            Finish(item);
            item.Done.TrySetCanceled();
        }
    }

    /// <summary>
    /// Takes one item out of its transmitter's queue wherever it sits in it, and says whether it
    /// was still there. Call under <see cref="_txGate"/>.
    /// </summary>
    /// <remarks>
    /// Rebuilt without it rather than dequeued: the item may be behind others of the same source,
    /// and their order is the order they will go out in. Taking one out of the middle is the only
    /// thing allowed to disturb a queue, and it disturbs nothing - what is left is still in the
    /// order the host handed it over.
    /// </remarks>
    private bool RemoveLocked(TxItem item)
    {
        if (!_txQueues.TryGetValue(item.Source, out Queue<TxItem>? queue) || !queue.Contains(item))
        {
            return false;
        }

        var kept = new Queue<TxItem>(queue.Where(queued => !ReferenceEquals(queued, item)));
        if (kept.Count == 0)
        {
            _txQueues.Remove(item.Source);
            _txOrder.Remove(item.Source);
            _waitLedgers.Remove(item.Source);
        }
        else
        {
            _txQueues[item.Source] = kept;
        }

        return true;
    }

    /// <summary>
    /// Refuses one transmission that another service has held past
    /// <see cref="TransmitInhibitTimeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>A held frame cannot wait indefinitely: an AX.25 host will have retried long before an
    /// ARQ session ends, so a definite answer beats a transmission that eventually escapes minutes
    /// late as a duplicate. The answer is the same exception the enqueue used to throw, on the
    /// same two paths, so nothing downstream can tell the difference.</para>
    /// <para>Nothing happens if the hold has lifted by the time this fires: the frame is about to
    /// go out, and refusing it because it was held EARLIER would drop a transmission that is
    /// already on its way. Nothing happens either if it has already gone, which is the ordinary
    /// case for every frame on a station that has an inhibit source at all.</para>
    /// </remarks>
    private void ExpireHeldTransmission(TxItem item)
    {
        if (TransmitInhibit?.Invoke() != true || TransmitLease.HolderNow() is not null)
        {
            return;
        }

        bool removed;
        lock (_txGate)
        {
            removed = RemoveLocked(item);
        }

        if (!removed)
        {
            return;
        }

        Finish(item);
        var refusal = new InvalidOperationException(
            $"another service is holding the channel (waited {TransmitInhibitTimeout.TotalSeconds:F0}s); "
            + "transmission dropped");

        Refuse(item, refusal);
    }

    /// <summary>
    /// Answers an item that is not going out, or not all the way: says so on
    /// <see cref="TxItem.Rejected"/> (the DROPPED line, a KISS host's ACKMODE answer) and then
    /// ends its task, faulted with <paramref name="reason"/>, or cancelled when the reason is a
    /// cancellation.
    /// </summary>
    /// <remarks>
    /// The announcement comes BEFORE the task ends, on every path, and that order is load-bearing:
    /// a caller awaiting the task is released the instant it ends, and if the announcement came
    /// second that caller could look for the rejection and not find it yet. TransmitInhibitTests
    /// caught exactly that on a loaded box, and the paths that ended the task first had the same
    /// hole. The task ends even if a handler throws, so no caller is left waiting on it.
    /// </remarks>
    private static void Refuse(TxItem item, Exception reason)
    {
        try
        {
            item.Rejected?.Invoke(reason);
        }
        finally
        {
            if (reason is OperationCanceledException cut)
            {
                item.Done.TrySetCanceled(cut.CancellationToken);
            }
            else
            {
                item.Done.TrySetException(reason);
            }
        }
    }

    /// <summary>
    /// Releases what an item held while it was queued, once it has left the queue for good.
    /// </summary>
    /// <remarks>
    /// Never under <see cref="_txGate"/>. Disposing a timer can wait on its own clock's lock, and
    /// the callback on the other side of that lock takes _txGate, which is a deadlock waiting for
    /// a slow enough box. Same family of hazard as the unregister-never-dispose rule in
    /// <see cref="TakeFrom"/>, and the same answer: do it where no lock is held.
    /// </remarks>
    private static void Finish(TxItem item)
    {
        item.HeldTooLong?.Dispose();
        item.HeldTooLong = null;
    }

    /// <summary>
    /// Books the time since the last change of phase against every transmitter that had something
    /// waiting for it. Call under <see cref="_txGate"/>.
    /// </summary>
    /// <remarks>
    /// <para>The whole wait accounting is this one method. The transmitter loop is always doing
    /// exactly one thing - waiting out a hold, waiting out carrier sense, waiting a slot after a
    /// lost roll, or keyed up - and it says which as it goes; everything between two of those
    /// statements is credited at the second one. A frame then gets its own breakdown by
    /// subtracting the ledger as it stood when it was queued, which costs nothing while it
    /// waits.</para>
    /// <para><b>Every waiting transmitter is credited, not just the one being served.</b> There
    /// is one transmitter loop and one radio: while it is waiting out carrier sense for one modem,
    /// every other modem's traffic is waiting too, and a breakdown that left those moments out
    /// would not add up to the frame's own held time. What the others get is
    /// <see cref="WaitSlot.OurTurn"/> - they were behind another of this station's links - except
    /// during a hold or a keyup, which are the station's business rather than one link's and are
    /// credited to everybody as what they are.</para>
    /// <para>Cost is one indexed add per waiting transmitter per phase change, with a second for
    /// each sub-channel that asserted carrier sense. Phase changes happen at slot time at worst,
    /// which is 10 to 100 ms on a real station, and nothing here allocates.</para>
    /// </remarks>
    private void CreditWaitLocked(long now)
    {
        long elapsed = now - _waitMark;
        _waitMark = now;
        if (elapsed <= 0 || _waitLedgers.Count == 0)
        {
            return;
        }

        foreach ((object source, long[] ledger) in _waitLedgers)
        {
            bool served = _waitFor is null || ReferenceEquals(source, _waitFor);
            ledger[(int)(served ? _waitSlot : WaitSlot.OurTurn)] += elapsed;
            if (!served || _waitSlot != WaitSlot.ChannelBusy)
            {
                continue;
            }

            for (int asserted = _waitAsserted; asserted != 0; asserted &= asserted - 1)
            {
                ledger[BusySubChannelBase + System.Numerics.BitOperations.TrailingZeroCount(asserted)] += elapsed;
            }
        }
    }

    /// <summary>
    /// Says what the transmitter is about to spend time on, so the moments before it are booked
    /// against whatever it was doing until now.
    /// </summary>
    /// <param name="slot">What it is about to do.</param>
    /// <param name="waitFor">
    /// The transmitter being served, or null for something that holds up all of them - a
    /// turnaround hold, or this station's own keyup.
    /// </param>
    /// <param name="asserted">
    /// Which sub-channels said the channel was busy, as a bit each, with
    /// <see cref="RadioBusyBit"/> for the radio's station-wide answer. Only read for
    /// <see cref="WaitSlot.ChannelBusy"/>.
    /// </param>
    private void EnterWait(WaitSlot slot, object? waitFor, int asserted = 0)
    {
        lock (_txGate)
        {
            CreditWaitLocked(_time.GetTimestamp());
            _waitSlot = slot;
            _waitFor = waitFor;
            _waitAsserted = asserted;
        }
    }

    /// <summary>
    /// Where one frame's wait went, from the ledger it has been subtracting from since it was
    /// queued.
    /// </summary>
    /// <remarks>
    /// The remainder after the measured causes is reported as
    /// <see cref="TransmitWaits.Unattributed"/> rather than quietly dropped or folded into the
    /// biggest cause: the parts of this have to add up to the whole, because the whole is a figure
    /// an operator already has from somewhere else and a breakdown that does not reconcile with it
    /// is worse than no breakdown.
    /// </remarks>
    private TransmitWaits WaitsFor(TxItem item, TimeSpan heldFor)
    {
        Span<long> spent = stackalloc long[LedgerLength];
        if (item.LedgerAtPickup is { } atPickup && item.LedgerAtQueue is { } atQueue)
        {
            for (int i = 0; i < LedgerLength; i++)
            {
                spent[i] = atPickup[i] - atQueue[i];
            }
        }

        int busiest = -1;
        int mask = 0;
        for (int sub = 0; sub < 16; sub++)
        {
            long ticks = spent[BusySubChannelBase + sub];
            if (ticks <= 0)
            {
                continue;
            }

            mask |= 1 << sub;
            if (busiest < 0 || ticks > spent[BusySubChannelBase + busiest])
            {
                busiest = sub;
            }
        }

        if (spent[BusyRadioSlot] > 0)
        {
            mask |= RadioBusyBit;
        }

        TimeSpan busy = Elapsed(spent[(int)WaitSlot.ChannelBusy]);
        TimeSpan backoff = Elapsed(spent[(int)WaitSlot.Backoff]);
        TimeSpan hold = Elapsed(spent[(int)WaitSlot.TurnaroundHold]);
        TimeSpan ours = Elapsed(spent[(int)WaitSlot.OurTransmission]);
        TimeSpan turn = Elapsed(spent[(int)WaitSlot.OurTurn]);
        TimeSpan inhibit = Elapsed(spent[(int)WaitSlot.TransmitInhibit]);
        TimeSpan accounted = busy + backoff + hold + ours + turn + inhibit;
        return new TransmitWaits
        {
            Total = heldFor,
            ChannelBusy = busy,
            Backoff = backoff,
            TurnaroundHold = hold,
            TransmitInhibit = inhibit,
            OurTransmission = ours,
            OurTurn = turn,
            Unattributed = heldFor > accounted ? heldFor - accounted : TimeSpan.Zero,
            BusySubChannels = mask,
            BusiestSubChannel = busiest < 0 ? null : busiest,
        };
    }

    /// <summary>Timestamp ticks as a duration, on this channel's own clock.</summary>
    private TimeSpan Elapsed(long ticks) => ticks <= 0 ? TimeSpan.Zero : _time.GetElapsedTime(0, ticks);

    /// <summary>
    /// Tells a queued transmission how long the channel held it, without letting a caller's
    /// handler take the transmitter down with it.
    /// </summary>
    /// <remarks>
    /// Guarded because this runs inside the keyup, between the modulation and the write. Every
    /// other event on this class is raised from a path where a throwing handler costs one frame;
    /// here it would cost the rest of the keyup and leave PTT to the finally, so the one caller
    /// who writes a bad handler would take out the station's transmitter rather than their own
    /// log line.
    /// </remarks>
    private void NoteHeldFor(TxItem item, TimeSpan heldFor)
    {
        if (item.Started is null && item.Noted is null)
        {
            return;
        }

        try
        {
            item.Started?.Invoke(heldFor);
            item.Noted?.Invoke(heldFor, WaitsFor(item, heldFor));
        }
        catch (Exception reporting) when (reporting is not OperationCanceledException)
        {
            // Nowhere useful to put it: the frame is going out regardless, and the transmitter
            // has no log of its own. Losing the figure is the right cost for keeping the keyup.
        }
    }

    /// <summary>Renews the hold when the link it protects is heard from.</summary>
    private void NoteHeard(object source)
    {
        lock (_txGate)
        {
            if (_quietOwner is not null && ReferenceEquals(source, _quietOwner))
            {
                _quietFrom = _time.GetTimestamp();
            }
        }
    }

    /// <summary>Blocks until some transmitter has something queued.</summary>
    private async Task WaitForWorkAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task signal;
            lock (_txGate)
            {
                if (_txOrder.Count > 0)
                {
                    return;
                }

                // Replaced under the same lock an enqueue takes, so an enqueue either saw a
                // non-empty queue above or will complete the instance we are about to await.
                _txSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = _txSignal.Task;
            }

            await signal.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The transmitter whose turn it is, or null while every transmitter with traffic is waiting
    /// out somebody else's turnaround.
    /// </summary>
    /// <remarks>
    /// Round robin over the sources that have work, skipping any the hold is keeping quiet. A
    /// transmission that owns the channel's timing is never held: ARDOP is running the channel
    /// rather than sharing it, and its own ARQ turnarounds are what it is protecting.
    /// </remarks>
    private object? NextEligibleSource(out TimeSpan? soonest, out long wakes, out long rendersDone)
    {
        lock (_txGate)
        {
            rendersDone = _rendersDone;
        }

        soonest = null;
        var toStart = new List<(object, TxItem[], int)>();
        object? chosen = NextEligibleSourceCore(ref soonest, toStart, out wakes);
        StartPackedRenders(toStart);
        return chosen;
    }

    /// <summary>The body of <see cref="NextEligibleSource"/>, under the lock.</summary>
    /// <remarks>
    /// A packing transmitter is eligible only once its run has gathered and its next burst has
    /// rendered, and until then it is passed over rather than waited for, so nobody else stands
    /// behind it. <paramref name="soonest"/> is when a gather ends; a render ending wakes the
    /// loop by itself.
    /// </remarks>
    private object? NextEligibleSourceCore(
        ref TimeSpan? soonest, List<(object, TxItem[], int)> toStart, out long wakes)
    {
        lock (_txGate)
        {
            wakes = _wakes;
            // Owning the channel's timing is a claim on the CHANNEL, not just an exemption from
            // the hold, so it is answered before the round robin's turn order. Without this pass
            // an ARDOP burst took its place in the queue behind a packet frame and then waited
            // out that frame's carrier sense - on GB7RDG, carrier sense raised by the very
            // station ARDOP was answering, whose 1000 Hz ConReq covers both packet modems'
            // passbands. Measured there over four days: half of all inbound ARQ calls (14 of 28)
            // were answered between 6 and 31 s late instead of the 1.05 s a clear channel takes,
            // every one of them landing 1.2 s after a packet frame finished, by which time the
            // caller had given up. The queued replies then went out back to back as a run of
            // one-second transmissions, one per connect request the caller had made.
            if (OwnsTimingSourceLocked() is { } owner)
            {
                return owner;
            }

            foreach (object source in _txOrder)
            {
                Queue<TxItem> queue = _txQueues[source];
                if (queue.Count == 0 || !(queue.Peek().OwnsTiming || !IsHeldLocked(source)))
                {
                    continue;
                }

                TxItem head = queue.Peek();
                if (IsPackedHead(head))
                {
                    if (GatherRemainingLocked(head) is { } gathering)
                    {
                        soonest = soonest is { } earlier && earlier < gathering ? earlier : gathering;
                        continue;
                    }

                    if (!PackedReadyLocked(source, queue, toStart))
                    {
                        continue;
                    }
                }

                return source;
            }

            return null;
        }
    }

    /// <summary>
    /// The first transmitter whose next frame owns the channel's timing, or null. Call under
    /// <see cref="_txGate"/>.
    /// </summary>
    private object? OwnsTimingSourceLocked()
    {
        foreach (object source in _txOrder)
        {
            if (_txQueues[source] is { Count: > 0 } queue && queue.Peek().OwnsTiming)
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>The same question from outside the lock, for the channel-access wait.</summary>
    private object? OwnsTimingSource()
    {
        lock (_txGate)
        {
            return OwnsTimingSourceLocked();
        }
    }

    /// <summary>
    /// How long until the hold lets somebody else transmit - the exact wait, so the scheduler
    /// sleeps once rather than polling.
    /// </summary>
    /// <remarks>
    /// Floored at one tick of a millisecond so a hold that expires between the eligibility check
    /// and this call cannot produce a zero-length wait, and capped by what the hold itself can be
    /// (its window, or the ceiling, whichever bites first) so a lost wake-up cannot strand the
    /// scheduler. A renewal shortens nothing and lengthens by at most one window, because the
    /// loop re-checks after every wait.
    /// </remarks>
    private TimeSpan RemainingHold()
    {
        lock (_txGate)
        {
            if (_quietOwner is null)
            {
                return MinimumSchedulerWait;
            }

            TimeSpan left = _quietWindow - _time.GetElapsedTime(_quietFrom);
            TimeSpan untilCeiling = MaxTurnaroundHold - _time.GetElapsedTime(_quietOwnerSince);
            TimeSpan wait = left < untilCeiling ? left : untilCeiling;
            return wait < MinimumSchedulerWait ? MinimumSchedulerWait : wait;
        }
    }

    /// <summary>Never wait zero: a scheduler that polls with no delay is a spinning core.</summary>
    private static readonly TimeSpan MinimumSchedulerWait = TimeSpan.FromMilliseconds(1);

    private bool IsHeldLocked(object source)
    {
        if (_quietOwner is null || ReferenceEquals(source, _quietOwner))
        {
            return false;
        }

        return _time.GetElapsedTime(_quietOwnerSince) < MaxTurnaroundHold
            && _time.GetElapsedTime(_quietFrom) < _quietWindow;
    }

    /// <summary>Takes the next item from one transmitter's queue, or null when it has run dry -
    /// or, given <paramref name="only"/>, when the next item is not that one.</summary>
    private TxItem? TakeFrom(object source, TxItem? only = null)
    {
        if (RefuseIfLeasedOut(source))
        {
            return null;
        }

        lock (_txGate)
        {
            if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue) || queue.Count == 0)
            {
                return null;
            }

            if (only is not null && !ReferenceEquals(queue.Peek(), only))
            {
                return null;
            }

            if (TransmitLease.HolderNow() is int holder && !TransmitLease.IsAttributed(source, holder))
            {
                // Taken in the instant since the check above. Nothing leaves the queue for a
                // transmitter the lease does not cover; the next pass refuses what is left.
                return null;
            }

            // Booked before the dequeue, while this transmitter's ledger is still registered: the
            // stretch that ends here - a keyup's earlier burst, the last slot of carrier sense -
            // is the end of this frame's wait, and the item is about to be told what that wait
            // was. The copy is what the breakdown is computed from, because from the next
            // instruction onwards the ledger belongs to the frames behind this one.
            CreditWaitLocked(_time.GetTimestamp());
            TxItem item = queue.Dequeue();
            if (item.Ledger is { } ledger)
            {
                item.LedgerAtPickup = (long[])ledger.Clone();
            }


            // Unregister, NOT Dispose. Dispose blocks until a callback that is already running
            // has finished, and that callback is Withdraw, whose first act is to take this very
            // lock - so a token firing in the few instructions between the dequeue above and
            // this line would leave the transmitter holding _txGate and waiting for Withdraw
            // while Withdraw waits for _txGate. Neither ever returns, the lock is held for the
            // life of the process, and every enqueue and every keyup behind it stops silently.
            // Unregister does not wait, and returns false when the callback is already under
            // way - which is exactly this type's "silent once the transmission is under way"
            // contract, since by then the item is out of the queue and Withdraw will find
            // nothing to remove.
            item.Withdrawal.Unregister();
            if (queue.Count == 0)
            {
                _txQueues.Remove(source);
                _txOrder.Remove(source);

                // A transmitter with nothing queued has nothing waiting, so its ledger goes with
                // its queue and the next frame starts a fresh one from zero. The item just taken
                // holds the array itself, so this cannot strand the figures of the frame that is
                // about to go out.
                _waitLedgers.Remove(source);
            }
            else
            {
                // Round robin: a transmitter that has just had a keyup goes to the back, so a
                // long backlog on one link cannot mute the other indefinitely.
                _txOrder.Remove(source);
                _txOrder.Add(source);
            }

            return item;
        }
    }

    private TxItem? PeekFrom(object source)
    {
        lock (_txGate)
        {
            return _txQueues.TryGetValue(source, out Queue<TxItem>? queue) && queue.Count > 0
                ? queue.Peek()
                : null;
        }
    }

    /// <summary>
    /// Whether an item goes out through the packed path: a sub-channel frame whose modem has
    /// <see cref="IFramePackingModem.Packing"/> set right now.
    /// </summary>
    private static bool IsPackedHead(TxItem item) =>
        !item.OwnsTiming && item.Frame is not null && item.Packed is { Packer.Packing: not null };

    /// <summary>
    /// How much longer a packing modem's head frame should wait for the rest of its run to
    /// arrive, or null when it should contend for the air now. Call under <see cref="_txGate"/>.
    /// </summary>
    /// <remarks>
    /// <para>A host writing a run of frames over KISS hands them over one at a time, and the
    /// transmitter can reach the first before the second has arrived, which would send a run
    /// meant for one burst as a short burst and then the rest. <see cref="FramePacking.Gather"/>
    /// is the modem's answer: measured from when the head frame was queued, so a frame that has
    /// already waited out a busy channel for longer does not wait again, and zero by default.</para>
    /// <para>A source that is gathering is simply not eligible yet (<see cref="NextEligibleSource"/>),
    /// so everyone else, and above all ARDOP's timing-critical replies, carries on meanwhile.</para>
    /// </remarks>
    private TimeSpan? GatherRemainingLocked(TxItem head)
    {
        if (head.Packed?.Packer.Packing is not { Gather: var gather } || gather <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan left = gather - _time.GetElapsedTime(head.QueuedAt);
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>
    /// A packed burst rendered ahead of its keyup, so the radio is never keyed while it waits for
    /// audio: a minute of MS110D through a <see cref="FrequencyShiftedModem"/>'s 639-tap filter is
    /// seconds of DSP.
    /// </summary>
    /// <param name="Head">The queued frame the burst starts with, which is how a render is matched
    /// to the queue it was made from.</param>
    /// <param name="TxDelay">The TXDELAY it was rendered with.</param>
    /// <param name="Result">The frames it carries (the head and those that fit behind it, as they
    /// stood when it started) and their audio.</param>
    private sealed record PackedRender(TxItem Head, int TxDelay, Task<PackedAudio> Result);

    /// <summary>A rendered packed burst: its frames, in order, its audio and its trim.</summary>
    private sealed record PackedAudio(TxItem[] Items, float[] Audio, double Trim);

    /// <summary>
    /// One render per packing transmitter at most: the burst that is next for it, started as soon
    /// as its run has gathered and again while the previous burst is on the air. Guarded by
    /// <see cref="_txGate"/>; the rendering itself happens off the lock, on the thread pool.
    /// </summary>
    private readonly Dictionary<object, PackedRender> _packedRenders = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether a packing transmitter's next burst is rendered and ready to key for, starting the
    /// render if nothing has. Call under <see cref="_txGate"/>; what to start is collected in
    /// <paramref name="toStart"/> and started by the caller once the lock is released.
    /// </summary>
    private bool PackedReadyLocked(object source, Queue<TxItem> queue, List<(object, TxItem[], int)> toStart)
    {
        TxItem head = queue.Peek();
        int txDelay = Csma.TxDelayMilliseconds;
        if (_packedRenders.TryGetValue(source, out PackedRender? render))
        {
            if (!render.Result.IsCompleted)
            {
                // Still rendering. Whether or not it is for this head, one render at a time: the
                // modem's upsampler is not shared across threads. Its completion wakes the loop.
                return false;
            }

            if (ReferenceEquals(render.Head, head) && render.TxDelay == txDelay)
            {
                return true;
            }
        }

        toStart.Add((source, PackedCandidatesLocked(queue), txDelay));
        return false;
    }

    /// <summary>The head of a packing queue and the frames behind it that could share its burst.
    /// Call under <see cref="_txGate"/>.</summary>
    private static TxItem[] PackedCandidatesLocked(Queue<TxItem> queue)
    {
        TxItem head = queue.Peek();
        var candidates = new List<TxItem>();
        foreach (TxItem queued in queue)
        {
            if (candidates.Count > 0
                && (queued.OwnsTiming || queued.Frame is null || queued.Packed is null
                    || !ReferenceEquals(queued.Packed.Packer, head.Packed!.Packer)
                    || candidates.Count >= MaxPackedFramesOffered))
            {
                break;
            }

            candidates.Add(queued);
        }

        return [.. candidates];
    }

    /// <summary>Starts the renders <see cref="PackedReadyLocked"/> asked for.</summary>
    private void StartPackedRenders(List<(object Source, TxItem[] Candidates, int TxDelay)> toStart)
    {
        foreach ((object source, TxItem[] candidates, int txDelay) in toStart)
        {
            // Recorded before it starts, so that its completion can never wake a loop that then
            // finds no render on record and starts a second one alongside it.
            var result = new Task<PackedAudio>(() => RenderPacked(candidates, txDelay));
            lock (_txGate)
            {
                if (_packedRenders.TryGetValue(source, out PackedRender? running) && !running.Result.IsCompleted)
                {
                    continue;
                }

                _packedRenders[source] = new PackedRender(candidates[0], txDelay, result);
            }

            result.Start(TaskScheduler.Default);

            // Wakes the transmitter however the render ends: a burst that renders is ready to
            // key for, and one that fails is refused at pickup like any frame the modem refuses.
            _ = result.ContinueWith(
                _ =>
                {
                    lock (_txGate)
                    {
                        _rendersDone++;
                        WakeLocked();
                    }
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Sizes and renders one packed burst from <paramref name="candidates"/>, head first.
    /// </summary>
    /// <remarks>
    /// <para>A modem that fails to size the run sends its head frame alone: the same answer it
    /// gives for a frame it would refuse, and never a reason to lose the transmitter.</para>
    /// <para>One trim for the whole burst because it is one transmission: the frames share a
    /// carrier, and the trim is asked for the head frame when the burst is rendered rather than
    /// when it keys. The hook is meant for a frame addressed to one station, and a packed burst
    /// is a broadcast in every case this exists for, where the hook answers zero anyway.</para>
    /// </remarks>
    private PackedAudio RenderPacked(TxItem[] candidates, int txDelay)
    {
        PackedFrame lead = candidates[0].Packed!;
        int fit = 1;
        if (candidates.Length > 1)
        {
            var frames = new byte[candidates.Length][];
            for (int i = 0; i < candidates.Length; i++)
            {
                frames[i] = candidates[i].Frame!;
            }

            try
            {
                fit = Math.Clamp(lead.Packer.FramesPerBurst(frames), 1, candidates.Length);
            }
            catch (Exception sizing) when (sizing is not OperationCanceledException)
            {
                fit = 1;
            }
        }

        TxItem[] items = candidates[..fit];
        var burst = new byte[fit][];
        for (int i = 0; i < fit; i++)
        {
            burst[i] = items[i].Frame!;
        }

        double trim = lead.ChooseTrim();
        float[] modulated;

        // One render at a time per modem whatever the bookkeeping says: a modem's resampler
        // carries state from one burst to the next and is not safe across threads.
        lock (RenderGate(lead.Packer))
        {
            modulated = lead.Packer.ModulateFrames(burst, txDelay);
        }

        return new PackedAudio(items, ApplyTransmitTrim(modulated, trim), trim);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _renderGates = [];

    private object RenderGate(IFramePackingModem packer) => _renderGates.GetValue(packer, _ => new object());

    /// <summary>
    /// Takes a packing transmitter's next burst off its queue, with its audio, ready to key for;
    /// or null when there is nothing ready to send, in which case the loop goes round again.
    /// </summary>
    /// <remarks>
    /// <para>The render was made from the queue as it stood a moment ago, so the frames it carries
    /// are taken only while they are still at the head in the same order. If part of the run has
    /// left the queue since (a refusal, a redundant frame removed), or TXDELAY has changed, what
    /// was taken goes back and the burst is rendered again before it contends. It is never
    /// rendered here: that would key the radio seconds after carrier sense said the channel was
    /// clear.</para>
    /// <para>A render that failed is refused here, frame by frame, as the modem's refusal of a
    /// lone frame always has been.</para>
    /// </remarks>
    private async Task<PackedAudio?> TakePackedBurstAsync(object source)
    {
        PackedRender? render;
        lock (_txGate)
        {
            _packedRenders.TryGetValue(source, out render);
            if (render is not null && render.Result.IsCompleted)
            {
                _packedRenders.Remove(source);
            }
        }

        if (render is null || !render.Result.IsCompleted || render.TxDelay != Csma.TxDelayMilliseconds)
        {
            return null;
        }

        long dropsBefore;
        lock (_txGate)
        {
            dropsBefore = _drops.TryGetValue(source, out (long Epoch, string Reason) seen) ? seen.Epoch : 0;
        }


        PackedAudio? rendered = null;
        Exception? failed = null;
        TxItem[] wanted;
        try
        {
            rendered = await render.Result.ConfigureAwait(false);
            wanted = rendered.Items;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            failed = failure;
            wanted = [render.Head];
        }

        var taken = new List<TxItem>(wanted.Length);
        foreach (TxItem item in wanted)
        {
            if (TakeFrom(source, item) is not { } next)
            {
                break;
            }

            taken.Add(next);
        }

        if (taken.Count == 0)
        {
            return null;
        }

        TakingPackedBurst?.Invoke();

        // Dropped while it was being taken: the drop could not see frames already in hand, and
        // they must go the way the rest of the queue went, not back into it or onto the air.
        string? droppedFor = null;
        lock (_txGate)
        {
            if (_drops.TryGetValue(source, out (long Epoch, string Reason) now) && now.Epoch != dropsBefore)
            {
                droppedFor = now.Reason;
            }
        }

        if (droppedFor is not null)
        {
            RefuseWith(taken, droppedFor);
            return new PackedAudio([], [], 0);
        }

        if (failed is not null)
        {
            foreach (TxItem refused in taken)
            {
                Finish(refused);
                Refuse(refused, failed);
            }

            return new PackedAudio([], [], 0);
        }

        if (taken.Count != wanted.Length)
        {
            PutBack(source, taken);
            return null;
        }

        return rendered;
    }

    /// <summary>For tests: called once a packed burst has been taken off its queue and before it
    /// keys, the one moment a drop or an expiry can land with the burst in hand.</summary>
    internal Action? TakingPackedBurst { get; set; }

    /// <summary>Returns frames to the head of their transmitter's queue, in order.</summary>
    private void PutBack(object source, IEnumerable<TxItem> items)
    {
        TxItem[] back = [.. items];
        if (back.Length == 0)
        {
            return;
        }

        lock (_txGate)
        {
            Queue<TxItem> rest = _txQueues.TryGetValue(source, out Queue<TxItem>? queue) ? queue : new Queue<TxItem>();
            _txQueues[source] = new Queue<TxItem>(back.Concat(rest));
            if (!_txOrder.Contains(source))
            {
                _txOrder.Insert(0, source);
            }

            if (back[0].Ledger is { } ledger)
            {
                _waitLedgers[source] = ledger;
            }

            WakeLocked();
        }
    }

    /// <summary>Tells a waiting transmitter something has changed. Call under <see cref="_txGate"/>.</summary>
    private void WakeLocked()
    {
        _wakes++;
        _txSignal.TrySetResult();
    }

    /// <summary>Counts every <see cref="WakeLocked"/>, so the loop can tell whether one happened
    /// between deciding to wait and starting to.</summary>
    private long _wakes;

    /// <summary>Counts packed renders that have ended, so the loop can tell that one finished
    /// between choosing to wait and starting to.</summary>
    private long _rendersDone;

    /// <summary>
    /// One packed burst, as a keyup of its own: keyed only once its audio exists, and unkeyed
    /// when it ends, so the next burst goes back through carrier sense like any other
    /// transmission.
    /// </summary>
    /// <remarks>
    /// <para><b>A keyup per burst, not a keyup per queue.</b> A broadcast can queue a quarter of
    /// an hour of frames, and one keyup draining it would hold the radio deaf for all of it: no
    /// receive, no gap for the modem's own Morse ident, nothing for a Flex's PTT arbitration to
    /// see and no rest for the PA. Ending the keyup after each burst gives all of those their
    /// turn, and costs one TXDELAY per burst, which on a burst of up to a minute is nothing.</para>
    /// <para><b>The next burst renders while this one plays.</b> Started before the write, so by
    /// the time this burst has gone out and the channel has been won again, the next one is
    /// normally ready: the transmitter never keys the radio and then waits for audio.</para>
    /// </remarks>
    private void RunPackedKeyup(object source, PackedAudio burst, IAudioOutput output, IPttControl ptt)
    {
        List<TxItem> items = [.. burst.Items];
        var heldFor = new TimeSpan[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            Finish(items[i]);
            heldFor[i] = _time.GetElapsedTime(items[i].QueuedAt);
        }

        _transmitting = true;
        EnterWait(WaitSlot.OurTransmission, null);
        TransmittingChanged?.Invoke(true);
        bool keyed = false;
        TimeSpan? quietAfter = null;
        try
        {
            try
            {
                ptt.Key();
                keyed = true;
            }
            catch (Exception keyFailure) when (keyFailure is not OperationCanceledException)
            {
                foreach (TxItem lost in items)
                {
                    Refuse(lost, keyFailure);
                }

                FaultAfterKeyFailure(source, keyFailure);
                PttFailed?.Invoke(keyFailure);
                return;
            }
            catch (OperationCanceledException cut)
            {
                // A key cut short by a cancellation. The loop ends on it, as it does for a write
                // cut short, and what is still queued stays queued for a transmitter started
                // again. But this burst's frames have already been taken off the queue, so
                // nothing else will ever answer them: they are dropped, and said to be.
                foreach (TxItem lost in items)
                {
                    Refuse(lost, cut);
                }

                throw;
            }

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    NoteHeldFor(items[i], heldFor[i]);
                    items[i].Packed!.Applied(burst.Trim);
                }

                quietAfter = items[^1].QuietAfter;

                // The next burst, if there is one, renders while this one plays.
                var toStart = new List<(object, TxItem[], int)>();
                lock (_txGate)
                {
                    if (_txQueues.TryGetValue(source, out Queue<TxItem>? queue) && queue.Count > 0
                        && IsPackedHead(queue.Peek()))
                    {
                        PackedReadyLocked(source, queue, toStart);
                    }
                }

                StartPackedRenders(toStart);
                WriteStoppably(output, burst.Audio, null, null, announce: mem => TransmittedAudio?.Invoke(mem));
                foreach (TxItem sent in items)
                {
                    sent.Done.TrySetResult(true);
                }

                items.Clear();
                if (Csma.TxTailMilliseconds > 0)
                {
                    var tail = new float[SampleRate * Csma.TxTailMilliseconds / 1000];
                    TransmittedAudio?.Invoke(tail);
                    output.Write(tail);
                }

                output.Drain();
            }
            catch (Exception deviceFailure) when (deviceFailure is not OperationCanceledException)
            {
                foreach (TxItem dying in items)
                {
                    Refuse(dying, deviceFailure);
                }

                FaultEverything(deviceFailure);
                throw;
            }
            catch (OperationCanceledException cut)
            {
                // Cut short by a cancellation: see the same catch in RunTransmitterAsync.
                foreach (TxItem dying in items)
                {
                    Refuse(dying, cut);
                }

                throw;
            }
        }
        finally
        {
            EndKeyup(ptt, keyed, source, quietAfter);
        }
    }

    private void OptimizeTransmitQueue(object source)
    {
        List<(TxItem Item, Task<bool> Survivor)> removed = [];
        lock (_txGate)
        {
            if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue) || queue.Count < 2)
            {
                return;
            }

            TxItem[] items = queue.ToArray();
            int[] survivors = Ax25TransmitOptimization.FindSurvivors(
                items.Select(item => item.Frame).ToArray());
            queue.Clear();
            for (int i = 0; i < items.Length; i++)
            {
                if (survivors[i] == i)
                {
                    queue.Enqueue(items[i]);
                }
                else
                {
                    items[i].Withdrawal.Unregister();
                    removed.Add((items[i], items[survivors[i]].Done.Task));
                }
            }
        }

        foreach ((TxItem item, Task<bool> survivor) in removed)
        {
            Finish(item);
            _ = CompleteSupersededAsync(item, survivor);
        }
    }

    private static async Task CompleteSupersededAsync(TxItem item, Task<bool> survivor)
    {
        try
        {
            await survivor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Done.TrySetCanceled();
            return;
        }
        catch (Exception failure)
        {
            Refuse(item, failure);

            return;
        }

        // ACKMODE still owes every host request an answer, but only the survivor went on air.
        item.Done.TrySetResult(false);
    }

    /// <summary>Starts, renews or clears the turnaround hold after a keyup.</summary>
    private void SetHold(object? keyupSource, TimeSpan? quietAfter)
    {
        lock (_txGate)
        {
            if (keyupSource is null || quietAfter is not TimeSpan window || window <= TimeSpan.Zero)
            {
                _quietOwner = null;
                return;
            }

            long now = _time.GetTimestamp();
            if (!ReferenceEquals(_quietOwner, keyupSource))
            {
                _quietOwnerSince = now;
            }

            _quietOwner = keyupSource;
            _quietFrom = now;
            _quietWindow = window;
        }
    }

    /// <summary>What a frame dropped by <see cref="DropQueued(int)"/> is refused with.</summary>
    public const string DroppedByHolderReason =
        "dropped by the transmit lease holder before it was sent";

    /// <summary>What a frame dropped as its holder's lease ran out is refused with.</summary>
    public const string ExpiredDropReason =
        "dropped because the transmit lease ran out before it was sent";

    /// <summary>
    /// Takes every frame still queued for <paramref name="subChannel"/>'s modem off the queue and
    /// refuses it, and says how many there were. A burst already keyed finishes; nothing else of
    /// that sub-channel's (its Morse ident, say) is touched.
    /// </summary>
    /// <remarks>
    /// For a broadcast that is abandoned part way: the frames it queued must not go out later as
    /// a burst nobody is running. Each is refused through <see cref="TransmitRejected"/> with one
    /// fixed sentence, as a lease's refusals are, so a KISS ACKMODE host gets no ack for it, the
    /// same as for any refused frame.
    /// </remarks>
    public int DropQueued(int subChannel) => DropQueued(subChannel, DroppedByHolderReason);

    /// <summary>How many times each modem's queue has been dropped, and why it last was. Guarded
    /// by <see cref="_txGate"/>.</summary>
    private readonly Dictionary<object, (long Epoch, string Reason)> _drops = new(ReferenceEqualityComparer.Instance);

    private int DropQueued(int subChannel, string reason)
    {
        if (!_modems.TryGetValue(subChannel, out IModem? modem))
        {
            return 0;
        }

        List<TxItem> dropped = [];
        lock (_txGate)
        {
            RemoveAllLocked(modem, dropped);

            // So a burst being taken off this queue as it is dropped, which the removal above
            // cannot see, is refused rather than put back or keyed (TakePackedBurstAsync).
            long epoch = _drops.TryGetValue(modem, out (long Epoch, string Reason) last) ? last.Epoch : 0;
            _drops[modem] = (epoch + 1, reason);
        }

        RefuseWith(dropped, reason);
        return dropped.Count;
    }

    /// <summary>
    /// Refuses everything queued that does not belong to a lease just taken, and lifts any
    /// turnaround hold, so the holder's traffic is next on the air.
    /// </summary>
    /// <remarks>
    /// Run from <see cref="TransmitLease.Take"/> before the holder is told it has the lease. A
    /// keyup already on the air finishes; nothing can call back audio the card already has.
    /// The refusal is the same one a new transmission gets, through the same path, so a host
    /// cannot tell a frame refused while queued from one refused as it arrived.
    /// </remarks>
    private void RefuseOutsideLease()
    {
        if (TransmitLease.HolderNow() is not int holder)
        {
            return;
        }

        List<TxItem> refused = [];
        lock (_txGate)
        {
            foreach (object source in _txOrder.ToArray())
            {
                if (!TransmitLease.IsAttributed(source, holder))
                {
                    RemoveAllLocked(source, refused);
                }
            }

            _quietOwner = null;
        }

        RefuseLeased(refused, holder);
    }

    /// <summary>
    /// Refuses everything one transmitter has queued, and whatever it has already taken in
    /// <paramref name="taken"/>, if a lease it does not belong to is held now. True when it did.
    /// </summary>
    /// <remarks>
    /// The last word before the radio keys, and the transmitter's first act on taking anything,
    /// so that a lease taken while this transmitter was waiting for the channel is honoured: the
    /// sweep that runs as the lease is taken cannot see a frame the transmitter already has in
    /// hand.
    /// </remarks>
    private bool RefuseIfLeasedOut(object source, IReadOnlyList<TxItem>? taken = null)
    {
        List<TxItem> refused = [];
        int holder;
        lock (_txGate)
        {
            if (IsExpiredHoldersModemLocked(source))
            {
                // A lease that ran out: its holder's burst in hand goes nowhere, so a dead head
                // end's last burst cannot follow its lease out, and the closing ident is next.
                if (taken is not null)
                {
                    refused.AddRange(taken);
                }

                RemoveAllLocked(source, refused);
            }
        }

        if (refused.Count > 0)
        {
            RefuseWith(refused, ExpiredDropReason);
            return true;
        }

        lock (_txGate)
        {
            if (TransmitLease.HolderNow() is not int held || TransmitLease.IsAttributed(source, held))
            {
                return false;
            }

            holder = held;
            if (taken is not null)
            {
                refused.AddRange(taken);
            }

            RemoveAllLocked(source, refused);
        }

        RefuseLeased(refused, holder);
        return true;
    }

    /// <summary>Whether <paramref name="source"/> is the modem of a holder whose lease ran out.
    /// Call under <see cref="_txGate"/> (or not; it takes only the lease's own lock).</summary>
    private bool IsExpiredHoldersModemLocked(object source) =>
        TransmitLease.ExpiringHolderNow() is int expired
        && _modems.TryGetValue(expired, out IModem? modem)
        && ReferenceEquals(modem, source);

    /// <summary>Refuses each of <paramref name="items"/> with one sentence. Never under the lock.</summary>
    private static void RefuseWith(List<TxItem> items, string reason)
    {
        foreach (TxItem item in items)
        {
            Finish(item);
            var refusal = new InvalidOperationException(reason);
            Refuse(item, refusal);
            _ = item.Done.Task.Exception;
        }
    }

    /// <summary>Takes every item one transmitter has queued out of the queue. Call under
    /// <see cref="_txGate"/>.</summary>
    private void RemoveAllLocked(object source, List<TxItem> into)
    {
        if (!_txQueues.TryGetValue(source, out Queue<TxItem>? queue))
        {
            return;
        }

        foreach (TxItem item in queue.ToArray())
        {
            if (RemoveLocked(item))
            {
                // Same reasoning as TakeFrom: never Dispose under this lock.
                item.Withdrawal.Unregister();
                into.Add(item);
            }
        }

        // A render still running keeps its record: it is what stops a second render of the same
        // modem starting alongside it (see PackedReadyLocked).
        if (_packedRenders.TryGetValue(source, out PackedRender? render) && render.Result.IsCompleted)
        {
            _packedRenders.Remove(source);
        }
    }

    /// <summary>Gives each of <paramref name="items"/> the lease's refusal. Never under the lock.</summary>
    private void RefuseLeased(List<TxItem> items, int holder)
    {
        foreach (TxItem item in items)
        {
            Finish(item);
            Exception refusal = TransmitLease.Refusal(holder);
            Refuse(item, refusal);
            _ = item.Done.Task.Exception;
        }
    }

    /// <summary>
    /// What a keyup that would not key costs: everything queued for a broken keying path, but
    /// only this transmitter's queue for a <see cref="TransmitterHeldException"/>, whose radio is
    /// fine and is only not to be keyed right now.
    /// </summary>
    private void FaultAfterKeyFailure(object source, Exception reason)
    {
        if (reason is not TransmitterHeldException)
        {
            FaultEverything(reason);
            return;
        }

        List<TxItem> refused = [];
        lock (_txGate)
        {
            RemoveAllLocked(source, refused);
        }

        foreach (TxItem item in refused)
        {
            Finish(item);
            Refuse(item, reason);
            _ = item.Done.Task.Exception;
        }
    }

    /// <summary>
    /// Whether any transmission is queued and not yet on the air. A cheap look, for a caller that
    /// would rather not start something slow just as the transmitter is about to key.
    /// </summary>
    public bool TransmitQueued
    {
        get
        {
            lock (_txGate)
            {
                return _txQueues.Count > 0;
            }
        }
    }

    /// <summary>Fails every queued transmission - a keyup or a device that has died takes them all.</summary>
    private void FaultEverything(Exception reason)
    {
        List<TxItem> queued = [];
        lock (_txGate)
        {
            foreach (Queue<TxItem> queue in _txQueues.Values)
            {
                queued.AddRange(queue);
            }

            _txQueues.Clear();
            _txOrder.Clear();
            _waitLedgers.Clear();

            // Finished renders only. A running one keeps its record until it ends, so nothing
            // queued afterwards starts a second render of the same modem alongside it.
            foreach (object rendering in _packedRenders.Keys.ToArray())
            {
                if (_packedRenders[rendering].Result.IsCompleted)
                {
                    _packedRenders.Remove(rendering);
                }
            }
            foreach (TxItem item in queued)
            {
                // Same reasoning as TakeFrom: never Dispose under this lock.
                item.Withdrawal.Unregister();
            }
        }

        foreach (TxItem item in queued)
        {
            Finish(item);
            Refuse(item, reason);
        }
    }

    /// <summary>
    /// Runs the transmit side until cancelled: waits for queued frames, acquires the
    /// channel (p-persistent CSMA), keys PTT, plays every queued frame back-to-back,
    /// drains, unkeys.
    /// </summary>
    public async Task RunTransmitterAsync(IAudioOutput output, IPttControl ptt, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        if (output.SampleRate != SampleRate)
        {
            throw new ArgumentException(
                $"output rate {output.SampleRate} != channel rate {SampleRate}", nameof(output));
        }

        while (true)
        {
            // Thrown rather than tested in the while condition, because cancelling this loop has
            // always faulted the task with OperationCanceledException and callers rely on it -
            // the old `await reader.WaitToReadAsync(cancellation)` threw from the await. With the
            // scheduler's own wait now able to return immediately when work is already queued,
            // a plain `while (!IsCancellationRequested)` let the loop exit cleanly instead, which
            // is a silent stop for a transmitter that was cancelled. Race-dependent, so it passed
            // locally in Debug and failed on CI in Release.
            cancellation.ThrowIfCancellationRequested();
            await WaitForWorkAsync(cancellation).ConfigureAwait(false);

            // From here until the keyup, everything this loop does is a queued frame's wait, and
            // it says which kind as it goes so the wait can be told apart afterwards. Nothing was
            // waiting before this point - the loop had nothing to send - so the stretch that ends
            // here is booked to nobody.
            EnterWait(WaitSlot.Unattributed, null);

            // Whose turn is it? While a turnaround hold is running the answer is only the
            // transmitter it protects; everyone else waits a slot and asks again. This is the
            // half CSMA cannot do: at this instant the reply we are protecting has not started,
            // so carrier sense has nothing to detect and deferring would be down to the roll.
            object? source = null;
            while (source is null)
            {
                cancellation.ThrowIfCancellationRequested();
                source = NextEligibleSource(out TimeSpan? gatherEnds, out long wakes, out long rendersDone);
                if (source is null)
                {
                    // Wait out what is left of the hold rather than polling at slot intervals.
                    // Exact, and safe against a slot time of zero: a host may set one, and
                    // LinBPQ was sending exactly that to this station until its config gained a
                    // SLOTTIME line, which would have made this a busy loop burning a core for
                    // the length of every hold. The channel-access roll below still uses the
                    // operator's slot time, because there it IS the channel-access parameter.
                    //
                    // Or the end of a packing modem's gather, if that comes first, or a packed
                    // burst finishing its render, or anything new being queued: a packing modem
                    // that is not ready yet is passed over, not waited for, so a frame for anyone
                    // else must be able to wake this wait.
                    //
                    // With neither of those it is the hold alone, waited out exactly as it always
                    // was: asleep until it ends, deaf to new frames. That is load-bearing for the
                    // order ports get the air in. A new frame from the link that owns the hold
                    // waits too, so the frame another port had already queued goes first when
                    // the hold ends.
                    bool packing = gatherEnds is not null || PackedRenderPending() || RenderEndedSince(rendersDone);
                    if (!packing)
                    {
                        EnterWait(WaitSlot.TurnaroundHold, null);
                        await Delay(RemainingHold(), cancellation).ConfigureAwait(false);
                        continue;
                    }

                    bool holding = HoldActive();
                    EnterWait(holding ? WaitSlot.TurnaroundHold : WaitSlot.Unattributed, null);
                    TimeSpan? until = holding ? RemainingHold() : null;
                    if (gatherEnds is { } gather && (until is null || gather < until))
                    {
                        until = gather;
                    }

                    await WaitForChangeAsync(wakes, until, cancellation).ConfigureAwait(false);
                    continue;
                }

                // Another service is running the channel - an ARDOP ARQ session, whose timing an
                // AX.25 frame landing mid-turnaround would break. Everything that shares the
                // channel waits here, IN THE QUEUE the host put it in, rather than each frame
                // waiting on a poll of its own before it is queued. See InhibitHolds.
                if (InhibitHolds(source))
                {
                    // Booked to everybody with something queued, because the hold is the
                    // station's business rather than one link's: it stops every transmitter that
                    // shares the channel, which is all of them bar the holder's own.
                    EnterWait(WaitSlot.TransmitInhibit, null);
                    source = null;
                    await Delay(InhibitPollInterval, cancellation).ConfigureAwait(false);
                }
            }

            // Classic p-persistence (AX.25 §6.4): when the channel is clear, roll p; on
            // failure wait one slot and try again; while busy, keep waiting slots.
            //
            // A transmission that owns the channel's timing skips all of it. ARDOP runs its own
            // channel discipline against ARQ turnaround budgets, and the busy it would be
            // deferring to is partly its own signal - at a shifted centre it sits inside a
            // packet modem's passband and asserts that modem's busy detector.
            // When this transmitter started finding the channel busy, for a lease that bounds how
            // long its holder waits (TransmitLease.MaxCarrierWait).
            long? busySince = null;
            while (!(PeekFrom(source) is { OwnsTiming: true }))
            {
                // A burst that owns the channel's timing may have been queued while we were
                // waiting out carrier sense for this one. It is not allowed to wait on a roll,
                // and this wait has no bound - a busy frequency can hold a packet frame here for
                // tens of seconds - so hand it the channel now. The frame we were waiting for
                // keeps its place and is picked up next time round.
                if (OwnsTimingSource() is { } urgent)
                {
                    source = urgent;
                    break;
                }

                // Asked ABOUT THIS TRANSMITTER, not about the station. A modem defers to its own
                // detector and to any modem sharing its passband, and not to one 1.3 kHz away
                // that it could not collide with - see ChannelBusyFor, which has what the
                // station-wide answer cost GB7RDG.
                if (ChannelBusyFor(source, out int asserted))
                {
                    // The lease holder's transmissions may be told to stop waiting: a slot booked
                    // for a time goes out after its limit rather than not at all. Nobody else's,
                    // and nobody's without a lease that asks for it.
                    if (TransmitLease.CarrierWaitFor(source) is { } limit)
                    {
                        busySince ??= _time.GetTimestamp();
                        TimeSpan waited = _time.GetElapsedTime(busySince.Value);
                        if (waited >= limit)
                        {
                            if (TransmitLease.HolderNow() is int holder)
                            {
                                TransmitLease.NoteCarrierWaitCutShort(holder, waited);
                            }

                            break;
                        }
                    }

                    EnterWait(WaitSlot.ChannelBusy, source, asserted);
                    await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
                    continue;
                }

                if (_random.Next(256) <= Csma.Persistence)
                {
                    break;
                }

                EnterWait(WaitSlot.Backoff, source);
                await Delay(Csma.SlotTimeMilliseconds, cancellation).ConfigureAwait(false);
            }

            OptimizeTransmitQueue(source);

            // Withdrawn while we waited for the channel. Nothing is left to send for this
            // source, so there is nothing to key for: a transmission taken back must not leave
            // the radio keying up on an empty burst, which is the whole point of being able to
            // take one back. Nothing else removes a queued item, so on every other path this is
            // the item NextEligibleSource just found and the test never fires.
            if (PeekFrom(source) is not { } next)
            {
                continue;
            }

            // A packing modem's burst, rendered before the keyup and sent as one of its own.
            if (IsPackedHead(next))
            {
                if (await TakePackedBurstAsync(source).ConfigureAwait(false) is { Items.Length: > 0 } packed)
                {
                    // The last word before the radio keys: a lease taken while this burst waited
                    // for the channel or rendered refuses it here, unkeyed.
                    if (!RefuseIfLeasedOut(source, packed.Items))
                    {
                        RunPackedKeyup(source, packed, output, ptt);
                    }
                }

                continue;
            }

            // The last word before the radio keys: a lease taken while this transmitter waited for
            // the channel refuses everything it has queued here, and nothing keys.
            if (RefuseIfLeasedOut(source))
            {
                continue;
            }

            _transmitting = true;

            // Keyed up, which is a fact about the station rather than about one link: every frame
            // still queued, on this transmitter or any other, is now waiting on our own airtime.
            // Set before the PTT, so the key itself counts as ours rather than as channel access.
            EnterWait(WaitSlot.OurTransmission, null);
            TransmittingChanged?.Invoke(true);
            bool keyed = false;
            // Declared out here because the finally that starts the turnaround hold needs them:
            // the hold has to be set while receive is still gated, so that its window starts at
            // the unkey rather than wherever this loop next gets scheduled.
            object? keyupSource = null;
            TimeSpan? quietAfter = null;
            try
            {
                try
                {
                    ptt.Key();
                    keyed = true;
                }
                catch (Exception keyFailure) when (keyFailure is not OperationCanceledException)
                {
                    // Fault everything queued rather than leaving enqueuers to time out one by
                    // one: the answer is definite, the frames are lost, and the loop survives
                    // to try the next keyup. FlexTxContendedException lands here by design -
                    // "another station holds the PA" is an outcome, not a broken radio.
                    FaultAfterKeyFailure(source, keyFailure);
                    PttFailed?.Invoke(keyFailure);
                    continue;
                }

                // keyupSource stays null until something has actually gone out, so a keyup whose
                // first frame the modem refuses is still free to be taken by whatever follows.
                TxItem? inFlight = null;
                try
                {
                    // ONE TRANSMITTER PER KEYUP, which per-source queues now make structural:
                    // this loop can only ever see one transmitter's traffic. Draining the whole
                    // channel put several transmitters' frames under one PTT - on a station with modems at
                    // 850 Hz and 2150 Hz, one burst starting on one and finishing on the other,
                    // the waterfall picture that looks like a frame torn in half and is really
                    // two whole frames sharing a keyup.
                    //
                    // What it cost was hearing. Receive is gated for the length of a keyup, so an
                    // appended frame kept us transmitting for another 0.7 to 3.4 s at 300 Bd,
                    // straight through the window the answer to the frame we just sent arrives
                    // in. GB7RDG-2's frame log measured it, and the classification was exact
                    // rather than a heuristic: a shared keyup's inter-frame gap IS the appended
                    // burst rendered with the token preamble below (0.667 s for that station's
                    // 15 B RR), while a separate keyup's is the same burst with a full TXDELAY,
                    // plus the tail, plus the p-persistence wait (0.887 + 0.020 + more). Of the
                    // 83 frames sent with ANOTHER MODEM's frame appended behind them, 2 were
                    // answered within 6 s - 2.4 %, against 35.5 % of the 14,634 that ended their
                    // keyup. Being second cost a little copy too, a strong adjacent burst ending
                    // as yours begins being worth a decibel or two at the knee (bpsk300 behind
                    // the AFSK ident, 24 trials: 10 of 24 at -4 dB AWGN against 22 of 24 in its
                    // own keyup). The token preamble below was the obvious suspect and was
                    // measured to be innocent - it scored the same as a full one on every row -
                    // but its premise, that the far end is already locked to this waveform, is
                    // still only true while the modem stays the same.
                    while (TakeFrom(source) is { } item)
                    {
                        inFlight = item;
                        Finish(item);
                        // Subsequent frames in one keyup need only a token preamble.
                        int txDelay = keyupSource is null ? Csma.TxDelayMilliseconds : 30;
                        // How long the channel held this frame, measured here: the wait ends when
                        // the transmitter picks the frame up, not when the audio finishes. Taken
                        // before Modulate so that rendering - which can be milliseconds of DSP for
                        // a long burst - is not counted as time spent waiting for the channel.
                        TimeSpan heldFor = _time.GetElapsedTime(item.QueuedAt);
                        float[] samples;
                        try
                        {
                            samples = item.Modulate(txDelay);
                        }
                        catch (ArgumentException rejection)
                        {
                            // A frame the modem refuses (oversize for the mode, empty) is
                            // dropped - it must not kill the transmitter loop. The enqueuer's
                            // task faults so ACKMODE hosts see the loss.
                            Refuse(item, rejection);
                            inFlight = null;
                            continue;
                        }

                        keyupSource = item.Source;
                        // Announced only once the frame is certain to go out - past the modem's
                        // chance to refuse it, and immediately before the write. A frame that was
                        // dropped never waited for the channel in any sense the operator cares
                        // about, and reporting a wait for it would put a held time on a row that
                        // never existed.
                        NoteHeldFor(item, heldFor);
                        // The hold belongs to the LAST thing actually sent, so a keyup that ends
                        // with a frame nobody will answer does not keep the others waiting.
                        quietAfter = item.QuietAfter;
                        // Told before the write, not after it. A real device's Write blocks until its
                        // buffer has room, so a burst longer than the buffer does not return from it
                        // until most of the burst has already played - and a display told afterwards
                        // spends the whole transmission painting silence and then paints the burst
                        // over again, taking twice as long with the first half black. Measured on the
                        // air and reproduced: 92 black lines ahead of 97 lines of signal.
                        // What this costs the transmitter is one scale-and-copy of the burst before
                        // the audio goes out, which is bounded, allocation-only and does not wait on
                        // anything - the rule that the transmitter must never wait on a picture still
                        // holds.
                        //
                        // For a stoppable item the announcement happens inside WriteStoppably, one
                        // block at a time, immediately before that block is written - not here, once,
                        // for the whole rendered array. A stop cuts the write short; announcing the
                        // full array up front announced audio that never reached the air, which held
                        // the receive tap (and the public monitor uplink, gated on the same pending
                        // count) closed for however much of the nominal burst was left unplayed - a
                        // stop 2 s into a 30 s test left 28 s of a tone nobody was sending painted on
                        // the waterfall with nothing behind it.
                        WriteStoppably(
                            output, samples, item.StopEarly, item.Written,
                            announce: mem => TransmittedAudio?.Invoke(mem));

                        // Not drained here. A drain waits for the card to play everything it
                        // holds, stops the stream and re-arms it, and ALSA pads the last period
                        // with silence on the way; the next frame is then modulated, written and
                        // the stream restarted only once all of that has happened. Done per frame,
                        // that put 30 to 35 ms of carrier-up silence between every frame of a keyup
                        // and the next (raw capture on radio2, 2026-09-19 17:36 UTC) - a hole in a
                        // waveform the token preamble presumes is continuous, and airtime spent on
                        // nothing. Written back to back the frames are one contiguous sample stream
                        // on the card, and the one drain a keyup needs is the one after the tail,
                        // which is what releasing PTT waits on.
                        //
                        // So Done means handed to the card, not played out: the frame is at most one
                        // card buffer (120 ms on ALSA) short of having left the air, and this keyup
                        // still holds the channel behind it. ACKMODE's answer and FrameTransmitted
                        // follow it and document the same. Modulating the next frame now overlaps
                        // that last buffer's playout; if it ever took longer the card would underrun,
                        // which PcmTransfer recovers (prepare, then retry from the first frame not
                        // yet written) rather than failing the keyup - a gap where the underrun was,
                        // never a lost frame.
                        item.Done.TrySetResult(true);
                        inFlight = null;
                    }

                    if (Csma.TxTailMilliseconds > 0)
                    {
                        // The tail is silence, but it is time we held the channel - a display that
                        // skips it under-reports how long the keyup actually was.
                        var tail = new float[SampleRate * Csma.TxTailMilliseconds / 1000];
                        TransmittedAudio?.Invoke(tail);
                        output.Write(tail);
                    }

                    output.Drain();
                }
                catch (Exception deviceFailure) when (deviceFailure is not OperationCanceledException)
                {
                    // The output device failed mid-keyup (an unplugged USB card, a dead ALSA
                    // handle). Everything queued gets a definite answer first - the in-flight
                    // frame's enqueuer would otherwise wait forever on a Done nobody will ever
                    // complete - and then the fault propagates: the task's owner decides what a
                    // station without a transmitter does, rather than this loop quietly dying
                    // and leaving a healthy-looking receive-only station.
                    if (inFlight is { } dying)
                    {
                        Refuse(dying, deviceFailure);
                    }

                    FaultEverything(deviceFailure);
                    throw;
                }
                catch (OperationCanceledException cut)
                {
                    // A cancellation in the middle of a frame: this loop's own, or an output
                    // whose stream was torn down under the write (Write takes no token, so the
                    // exception says nothing about whose cancellation it is). The loop ends on it
                    // exactly as it always has, and what is still queued stays queued for a
                    // transmitter started again. But the frame that was going out gets an
                    // answer: it never finished, so its caller must not wait for ever, and
                    // neither must every frame sent after it, whose announcement waits for this
                    // one's (SendAndAnnounceAsync).
                    if (inFlight is { } dying)
                    {
                        Refuse(dying, cut);
                    }

                    throw;
                }
            }
            finally
            {
                EndKeyup(ptt, keyed, keyupSource, quietAfter);
            }
        }
    }

    /// <summary>
    /// Ends a keyup: unkeys, clears the demodulators of our own transmission while receive is
    /// still gated, starts any turnaround hold and hands the channel back.
    /// </summary>
    private void EndKeyup(IPttControl ptt, bool keyed, object? keyupSource, TimeSpan? quietAfter)
    {
        // Best-effort: an unkey that throws (the radio's session died mid-burst) must
        // not mask the burst's own result or kill the loop - and after a failed keyup
        // there is nothing to unkey. The arbitrated Flex PTT additionally suppresses
        // unkey for a keyup it did not win, so this cannot cut a peer's burst.
        if (keyed)
        {
            try
            {
                ptt.Unkey();
            }
            catch (Exception unkeyFailure) when (unkeyFailure is not OperationCanceledException)
            {
                PttFailed?.Invoke(unkeyFailure);
            }
        }

        // Receive is still gated: sweep the demodulators clean of our own transmission
        // BEFORE handing the channel back. The other order re-opened receive first, so
        // the audio thread could re-enter modem.Process concurrently with this loop's
        // ResetCarrierState - torn filter and DCD state exactly when the first reply
        // after our transmission arrives. (IModem documents ResetCarrierState as a call
        // for while the channel transmits.) TransmittingChanged's own subscribers that
        // reset receive taps - the id-beacon ghosts - get the same still-gated
        // guarantee, which is why the event too fires before the gate opens.
        foreach (IModem modem in _modems.Values)
        {
            modem.ResetCarrierState();
        }

        // Set before receive reopens, so the window starts at the unkey rather than
        // wherever this loop next gets scheduled.
        SetHold(keyed ? keyupSource : null, keyed ? quietAfter : null);

        TransmittingChanged?.Invoke(false);
        _transmitting = false;
        EnterWait(WaitSlot.Unattributed, null);
    }

    /// <summary>Whether a packed render has ended since <paramref name="seen"/> was read: one that
    /// was started and finished while the loop was deciding to wait, and is ready to go.</summary>
    private bool RenderEndedSince(long seen)
    {
        lock (_txGate)
        {
            return _rendersDone != seen;
        }
    }

    /// <summary>Whether a packed burst is rendering, whose end will wake the loop.</summary>
    private bool PackedRenderPending()
    {
        lock (_txGate)
        {
            foreach (PackedRender render in _packedRenders.Values)
            {
                if (!render.Result.IsCompleted)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether a turnaround hold is keeping somebody quiet right now.</summary>
    private bool HoldActive()
    {
        lock (_txGate)
        {
            return _quietOwner is not null
                && _time.GetElapsedTime(_quietOwnerSince) < MaxTurnaroundHold
                && _time.GetElapsedTime(_quietFrom) < _quietWindow;
        }
    }

    /// <summary>
    /// Waits until something may have changed: <paramref name="until"/> passes, or anything is
    /// queued or finishes rendering after <paramref name="seen"/> was read. With no deadline it
    /// waits for the change alone.
    /// </summary>
    private async Task WaitForChangeAsync(long seen, TimeSpan? until, CancellationToken cancellation)
    {
        Task changed;
        lock (_txGate)
        {
            if (_wakes != seen)
            {
                return;
            }

            // Replaced under the same lock an enqueue takes, as WaitForWorkAsync does, so a wake
            // either happened before this (and the count says so) or completes this instance.
            _txSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed = _txSignal.Task;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task deadline = until is { } wait
            ? Task.Delay(wait < MinimumSchedulerWait ? MinimumSchedulerWait : wait, _time, stop.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        await Task.WhenAny(changed, deadline).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
    }

    private Task Delay(int milliseconds, CancellationToken cancellation) =>
        Delay(TimeSpan.FromMilliseconds(milliseconds), cancellation);

    private Task Delay(TimeSpan wait, CancellationToken cancellation) =>
        Task.Delay(wait, _time, cancellation);

    /// <summary>
    /// How often a stoppable write checks in. Short enough that a stop reads as immediate against
    /// a burst that can run to a minute; the same order of magnitude the waterfall already uses
    /// for a block of received audio.
    /// </summary>
    private const int StopCheckMilliseconds = 40;

    /// <summary>
    /// Writes one item's audio, in blocks it can be asked to stop between rather than as a single
    /// call - for the one transmitter that can change its mind mid-keyup, the operator's test
    /// tone (see <c>TxTestRunner</c> in the daemon, which is not visible from here, hence the
    /// delegates rather than a concrete type).
    /// </summary>
    /// <remarks>
    /// <para>Every other caller passes <paramref name="stopEarly"/> null and gets exactly the one
    /// <see cref="IAudioOutput.Write"/> call it always did, announced whole and up front exactly
    /// as before. A test transmission renders its whole burst up front too - a modem modulates
    /// frames and there is no frame here either - so the only thing this changes for it is how
    /// that already-rendered burst reaches the device: a stop can only take effect between
    /// blocks, which is why the burst is walked in blocks with a check between each rather than
    /// handed to the device in one call nothing can interrupt.
    /// </para>
    /// <para><b>Announced one block at a time, not as one call for the whole array.</b>
    /// <paramref name="announce"/> is what feeds the waterfall's own transmit pacer (and, through
    /// it, the receive tap and the public monitor uplink - both gated shut for as long as
    /// announced-but-not-yet-played audio is outstanding). Announcing the whole rendered array up
    /// front, as a single call would, tells the pacer about audio a stop is about to prevent from
    /// ever reaching the device - which is exactly backwards for a caller whose whole reason for
    /// existing is that the audio might not all go out. Announcing each block immediately before
    /// it is written keeps the display, the receive gate and the device in step regardless of
    /// where a stop lands.</para>
    /// <para><b>Nothing is drained between these blocks.</b> That is what the class remarks on
    /// <c>TxTestRunner</c> warn against - draining stops and re-primes a real card's PCM, and a
    /// burst with a hole in it every few hundred milliseconds is a poor instrument. These blocks
    /// share one continuous write with no drain until the caller's own drain afterwards, so the
    /// stream is never stopped mid-burst; only <see cref="IAudioOutput.Write"/> is called more
    /// than once, which every multi-frame keyup already does today, once per queued frame.</para>
    /// </remarks>
    private static void WriteStoppably(
        IAudioOutput output, float[] samples, Func<bool>? stopEarly, Action<int>? written,
        Action<ReadOnlyMemory<float>> announce)
    {
        if (stopEarly is null)
        {
            announce(samples);
            output.Write(samples);
            written?.Invoke(samples.Length);
            return;
        }

        int block = Math.Max(1, output.SampleRate * StopCheckMilliseconds / 1000);
        int offset = 0;
        while (offset < samples.Length)
        {
            int n = Math.Min(block, samples.Length - offset);
            if (stopEarly())
            {
                // Faded rather than cut dead, for the same reason every other edge in this
                // station's audio is shaped: a hard-keyed edge splatters. This is the last block
                // that goes out, so there is nothing after it to pick the envelope back up from.
                FadeToSilence(samples.AsSpan(offset, n));
                announce(samples.AsMemory(offset, n));
                output.Write(samples.AsSpan(offset, n));
                offset += n;
                break;
            }

            announce(samples.AsMemory(offset, n));
            output.Write(samples.AsSpan(offset, n));
            offset += n;
        }

        written?.Invoke(offset);
    }

    /// <summary>Raised-cosine fade from full amplitude at the first sample to silence at the
    /// last, over whatever span it is given - the same shape
    /// <see cref="Packet.SoundModem.Audio.TestTone"/> uses for its own edges, applied here to a
    /// block that is ending the burst rather than one it rendered that way itself.</summary>
    private static void FadeToSilence(Span<float> block)
    {
        int last = block.Length - 1;
        if (last <= 0)
        {
            block.Clear();
            return;
        }

        for (int k = 0; k <= last; k++)
        {
            block[k] *= (float)(0.5 * (1.0 + Math.Cos(Math.PI * k / last)));
        }
    }
}
