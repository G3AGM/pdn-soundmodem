using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.UberSdr;

/// <summary>
/// One line an UberSDR device wants journalled, and whether it announces a wait before another
/// attempt at the receiver.
/// </summary>
/// <remarks>
/// <para>The always-on device needs nothing but the sentence: every line it writes is
/// <c>ubersdr: &lt;sentence&gt;</c>. The on-demand wrapper writes the same sentences with the
/// page's viewer count in front of them, and marks the ones that are waits, because a wait
/// entered with nobody watching is worth saying in words rather than leaving to be inferred
/// from a zero (issue #409).</para>
/// <para>The flag travels with the sentence rather than being chosen at the call site that
/// writes it, and both come out of one pure function per line. That is deliberate: which lines
/// are waits is the whole of what the wrapper's <c>retrying for nobody</c> clause means, and a
/// boolean typed at a call site is a boolean no test can hold. Compose here, assert there.</para>
/// <para>The sentence carries no <c>ubersdr:</c> prefix; whoever writes the line decides what
/// goes in front of it.</para>
/// </remarks>
/// <param name="Sentence">The line, without its <c>ubersdr:</c> prefix.</param>
/// <param name="BeforeAWait">The line ends with "and the next attempt is in N seconds".</param>
internal readonly record struct UberSdrLine(string Sentence, bool BeforeAWait);

/// <summary>
/// A live UberSDR instance as an <see cref="IAudioInput"/>: the receiver's IQ stream, SSB
/// demodulated to real audio at the channel's DSP rate, so every modem, the waterfall and the
/// frame log attach to a KiwiSDR-style web receiver exactly as they attach to a sound card.
/// </summary>
/// <remarks>
/// <para><b>Receive only.</b> There is no transmitter at the far end of a WebSocket, and the
/// daemon refuses to pretend otherwise - see the <c>ubersdr:</c> device handling in the daemon.
/// What this buys is a station wherever the antenna is good rather than wherever the operator
/// is, at the cost of never answering anything it hears.</para>
/// <para><b>Why IQ and not the instance's own audio.</b> UberSDR will demodulate SSB for us, but
/// then its filter, its AGC and its resampler are all in the path and none of them is ours to
/// choose. Taking <c>iq48</c> instead puts the whole ±24 kHz of complex baseband here, so the
/// receive filter is the one the band plan asked for and the AGC is nobody's - which is what
/// makes SNR figures off this path mean the same thing as SNR figures off a sound card. The
/// instrument audit behind that (linear channel, no AGC on the IQ path, 12 dB of headroom, GPSDO
/// locked) is in <c>docs/dev/archive/ms110d/evidence/2026-07-24-ota-c0/</c>.</para>
/// <para><b>Sessions end.</b> Public instances cap a session at <c>max_session_time</c> (3 hours
/// on the ones measured), and a modem is expected to run for months. The receive loop therefore
/// treats a closed socket as ordinary and reconnects, discarding the
/// <see cref="UberSdrTuning.StartupGuardMs"/> level ramp each time; only a receiver that stays
/// unreachable for <see cref="ReconnectGiveUpAfter"/> raises <see cref="Lost"/>.</para>
/// <para>Protocol per <c>docs/dev/archive/ms110d/ota-capture-client-plan.md</c>; the framing is decoded by
/// <see cref="PcmBinaryDecoder"/>, which is a port from the upstream Go client.</para>
/// </remarks>
public sealed class UberSdrAudioInput : IUberSdrSession
{
    private const string UserAgent = "pdn-soundmodem (ubersdr: receive device)";

    /// <summary>Reconnection attempts stop being hopeful and start being a fault after this
    /// long, at which point <see cref="Lost"/> fires so the service can restart rather than sit
    /// on a dead stream.</summary>
    public static readonly TimeSpan ReconnectGiveUpAfter = TimeSpan.FromMinutes(5);

    // One client for every instance this process talks to, with a connection lifetime rather
    // than the default infinite one. The default pins a DNS answer for as long as the process
    // runs, which did not matter while the host came out of a config file and the process was
    // restarted to change it; it matters when hosts come out of a directory that can move them,
    // because a tunnel host that moves leaves a process that has been up for a week dialling an
    // address nobody is listening on. Five minutes is the usual figure for this: long enough
    // that the pool still does its job, short enough that a move is picked up by itself.
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly UberSdrEndpoint _endpoint;
    private readonly UberSdrTuning _tuning;
    private readonly Action<UberSdrLine>? _journal;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly int _iqRate;

    private int _head;
    private int _tail;
    private int _count;
    private long _dropped;
    private long _droppedReported;
    private long _resumed;
    private long _lossAtRead;
    private int _sessionsHeard; // pump thread only
    private long _published;
    private bool _ended;
    private bool _sessionLive;
    private Task? _pump;
    private ClientWebSocket? _first;
    private readonly TimeProvider _time;

    private UberSdrAudioInput(
        UberSdrEndpoint endpoint,
        UberSdrTuning tuning,
        int iqRate,
        ConnectionResponse connection,
        string? receiverDescription,
        Action<UberSdrLine>? journal,
        TimeProvider time)
    {
        _endpoint = endpoint;
        _tuning = tuning;
        _iqRate = iqRate;
        _journal = journal;
        _time = time;
        Connection = connection;
        ReceiverDescription = receiverDescription;

        // Eight seconds of slack. The daemon drains in 100 ms blocks, so this is not there to be
        // used - it is there so a GC pause or a busy box costs latency rather than samples.
        _ring = new float[tuning.OutputRate * 8];
    }

    /// <summary>Raised once when the receiver has been unreachable for
    /// <see cref="ReconnectGiveUpAfter"/>, with a sentence saying so. The daemon stops on this,
    /// so the service restarts and tries the instance afresh.</summary>
    public event Action<string>? Lost;

    /// <summary>The instance this is streaming from.</summary>
    public UberSdrEndpoint Endpoint => _endpoint;

    /// <summary>The pre-flight reply - session limits and the IQ modes on offer.</summary>
    public ConnectionResponse Connection { get; }

    /// <summary>A one-line summary of the receiver from <c>/api/description</c> (callsign,
    /// site, antenna, frequency reference), or null when it would not say.</summary>
    public string? ReceiverDescription { get; }

    /// <inheritdoc />
    public int SampleRate => _tuning.OutputRate;

    /// <summary>Samples dropped because nothing drained the buffer in time. Non-zero means the
    /// box cannot keep up, and is reported rather than hidden.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>
    /// A running count that moves whenever the audio this input hands out has a hole in it:
    /// every sample dropped because the buffer filled, plus one for each session after the
    /// first that delivers audio (a reconnect, or the ordinary renewal at the receiver's
    /// session limit, leaves the time in between unheard). The daemon reads it once per block
    /// and marks the block that follows a move as following lost audio. A <see cref="Read"/>
    /// that returns nothing is not a hole: the ring holds what has arrived, and audio that is
    /// late is still delivered whole.
    /// </summary>
    /// <remarks>The count as of the last <see cref="Read"/> that delivered audio, taken under
    /// the ring's lock with the samples it handed out. Read straight off the live counters, a
    /// session's first audio published between a Read returning and the reader asking would
    /// have put the mark one block early, on audio from before the hole.</remarks>
    public long Discontinuities => Interlocked.Read(ref _lossAtRead);

    /// <summary>
    /// True while an IQ session is open and expected to be delivering audio; false between
    /// sessions, when quiet is deliberate - reconnect backoff, or an instance refusing us
    /// until its daily quota resets. The daemon's starvation watch reads this: a live session
    /// that delivers nothing for the threshold is a hung stream worth a restart, while a
    /// deliberate wait restarted every threshold would re-ask a public receiver with a fresh
    /// session each time - the exact hammering the reconnect policy exists to avoid. The
    /// unreachable-receiver family is reported exactly once, by <see cref="Lost"/> on its own
    /// five-minute clock, never by starvation.
    /// </summary>
    public bool SessionLive => Volatile.Read(ref _sessionLive);

    /// <summary>
    /// Connects to <paramref name="endpoint"/> and starts streaming. The pre-flight and the
    /// first WebSocket connect happen here, so a wrong host, a refused mode or a password the
    /// instance does not accept is an error at start-up rather than a silence later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The receiver refused the connection or the
    /// requested IQ mode; the message is written for an operator to act on.</exception>
    /// <exception cref="UberSdrRefusedException">The receiver refused the first connection with
    /// HTTP 429 (rate limited or out of daily quota): wait and try again later.</exception>
    public static Task<UberSdrAudioInput> OpenAsync(
        UberSdrEndpoint endpoint,
        UberSdrTuning tuning,
        Action<string>? log,
        CancellationToken cancellation,
        TimeProvider? time = null)
        => OpenAsync(endpoint, tuning, Prefixed(log), cancellation, time);

    /// <summary>
    /// The always-on device's journal: every line the receive loop writes, with the device name
    /// in front of it and nothing else.
    /// </summary>
    /// <remarks>
    /// Flavour A's lines are what they have always been. There is no viewer count here and no
    /// wait is ever marked, because a station streaming for months has no audience to count and
    /// nobody to be retrying on behalf of.
    /// </remarks>
    /// <param name="log">Where the lines go, or null to write none.</param>
    internal static Action<UberSdrLine>? Prefixed(Action<string>? log) =>
        log is null ? null : line => log($"ubersdr: {line.Sentence}");

    /// <summary>
    /// Connects to <paramref name="endpoint"/> and starts streaming, writing the loop's lines
    /// to <paramref name="journal"/>. The on-demand wrapper opens sessions this way so that it
    /// can put the page's viewer count on every line the session writes, and mark a backoff
    /// nobody is waiting for (issue #409).
    /// </summary>
    /// <exception cref="InvalidOperationException">The receiver refused the connection or the
    /// requested IQ mode; the message is written for an operator to act on.</exception>
    internal static async Task<UberSdrAudioInput> OpenAsync(
        UberSdrEndpoint endpoint,
        UberSdrTuning tuning,
        Action<UberSdrLine>? journal,
        CancellationToken cancellation,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        int iqRate = IqRateFor(tuning.Mode);
        if (iqRate % tuning.OutputRate != 0)
        {
            throw new InvalidOperationException(
                $"{tuning.Mode} delivers {iqRate} Hz IQ, which is not a whole multiple of this "
                + $"channel's {tuning.OutputRate} Hz DSP rate. Resampling by a fraction is not "
                + "something this path does; the 12 kHz and 48 kHz mode families both divide "
                + "48000, so an iq48 stream serves either.");
        }

        string sessionId = Guid.NewGuid().ToString();
        ConnectionResponse connection = await PreflightAsync(endpoint, sessionId, tuning, cancellation)
            .ConfigureAwait(false);

        // A refusal that only time can lift - the address's daily listening allowance is spent,
        // or the instance is rate-limiting - is not a start-up error. The station comes up (KISS,
        // waterfall, silence) and the receive loop asks again patiently, exactly as it would had
        // the quota run out mid-afternoon instead of at boot. Everything else that is wrong with
        // the reply stays a start-up error, because a config typo retried forever is a silence
        // nobody can explain.
        bool refusedForNow = connection.RefusedForNow;
        RequireAcceptable(endpoint, tuning, connection);

        string? description = Describe(
            await FetchDescriptionAsync(endpoint, cancellation).ConfigureAwait(false));

        var input = new UberSdrAudioInput(
            endpoint, tuning, iqRate, connection, description, journal, time ?? TimeProvider.System);

        try
        {
            if (refusedForNow)
            {
                journal?.Invoke(RefusedAtStartupLine(
                    endpoint,
                    connection.Reason ?? "daily listening allowance exhausted",
                    connection.DailyTimeUsedSecs));
            }
            else
            {
                // Prove the stream opens before returning: the daemon prints its start-up banner
                // off the back of this, and a banner claiming a receiver we never reached is
                // worse than an error.
                input._first = await input.ConnectAsync(sessionId, cancellation).ConfigureAwait(false);
            }

            input._pump = Task.Run(
                () => input.PumpAsync(sessionId, startRefused: refusedForNow), CancellationToken.None);
            return input;
        }
        catch
        {
            // The half-built instance was abandoned here before: nothing faulted visibly (the
            // caller gets the exception either way), but its cancellation source - and any
            // socket a future edit stores before the throw - leaked for the process lifetime.
            input.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>Blocks briefly for samples and returns 0 if none arrive, which is what the
    /// daemon's receive loop expects of a device with nothing to say.</remarks>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            while (_count == 0)
            {
                if (_ended)
                {
                    return 0;
                }

                if (!Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100)))
                {
                    return 0;
                }
            }

            // The count as of the samples about to be handed out: every hole it includes lies
            // before them, so a reader that compares it after this Read marks the right block.
            Interlocked.Exchange(ref _lossAtRead, _dropped + _resumed);
            int take = Math.Min(destination.Length, _count);
            for (int i = 0; i < take; i++)
            {
                destination[i] = _ring[_tail];
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }
            }

            _count -= take;
            return take;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutdown: the pump's own failures have already been logged.
        }

        _first?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>The complex sample rate an IQ mode name promises: <c>iq48</c> → 48000.</summary>
    internal static int IqRateFor(string mode)
    {
        if (mode is not null
            && mode.StartsWith("iq", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(mode.AsSpan(2), out int kHz)
            && kHz > 0)
        {
            return kHz * 1000;
        }

        throw new InvalidOperationException(
            $"'{mode}' is not an UberSDR IQ mode. Use \"iq48\" (every instance) or \"iq96\" "
            + "where the receiver offers it - this device streams complex baseband, not the "
            + "instance's own demodulated audio.");
    }

    /// <summary>The reconnecting receive loop. Runs until disposed.</summary>
    /// <remarks>
    /// Pacing is <see cref="UberSdrReconnectPolicy"/>'s: consecutive failures escalate, one
    /// healthy session resets. Three failure classes, deliberately distinct: transport
    /// failures feed the give-up clock (a service restart can genuinely help there); an
    /// explicit refusal (HTTP 429, spent daily quota) waits on a long ladder and NEVER gives
    /// up, because restarting cannot mint quota and a crash-looping unit re-asks every
    /// RestartSec - the exact hammering this loop exists to avoid; and a session that dies
    /// before delivering real audio escalates too, because "accepts then instantly closes,
    /// forever" met a fixed one-second breath here once, and the result was a public receiver
    /// pelted all night.
    /// </remarks>
    private async Task PumpAsync(string sessionId, bool startRefused)
    {
        CancellationToken cancellation = _stopping.Token;
        ClientWebSocket? socket = Interlocked.Exchange(ref _first, null);
        var policy = new UberSdrReconnectPolicy();
        long? downSince = null;

        if (startRefused && !await WaitAsync(policy.After(UberSdrReconnectOutcome.Refused)))
        {
            return;
        }

        while (!cancellation.IsCancellationRequested)
        {
            if (socket is null)
            {
                try
                {
                    socket = await ConnectAsync(sessionId, cancellation).ConfigureAwait(false);
                    downSince = null;
                    _journal?.Invoke(ReconnectedLine(_endpoint));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (UberSdrRefusedException e)
                {
                    downSince = null; // refused is answered, not unreachable
                    TimeSpan wait = policy.After(UberSdrReconnectOutcome.Refused);
                    _journal?.Invoke(RefusingLine(_endpoint, e.Message, wait));
                    if (!await WaitAsync(wait))
                    {
                        return;
                    }

                    continue;
                }
                catch (Exception e)
                {
                    // The injected clock's monotonic timestamps, not UtcNow: an NTP step during
                    // an outage used to inflate the outage's apparent length and could trip the
                    // give-up restart early (or, stepping back, defer it indefinitely).
                    downSince ??= _time.GetTimestamp();
                    TimeSpan down = _time.GetElapsedTime(downSince.Value);
                    if (down > ReconnectGiveUpAfter)
                    {
                        RaiseLost(
                            $"{_endpoint} has been unreachable for {down.TotalMinutes:F0} minutes "
                            + $"({e.Message}). Stopping so the service restarts and tries again.");
                        return;
                    }

                    TimeSpan wait = policy.After(UberSdrReconnectOutcome.Transient);
                    _journal?.Invoke(UnreachableLine(_endpoint, e.Message, wait));
                    if (!await WaitAsync(wait))
                    {
                        return;
                    }

                    continue;
                }
            }

            long publishedBefore = Interlocked.Read(ref _published);
            long openedAt = _time.GetTimestamp();
            bool reasonLogged = false;
            Volatile.Write(ref _sessionLive, true);
            try
            {
                await ReceiveAsync(socket, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                break;
            }
            catch (Exception e)
            {
                reasonLogged = true;
                _journal?.Invoke(StreamEndedLine(_endpoint, e.Message));
            }
            finally
            {
                Volatile.Write(ref _sessionLive, false);
            }

            TimeSpan lasted = _time.GetElapsedTime(openedAt);
            socket.Dispose();
            socket = null;
            if (cancellation.IsCancellationRequested)
            {
                break;
            }

            // Ordinary: the instance caps a session at max_session_time, so an unremarkable
            // stream ends every few hours and simply has to be picked up again. The line
            // between that and "the instance accepts us and instantly drops us" is drawn in
            // audio actually delivered.
            long delivered = Interlocked.Read(ref _published) - publishedBefore;
            bool healthy = delivered >= 10L * _tuning.OutputRate;
            TimeSpan pause = policy.After(
                healthy ? UberSdrReconnectOutcome.Healthy : UberSdrReconnectOutcome.ShortSession);
            _journal?.Invoke(SessionEndedLine(
                _endpoint, healthy, lasted, delivered, _tuning.OutputRate, reasonLogged, pause));
            if (!await WaitAsync(pause))
            {
                break;
            }
        }

        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }

        // False when the wait was cut short by disposal - the loop's only reason to stop waiting.
        async Task<bool> WaitAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, _time, cancellation).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _ended = true;
                    Monitor.PulseAll(_gate);
                }

                return false;
            }
        }
    }

    // Every line the device journals is composed here, by a pure function that returns the
    // sentence and whether it is a wait. Nothing below chooses that flag where it writes: the
    // on-demand wrapper's "retrying for nobody" clause means exactly "this wait was entered on
    // nobody's behalf", and a flag typed at a call site is one no test can hold (review of #410).

    /// <summary>The socket is open again after an attempt that was not the first.</summary>
    /// <param name="endpoint">The receiver.</param>
    internal static UberSdrLine ReconnectedLine(UberSdrEndpoint endpoint) =>
        new($"reconnected to {endpoint}", BeforeAWait: false);

    /// <summary>The receiver answered the upgrade with "not you, not now" - HTTP 429, or a
    /// daily allowance this address has spent.</summary>
    /// <param name="endpoint">The receiver.</param>
    /// <param name="message">What the refusal said.</param>
    /// <param name="wait">How long before asking again.</param>
    internal static UberSdrLine RefusingLine(
        UberSdrEndpoint endpoint, string message, TimeSpan wait) =>
        new($"{endpoint} is refusing us for now ({message}); "
            + $"asking again in {wait.TotalMinutes:F0} min", BeforeAWait: true);

    /// <summary>The connect failed at the transport.</summary>
    /// <param name="endpoint">The receiver.</param>
    /// <param name="message">What the failure said.</param>
    /// <param name="wait">How long before trying again.</param>
    internal static UberSdrLine UnreachableLine(
        UberSdrEndpoint endpoint, string message, TimeSpan wait) =>
        new($"{endpoint} unreachable ({message}); retrying in {wait.TotalSeconds:F0}s",
            BeforeAWait: true);

    /// <summary>An open session's receive threw, which is how most sessions end.</summary>
    /// <param name="endpoint">The receiver.</param>
    /// <param name="message">What the receive threw.</param>
    internal static UberSdrLine StreamEndedLine(UberSdrEndpoint endpoint, string message) =>
        new($"stream from {endpoint} ended ({message})", BeforeAWait: false);

    /// <summary>The pre-flight at start-up said the allowance is spent, which is a wait rather
    /// than a start-up error: the station comes up and the receive loop asks again.</summary>
    /// <param name="endpoint">The receiver.</param>
    /// <param name="reason">What the pre-flight gave as the reason.</param>
    /// <param name="dailyTimeUsedSecs">Seconds of listening this address has used today.</param>
    internal static UberSdrLine RefusedAtStartupLine(
        UberSdrEndpoint endpoint, string reason, int dailyTimeUsedSecs) =>
        new($"{endpoint} is refusing us for now ({reason}, {dailyTimeUsedSecs} s used today). "
            + "The stream will be retried patiently; audio starts when the receiver lets us "
            + "back in.", BeforeAWait: false);

    /// <summary>Nothing drained the ring in time and audio was lost.</summary>
    /// <param name="dropped">Samples lost since the stream started.</param>
    /// <param name="outputRate">The audio rate they were counted at, Hz.</param>
    internal static UberSdrLine BufferFilledLine(long dropped, int outputRate) =>
        new($"WARNING - dropped {dropped} samples ({dropped / (double)outputRate:F1} s) because "
            + "the receive buffer filled. The machine is not keeping up with the stream.",
            BeforeAWait: false);

    /// <summary>
    /// The line one ended session leaves behind, on its way into the wait for the next attempt.
    /// </summary>
    /// <remarks>
    /// Two figures rather than one, both in milliseconds, because "the session ended after only
    /// 0.0 s of audio" was every line of six hours of churn in issue #409 and could not tell a
    /// receiver that accepts and drops on the spot from one that streams silence for a moment
    /// and then goes: the first number is how long the socket was up, the second how much audio
    /// came down it.
    /// </remarks>
    /// <param name="endpoint">The receiver.</param>
    /// <param name="healthy">The session delivered real audio, so this is the ordinary
    /// max-session-time rollover rather than a receiver dropping us.</param>
    /// <param name="lasted">Wall time from the socket opening to it closing.</param>
    /// <param name="audioSamples">Audio samples delivered during the session.</param>
    /// <param name="outputRate">The audio rate those samples were counted at, Hz.</param>
    /// <param name="reasonAlreadyLogged">The receive threw and <see cref="StreamEndedLine"/>
    /// has already printed what it said, so this line leaves the subject alone. False when the
    /// stream closed with no error at all, where nothing else will say the session is over and
    /// this line says so itself.</param>
    /// <param name="pause">How long before the next attempt.</param>
    internal static UberSdrLine SessionEndedLine(
        UberSdrEndpoint endpoint,
        bool healthy,
        TimeSpan lasted,
        long audioSamples,
        int outputRate,
        bool reasonAlreadyLogged,
        TimeSpan pause)
    {
        if (healthy)
        {
            return new($"reconnecting to {endpoint}", BeforeAWait: true);
        }

        string why = reasonAlreadyLogged ? "" : " (the receiver closed the stream)";
        return new(
            $"the session ended after {lasted.TotalMilliseconds:F0} ms with only "
            + $"{audioSamples * 1000 / outputRate} ms of audio{why}; backing off "
            + $"{pause.TotalSeconds:F0}s before reconnecting to {endpoint}",
            BeforeAWait: true);
    }

    /// <summary>Receives one session's worth of IQ, converting and buffering as it arrives.</summary>
    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        // One converter per session: its filter state belongs to a contiguous stream, and a
        // reconnect is a discontinuity by definition.
        var converter = new StreamingSsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = _iqRate,
            OutputRate = _tuning.OutputRate,
            DialHz = 0, // the receiver is tuned to the dial, so the carrier is already at DC
            Sideband = _tuning.Sideband,
            SsbLowHz = _tuning.SsbLowHz,
            SsbHighHz = _tuning.SsbHighHz,
        });

        using var decoder = new PcmBinaryDecoder { LastSampleRate = _iqRate, LastChannels = 2 };
        var accumulator = new ArrayBufferWriter<byte>(64 * 1024);
        byte[] receive = ArrayPool<byte>.Shared.Rent(64 * 1024);
        short[] iq = [];
        float[] audio = [];
        ulong firstPacketNs = 0;
        ulong guardNs = (ulong)_tuning.StartupGuardMs * 1_000_000UL;
        bool heard = false;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                accumulator.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receive, cancellation).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    accumulator.Write(receive.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue; // text status frames say nothing an IQ stream needs
                }

                PcmPacket packet;
                try
                {
                    packet = decoder.Decode(accumulator.WrittenSpan, isZstd: true);
                }
                catch (InvalidDataException)
                {
                    continue; // one unreadable packet is a glitch, not a reason to drop the stream
                }

                if (packet.GpsTimestampNanos == 0)
                {
                    continue;
                }

                if (firstPacketNs == 0)
                {
                    firstPacketNs = packet.GpsTimestampNanos;
                }

                // The level ramps over the first second of a stream. Feeding that to the modems
                // would put a fade at the head of every session and a stripe on the waterfall.
                if (packet.GpsTimestampNanos - firstPacketNs < guardNs)
                {
                    continue;
                }

                ReadOnlySpan<byte> pcm = packet.Pcm.Span;
                int shorts = pcm.Length / 2;
                if (iq.Length < shorts)
                {
                    iq = new short[shorts];
                    audio = new float[converter.MaxOutputFor(shorts / 2) + 1];
                }

                // PcmBinaryDecoder hands back a little-endian payload (the wire is big-endian);
                // read it as such rather than reinterpreting the bytes, so this does not quietly
                // depend on the host's byte order.
                for (int s = 0; s < shorts; s++)
                {
                    iq[s] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(
                        pcm.Slice(s * 2, 2));
                }

                int produced = converter.Process(iq.AsSpan(0, shorts - (shorts % 2)), audio);

                // The first audio of any session but the first follows the time between them,
                // which nobody heard.
                bool afterHole = false;
                if (!heard && produced > 0)
                {
                    heard = true;
                    afterHole = _sessionsHeard++ > 0;
                }

                Publish(audio.AsSpan(0, produced), afterHole);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receive);
        }
    }

    /// <summary>Copies converted audio into the ring, applying gain and complaining if a lagging
    /// consumer is costing samples.</summary>
    /// <param name="samples">The audio.</param>
    /// <param name="afterHole">The audio is the first of a session after the first, so it
    /// follows a stretch nobody heard (<see cref="Discontinuities"/>).</param>
    internal void Publish(ReadOnlySpan<float> samples, bool afterHole = false)
    {
        // The pump reads this across threads to tell a real session from an instant death.
        Interlocked.Add(ref _published, samples.Length);
        float gain = _tuning.Gain;
        long dropped = 0;
        long total = 0;
        lock (_gate)
        {
            if (afterHole)
            {
                Interlocked.Increment(ref _resumed);
            }

            foreach (float sample in samples)
            {
                if (_count == _ring.Length)
                {
                    // Bounded rather than unbounded: a consumer that has stopped draining is a
                    // fault to be reported, not a reason to grow until the process dies.
                    if (++_tail == _ring.Length)
                    {
                        _tail = 0;
                    }

                    _count--;
                    dropped++;
                }

                _ring[_head] = sample * gain;
                if (++_head == _ring.Length)
                {
                    _head = 0;
                }

                _count++;
            }

            // Counted before the lock goes, so a reader never takes the audio after the hole
            // without the count that says it is there.
            if (dropped > 0)
            {
                total = Interlocked.Add(ref _dropped, dropped);
            }

            Monitor.PulseAll(_gate);
        }

        if (dropped == 0)
        {
            return;
        }

        // Once a box is behind it stays behind for a while; one line per second of loss is
        // enough to say so without burying the journal.
        if (total - Interlocked.Read(ref _droppedReported) >= _tuning.OutputRate)
        {
            Interlocked.Exchange(ref _droppedReported, total);
            _journal?.Invoke(BufferFilledLine(total, _tuning.OutputRate));
        }
    }

    private void RaiseLost(string reason)
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }

        Lost?.Invoke(reason);
    }

    /// <summary>Opens the IQ WebSocket.</summary>
    private async Task<ClientWebSocket> ConnectAsync(string sessionId, CancellationToken cancellation)
    {
        var query = new StringBuilder()
            .Append("frequency=").Append(_tuning.FrequencyHz)
            .Append("&mode=").Append(Uri.EscapeDataString(_tuning.Mode))
            .Append("&user_session_id=").Append(sessionId)
            .Append("&format=pcm-zstd");
        if (!string.IsNullOrEmpty(_tuning.Password))
        {
            query.Append("&password=").Append(Uri.EscapeDataString(_tuning.Password));
        }

        var uri = new Uri($"{_endpoint.WebSocketScheme}://{_endpoint.Host}:{_endpoint.Port}/ws?{query}");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        // Keep the upgrade's HTTP status so a 429 - the receiver rate-limiting or out of daily
        // quota - is distinguishable from a transport failure; the two deserve very different
        // retry cadences.
        socket.Options.CollectHttpResponseDetails = true;
        if (!string.IsNullOrEmpty(_tuning.Password))
        {
            socket.Options.SetRequestHeader("X-Password", _tuning.Password);
        }

        try
        {
            await socket.ConnectAsync(uri, cancellation).ConfigureAwait(false);
        }
        catch (WebSocketException e) when (socket.HttpStatusCode == HttpStatusCode.TooManyRequests)
        {
            socket.Dispose();
            throw new UberSdrRefusedException("HTTP 429 - rate limited or out of daily quota", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    /// <summary>
    /// The pre-flight verdict as a start-up error: a refusal that time will not lift, or an IQ
    /// mode the instance does not offer this client, each with the sentence an operator needs.
    /// A refusal that only time can lift (<see cref="ConnectionResponse.RefusedForNow"/>)
    /// passes, because the receive loop knows how to wait for that one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The reply says this configuration will never
    /// stream from this instance as it stands.</exception>
    internal static void RequireAcceptable(
        UberSdrEndpoint endpoint, UberSdrTuning tuning, ConnectionResponse connection)
    {
        if (connection.RefusedForNow)
        {
            return;
        }

        if (!connection.Allowed)
        {
            throw new InvalidOperationException(
                $"{endpoint} refused the connection: {connection.Reason ?? "no reason given"}");
        }

        if (connection.AllowedIqModes is { Count: > 0 } modes
            && !connection.Bypassed
            && !modes.Contains(tuning.Mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{endpoint} does not offer IQ mode '{tuning.Mode}' to this client - it allows "
                + $"{string.Join(", ", modes)}. Set \"ubersdr\": {{ \"mode\": \"{modes[0]}\" }}, or "
                + "ask the receiver's operator for access.");
        }
    }

    /// <summary>
    /// <c>POST /connection</c>: whether we may stream at all, and whether the mode we want is on
    /// offer. Asked before the WebSocket so a refusal arrives as a sentence rather than as a
    /// socket that closes for no stated reason. Refusals come back as the reply itself
    /// (<see cref="ConnectionResponse.Allowed"/> false) - the caller decides which refusals are
    /// errors and which are "wait"; only an unreachable or non-UberSDR endpoint throws.
    /// </summary>
    internal static async Task<ConnectionResponse> PreflightAsync(
        UberSdrEndpoint endpoint, string sessionId, UberSdrTuning tuning, CancellationToken cancellation)
    {
        var body = new JsonObject { ["user_session_id"] = sessionId };
        if (!string.IsNullOrEmpty(tuning.Password))
        {
            body["password"] = tuning.Password;
        }

        ConnectionResponse? reply;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.HttpBase}/connection")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellation)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // The instance would not even discuss it. Same class as a spent daily quota:
                // nothing on our side is wrong, only time helps - report it as such rather
                // than as unreachability.
                return new ConnectionResponse
                {
                    Allowed = false,
                    Reason = "HTTP 429 - rate limited",
                    DailyTimeRemainingSecs = 0,
                };
            }

            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation)
                .ConfigureAwait(false);
            reply = await JsonSerializer.DeserializeAsync<ConnectionResponse>(
                stream, cancellationToken: cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new InvalidOperationException(
                $"cannot reach the UberSDR instance at {endpoint.HttpBase}: {e.Message}", e);
        }

        if (reply is null)
        {
            throw new InvalidOperationException(
                $"{endpoint.HttpBase}/connection returned nothing - is that an UberSDR instance?");
        }

        return reply;
    }

    internal static async Task<string?> FetchDescriptionAsync(
        UberSdrEndpoint endpoint, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.HttpBase}/api/description");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellation)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null; // who the receiver is is nice to know, not needed to receive
        }
    }

    /// <summary>Boils <c>/api/description</c> down to the line worth printing: who is listening,
    /// where, and whether their frequency reference is honest.</summary>
    internal static string? Describe(string? descriptionJson)
    {
        if (string.IsNullOrWhiteSpace(descriptionJson))
        {
            return null;
        }

        // Every field here is the receiver operator's to fill in as they please, so nothing about
        // its shape is guaranteed. A description that will not parse costs a line of the start-up
        // banner, not the receiver.
        try
        {
            if (JsonNode.Parse(descriptionJson) is not JsonObject document)
            {
                return null;
            }

            var parts = new List<string>();
            if (document["receiver"] is JsonObject receiver)
            {
                foreach (string field in (string[])["callsign", "name", "location"])
                {
                    if (receiver[field]?.GetValue<string>() is { Length: > 0 } value)
                    {
                        parts.Add(value);
                    }
                }
            }

            if (document["frequency_reference"] is JsonObject reference
                && reference["enabled"]?.GetValue<bool>() == true)
            {
                double offset = reference["frequency_offset"]?.GetValue<double>() ?? 0;
                parts.Add($"reference offset {offset:F0} Hz");
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
