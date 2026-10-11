using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// One station's receive path: an audio input, the channel its samples feed, the watches that
/// decide the feed has died, and the loop that turns between them.
/// </summary>
/// <remarks>
/// <para>Carved out of the daemon's top-level statements so that there can be more than one of
/// it in a process. Everything here used to be loop-scope locals in <c>Program.cs</c>, and the
/// only thing that changed on the way out is where the process-level decisions are taken: a
/// station now says <i>this station is down and here is the sentence</i> through
/// <see cref="Faulted"/>, and the host decides what that means. For the single-station daemon
/// the host journals the sentence and exits 1, which is what the station used to do itself; for
/// a host running fifty of them, one wedged receiver must not take the other forty-nine down.
/// There is deliberately no <c>Environment.Exit</c> and no process exit anywhere in this
/// class.</para>
/// <para><b>One implementation, no drift.</b> Every device kind - ALSA, Flex, UberSDR, pipe,
/// wav-loop - runs this loop and these watches. A second copy for a second flavour would be
/// invisible until the two disagreed about a dead feed at 3 a.m.</para>
/// <para>The journal lines are the station's own words through <see cref="StationJournal"/>,
/// which prefixes a tag only when it has one. A single station has none, so its journal reads
/// exactly as it did before this class existed.</para>
/// </remarks>
internal sealed class Station : IDisposable
{
    /// <summary>How often the health probes are polled, from the receive loop.</summary>
    private static readonly TimeSpan HealthPollPeriod = TimeSpan.FromSeconds(10);

    private readonly StationOptions _options;
    private readonly StationJournal _journal;
    private readonly SoundModemChannel _channel;
    private readonly IAudioInput _input;
    private readonly TimeProvider _time;

    private readonly int _inputRate;
    private readonly Decimator? _decimator;
    private readonly float[] _inputBuffer;
    private readonly float[] _dspBuffer;

    private readonly DeadFeedWatch? _deadFeedWatch;
    private readonly StarvationWatch? _starvationWatch;
    private readonly ITimer? _starvationTimer;
    private readonly XrunWatch _xrunWatch = new();
    private readonly string _silenceMessage;
    private readonly string _starvationMessage;

    /// <summary>Cancelled by the host's token or by this station's own fault, whichever comes
    /// first. The plan's "its own CancellationTokenSource": a station that stops does not have
    /// to stop the process to do it. Linked in the constructor rather than when the loop starts,
    /// so a station built now and started later is stoppable in between - fifty of them
    /// constructed before their threads run would otherwise poll a starvation watch against a
    /// token nothing could cancel.</summary>
    private readonly CancellationTokenSource _stopping;

    // Whether the station itself is keyed, for the silence watch. A keyed Flex is not receiving
    // and its DAX stream delivers exact zeros, which is byte-for-byte what a dead feed looks
    // like - so without this a station that transmits enough restarts itself. It did: the FreeDV
    // campaign pacing frames every 8 s took the GB7RDG node off the air on 2026-08-15. Two flags
    // because the receive loop samples them per block: the live state, and a sticky "there was a
    // keyup since you last looked" for transmissions shorter than one read.
    private int _keyedNow;
    private int _keyedSinceRead;

    /// <summary>Kept so <see cref="Dispose"/> can take it off the channel again. The channel has
    /// no RemoveReceiveTap, but its events do unsubscribe, and a station that outlived its
    /// subscription would go on writing flags nobody reads.</summary>
    private readonly Action<bool> _keyedHandler;

    // Whether the loop is inside _input.Read right now. A Read that blocks for ever (the ALSA
    // stall family) is the one failure cancellation cannot reach, and this flag is how the grace
    // timer tells that apart from a loop that stopped tidily.
    private int _insideRead;

    // The grace timer, and the gate that stops one being armed after the station has closed.
    // Armed by a starvation fault and taken down again when the loop returns or the station is
    // disposed: a station that got out of its loop is not stalled, however it got out.
    private readonly Lock _graceGate = new();
    private ITimer? _starvationGrace;
    private bool _closed;

    /// <summary>Builds the station's watches and arms them. The loop turns in <see cref="Run"/>.</summary>
    /// <param name="options">What this station is made of.</param>
    /// <param name="stopping">The host's token. Cancelling it stops this station; the station can
    /// also stop itself, and from the loop's side the two are the same event.</param>
    public Station(StationOptions options, CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.BlockMilliseconds, 0);
        _options = options;
        _journal = options.Journal;
        _channel = options.Channel;
        _input = options.Input;
        _time = options.TimeProvider;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        // Decimate the source to the DSP rate. When it already runs at the DSP rate (a 48 kHz
        // mode's full-bandwidth DAX, --capture-rate 12000, or a 12 kHz virtual card) there is
        // nothing to decimate - a Decimator with factor 1 is invalid, so feed samples straight
        // through.
        _inputRate = _input.SampleRate;
        _decimator = _inputRate == options.DspRate
            ? null
            : new Decimator(_inputRate, _inputRate / options.DspRate);

        // 100 ms RX blocks for the packet modes; 20 ms when ARDOP runs - its ARQ timing budgets
        // (IRS ACK inside the ISS repeat window) want RX latency low.
        int blockSamples = _inputRate * options.BlockMilliseconds / 1000;
        _inputBuffer = new float[blockSamples];
        _dspBuffer = new float[_decimator?.MaxOutput(blockSamples) ?? blockSamples];

        (double silenceSeconds, double starvationSeconds) =
            DeadFeedConfig.Resolve(options.DeadFeed, options.DeviceKind);
        _deadFeedWatch = silenceSeconds > 0 ? new DeadFeedWatch(_inputRate, silenceSeconds) : null;
        _silenceMessage = SilenceMessage(options.DeviceKind, silenceSeconds);
        _starvationMessage = StarvationMessage(options.DeviceKind, starvationSeconds);

        _keyedHandler = keyed =>
        {
            Volatile.Write(ref _keyedNow, keyed ? 1 : 0);
            if (keyed)
            {
                Interlocked.Exchange(ref _keyedSinceRead, 1);
            }
        };
        _channel.TransmittingChanged += _keyedHandler;

        StarvationWatch? starvation = starvationSeconds > 0
            ? new StarvationWatch(_time, starvationSeconds)
            : null;
        _starvationWatch = starvation;
        _starvationTimer = starvation is null ? null : _time.CreateTimer(
            _ => PollStarvation(starvation),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// This station is down, and this is the sentence to show for it. Raised at most once per
    /// fault; a stalled shutdown raises a second one, because it needs a different answer.
    /// </summary>
    public event Action<StationFault>? Faulted;

    /// <summary>The tag this station's journal lines carry; empty when it is the only one.</summary>
    public string Tag => _journal.Tag;

    /// <summary>The channel this station's samples feed.</summary>
    public SoundModemChannel Channel => _channel;

    /// <summary>The audio input this station reads.</summary>
    public IAudioInput Input => _input;

    /// <summary>
    /// Turns the receive loop until the host's token is cancelled, the station faults, or the
    /// input dies. Synchronous and blocking, because every input's <c>Read</c> is: a host
    /// running more than one station gives each its own thread.
    /// </summary>
    public void Run()
    {
        try
        {
            RunLoop();
        }
        finally
        {
            // Out of the loop, however it got out - so this station is not stalled, and a grace
            // timer left armed to say that it is would end the host's process for nothing.
            CloseGrace();
        }
    }

    private void RunLoop()
    {
        long lastHealthPoll = _time.GetTimestamp();
        while (!_stopping.IsCancellationRequested)
        {
            int got;
            try
            {
                Volatile.Write(ref _insideRead, 1);
                got = _input.Read(_inputBuffer);
            }
            catch (InvalidOperationException deviceDeath)
            {
                // The ALSA death-with-an-errno family (USB card unplugged: snd_pcm_readi -ENODEV,
                // beyond snd_pcm_recover). Without this catch it escaped as a raw stack trace with
                // an abort exit code; the recovery is the same restart contract as every other
                // dead feed, so say what happened in one line and take it.
                Fault(
                    $"receive feed dead: the input device failed ({deviceDeath.Message}) - "
                    + "restarting to reopen it");
                break;
            }
            finally
            {
                // Cleared whichever way Read left: what the grace timer reads to tell a wedged
                // Read from a loop that got out.
                Volatile.Write(ref _insideRead, 0);
            }

            if (got == 0)
            {
                // Never a busy spin: every input that can return 0 has already waited inside Read
                // (100 ms ubersdr, 200 ms flex; ALSA and wav-loop never return 0) - see the
                // dead-feed notes on StationOptions.DeviceKind.
                continue;
            }

            _starvationWatch?.NoteDelivery();

            // Sticky rather than instantaneous: a whole transmission can start and finish inside one
            // read block, and reading the live flag afterwards would see an idle station and count our
            // own keyed silence as a dead feed. Taking-and-clearing means any keyup since the last block
            // re-arms the watch exactly once.
            bool keyedThisBlock = Interlocked.Exchange(ref _keyedSinceRead, 0) == 1
                || Volatile.Read(ref _keyedNow) == 1;

            if (_deadFeedWatch is not null
                && _deadFeedWatch.Observe(_inputBuffer.AsSpan(0, got), keyedThisBlock))
            {
                // Silence the station itself asked for is not evidence of anything. The excuse is
                // the host's to give, because only it knows what its device has been told to do;
                // said once rather than every block, because Observe latches after firing and
                // re-arms only on live audio.
                if (_options.SilenceExcuse?.Invoke() is string excuse)
                {
                    _journal.WriteError(excuse);
                    continue;
                }

                // Restart-to-recover: both real feed deaths were fixed by a process restart and
                // nothing less is proven, so take the orderly shutdown and let the unit rebuild
                // the device session from scratch. If this line repeats every threshold the feed
                // is not coming back by itself - the message says where to look.
                Fault(_silenceMessage);
                break;
            }

            // Polled from the receive loop rather than a timer: this loop only turns when audio is
            // flowing, which is exactly when an xrun means something, and it costs a few field
            // reads. The dropped-write counters ride the same poll - they were dead counters
            // before it: a full disk left a station keeping an empty frame log for weeks with
            // nothing anywhere saying so.
            if (_time.GetElapsedTime(lastHealthPoll) >= HealthPollPeriod)
            {
                lastHealthPoll = _time.GetTimestamp();
                if (_options.XrunCounters?.Invoke() is (int captureXruns, int playbackXruns)
                    && _xrunWatch.Poll(captureXruns, playbackXruns) is string lostAudio)
                {
                    _journal.WriteError(lostAudio);
                }

                foreach (Func<string?> check in _options.HealthChecks)
                {
                    if (check() is string lost)
                    {
                        _journal.WriteError(lost);
                    }
                }
            }

            // The card's own samples, before the decimator, for anything that has to judge them
            // on the scale the converter actually works on. Not while we are keyed: nothing the
            // card hears during our own transmission is a measurement of the band, and the
            // channel drops those blocks for the same reason.
            if (!keyedThisBlock)
            {
                _options.CardRateTap?.Invoke(_inputBuffer.AsSpan(0, got));
            }

            if (_decimator is null)
            {
                _channel.ProcessReceive(_inputBuffer.AsSpan(0, got));
            }
            else
            {
                int produced = _decimator.Process(_inputBuffer.AsSpan(0, got), _dspBuffer);
                _channel.ProcessReceive(_dspBuffer.AsSpan(0, produced));
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channel.TransmittingChanged -= _keyedHandler;
        _starvationTimer?.Dispose();
        CloseGrace();
        _stopping.Dispose();
    }

    /// <summary>What a dead feed looks like on each device family, in the operator's words.</summary>
    private static string SilenceMessage(DeadFeedDevice device, double silenceSeconds) => device switch
    {
        DeadFeedDevice.Flex =>
            $"receive feed dead: {silenceSeconds:F0} s of unbroken digital silence from the radio "
            + "- restarting to rebuild the session (recurring? check DAX/slice config - a "
            + "deliberately muted DAX stream restart-loops this way)",
        DeadFeedDevice.UberSdr =>
            $"receive feed dead: {silenceSeconds:F0} s of unbroken digital silence from the "
            + "receiver's IQ stream - its SDR feed has likely died - restarting to reconnect "
            + "afresh",
        DeadFeedDevice.OpenWebRx =>
            $"receive feed dead: {silenceSeconds:F0} s of unbroken digital silence from the "
            + "receiver's audio stream - its SDR feed has likely died - restarting to reconnect "
            + "afresh",
        _ =>
            $"receive feed dead: {silenceSeconds:F0} s of unbroken digital silence from the sound "
            + "device - restarting (\"deadFeed\".\"silenceSeconds\" asked for this watch; a "
            + "genuinely silent input restart-loops this way - set it 0 to turn the watch off)",
    };

    /// <summary>And what a starved one looks like: the feed stopped delivering at all.</summary>
    private static string StarvationMessage(DeadFeedDevice device, double starvationSeconds)
        => device switch
        {
            DeadFeedDevice.Flex =>
                $"receive feed starved: no samples from the radio for {starvationSeconds:F0} s "
                + "- the DAX stream has stopped while the session looks alive - restarting to "
                + "rebuild it",
            DeadFeedDevice.UberSdr or DeadFeedDevice.OpenWebRx =>
                $"receive feed starved: an open session delivered no audio for "
                + $"{starvationSeconds:F0} s - a hung stream - restarting to reconnect afresh",
            DeadFeedDevice.Uplink =>
                $"receive feed starved: this site asked a connected station for audio and got "
                + $"none for {starvationSeconds:F0} s - a half-open socket - dropping it so the "
                + "station reconnects",
            _ =>
                $"receive feed starved: the sound device returned no samples for "
                + $"{starvationSeconds:F0} s - a stalled or unplugged card - restarting to "
                + "reopen it",
        };

    /// <summary>
    /// The starvation watch, polled from a timer OUTSIDE the receive loop - which is what lets a
    /// <c>Read</c> that blocks forever still be seen.
    /// </summary>
    private void PollStarvation(StarvationWatch watch)
    {
        if (_stopping.IsCancellationRequested)
        {
            return; // already shutting down; firing now would relabel a clean stop as a fault
        }

        if (_options.SessionLive is not null && !_options.SessionLive())
        {
            // Quiet between UberSDR sessions is the reconnect policy pacing itself, and quiet
            // with nobody watching an on-demand receiver is the whole idea; see SessionLive for
            // why restarting through either would hammer a public receiver.
            watch.NoteExpectedQuiet();
            return;
        }

        if (!watch.IsStarved())
        {
            return;
        }

        Fault(_starvationMessage);
        ArmGrace();
    }

    /// <summary>
    /// Starts the clock on the orderly shutdown a starvation fault has just asked for.
    /// </summary>
    /// <remarks>
    /// If the receive loop is stuck inside a blocked <c>Read</c> (the ALSA stall family) that
    /// shutdown can never run: the loop only re-checks cancellation when <c>Read</c> returns, and
    /// nothing short of ending the process recovers it. So wait the grace period and then look:
    /// still inside <c>Read</c> is a stalled station and is said so; anything else got out under
    /// its own steam and is simply down. Ending the process is the host's decision either way and
    /// never this station's, which is why both answers are just an event.
    /// </remarks>
    private void ArmGrace()
    {
        lock (_graceGate)
        {
            if (_closed)
            {
                return;
            }

            _starvationGrace = _time.CreateTimer(
                _ =>
                {
                    if (Volatile.Read(ref _insideRead) == 0)
                    {
                        return;   // the loop got out; this station is down, not wedged
                    }

                    Faulted?.Invoke(new StationFault(
                        "receive feed starved: the orderly shutdown stalled (the input's Read is "
                        + "blocked) - exiting hard so the service restarts",
                        Stalled: true));
                },
                null, _options.StalledShutdownGrace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>No more grace timers, and down with any already armed. Called when the loop
    /// returns and again on dispose, so one cannot outlive the loop it was watching.</summary>
    private void CloseGrace()
    {
        ITimer? grace;
        lock (_graceGate)
        {
            _closed = true;
            grace = _starvationGrace;
            _starvationGrace = null;
        }

        grace?.Dispose();
    }

    /// <summary>Says the station is down and stops its loop. The host decides what it costs.</summary>
    private void Fault(string reason)
    {
        Faulted?.Invoke(new StationFault(reason, Stalled: false));
        _stopping.Cancel();
    }
}

/// <summary>
/// Why a station stopped, and whether it managed to stop tidily.
/// </summary>
/// <param name="Reason">The sentence to journal, and to show wherever this station's state is
/// displayed. Written in the operator's language, not the code's.</param>
/// <param name="Stalled">The receive loop could not be brought down: it is wedged inside a
/// blocked <c>Read</c> and no amount of cancellation will reach it. Nothing short of ending the
/// process recovers a station in this state, which is why it is told apart from an ordinary
/// fault rather than folded into one.</param>
internal readonly record struct StationFault(string Reason, bool Stalled);

/// <summary>
/// Where a station's lines go, and the tag they carry.
/// </summary>
/// <remarks>
/// One station in a process writes no tag at all, so its journal reads byte for byte as it did
/// before there was a <see cref="Station"/> type - which is what the live node stations, and the
/// tests that pin their output, depend on. A host running several prefixes each station's slug,
/// so fifty of them in one journal are readable: <c>m9psy-1: ubersdr: live, 2 viewers: ...</c>.
/// </remarks>
/// <param name="Tag">The station's slug, or empty for the only station in the process.</param>
/// <param name="Out">Where an ordinary line goes; <c>Console.WriteLine</c> in the daemon.</param>
/// <param name="Error">Where a line that reports a problem goes; <c>Console.Error.WriteLine</c>.
/// Kept apart because journald grades the two differently and an operator greps on it.</param>
internal sealed record StationJournal(string Tag, Action<string> Out, Action<string> Error)
{
    /// <summary>The console the daemon writes to, tagged or not.</summary>
    public static StationJournal Console(string tag = "")
        => new(tag, System.Console.WriteLine, System.Console.Error.WriteLine);

    /// <summary>Writes one ordinary line, tagged if this station has a tag.</summary>
    public void Write(string line) => Out(Prefixed(line));

    /// <summary>Writes one problem line, tagged if this station has a tag.</summary>
    public void WriteError(string line) => Error(Prefixed(line));

    /// <summary>
    /// The problem sink as a bare delegate, for the library's <c>Action&lt;string&gt; log</c>
    /// parameters - the UberSDR input's phase lines come out this way, so they carry the tag
    /// too.
    /// </summary>
    public Action<string> ErrorSink => WriteError;

    private string Prefixed(string line) => Tag.Length == 0 ? line : $"{Tag}: {line}";
}

/// <summary>Everything a <see cref="Station"/> needs to run one receive path. A record so that a
/// host building fifty of them can state the shared half once and vary the rest with
/// <c>with</c>.</summary>
internal sealed record StationOptions
{
    /// <summary>The channel this station's samples feed.</summary>
    public required SoundModemChannel Channel { get; init; }

    /// <summary>The audio input to read. Not disposed here: whoever opened it closes it.</summary>
    public required IAudioInput Input { get; init; }

    /// <summary>The channel's DSP rate, which the input is decimated to.</summary>
    public required int DspRate { get; init; }

    /// <summary>Where this station's lines go, and the tag they carry.</summary>
    public required StationJournal Journal { get; init; }

    /// <summary>
    /// Which family the input belongs to. It decides the dead-feed defaults and the wording of
    /// the two failure messages, because each family has its own way of dying:
    /// <list type="bullet">
    /// <item><description><b>flex</b> - a dead VITA stream keeps DELIVERING (the radio pads exact
    /// zeros at full rate; measured 2026-08-07: 6.8 h of zeros recorded), so the silence watch is
    /// the one that sees it. If instead the DAX UDP path breaks while the TCP session stays up,
    /// <c>FlexAudioInput.Read</c> waits 200 ms for packets and returns 0 - paced, not a spin -
    /// and only the starvation watch can see that. A dead TCP session raises
    /// <c>Client.Disconnected</c>, which is the host's to handle.</description></item>
    /// <item><description><b>ubersdr</b> - <c>Read</c> waits 100 ms and returns 0 when the ring is
    /// empty. A hung established WebSocket (half-open TCP; .NET sends pings but by default never
    /// times out missing pongs) starves the ring while the pump sits in <c>ReceiveAsync</c>
    /// believing the session is live: starvation's case. An instance whose SDR feed dies but keeps
    /// streaming delivers exact-zero IQ, which demodulates to exact-zero audio: silence's case.
    /// Deliberate quiet - reconnect backoff, quota refusals, an on-demand receiver nobody is
    /// watching - is declared by <see cref="SessionLive"/> and postpones starvation; a receiver
    /// unreachable past five minutes is the input's own <c>Lost</c> event, which the host
    /// handles, so no death is reported two ways.</description></item>
    /// <item><description><b>openwebrx</b> - the same as ubersdr, on the receiver's demodulated
    /// audio rather than its IQ. A session counts as live only once it has delivered audio and
    /// while the receiver's band reaches the dial; before the first audio the input times a
    /// silent session out itself.</description></item>
    /// <item><description><b>alsa</b> - <c>AlsaPcm.Read</c> BLOCKS until the span fills and never
    /// returns 0, which is why starvation is polled from a timer and not from the loop. A card
    /// that dies outright (USB unplug: -ENODEV) makes <c>Read</c> throw. Silence is off by
    /// deliberate default: genuinely-silent wired inputs exist.</description></item>
    /// <item><description><b>wavloop</b> - paces itself and always returns a full buffer; it can
    /// neither starve nor die, and a silent recording looping is legitimate. Both off.</description></item>
    /// </list>
    /// <para>No 0-return above is a busy spin - each has already waited inside <c>Read</c> - so
    /// the loop needs no backoff of its own.</para>
    /// </summary>
    public required DeadFeedDevice DeviceKind { get; init; }

    /// <summary>The configured thresholds; null takes the device family's defaults, 0 in either
    /// field turns that watch off.</summary>
    public DeadFeedConfig? DeadFeed { get; init; }

    /// <summary>
    /// Whether the input has a session to be starved of, for inputs whose quiet can be
    /// deliberate. Null for every device whose quiet never is.
    /// </summary>
    public Func<bool>? SessionLive { get; init; }

    /// <summary>
    /// Asked when the silence watch fires: a sentence means the silence was asked for and the
    /// station keeps running with that line in the journal, null means it is a dead feed. Null
    /// delegate for every device that has no such state.
    /// </summary>
    public Func<string?>? SilenceExcuse { get; init; }

    /// <summary>
    /// The sound device's capture and playback xrun counters, read on the same poll. Null for
    /// every device that has none: an xrun is an ALSA concept, and it is the difference between
    /// "the band is quiet" and "this machine will not schedule us".
    /// </summary>
    public Func<(int Capture, int Playback)>? XrunCounters { get; init; }

    /// <summary>
    /// Polled every ten seconds from the receive loop, in order, after the xrun counters; each
    /// returns a line to journal or null. What rides here is anything that only matters while
    /// audio is flowing and would otherwise be a dead counter: unwritten frame-log rows,
    /// unwritten survey captures.
    /// </summary>
    public IReadOnlyList<Func<string?>> HealthChecks { get; init; } = [];

    /// <summary>How much audio one <c>Read</c> asks for. 100 ms for the packet modes; 20 ms when
    /// ARDOP runs, whose ARQ timing budgets want RX latency low.</summary>
    public int BlockMilliseconds { get; init; } = 100;

    /// <summary>
    /// Each block of audio exactly as the device delivered it, at the card's own rate and
    /// <b>before</b> the decimator. Null (the default) is a station that has nobody asking.
    /// </summary>
    /// <remarks>
    /// <para>The channel's own <c>AddReceiveTap</c> is downstream of the 48 to 12 kHz decimating
    /// FIR, which is fine for anything measuring a level and wrong for anything measuring the
    /// converter's range: the filter's ripple moves peaks either way, so full scale downstream is
    /// not full scale at the card. Two things need it: the waterfall's level meter, for its clip
    /// indicator (<c>WaterfallWebServer.MeterInputClipping</c>), and the channel, for the clip
    /// flag each decoded frame carries (<c>SoundModemChannel.NoteCardClipping</c>). The daemon
    /// composes them into this one tap, and the two want different stations: the meter has read
    /// whatever the station's input delivers since v0.59.0, while the frame's flag is only wired
    /// where the input really is a sound card, because a frame on a Flex or an ubersdr feed
    /// cannot honestly say anything about a converter this station does not have.</para>
    /// <para>Called on the receive loop's own thread, once per block, and skipped for any block
    /// the station was keyed during - so it must return promptly and must not allocate. The span
    /// is the loop's buffer and is not valid after the call returns.</para>
    /// </remarks>
    public ReceiveTap? CardRateTap { get; init; }

    /// <summary>The wall clock the starvation watch and its grace timer run on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long the orderly shutdown a starvation fault asks for is given to happen. If
    /// the loop is still inside <c>Read</c> when it is up, the station reports itself stalled; if
    /// it got out, nothing further is said.</summary>
    public TimeSpan StalledShutdownGrace { get; init; } = TimeSpan.FromSeconds(15);
}
