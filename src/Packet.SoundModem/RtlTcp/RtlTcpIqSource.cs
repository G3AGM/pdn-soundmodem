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

    /// <summary>
    /// The converter's own real local oscillator frequency, Hz: for a dongle behind a converter
    /// whose own LO puts the signal somewhere else than the frequency this source's samples are
    /// taken to represent - an IF tap off another radio, say. 0 (the default) tunes the dongle
    /// to the centre frequency itself. Controlled only; everything above this source still reads
    /// <see cref="CentreFrequencyHz"/>, not the dongle's own tuned frequency. Combined with
    /// <see cref="IqSwap"/>: see there for how the two meet.
    /// </summary>
    public double LfoOffsetHz { get; init; }

    /// <summary>
    /// Swap the I and Q samples the server sends, after every other setting here: for a
    /// converter whose own mixing reverses frequency sense (so the spectrum comes out
    /// mirrored). False (the default) takes them as the server sends them.
    /// </summary>
    /// <remarks>
    /// The swap is a reflection about whatever frequency the dongle is physically tuned to, so a
    /// converter's real local oscillator and the station's centre frequency do not simply add
    /// once a reflection is in the path: non-inverting, the dongle is tuned to
    /// <c>centre + lfoOffsetHz</c>, same as a dongle with no converter in front of it but moved
    /// by a fixed amount; inverting, it is tuned to <c>lfoOffsetHz - centre</c>, so that the
    /// swap's reflection lands the dial back where the station's own DC-clearance placement
    /// (<see cref="IqTuning.DcClearOffsetHz"/>) put it before the converter. <see cref="LfoOffsetHz"/>
    /// is the converter's real LO either way, not a value picked to make one dial work.
    /// </remarks>
    public bool IqSwap { get; init; }

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
    /// How long a stretch of the stream, at the new rate, to throw away after this client retunes
    /// or sets the rate, on top of what had reached the socket and
    /// <see cref="RetuneDiscardFloorBytes"/>: what was in flight on the link and the tuner
    /// settling, whose samples are at a tuning nobody can tell. Counted as lost.
    /// </summary>
    public TimeSpan RetuneSettle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Bytes thrown away after a retune or a connect whatever the rate, on top of
    /// <see cref="RetuneSettle"/>: rtl_tcp hands the dongle's samples on in 256 KiB transfers,
    /// and the one being filled when a command lands is part old tuning and part new, with
    /// another queued behind it that the client cannot see. Two of them by default.
    /// </summary>
    public long RetuneDiscardFloorBytes { get; init; } = 2 * 262_144;

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
/// <para><b>Controlled.</b> Opening sends the rate, the centre (combined with
/// <see cref="RtlTcpSourceOptions.LfoOffsetHz"/> for a dongle behind a converter whose own local
/// oscillator is not where the signal is heard - see <see cref="TunerHz"/> and
/// <see cref="RtlTcpSourceOptions.IqSwap"/> for how the two combine), the frequency correction,
/// the gain mode and gain, and the AGC mode, and sends them again on every reconnect, because
/// the server may have restarted with its own defaults. <see cref="Tune"/> retunes; since the
/// protocol does not say where in the stream a command took effect, what was already in flight
/// and <see cref="RtlTcpSourceOptions.RetuneSettle"/> after it are thrown away and counted as
/// lost, and the next block is the first at the new tuning (<see cref="TuningGeneration"/>
/// moves). <see cref="RtlTcpSourceOptions.IqSwap"/> also swaps every sample's I and Q after
/// conversion, for a spectrum that otherwise comes out mirrored; it needs no retune and takes
/// effect at once.</para>
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
    private volatile bool _gaveUp;
    private bool _headerSaid;
    private byte[] _bytes = [];
    private bool _haveCarry;
    private byte _carry;

    // What the station has asked for (controlled), under _sendGate; and whether a change is
    // waiting for the reader to take it up.
    private double _wantedCentreHz;
    private int _wantedRate;
    private bool _retunePending;
    private long _retuneSequence;
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
    private DateTimeOffset _goneSince;      // when samples last arrived: the give-up clock
    private DateTimeOffset _lossFrom;       // up to when lost time has already been counted
    private TimeSpan _backoff;              // the wait before the next reconnect attempt
    private long _bytesThisConnection;
    private int _receiveBufferBytes = 4096; // the socket's, read once a connect: the getter boxes
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
            CheckControllable(options.CentreFrequencyHz, options.LfoOffsetHz, options.IqSwap, options.SampleRate);
        }

        _endpoint = endpoint;
        _options = options;
        _time = options.TimeProvider;
        CentreFrequencyHz = options.CentreFrequencyHz;
        SampleRate = options.SampleRate;
        _wantedCentreHz = options.CentreFrequencyHz;
        _wantedRate = options.SampleRate;
        _backoff = options.ReconnectDelay;
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

    /// <summary>What the dongle itself is tuned to for <see cref="CentreFrequencyHz"/>, Hz: the
    /// centre once <see cref="RtlTcpSourceOptions.LfoOffsetHz"/> and
    /// <see cref="RtlTcpSourceOptions.IqSwap"/> are applied, for the journal.</summary>
    public double TunerFrequencyHz => TunerHz(CentreFrequencyHz, _options.LfoOffsetHz, _options.IqSwap);

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
    /// Raised once, from the reader thread, when the server has delivered nothing for
    /// <see cref="RtlTcpSourceOptions.GiveUpAfter"/>, unreachable or connecting and then sending
    /// nothing; <see cref="Read"/> returns 0 from then on.
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
        try
        {
            Publish(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        DateTimeOffset now = _time.GetUtcNow();
        _rateWatch?.Restart(now);
        _goneSince = now;
        _lossFrom = now;
        if (!_options.ReadOnly)
        {
            // What the server was streaming before the settings landed is at its own defaults,
            // and nothing came before it, so it is not counted.
            _discardBytes = SettleBytes(_options.SampleRate);
            _discardCounts = false;
            lock (_sendGate)
            {
                _retunePending = false;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="socket"/> the connection, sending the settings first on a controlled
    /// server: under the same lock a <see cref="Tune"/> takes, so a retune either goes out on
    /// this socket or is in the settings sent, never lost between the two.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The source was closed meanwhile.</exception>
    private void Publish(Socket socket)
    {
        lock (_sendGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_options.ReadOnly)
            {
                SendSettings(socket);

                // Whatever comes first is at a tuning nobody can tell; and the settings just sent
                // may carry a retune the reader has not taken up yet.
                _retunePending = true;
                _retuneSequence++;
                _retuneDiscardBytes = SettleBytes(_wantedRate);
            }

            _socket = socket;
            _connected = true;
            _bytesThisConnection = 0;
            try
            {
                _receiveBufferBytes = socket.ReceiveBufferSize;
            }
            catch (SocketException)
            {
                _receiveBufferBytes = 4096;
            }
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
        CheckControllable(centreHz, _options.LfoOffsetHz, _options.IqSwap, rate);
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

                    RtlTcpProtocol.Encode(_commands.AsSpan(length), RtlTcpCommand.SetFrequency, (uint)Math.Round(TunerHz(centreHz, _options.LfoOffsetHz, _options.IqSwap)));
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
            _retuneSequence++;
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

        while (!_disposed && !_gaveUp)
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

            long sequence = TakeUpRetune();

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
            _lossFrom = now;
            _bytesThisConnection += got;
            if (_bytesThisConnection >= 2L * SampleRate)
            {
                // A second of samples on this connection: it is healthy, so the next drop is
                // retried promptly again.
                _backoff = _options.ReconnectDelay;
            }

            if (TakeUpRetune() != sequence)
            {
                // A retune landed while the reader waited: what it woke with may be either side
                // of it, so it goes with the settling.
                _discardBytes += even;
            }

            // What is thrown away goes first, before anything is judged, so a retune is applied
            // (and the rate watch told its rate) even while the stream is off its rate.
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
            }

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
                    _pendingLost += bytes.Length / 2;
                    continue;
                }
            }

            if (bytes.IsEmpty)
            {
                continue;
            }

            int floats = bytes.Length;
            Span<float> output = interleaved[..floats];
            RtlTcpProtocol.Convert(bytes, output);
            if (_options.IqSwap)
            {
                SwapIq(output);
            }

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

    /// <summary>Takes up a retune asked for since the last call, if there is one; returns the
    /// retune count as it stands.</summary>
    private long TakeUpRetune()
    {
        lock (_sendGate)
        {
            if (!_retunePending)
            {
                return _retuneSequence;
            }

            _retunePending = false;

            // Whole I,Q pairs: an odd discard would start the next block on a Q.
            _discardBytes = (_discardBytes + _retuneDiscardBytes + 1) & ~1L;
            _discardCounts = true;
            _applyAfterDiscard = true;
            _applyCentreHz = _wantedCentreHz;
            _applyRate = _wantedRate;
            if (_discardBytes == 0)
            {
                ApplyTuning();
            }

            return _retuneSequence;
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

    /// <summary>
    /// What is thrown away after a retune or a connect at <paramref name="rate"/>, on top of what
    /// the client had already received and not read, in bytes; always even.
    /// </summary>
    /// <remarks>
    /// Two parts, added rather than the larger taken, because they cover different things. The
    /// floor is the server's side: the USB transfer being filled when the command lands, and the
    /// one already queued behind it, which no client can see (<c>Available</c> counts only what
    /// reached this socket). The settling time is the link's and the tuner's: what was in flight
    /// on the wire and in the kernel's buffers when the command went, and the dongle settling at
    /// the new tuning, worth that long at the new rate. A link slower than the stream is the one
    /// case this does not bound: the server's queue then grows without limit, and the rate watch
    /// sees the reader set the pace; no client-side count can cover it.
    /// </remarks>
    private long SettleBytes(int rate) =>
        (2L * (long)Math.Round(rate * _options.RetuneSettle.TotalSeconds)) + ((_options.RetuneDiscardFloorBytes + 1) & ~1L);

    /// <summary>Whether the reader has fallen behind the server: a backlog of more than half the
    /// socket's buffer, or half a second of samples, whichever is less.</summary>
    private bool Behind(Socket socket)
    {
        try
        {
            int backlog = socket.Available;
            long threshold = Math.Min(Math.Max(_receiveBufferBytes / 2, 4096), SampleRate);
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
        DateTimeOffset nextFailureLine = DateTimeOffset.MinValue;
        int quietFailures = 0;
        string? lastFailure = null;
        bool wait = _bytesThisConnection < 2L * SampleRate;
        while (!_disposed)
        {
            // Gone too long, whether the attempts fail or connect and then deliver nothing.
            DateTimeOffset now = _time.GetUtcNow();
            if (now - since >= _options.GiveUpAfter)
            {
                _gaveUp = true;
                if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
                {
                    Lost?.Invoke($"the rtl_tcp server at {_endpoint} has delivered nothing for "
                        + $"{(now - since).TotalSeconds:F0} s" + (lastFailure is null ? "" : $" ({lastFailure})"));
                }

                return false;
            }

            // A connection that did not deliver for long is no reason to hammer the server: wait,
            // and wait longer each time, until one does.
            if (wait)
            {
                try
                {
                    Task.Delay(_backoff, _time, _closing.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, _options.ReconnectDelayCap.Ticks));
            }

            wait = true;
            Socket socket;
            try
            {
                socket = ConnectAsync(_closing.Token).GetAwaiter().GetResult();
                try
                {
                    Publish(socket);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (Exception e) when (_disposed && e is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                return false;
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
            {
                lastFailure = e.Message;
                now = _time.GetUtcNow();
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

                continue;
            }

            // The time since the stream was last counted is lost; counted once, however many
            // connections it took.
            DateTimeOffset back = _time.GetUtcNow();
            TimeSpan gone = back - _lossFrom;
            _lossFrom = back;
            Interlocked.Increment(ref _reconnects);
            _pendingLost += Math.Max(1, (long)(gone.TotalSeconds * SampleRate));
            _rateWatch?.Restart(back);
            _stepWatch?.Restart();
            Say($"rtltcp: reconnected to {_endpoint} after {(back - since).TotalSeconds:F1} s without samples; "
                + "the time they were missing is marked as a gap");
            return true;
        }

        return false;
    }

    /// <summary>Connects and reads the header; the settings are sent by <see cref="Publish"/>.</summary>
    /// <remarks>
    /// <para>The socket is only ever used in blocking mode, and nothing asynchronous touches it.
    /// .NET leaves a socket that has once been non-blocking (an asynchronous call, or
    /// <see cref="Socket.Blocking"/> set false) non-blocking underneath for good, and a blocking
    /// <see cref="Socket.Receive(Span{byte})"/> on it then allocates every time it has to wait,
    /// which for a reader that keeps up is every read
    /// (<c>A_Steady_Stream_Is_Read_Without_Allocating</c>).</para>
    /// <para>So the lookup, the connect and the header are done blocking on a thread of their own,
    /// and waited for here with the timeout and the caller's cancellation. A host that never
    /// answers, or a dispose, ends the wait at once: the socket is closed and the attempt left to
    /// finish on its own thread, which it does by the system's own connect timeout at the latest,
    /// its result thrown away.</para>
    /// </remarks>
    private async Task<Socket> ConnectAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_options.ConnectTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var progress = new ConnectProgress();
        Task<RtlTcpHeader?> attempt = Task.Factory.StartNew(
            () => ConnectBlocking(socket, progress), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        RtlTcpHeader? parsed;
        try
        {
            parsed = await attempt.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!attempt.IsCompleted)
        {
            socket.Dispose();
            _ = attempt.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            cancellation.ThrowIfCancellationRequested();
            throw new IOException(progress.Connected
                ? $"{_endpoint} accepted the connection and then sent nothing in {_options.ConnectTimeout.TotalSeconds:F0} s"
                : $"no answer from {_endpoint} in {_options.ConnectTimeout.TotalSeconds:F0} s");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (parsed != Header || !_headerSaid)
        {
            _headerSaid = true;
            Say(parsed is RtlTcpHeader h
                ? $"rtltcp: {_endpoint} has a dongle with an {h.TunerName} tuner ({h.GainCount} gain steps)"
                : $"rtltcp: {_endpoint} sent no dongle header; taking its stream as IQ from the start");
        }

        Header = parsed;
        socket.ReceiveTimeout = (int)Math.Clamp(_options.NoDataTimeout.TotalMilliseconds, 1, int.MaxValue);
        return socket;
    }

    /// <summary>The blocking half of <see cref="ConnectAsync"/>, on its own thread.</summary>
    private RtlTcpHeader? ConnectBlocking(Socket socket, ConnectProgress progress)
    {
        socket.ReceiveTimeout = (int)Math.Clamp(_options.ConnectTimeout.TotalMilliseconds, 1, int.MaxValue);
        socket.Connect(_endpoint.Host, _endpoint.Port);
        progress.Connected = true;
        byte[] header = new byte[RtlTcpProtocol.HeaderLength];
        int have = 0;
        while (have < header.Length)
        {
            int n = socket.Receive(header.AsSpan(have));
            if (n == 0)
            {
                throw new IOException($"{_endpoint} closed the connection before sending anything; "
                    + "rtl_tcp serves one client at a time, so another may have it");
            }

            have += n;
        }

        return RtlTcpProtocol.ParseHeader(header);
    }

    /// <summary>How far a connect attempt got, for saying why it timed out.</summary>
    private sealed class ConnectProgress
    {
        private volatile bool _connected;

        public bool Connected
        {
            get => _connected;
            set => _connected = value;
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
            Add(c, RtlTcpCommand.SetFrequency, (uint)Math.Round(TunerHz(_wantedCentreHz, _options.LfoOffsetHz, _options.IqSwap)));
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

    /// <summary>
    /// What the dongle is actually told to tune to, Hz: see <see cref="RtlTcpSourceOptions.IqSwap"/>
    /// for why swapped and unswapped take the LFO offset differently.
    /// </summary>
    private static double TunerHz(double centreHz, double lfoOffsetHz, bool iqSwap) =>
        iqSwap ? lfoOffsetHz - centreHz : centreHz + lfoOffsetHz;

    private static void CheckControllable(double centreHz, double lfoOffsetHz, bool iqSwap, int sampleRate)
    {
        if (!double.IsFinite(lfoOffsetHz))
        {
            throw new ArgumentOutOfRangeException(nameof(lfoOffsetHz), lfoOffsetHz, "the LFO offset must be a frequency");
        }

        double tunerHz = TunerHz(centreHz, lfoOffsetHz, iqSwap);
        if (!double.IsFinite(tunerHz) || tunerHz < 1 || tunerHz > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(centreHz), centreHz,
                "an rtl_tcp server is tuned to a whole number of Hz between 1 and 4294967295"
                + (lfoOffsetHz == 0 ? "" : " once the LFO offset is added"));
        }

        if (!RtlTcpProtocol.IsDongleRate(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate,
                "an RTL dongle runs at 225001 to 300000 or 900001 to 3200000 samples a second");
        }
    }

    /// <summary>Swaps each sample's I and Q in place: a spectrum that comes out mirrored is read the right way round.</summary>
    private static void SwapIq(Span<float> interleaved)
    {
        for (int i = 0; i < interleaved.Length; i += 2)
        {
            (interleaved[i], interleaved[i + 1]) = (interleaved[i + 1], interleaved[i]);
        }
    }

    private void Say(string line) => _options.Journal?.Invoke(line);
}
