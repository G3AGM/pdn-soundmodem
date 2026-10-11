using System.Net.Sockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.RtlTcp;

/// <summary>Settings for an <see cref="RtlTcpIqSource"/>.</summary>
public sealed record RtlTcpSourceOptions
{
    /// <summary>
    /// True for a server that takes no orders: OpenWebRX's rtl_tcp-compatible port. Nothing is
    /// ever sent to it, and the centre and the rate are what this record says they are, which is
    /// what its operator pinned the profile to.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>The centre frequency, Hz: asked for on a controlled server, taken on trust on a
    /// read-only one.</summary>
    public required double CentreFrequencyHz { get; init; }

    /// <summary>The sample rate, complex samples a second: asked for, or taken on trust.</summary>
    public required int SampleRate { get; init; }

    /// <summary>The tuner gain to ask for, dB, or null for the tuner's own automatic gain.
    /// Controlled only.</summary>
    public double? TunerGainDb { get; init; }

    /// <summary>The frequency correction to ask for, parts per million. Controlled only.</summary>
    public int FrequencyCorrectionPpm { get; init; }

    /// <summary>Whether to turn on the RTL2832's own digital AGC. Off by default: a level that
    /// moves under the demodulator is not wanted. Controlled only.</summary>
    public bool RtlAgc { get; init; }

    /// <summary>How long a connect, and the header after it, may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a connected server may send nothing before the feed is taken as dead, dropped
    /// and reconnected, the time it was gone reported as lost samples. A dongle at any rate sends
    /// hundreds of kilobytes a second, so a few seconds of nothing is a hung server or a dead
    /// link, not a quiet band.
    /// </summary>
    public TimeSpan NoDataTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long reconnecting may go on failing before the source gives up, raises
    /// <see cref="RtlTcpIqSource.Lost"/> and closes.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The first wait between reconnect attempts; it doubles to
    /// <see cref="ReconnectDelayCap"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan ReconnectDelayCap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much of the stream to throw away after this client retunes or sets the rate, on top
    /// of what was already on its way: the dongle's settling and the server's own buffering,
    /// whose samples are at a tuning nobody can tell. Counted as lost.
    /// </summary>
    public TimeSpan RetuneSettle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Whether to watch a read-only server for a profile switch: its rate against the wall clock
    /// (<see cref="IqRateWatch"/>, which drops the stream while it is wrong) and its character
    /// (<see cref="IqStepWatch"/>, which only says so). On by default; a controlled server is
    /// watched for its rate too, which catches a server that ignored the rate asked for.
    /// </summary>
    public bool WatchProfile { get; init; } = true;

    /// <summary>The rate watch's window.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Where the source's lines go (connects, drops, profile warnings), or null.</summary>
    public Action<string>? Journal { get; init; }

    /// <summary>The clock for waits, rate limits and the rate watch.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// An <see cref="IIqSource"/> over the rtl_tcp protocol: an RTL-SDR dongle behind
/// <c>rtl_tcp</c>, any server that speaks it, or OpenWebRX's read-only rtl_tcp-compatible port.
/// </summary>
/// <remarks>
/// <para><b>Reading.</b> <see cref="Read"/> runs on the front end's reader thread and does the
/// socket reads itself: one receive per call, its unsigned bytes turned to floats by table. A
/// dropped or silent connection is reconnected inside <see cref="Read"/>, which blocks while it
/// does; the time it was gone is added to <see cref="SamplesLost"/> on the first block after, so
/// the front end marks a gap rather than splicing. <see cref="Connected"/> says which, for the
/// station's starvation watch.</para>
/// <para><b>Controlled.</b> Opening sends the rate, the centre, the frequency correction, the gain
/// mode and gain, and the AGC mode, and sends them again on every reconnect, because the server
/// may have restarted with its own defaults. <see cref="Tune"/> retunes; since the protocol does
/// not say where in the stream a command took effect, what was already in flight and
/// <see cref="RtlTcpSourceOptions.RetuneSettle"/> after it are thrown away and counted as lost,
/// and the next block is the first at the new tuning (<see cref="TuningGeneration"/> moves).</para>
/// <para><b>Read-only.</b> Nothing is ever sent. The centre and rate are taken from settings. A
/// profile switch at a different rate is caught by <see cref="IqRateWatch"/>: the stream is
/// dropped (counted as lost) while it is wrong, <see cref="SessionLive"/> goes false so the
/// station waits rather than restarts, and the first block once the rate is right again is a
/// fresh start. A switch at the same rate can only be guessed at (<see cref="IqStepWatch"/>) and
/// is said in the journal, nothing more.</para>
/// <para><b>Cost.</b> Buffers are sized on the first read; a steady stream allocates nothing.
/// Reconnecting allocates, which is not steady state.</para>
/// <para>Original, from the protocol description (see <see cref="RtlTcpProtocol"/>).</para>
/// </remarks>
public sealed class RtlTcpIqSource : IIqSource, IDisposable
{
    private static readonly TimeSpan FailureLineInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepLineInterval = TimeSpan.FromMinutes(1);

    private readonly RtlTcpEndpoint _endpoint;
    private readonly RtlTcpSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _closing = new();
    private readonly object _sendGate = new();
    private readonly byte[] _commands = new byte[6 * RtlTcpProtocol.CommandLength];
    private readonly IqRateWatch? _rateWatch;
    private IqStepWatch? _stepWatch;

    private Socket? _socket;
    private volatile bool _disposed;
    private volatile bool _connected;
    private int _lostRaised;
    private byte[] _bytes = [];
    private bool _haveCarry;
    private byte _carry;

    // What the station has asked for (controlled), under _sendGate; and whether a change is
    // waiting for the reader to take it up.
    private double _wantedCentreHz;
    private int _wantedRate;
    private bool _retunePending;
    private long _retuneDiscardBytes;

    // The reader's own state.
    private long _discardBytes;
    private bool _discardCounts;
    private bool _applyAfterDiscard;
    private double _applyCentreHz;
    private int _applyRate;
    private long _pendingLost;
    private bool _pendingGeneration;
    private bool _everDelivered;
    private DateTimeOffset _goneSince;
    private DateTimeOffset _nextStepLine = DateTimeOffset.MinValue;

    // Read from other threads; 64-bit, so Interlocked to keep a 32-bit Pi from tearing them.
    private long _generation;
    private long _samplesLost;
    private long _reconnects;
    private long _profileDrops;
    private long _bytesReceived;

    /// <summary>Makes a source for the server at <paramref name="endpoint"/>; nothing is opened
    /// until <see cref="OpenAsync"/>.</summary>
    public RtlTcpIqSource(RtlTcpEndpoint endpoint, RtlTcpSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SampleRate, 1);
        if (!double.IsFinite(options.CentreFrequencyHz) || options.CentreFrequencyHz < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CentreFrequencyHz, "the centre frequency must be a frequency");
        }

        if (!options.ReadOnly)
        {
            CheckControllable(options.CentreFrequencyHz, options.SampleRate);
        }

        _endpoint = endpoint;
        _options = options;
        _time = options.TimeProvider;
        CentreFrequencyHz = options.CentreFrequencyHz;
        SampleRate = options.SampleRate;
        _wantedCentreHz = options.CentreFrequencyHz;
        _wantedRate = options.SampleRate;
        if (options.WatchProfile)
        {
            _rateWatch = new IqRateWatch(options.SampleRate, options.RateWindow);
            if (options.ReadOnly)
            {
                _stepWatch = new IqStepWatch(options.SampleRate);
            }
        }
    }

    /// <summary>The server.</summary>
    public RtlTcpEndpoint Endpoint => _endpoint;

    /// <summary>Whether nothing is ever sent to the server.</summary>
    public bool ReadOnly => _options.ReadOnly;

    /// <inheritdoc />
    public int SampleRate { get; private set; }

    /// <inheritdoc />
    public double CentreFrequencyHz { get; private set; }

    /// <inheritdoc />
    public long TuningGeneration => Interlocked.Read(ref _generation);

    /// <inheritdoc />
    public long SamplesLost => Interlocked.Read(ref _samplesLost);

    /// <summary>What the server said about its dongle at the last connect, or null when it sent
    /// no header (or has not been connected to).</summary>
    public RtlTcpHeader? Header { get; private set; }

    /// <summary>Whether a connection is up.</summary>
    public bool Connected => _connected;

    /// <summary>
    /// Whether the station should expect samples: connected, and (as far as can be told) on the
    /// profile it was told. False while reconnecting or while the rate is wrong, so the station
    /// waits rather than restarting over a quiet it already knows the reason for.
    /// </summary>
    public bool SessionLive => _connected && !(_rateWatch?.Off ?? false);

    /// <summary>Reconnects since opening.</summary>
    public long Reconnects => Interlocked.Read(ref _reconnects);

    /// <summary>Bytes of IQ received since opening, kept or not.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Times the stream was dropped for arriving at the wrong rate.</summary>
    public long ProfileDrops => Interlocked.Read(ref _profileDrops);

    /// <summary>Times the stream changed character as a profile switch would (a hint only).</summary>
    public long SuspectedProfileChanges => _stepWatch?.Steps ?? 0;

    /// <summary>The centre the station last asked for: where the source is, or is going.</summary>
    public double TargetCentreHz
    {
        get
        {
            lock (_sendGate)
            {
                return _wantedCentreHz;
            }
        }
    }

    /// <summary>
    /// Raised once, from the reader thread, when the server has been unreachable for
    /// <see cref="RtlTcpSourceOptions.GiveUpAfter"/>; <see cref="Read"/> returns 0 from then on.
    /// </summary>
    public event Action<string>? Lost;

    /// <summary>
    /// Connects, reads the header and (controlled) sends the settings. Throws when the server
    /// cannot be reached, so start-up can say so; once open, a later drop is reconnected.
    /// </summary>
    /// <exception cref="IOException">The connection failed or the server closed it at once.</exception>
    /// <exception cref="SocketException">The server could not be reached.</exception>
    public async Task OpenAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Socket socket = await ConnectAsync(cancellation).ConfigureAwait(false);
        lock (_sendGate)
        {
            _socket = socket;
            _connected = true;
        }

        DateTimeOffset now = _time.GetUtcNow();
        _rateWatch?.Restart(now);
        _goneSince = now;
        if (!_options.ReadOnly)
        {
            // What the server was streaming before the settings landed is at its own defaults.
            _discardBytes = SettleBytes(_options.SampleRate);
            _discardCounts = false;
        }
    }

    /// <summary>
    /// Retunes a controlled server: the centre, and the rate if one is given. Sent at once from
    /// the caller's thread; the reader takes it up, throwing away what is in flight, and the
    /// first block after is the first at the new tuning.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server is read-only.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A centre or rate a dongle cannot take.</exception>
    public void Tune(double centreHz, int? sampleRate = null)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"the rtl_tcp server at {_endpoint} is read-only; it is tuned by its own operator");
        }

        int rate = sampleRate ?? TargetRate();
        CheckControllable(centreHz, rate);
        lock (_sendGate)
        {
            bool rateMoved = rate != _wantedRate;
            _wantedCentreHz = centreHz;
            _wantedRate = rate;
            long inFlight = 0;
            if (_socket is { } socket)
            {
                try
                {
                    int length = 0;
                    if (rateMoved)
                    {
                        RtlTcpProtocol.Encode(_commands.AsSpan(length), RtlTcpCommand.SetSampleRate, (uint)rate);
                        length += RtlTcpProtocol.CommandLength;
                    }

                    RtlTcpProtocol.Encode(_commands.AsSpan(length), RtlTcpCommand.SetFrequency, (uint)Math.Round(centreHz));
                    length += RtlTcpProtocol.CommandLength;
                    socket.Send(_commands.AsSpan(0, length));
                    inFlight = socket.Available;
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    // The reader sees the drop and reconnects, sending the wanted tuning then.
                }
            }

            _retunePending = true;
            _retuneDiscardBytes = inFlight + SettleBytes(Math.Max(rate, SampleRate));
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> interleaved)
    {
        if (interleaved.Length < 2)
        {
            throw new ArgumentException("room for at least one I,Q pair is needed", nameof(interleaved));
        }

        if (_bytes.Length < interleaved.Length)
        {
            _bytes = new byte[interleaved.Length];
        }

        while (!_disposed)
        {
            Socket? socket = _socket;
            if (socket is null || !_connected)
            {
                if (!Reconnect())
                {
                    return 0;
                }

                continue;
            }

            TakeUpRetune();

            int want = interleaved.Length & ~1;
            int offset = 0;
            if (_haveCarry)
            {
                _bytes[0] = _carry;
                offset = 1;
                _haveCarry = false;
            }

            int got;
            try
            {
                got = socket.Receive(_bytes.AsSpan(offset, want - offset));
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                Drop($"the server sent nothing for {_options.NoDataTimeout.TotalSeconds:F0} s");
                continue;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
            {
                if (_disposed)
                {
                    return 0;
                }

                Drop($"the connection failed ({e.Message})");
                continue;
            }

            if (got == 0)
            {
                Drop("the server closed the connection");
                continue;
            }

            int total = offset + got;
            int even = total & ~1;
            if (even != total)
            {
                _carry = _bytes[even];
                _haveCarry = true;
            }

            Span<byte> bytes = _bytes.AsSpan(0, even);
            DateTimeOffset now = _time.GetUtcNow();
            Interlocked.Add(ref _bytesReceived, got);
            _goneSince = now;
            if (_rateWatch is not null)
            {
                bool behind = Behind(socket);
                switch (_rateWatch.Observe(even / 2, now, behind))
                {
                    case IqRateVerdict.WentOff:
                        Interlocked.Increment(ref _profileDrops);
                        Say($"rtltcp: {_endpoint} is sending about {_rateWatch.LastMeasuredRate:F0} samples a "
                            + $"second, not the {_rateWatch.ExpectedRate} it should. "
                            + (_options.ReadOnly
                                ? "Its profile has probably been switched. "
                                : "It may not have taken the rate asked for. ")
                            + "Its samples are being dropped until the rate is right again, rather than "
                            + "decoded as if they were the band this station is set for.");
                        break;
                    case IqRateVerdict.CameBack:
                        Say($"rtltcp: {_endpoint} is sending at {_rateWatch.ExpectedRate} samples a second again; "
                            + "listening again from a fresh start");
                        _pendingGeneration = true;
                        _stepWatch?.Restart();
                        break;
                }

                if (_rateWatch.Off)
                {
                    _pendingLost += even / 2;
                    continue;
                }
            }

            if (_discardBytes > 0)
            {
                int drop = (int)Math.Min(_discardBytes, even);
                _discardBytes -= drop;
                if (_discardCounts)
                {
                    _pendingLost += drop / 2;
                }

                bytes = bytes[drop..];
                if (_discardBytes == 0 && _applyAfterDiscard)
                {
                    ApplyTuning();
                }

                if (bytes.IsEmpty)
                {
                    continue;
                }
            }

            int floats = bytes.Length;
            Span<float> output = interleaved[..floats];
            RtlTcpProtocol.Convert(bytes, output);
            if (_stepWatch is not null && _stepWatch.Feed(output))
            {
                ReportStep(now);
            }

            Commit();
            return floats;
        }

        return 0;
    }

    /// <summary>Closes the connection and stops any reconnecting; a blocked <see cref="Read"/>
    /// returns 0.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing.Cancel();
        lock (_sendGate)
        {
            _connected = false;
            _socket?.Dispose();
            _socket = null;
        }
    }

    /// <summary>Publishes what the block about to be returned carries: a gap before it, a new
    /// tuning.</summary>
    private void Commit()
    {
        if (_pendingLost > 0)
        {
            if (_everDelivered)
            {
                Interlocked.Add(ref _samplesLost, _pendingLost);
            }

            _pendingLost = 0;
        }

        if (_pendingGeneration)
        {
            if (_everDelivered)
            {
                Interlocked.Increment(ref _generation);
            }

            _pendingGeneration = false;
        }

        _everDelivered = true;
    }

    private void TakeUpRetune()
    {
        lock (_sendGate)
        {
            if (!_retunePending)
            {
                return;
            }

            _retunePending = false;
            _discardBytes += _retuneDiscardBytes;
            _discardCounts = true;
            _applyAfterDiscard = true;
            _applyCentreHz = _wantedCentreHz;
            _applyRate = _wantedRate;
            if (_discardBytes == 0)
            {
                ApplyTuning();
            }
        }
    }

    private void ApplyTuning()
    {
        _applyAfterDiscard = false;
        bool rateMoved = _applyRate != SampleRate;
        if (_applyCentreHz != CentreFrequencyHz || rateMoved)
        {
            CentreFrequencyHz = _applyCentreHz;
            SampleRate = _applyRate;
            _pendingGeneration = true;
        }

        _rateWatch?.Restart(_time.GetUtcNow(), rateMoved ? _applyRate : null);
    }

    private int TargetRate()
    {
        lock (_sendGate)
        {
            return _wantedRate;
        }
    }

    private long SettleBytes(int rate) => 2L * (long)(rate * _options.RetuneSettle.TotalSeconds);

    /// <summary>Whether the reader has fallen behind the server: a backlog of more than half the
    /// socket's buffer, or half a second of samples, whichever is less.</summary>
    private bool Behind(Socket socket)
    {
        try
        {
            int backlog = socket.Available;
            long threshold = Math.Min(Math.Max(socket.ReceiveBufferSize / 2, 4096), SampleRate);
            return backlog >= threshold;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void ReportStep(DateTimeOffset now)
    {
        if (now < _nextStepLine)
        {
            return;
        }

        _nextStepLine = now + StepLineInterval;
        Say($"rtltcp: {_endpoint}'s stream changed suddenly and has stayed changed ({_stepWatch!.LastStep}). "
            + "If the server's operator switched its profile, this station is now hearing a different band "
            + "at the same sample rate, which the rtl_tcp protocol has no way to say. Ask for the profile "
            + "to be pinned; a strong signal arriving can look the same, so this is a warning only.");
    }

    /// <summary>Drops a connection that failed; the next <see cref="Read"/> reconnects.</summary>
    private void Drop(string reason)
    {
        lock (_sendGate)
        {
            _connected = false;
            _socket?.Dispose();
            _socket = null;
        }

        _haveCarry = false;
        Say($"rtltcp: {_endpoint}: {reason}; reconnecting");
    }

    /// <summary>Reconnects, waiting between attempts, until it works, the source is closed, or
    /// it has failed for <see cref="RtlTcpSourceOptions.GiveUpAfter"/>.</summary>
    private bool Reconnect()
    {
        DateTimeOffset since = _goneSince;
        TimeSpan delay = _options.ReconnectDelay;
        DateTimeOffset nextFailureLine = DateTimeOffset.MinValue;
        int quietFailures = 0;
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = ConnectAsync(_closing.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_disposed)
            {
                return false;
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                DateTimeOffset now = _time.GetUtcNow();
                if (now - since >= _options.GiveUpAfter)
                {
                    if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
                    {
                        Lost?.Invoke($"the rtl_tcp server at {_endpoint} has been unreachable for "
                            + $"{(now - since).TotalSeconds:F0} s ({e.Message})");
                    }

                    return false;
                }

                if (now >= nextFailureLine)
                {
                    string more = quietFailures > 0 ? $" ({quietFailures} more failed attempt(s) since the last line)" : "";
                    Say($"rtltcp: cannot reach {_endpoint} ({e.Message}); still trying{more}");
                    nextFailureLine = now + FailureLineInterval;
                    quietFailures = 0;
                }
                else
                {
                    quietFailures++;
                }

                try
                {
                    Task.Delay(delay, _time, _closing.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectDelayCap.Ticks));
                continue;
            }

            lock (_sendGate)
            {
                if (_disposed)
                {
                    socket.Dispose();
                    return false;
                }

                _socket = socket;
                _connected = true;
                if (!_options.ReadOnly)
                {
                    // The settings just sent may be a retune the reader has not taken up; either
                    // way what comes first is at a tuning nobody can tell.
                    _retunePending = true;
                    _retuneDiscardBytes = SettleBytes(_wantedRate);
                }
            }

            DateTimeOffset back = _time.GetUtcNow();
            TimeSpan gone = back - since;
            Interlocked.Increment(ref _reconnects);
            _pendingLost += Math.Max(1, (long)(gone.TotalSeconds * SampleRate));
            _rateWatch?.Restart(back);
            _stepWatch?.Restart();
            Say($"rtltcp: reconnected to {_endpoint} after {gone.TotalSeconds:F1} s; the time it was gone is "
                + "marked as a gap");
            return true;
        }

        return false;
    }

    /// <summary>Connects, reads the header and (controlled) sends the wanted settings.</summary>
    private async Task<Socket> ConnectAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_options.ConnectTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            try
            {
                await socket.ConnectAsync(_endpoint.Host, _endpoint.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new IOException($"no answer from {_endpoint} in {_options.ConnectTimeout.TotalSeconds:F0} s");
            }

            byte[] header = new byte[RtlTcpProtocol.HeaderLength];
            int have = 0;
            while (have < header.Length)
            {
                int n;
                try
                {
                    n = await socket.ReceiveAsync(header.AsMemory(have), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new IOException($"{_endpoint} accepted the connection and then sent nothing in "
                        + $"{_options.ConnectTimeout.TotalSeconds:F0} s");
                }

                if (n == 0)
                {
                    throw new IOException($"{_endpoint} closed the connection before sending anything; "
                        + "rtl_tcp serves one client at a time, so another may have it");
                }

                have += n;
            }

            RtlTcpHeader? parsed = RtlTcpProtocol.ParseHeader(header);
            if (parsed != Header || !_everDelivered)
            {
                Say(parsed is RtlTcpHeader h
                    ? $"rtltcp: {_endpoint} has a dongle with an {h.TunerName} tuner ({h.GainCount} gain steps)"
                    : $"rtltcp: {_endpoint} sent no dongle header; taking its stream as IQ from the start");
            }

            Header = parsed;
            socket.ReceiveTimeout = (int)Math.Clamp(_options.NoDataTimeout.TotalMilliseconds, 1, int.MaxValue);
            if (!_options.ReadOnly)
            {
                SendSettings(socket);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends every setting, as at a connect: rate first, as rtl_tcp clients do, since a
    /// rate change can disturb the tuning.</summary>
    private void SendSettings(Socket socket)
    {
        lock (_sendGate)
        {
            Span<byte> c = _commands;
            int length = 0;
            void Add(Span<byte> into, RtlTcpCommand command, uint parameter)
            {
                RtlTcpProtocol.Encode(into[length..], command, parameter);
                length += RtlTcpProtocol.CommandLength;
            }

            Add(c, RtlTcpCommand.SetSampleRate, (uint)_wantedRate);
            Add(c, RtlTcpCommand.SetFrequency, (uint)Math.Round(_wantedCentreHz));
            Add(c, RtlTcpCommand.SetFrequencyCorrection, unchecked((uint)_options.FrequencyCorrectionPpm));
            Add(c, RtlTcpCommand.SetGainMode, _options.TunerGainDb is null ? 0u : 1u);
            if (_options.TunerGainDb is double gain)
            {
                Add(c, RtlTcpCommand.SetGain, (uint)Math.Max(0, (int)Math.Round(gain * 10)));
            }

            Add(c, RtlTcpCommand.SetAgcMode, _options.RtlAgc ? 1u : 0u);
            socket.Send(c[..length]);
        }
    }

    private static void CheckControllable(double centreHz, int sampleRate)
    {
        if (!double.IsFinite(centreHz) || centreHz < 1 || centreHz > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(centreHz), centreHz,
                "an rtl_tcp server is tuned to a whole number of Hz between 1 and 4294967295");
        }

        if (!RtlTcpProtocol.IsDongleRate(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate,
                "an RTL dongle runs at 225001 to 300000 or 900001 to 3200000 samples a second");
        }
    }

    private void Say(string line) => _options.Journal?.Invoke(line);
}
