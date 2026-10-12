using System.Net.WebSockets;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.SdrConnect;

/// <summary>
/// A connection to SDRconnect's WebSocket API that keeps one stream coming: it says what it found
/// at the connect, sets the radio up, takes the IQ or the demodulated audio, hands it on in whole
/// frames, and reconnects when the connection drops.
/// </summary>
/// <remarks>
/// <para><b>What SDRconnect says is what is used.</b> The rate and the LO the stream is taken to
/// be at are the ones SDRconnect reports (<c>get_property_response</c> and
/// <c>property_changed</c>), never the ones asked for, so a rate SDRconnect does not offer is
/// met by the one it chose.</para>
/// <para><b>Someone else at the knobs.</b> SDRconnect is a desktop program, and its window, or
/// another client, can retune the radio this station is listening through. A change to the LO,
/// the VFO or the demodulator that this client did not ask for is said in the journal and put
/// back. A change this client did ask for is recognised by its value: until SDRconnect reports the
/// value asked for, or <see cref="SdrConnectOptions.AnswerTimeout"/> passes, other reports of
/// that property are taken as SDRconnect getting there. One that never arrives is said and taken,
/// so the client does not fight a radio that cannot do what it asks. A new rate is always taken.</para>
/// <para><b>Gaps.</b> The frames carry no counter. Every reported change of tuning is followed
/// by <see cref="SdrConnectOptions.RetuneSettle"/> thrown away and counted as lost, since the
/// stream does not say where the change landed. A reconnect counts the time it was gone. And the
/// stream is measured against the wall clock (<see cref="IqRateWatch"/>): while it arrives short
/// of its rate, which is SDRconnect or the link dropping frames, the shortfall is counted.</para>
/// <para><b>Threads.</b> One receive loop per connection does the stream and the property reports;
/// a timer watches for a silent stream; sends go through one gate. Steady state is a receive and a
/// conversion per message, into buffers sized at the connect.</para>
/// <para>Original, from SDRplay's published SDRconnect WebSocket API document, version 1.0.3 (see
/// <see cref="SdrConnectProtocol"/>).</para>
/// </remarks>
internal sealed class SdrConnectClient : IDisposable
{
    private static readonly TimeSpan FailureLineInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ForeignLineInterval = TimeSpan.FromSeconds(60);

    /// <summary>What is asked for at every connect, for the report and the set-up.</summary>
    private static readonly string[] Asked =
    [
        SdrConnectProtocol.Properties.ApiVersion,
        SdrConnectProtocol.Properties.ActiveDevice,
        SdrConnectProtocol.Properties.ValidDevices,
        SdrConnectProtocol.Properties.CanControl,
        SdrConnectProtocol.Properties.Started,
        SdrConnectProtocol.Properties.SampleRate,
        SdrConnectProtocol.Properties.CentreFrequency,
        SdrConnectProtocol.Properties.ValidAntennas,
        SdrConnectProtocol.Properties.ActiveAntenna,
        SdrConnectProtocol.Properties.LnaState,
        SdrConnectProtocol.Properties.LnaStateMin,
        SdrConnectProtocol.Properties.LnaStateMax,
    ];

    /// <summary>Asked for as well when the audio stream is taken.</summary>
    private static readonly string[] AskedForAudio =
    [
        SdrConnectProtocol.Properties.VfoFrequency,
        SdrConnectProtocol.Properties.Demodulator,
        SdrConnectProtocol.Properties.DemodMaxBandwidth,
        SdrConnectProtocol.Properties.AudioVolumePercent,
    ];

    private readonly SdrConnectEndpoint _endpoint;
    private readonly SdrConnectOptions _options;
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly ISdrConnectSink _sink;
    private readonly TimeProvider _time;
    private readonly ushort _wantedType;
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly TaskCompletionSource<string?> _firstVerdict = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Under _stateGate: what SDRconnect last said, what is waited on, what this client wants.
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string>> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _awaiting = new(StringComparer.Ordinal);
    private double _wantedCentreHz;
    private double? _wantedVfoHz;
    private string _wantedDemodulator;
    private DateTimeOffset _nextForeignLine = DateTimeOffset.MinValue;
    private int _quietForeign;

    private WebSocket? _socket;
    private ITimer? _watchdog;
    private Task? _supervisor;
    private volatile bool _disposed;
    private volatile bool _connected;
    private volatile bool _streamOn;
    private volatile bool _startPending;
    private volatile string? _abortReason;
    private int _lostRaised;

    // The receive loop's own state.
    private readonly byte[] _buffer;
    private byte[] _text = new byte[4096];
    private int _textLength;
    private readonly byte[] _header = new byte[SdrConnectProtocol.TypeBytes];
    private int _headerHave;
    private ushort _messageType;
    private readonly byte[] _carry = new byte[SdrConnectProtocol.FrameBytes];
    private int _carryLength;
    private double _appliedFrequencyHz;
    private int _appliedRate;
    private bool _pendingTuning;
    private long _discardFrames;
    private bool _discardCounts;
    private long _pendingLost;
    private bool _everStarted;
    private DateTimeOffset _lossFrom;
    private IqRateWatch? _rateWatch;
    private DateTimeOffset _lastObserved;
    private double _shortfall;
    private long _sessionFrames;

    // Read from other threads; 64-bit, so Interlocked to keep a 32-bit Pi from tearing them.
    private long _lastFrameTicks;   // the give-up clock: frames only
    private long _watchFromTicks;   // the silence watch: the last frame, or the stream being turned on
    private long _framesDelivered;
    private long _reconnects;
    private long _foreignChanges;

    /// <summary>Makes a client; nothing is opened until <see cref="OpenAsync"/>.</summary>
    public SdrConnectClient(
        SdrConnectEndpoint endpoint, SdrConnectOptions options, ISdrConnectSink sink,
        Func<CancellationToken, Task<WebSocket>>? connect = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        if (!double.IsFinite(options.CentreFrequencyHz) || options.CentreFrequencyHz < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CentreFrequencyHz, "the centre frequency must be a frequency");
        }

        if (options.Stream == SdrConnectStream.Audio && options.VfoFrequencyHz is null)
        {
            throw new ArgumentException("the audio stream needs a VFO frequency", nameof(options));
        }

        _endpoint = endpoint;
        _options = options;
        _sink = sink;
        _connect = connect ?? (ct => ConnectAsync(endpoint, options.ConnectTimeout, ct));
        _time = options.TimeProvider;
        _wantedType = options.Stream == SdrConnectStream.Iq ? SdrConnectProtocol.PrimaryIq : SdrConnectProtocol.PrimaryAudio;
        _wantedCentreHz = options.CentreFrequencyHz;
        _wantedVfoHz = options.VfoFrequencyHz;
        _wantedDemodulator = options.Demodulator;
        _buffer = new byte[Math.Max(options.ReceiveBufferBytes, 1024)];
    }

    /// <summary>SDRconnect's address.</summary>
    public SdrConnectEndpoint Endpoint => _endpoint;

    /// <summary>The stream taken.</summary>
    public SdrConnectStream Stream => _options.Stream;

    /// <summary>Whether the station should expect frames: connected, set up and streaming.</summary>
    public bool SessionLive => _connected && _streamOn && !(_rateWatch?.Off ?? false);

    /// <summary>Reconnects since opening.</summary>
    public long Reconnects => Interlocked.Read(ref _reconnects);

    /// <summary>Changes to the tuning that someone other than this client made.</summary>
    public long ForeignChanges => Interlocked.Read(ref _foreignChanges);

    /// <summary>Frames of the stream taken that were handed on.</summary>
    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    /// <summary>What SDRconnect said at the first connect, property by property; a property it
    /// did not answer is absent.</summary>
    public IReadOnlyDictionary<string, string> Report { get; private set; } = new Dictionary<string, string>();

    /// <summary>The LO SDRconnect last reported, Hz, or the one asked for before it has.</summary>
    public double ReportedCentreHz =>
        Number(SdrConnectProtocol.Properties.CentreFrequency) ?? _options.CentreFrequencyHz;

    /// <summary>The device rate SDRconnect last reported, or 0 before it has.</summary>
    public int ReportedRate => (int)Math.Round(Number(SdrConnectProtocol.Properties.SampleRate) ?? 0);

    /// <summary>The LO this client last asked for, Hz.</summary>
    public double TargetCentreHz
    {
        get
        {
            lock (_stateGate)
            {
                return _wantedCentreHz;
            }
        }
    }

    /// <summary>The VFO this client last asked for, Hz, or null on the IQ stream.</summary>
    public double? TargetVfoHz
    {
        get
        {
            lock (_stateGate)
            {
                return _wantedVfoHz;
            }
        }
    }

    /// <summary>
    /// Raised once, when SDRconnect has delivered nothing for
    /// <see cref="SdrConnectOptions.GiveUpAfter"/>; the client has stopped.
    /// </summary>
    public event Action<string>? Lost;

    /// <summary>
    /// Connects, asks what SDRconnect has, sets the radio up and starts the stream. Throws when
    /// SDRconnect cannot be reached or refuses, so start-up can say so; once open, a later drop is
    /// reconnected.
    /// </summary>
    /// <exception cref="IOException">SDRconnect could not be reached.</exception>
    /// <exception cref="InvalidOperationException">SDRconnect answered but will not do; the message
    /// is written for an operator.</exception>
    public async Task OpenAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WebSocket first;
        try
        {
            first = await _connect(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or System.Net.Sockets.SocketException)
        {
            throw new IOException($"cannot reach SDRconnect at {_endpoint.WebSocketUri}: {e.Message}", e);
        }

        _lossFrom = _time.GetUtcNow();
        Interlocked.Exchange(ref _lastFrameTicks, _lossFrom.UtcTicks);
        _watchdog = _time.CreateTimer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _supervisor = Task.Run(() => SuperviseAsync(first), CancellationToken.None);

        TimeSpan limit = (_options.AnswerTimeout * 3) + TimeSpan.FromSeconds(5);
        Task timeout = Task.Delay(limit, _time, cancellation);
        if (await Task.WhenAny(_firstVerdict.Task, timeout).ConfigureAwait(false) != _firstVerdict.Task)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"SDRconnect at {_endpoint} accepted the connection but did not finish setting up within "
                + $"{limit.TotalSeconds:F0} s. Is that SDRconnect's WebSocket port?");
        }

        if (await _firstVerdict.Task.ConfigureAwait(false) is string refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    /// <summary>Moves the LO. Sent at once; the stream follows when SDRconnect reports it.</summary>
    public void TuneCentre(double centreHz)
    {
        lock (_stateGate)
        {
            _wantedCentreHz = centreHz;
        }

        Fire(SendWantedAsync(SdrConnectProtocol.Properties.CentreFrequency));
    }

    /// <summary>Moves the VFO, and the LO first if <paramref name="centreHz"/> is given.</summary>
    public void TuneVfo(double vfoHz, double? centreHz)
    {
        lock (_stateGate)
        {
            _wantedVfoHz = vfoHz;
            if (centreHz is double centre)
            {
                _wantedCentreHz = centre;
            }
        }

        Fire(TuneVfoAsync(centreHz is not null));
    }

    private async Task TuneVfoAsync(bool centreToo)
    {
        if (centreToo)
        {
            await SendWantedAsync(SdrConnectProtocol.Properties.CentreFrequency).ConfigureAwait(false);
        }

        await SendWantedAsync(SdrConnectProtocol.Properties.VfoFrequency).ConfigureAwait(false);
    }

    /// <summary>Closes the connection and stops reconnecting.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing.Cancel();
        _watchdog?.Dispose();
        Abort("the station is closing");
        try
        {
            _supervisor?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutdown: the loop's own failures have already been journalled.
        }

        _firstVerdict.TrySetResult("the connection was closed before SDRconnect answered");
        _sink.End();
    }

    private static async Task<WebSocket> ConnectAsync(SdrConnectEndpoint endpoint, TimeSpan timeout, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "pdn-soundmodem (sdrconnect: receive device)");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout);
            await socket.ConnectAsync(endpoint.WebSocketUri, limit.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            socket.Dispose();
            throw new WebSocketException($"no answer from {endpoint.WebSocketUri} within {timeout.TotalSeconds:F0} s");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The reconnecting loop: one connection at a time, until closed or given up.</summary>
    private async Task SuperviseAsync(WebSocket first)
    {
        WebSocket? socket = first;
        bool firstSession = true;
        TimeSpan backoff = _options.ReconnectDelay;
        DateTimeOffset nextFailureLine = DateTimeOffset.MinValue;
        int quietFailures = 0;
        string? lastFailure = null;
        while (!_disposed)
        {
            if (socket is null)
            {
                DateTimeOffset now = _time.GetUtcNow();
                TimeSpan gone = now - new DateTimeOffset(Interlocked.Read(ref _lastFrameTicks), TimeSpan.Zero);
                if (gone >= _options.GiveUpAfter)
                {
                    if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
                    {
                        Lost?.Invoke($"SDRconnect at {_endpoint} has delivered nothing for {gone.TotalSeconds:F0} s"
                            + (lastFailure is null ? "" : $" ({lastFailure})"));
                    }

                    _sink.End();
                    return;
                }

                try
                {
                    await Task.Delay(backoff, _time, _closing.Token).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.ReconnectDelayCap.Ticks));
                    socket = await _connect(_closing.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_disposed)
                {
                    return;
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException
                                            or System.Net.Sockets.SocketException or OperationCanceledException)
                {
                    lastFailure = e.Message;
                    now = _time.GetUtcNow();
                    if (now >= nextFailureLine)
                    {
                        string more = quietFailures > 0 ? $" ({quietFailures} more failed attempt(s) since the last line)" : "";
                        Say($"sdrconnect: cannot reach {_endpoint} ({e.Message}); still trying{more}");
                        nextFailureLine = now + FailureLineInterval;
                        quietFailures = 0;
                    }
                    else
                    {
                        quietFailures++;
                    }

                    continue;
                }

                Interlocked.Increment(ref _reconnects);
            }

            string reason = await RunSessionAsync(socket, firstSession).ConfigureAwait(false);
            socket.Dispose();
            socket = null;
            if (_disposed)
            {
                return;
            }

            if (firstSession && !_firstVerdict.Task.IsCompleted)
            {
                _firstVerdict.TrySetResult($"SDRconnect at {_endpoint} dropped the connection while it was being set up ({reason})");
                return;
            }

            if (firstSession && _firstVerdict.Task.Result is not null)
            {
                return; // refused at start-up: OpenAsync throws and the owner disposes
            }

            firstSession = false;
            if (_sessionFrames >= Math.Max(_appliedRate, 1))
            {
                // A second of frames on that connection: it was healthy, so the next drop is
                // retried promptly again.
                backoff = _options.ReconnectDelay;
            }

            Say($"sdrconnect: {_endpoint}: {reason}; reconnecting");
        }
    }

    /// <summary>One connection: its receive loop and its set-up, until it ends.</summary>
    private async Task<string> RunSessionAsync(WebSocket socket, bool firstSession)
    {
        lock (_stateGate)
        {
            _socket = socket;
            _values.Clear();
            _awaiting.Clear();
        }

        _abortReason = null;
        _connected = true;
        _streamOn = false;
        _startPending = false;
        _sessionFrames = 0;
        _headerHave = 0;
        _carryLength = 0;
        _textLength = 0;

        Task<string> receiving = ReceiveLoopAsync(socket);
        string? refusal;
        try
        {
            refusal = await SetUpAsync(firstSession).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            refusal = $"SDRconnect at {_endpoint} could not be set up: {e.Message}";
        }

        if (firstSession)
        {
            _firstVerdict.TrySetResult(refusal);
        }
        else if (refusal is not null)
        {
            Say($"sdrconnect: {refusal}");
        }

        if (refusal is not null)
        {
            Abort("it was not set up");
        }

        string reason = await receiving.ConfigureAwait(false);
        _connected = false;
        _streamOn = false;
        lock (_stateGate)
        {
            _socket = null;
            foreach (TaskCompletionSource<string> waiter in _waiting.Values)
            {
                waiter.TrySetCanceled();
            }

            _waiting.Clear();
        }

        return reason;
    }

    /// <summary>
    /// Asks what SDRconnect has and says it, then sets the radio up and turns the stream on.
    /// Returns why the station cannot use it, or null.
    /// </summary>
    private async Task<string?> SetUpAsync(bool firstSession)
    {
        if (_options.DeviceSerial is { } serial)
        {
            await SendAsync(SdrConnectProtocol.Encode(SdrConnectProtocol.Events.SelectedDeviceSerial, "", serial)).ConfigureAwait(false);
        }
        else if (_options.DeviceName is { } name)
        {
            await SendAsync(SdrConnectProtocol.Encode(SdrConnectProtocol.Events.SelectedDeviceName, "", name)).ConfigureAwait(false);
        }

        IReadOnlyDictionary<string, string> report = await AskAsync(
            _options.Stream == SdrConnectStream.Audio ? [.. Asked, .. AskedForAudio] : Asked).ConfigureAwait(false);
        if (firstSession)
        {
            Report = report;
            foreach (string line in Describe(report))
            {
                Say(line);
            }
        }

        if (report.TryGetValue(SdrConnectProtocol.Properties.CanControl, out string? control)
            && SdrConnectProtocol.ParseBool(control) == false)
        {
            return $"SDRconnect at {_endpoint} says this client cannot control its radio (can_control is false). "
                + "This station has to set the radio's frequency itself. Check that SDRconnect has the radio "
                + "selected and is not itself a client of another SDRconnect that holds control.";
        }

        if (report.TryGetValue(SdrConnectProtocol.Properties.Started, out string? started)
            && SdrConnectProtocol.ParseBool(started) == false)
        {
            Say($"sdrconnect: the radio was not running in SDRconnect at {_endpoint}; starting it");
            await SendAsync(SdrConnectProtocol.Enable(SdrConnectProtocol.Events.DeviceStreamEnable, true)).ConfigureAwait(false);
        }

        if (_options.LnaState is int lna)
        {
            int? min = Whole(report, SdrConnectProtocol.Properties.LnaStateMin);
            int? max = Whole(report, SdrConnectProtocol.Properties.LnaStateMax);
            if ((min is int low && lna < low) || (max is int high && lna > high))
            {
                Say($"sdrconnect: \"lnaState\" {lna} is outside the {min}-{max} this radio offers; leaving SDRconnect's gain as it is");
            }
            else
            {
                await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.LnaState, lna.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
        }

        if (_options.Antenna is { } antenna)
        {
            if (report.TryGetValue(SdrConnectProtocol.Properties.ValidAntennas, out string? antennas)
                && !antennas.Split(',', StringSplitOptions.TrimEntries).Contains(antenna, StringComparer.OrdinalIgnoreCase))
            {
                Say($"sdrconnect: \"antenna\" \"{Ascii(antenna)}\" is not one SDRconnect lists ({Ascii(antennas)}); leaving the antenna as it is");
            }
            else
            {
                await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.ActiveAntenna, antenna)).ConfigureAwait(false);
            }
        }

        await SendAsync(SdrConnectProtocol.Enable(SdrConnectProtocol.Events.SpectrumEnable, false)).ConfigureAwait(false);
        if (_options.SampleRate is int rate)
        {
            // Not held to: whatever rate SDRconnect then reports is the one taken.
            await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.SampleRate, SdrConnectProtocol.Number(rate))).ConfigureAwait(false);
        }

        await SendWantedAsync(SdrConnectProtocol.Properties.CentreFrequency).ConfigureAwait(false);
        if (_options.Stream == SdrConnectStream.Audio)
        {
            await SetUpAudioAsync(report).ConfigureAwait(false);
        }

        // What SDRconnect has now: the stream is taken to be at what it says, not what was asked.
        IReadOnlyDictionary<string, string> settled = await AskAsync(
            _options.Stream == SdrConnectStream.Audio
                ? [SdrConnectProtocol.Properties.SampleRate, SdrConnectProtocol.Properties.CentreFrequency, SdrConnectProtocol.Properties.VfoFrequency]
                : [SdrConnectProtocol.Properties.SampleRate, SdrConnectProtocol.Properties.CentreFrequency]).ConfigureAwait(false);
        if (Number(SdrConnectProtocol.Properties.SampleRate) is not double reported || reported < 1)
        {
            return $"SDRconnect at {_endpoint} did not say its sample rate (device_sample_rate), so its stream "
                + "cannot be used. Is the radio selected and running in SDRconnect?";
        }

        if (firstSession)
        {
            string at = _options.Stream == SdrConnectStream.Audio && settled.TryGetValue(SdrConnectProtocol.Properties.VfoFrequency, out string? vfo)
                ? $", VFO {Ascii(vfo)} Hz"
                : "";
            Say($"sdrconnect: set up: LO {Ascii(settled.GetValueOrDefault(SdrConnectProtocol.Properties.CentreFrequency, "(not said)"))} Hz, "
                + $"rate {reported:F0} S/s{at}; taking the {(_options.Stream == SdrConnectStream.Iq ? "IQ" : "audio")} stream");
        }

        Interlocked.Exchange(ref _watchFromTicks, _time.GetUtcNow().UtcTicks);
        _startPending = true;
        await SendAsync(SdrConnectProtocol.Enable(
            _options.Stream == SdrConnectStream.Iq ? SdrConnectProtocol.Events.IqStreamEnable : SdrConnectProtocol.Events.AudioStreamEnable,
            true)).ConfigureAwait(false);
        return null;
    }

    /// <summary>The audio stream's demodulator, VFO and filter, and SDRconnect's own audio
    /// processing turned off, so what arrives is the demodulator's output and nothing else.</summary>
    private async Task SetUpAudioAsync(IReadOnlyDictionary<string, string> report)
    {
        string demodulator;
        lock (_stateGate)
        {
            demodulator = _wantedDemodulator;
        }

        await SendWantedAsync(SdrConnectProtocol.Properties.Demodulator).ConfigureAwait(false);
        await SendWantedAsync(SdrConnectProtocol.Properties.VfoFrequency).ConfigureAwait(false);
        if (_options.FilterBandwidthHz is double bandwidth)
        {
            if (Whole(report, SdrConnectProtocol.Properties.DemodMaxBandwidth) is int widest && bandwidth > widest)
            {
                Say($"sdrconnect: SDRconnect's widest filter is {widest} Hz; asking for that rather than {bandwidth:F0}");
                bandwidth = widest;
            }

            await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.FilterBandwidth, SdrConnectProtocol.Number(bandwidth))).ConfigureAwait(false);
        }

        bool fm = demodulator.Equals("NFM", StringComparison.OrdinalIgnoreCase);
        if (!fm && _options.SsbLowCutHz is double lowCut)
        {
            await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.SsbLowCut, SdrConnectProtocol.Number(lowCut))).ConfigureAwait(false);
        }

        string[] off =
        [
            SdrConnectProtocol.Properties.AgcEnable,
            SdrConnectProtocol.Properties.NoiseReductionEnable,
            SdrConnectProtocol.Properties.SquelchEnable,
            SdrConnectProtocol.Properties.AudioLimiters,
            SdrConnectProtocol.Properties.AudioFilter,
        ];
        foreach (string property in off)
        {
            await SendAsync(SdrConnectProtocol.Set(property, "false")).ConfigureAwait(false);
        }

        if (fm)
        {
            // Flat, as the station's own FM discriminator is: the modems expect what a radio's
            // data port gives, not its speaker.
            await SendAsync(SdrConnectProtocol.Set(SdrConnectProtocol.Properties.NfmDeemphasisEnable, "false")).ConfigureAwait(false);
        }
    }

    /// <summary>Asks for <paramref name="properties"/> and waits up to the answer timeout; what was
    /// not answered is left out.</summary>
    private async Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<string> properties)
    {
        var waits = new List<(string Property, Task<string> Answer)>(properties.Count);
        lock (_stateGate)
        {
            foreach (string property in properties)
            {
                var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[property] = waiter;
                waits.Add((property, waiter.Task));
            }
        }

        foreach (string property in properties)
        {
            await SendAsync(SdrConnectProtocol.Get(property)).ConfigureAwait(false);
        }

        Task all = Task.WhenAll(waits.Select(w => w.Answer));
        await Task.WhenAny(all, Task.Delay(_options.AnswerTimeout, _time, _closing.Token)).ConfigureAwait(false);
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_stateGate)
        {
            foreach ((string property, Task<string> answer) in waits)
            {
                if (answer.IsCompletedSuccessfully)
                {
                    answers[property] = answer.Result;
                }

                _waiting.Remove(property);
            }
        }

        return answers;
    }

    /// <summary>The start-up report, as journal lines.</summary>
    internal static IEnumerable<string> Describe(IReadOnlyDictionary<string, string> report)
    {
        string Said(string property) =>
            report.TryGetValue(property, out string? value) ? Ascii(value) : "(no answer)";

        yield return $"sdrconnect: API version {Said(SdrConnectProtocol.Properties.ApiVersion)}; "
            + $"device {Said(SdrConnectProtocol.Properties.ActiveDevice)} "
            + $"(devices: {Said(SdrConnectProtocol.Properties.ValidDevices)}); "
            + $"can control: {Said(SdrConnectProtocol.Properties.CanControl)}; "
            + $"running: {Said(SdrConnectProtocol.Properties.Started)}";
        yield return $"sdrconnect: sample rate {Said(SdrConnectProtocol.Properties.SampleRate)}; "
            + $"LO {Said(SdrConnectProtocol.Properties.CentreFrequency)} Hz; "
            + $"LNA state {Said(SdrConnectProtocol.Properties.LnaState)} "
            + $"(range {Said(SdrConnectProtocol.Properties.LnaStateMin)} to {Said(SdrConnectProtocol.Properties.LnaStateMax)}); "
            + $"antenna {Said(SdrConnectProtocol.Properties.ActiveAntenna)} "
            + $"(antennas: {Said(SdrConnectProtocol.Properties.ValidAntennas)})";
        if (report.ContainsKey(SdrConnectProtocol.Properties.VfoFrequency)
            || report.ContainsKey(SdrConnectProtocol.Properties.Demodulator))
        {
            yield return $"sdrconnect: VFO {Said(SdrConnectProtocol.Properties.VfoFrequency)} Hz; "
                + $"demodulator {Said(SdrConnectProtocol.Properties.Demodulator)}; "
                + $"widest filter {Said(SdrConnectProtocol.Properties.DemodMaxBandwidth)} Hz; "
                + $"audio volume {Said(SdrConnectProtocol.Properties.AudioVolumePercent)}";
        }
    }

    /// <summary>See <see cref="SdrConnectProtocol.Printable"/>.</summary>
    internal static string Ascii(string text) => SdrConnectProtocol.Printable(text);

    /// <summary>The receive loop: text messages to <see cref="OnText"/>, binary to
    /// <see cref="OnBinary"/>, until the connection ends; returns why it ended.</summary>
    private async Task<string> ReceiveLoopAsync(WebSocket socket)
    {
        Memory<byte> buffer = _buffer;
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                // No token: a registration per receive would allocate. A close aborts the socket,
                // which ends the receive.
                result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                return _abortReason ?? $"the connection failed ({e.Message})";
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return _abortReason ?? "SDRconnect closed the connection";
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                OnText(_buffer.AsSpan(0, result.Count), result.EndOfMessage);
            }
            else
            {
                OnBinary(_buffer.AsSpan(0, result.Count), result.EndOfMessage);
            }
        }
    }

    private void OnText(ReadOnlySpan<byte> part, bool end)
    {
        if (_textLength + part.Length > _text.Length)
        {
            Array.Resize(ref _text, Math.Max(_text.Length * 2, _textLength + part.Length));
        }

        part.CopyTo(_text.AsSpan(_textLength));
        _textLength += part.Length;
        if (!end)
        {
            return;
        }

        int length = _textLength;
        _textLength = 0;
        if (SdrConnectProtocol.TryParse(_text.AsSpan(0, length), out SdrConnectMessage message))
        {
            OnMessage(message);
        }
    }

    /// <summary>A property report: remembered, handed to whoever asked, and acted on.</summary>
    internal void OnMessage(SdrConnectMessage message)
    {
        if (message.EventType is not (SdrConnectProtocol.Events.PropertyChanged or SdrConnectProtocol.Events.GetPropertyResponse)
            || !message.IsPrimary || message.Property.Length == 0)
        {
            return;
        }

        lock (_stateGate)
        {
            _values[message.Property] = message.Value;
            if (_waiting.Remove(message.Property, out TaskCompletionSource<string>? waiter))
            {
                waiter.TrySetResult(message.Value);
            }
        }

        switch (message.Property)
        {
            case SdrConnectProtocol.Properties.CentreFrequency:
                if (SdrConnectProtocol.ParseNumber(message.Value) is double centre)
                {
                    if (_options.Stream == SdrConnectStream.Iq && centre != _appliedFrequencyHz)
                    {
                        _appliedFrequencyHz = centre;
                        Moved();
                    }

                    Check(message.Property, message.Value);
                }

                break;
            case SdrConnectProtocol.Properties.SampleRate:
                if (SdrConnectProtocol.ParseNumber(message.Value) is double rate && rate >= 1)
                {
                    int whole = (int)Math.Round(rate);
                    if (_options.Stream == SdrConnectStream.Iq && whole != _appliedRate)
                    {
                        if (_streamOn && _appliedRate != 0)
                        {
                            Say($"sdrconnect: {_endpoint} is now at {whole} samples a second (was {_appliedRate})");
                        }

                        _appliedRate = whole;
                        Moved();
                    }
                }

                break;
            case SdrConnectProtocol.Properties.VfoFrequency:
                if (_options.Stream == SdrConnectStream.Audio && SdrConnectProtocol.ParseNumber(message.Value) is double vfo)
                {
                    if (vfo != _appliedFrequencyHz)
                    {
                        _appliedFrequencyHz = vfo;
                        Moved();
                    }

                    Check(message.Property, message.Value);
                }

                break;
            case SdrConnectProtocol.Properties.Demodulator:
                if (_options.Stream == SdrConnectStream.Audio)
                {
                    if (_streamOn)
                    {
                        Moved();
                    }

                    Check(message.Property, message.Value);
                }

                break;
        }
    }

    /// <summary>The stream is at a new tuning from somewhere after now: throw away the settling,
    /// and mark what follows as new.</summary>
    private void Moved()
    {
        if (!_streamOn)
        {
            return; // the start takes up whatever has been reported by then
        }

        int rate = StreamRate();
        _pendingTuning = true;
        _discardFrames += Frames(_options.RetuneSettle, rate);
        _discardCounts = true;
        _rateWatch?.Restart(_time.GetUtcNow(), rate);
        _shortfall = 0;
    }

    /// <summary>
    /// Whether a reported value of a property this client sets is its own, SDRconnect getting
    /// there, or someone else's; someone else's is said and put back.
    /// </summary>
    private void Check(string property, string value)
    {
        string? wanted;
        bool foreign;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_stateGate)
        {
            wanted = Wanted(property);
            if (wanted is null || Same(property, value, wanted))
            {
                _awaiting.Remove(property);
                return;
            }

            if (_awaiting.TryGetValue(property, out DateTimeOffset until) && now < until)
            {
                return; // on its way to the value asked for
            }

            foreign = _streamOn && !_awaiting.ContainsKey(property);
        }

        if (!foreign)
        {
            return;
        }

        Interlocked.Increment(ref _foreignChanges);
        if (now >= _nextForeignLine)
        {
            string more = _quietForeign > 0 ? $" ({_quietForeign} more since the last line)" : "";
            Say($"sdrconnect: SDRconnect's {Describe(property)} was changed to {Ascii(value)} by someone else, "
                + $"perhaps in the SDRconnect window; putting it back to {wanted}{more}. While this station "
                + "is listening, please leave the radio's frequency alone.");
            _nextForeignLine = now + ForeignLineInterval;
            _quietForeign = 0;
        }
        else
        {
            _quietForeign++;
        }

        Fire(SendWantedAsync(property));
    }

    /// <summary>Called by the watchdog: a value asked for that never arrived is taken as what the
    /// radio does, rather than asked for again and again.</summary>
    private void Settle(DateTimeOffset now)
    {
        List<(string Property, string Wanted, string? Value)>? taken = null;
        lock (_stateGate)
        {
            foreach ((string property, DateTimeOffset until) in _awaiting)
            {
                if (now < until)
                {
                    continue;
                }

                taken ??= [];
                taken.Add((property, Wanted(property) ?? "", _values.GetValueOrDefault(property)));
            }

            if (taken is null)
            {
                return;
            }

            foreach ((string property, _, string? value) in taken)
            {
                _awaiting.Remove(property);
                if (value is null)
                {
                    continue;
                }

                switch (property)
                {
                    case SdrConnectProtocol.Properties.CentreFrequency when SdrConnectProtocol.ParseNumber(value) is double centre:
                        _wantedCentreHz = centre;
                        break;
                    case SdrConnectProtocol.Properties.VfoFrequency when SdrConnectProtocol.ParseNumber(value) is double vfo:
                        _wantedVfoHz = vfo;
                        break;
                    case SdrConnectProtocol.Properties.Demodulator:
                        _wantedDemodulator = value;
                        break;
                }
            }
        }

        foreach ((string property, string wanted, string? value) in taken)
        {
            if (value is not null && !Same(property, value, wanted) && property != SdrConnectProtocol.Properties.SampleRate)
            {
                Say($"sdrconnect: SDRconnect did not take {Describe(property)} {wanted}; it reports {Ascii(value)}, "
                    + "which this station now takes as what the radio does");
            }
        }
    }

    private static string Describe(string property) => property switch
    {
        SdrConnectProtocol.Properties.CentreFrequency => "LO (device_center_frequency)",
        SdrConnectProtocol.Properties.VfoFrequency => "VFO (device_vfo_frequency)",
        SdrConnectProtocol.Properties.Demodulator => "demodulator",
        SdrConnectProtocol.Properties.SampleRate => "sample rate (device_sample_rate)",
        _ => property,
    };

    /// <summary>What this client wants <paramref name="property"/> to be, or null for a property it
    /// does not hold to. Under <see cref="_stateGate"/>.</summary>
    private string? Wanted(string property) => property switch
    {
        SdrConnectProtocol.Properties.CentreFrequency => SdrConnectProtocol.Number(_wantedCentreHz),
        SdrConnectProtocol.Properties.VfoFrequency when _wantedVfoHz is double vfo => SdrConnectProtocol.Number(vfo),
        SdrConnectProtocol.Properties.Demodulator when _options.Stream == SdrConnectStream.Audio => _wantedDemodulator,
        _ => null,
    };

    private static bool Same(string property, string value, string wanted)
    {
        if (property == SdrConnectProtocol.Properties.Demodulator)
        {
            return value.Equals(wanted, StringComparison.OrdinalIgnoreCase);
        }

        return SdrConnectProtocol.ParseNumber(value) is double a && SdrConnectProtocol.ParseNumber(wanted) is double b
            && Math.Abs(a - b) < 1;
    }

    private void OnBinary(ReadOnlySpan<byte> part, bool end)
    {
        if (_startPending)
        {
            Start();
        }

        int offset = 0;
        while (_headerHave < SdrConnectProtocol.TypeBytes && offset < part.Length)
        {
            _header[_headerHave++] = part[offset++];
            if (_headerHave == SdrConnectProtocol.TypeBytes)
            {
                _messageType = SdrConnectProtocol.BinaryType(_header);
            }
        }

        if (_headerHave == SdrConnectProtocol.TypeBytes && _messageType == _wantedType && _streamOn)
        {
            ReadOnlySpan<byte> payload = part[offset..];
            if (_carryLength > 0)
            {
                int need = Math.Min(SdrConnectProtocol.FrameBytes - _carryLength, payload.Length);
                payload[..need].CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += need;
                payload = payload[need..];
                if (_carryLength == SdrConnectProtocol.FrameBytes)
                {
                    _carryLength = 0;
                    Deliver(_carry);
                }
            }

            int whole = payload.Length & ~(SdrConnectProtocol.FrameBytes - 1);
            if (whole > 0)
            {
                Deliver(payload[..whole]);
            }

            ReadOnlySpan<byte> rest = payload[whole..];
            rest.CopyTo(_carry);
            _carryLength = rest.Length;
        }

        if (end)
        {
            _headerHave = 0;
            _carryLength = 0;
        }
    }

    /// <summary>The set-up has turned the stream on: from here frames are handed on.</summary>
    private void Start()
    {
        _startPending = false;
        _carryLength = 0;
        DateTimeOffset now = _time.GetUtcNow();
        if (_options.Stream == SdrConnectStream.Iq)
        {
            _appliedRate = (int)Math.Round(Number(SdrConnectProtocol.Properties.SampleRate) ?? _appliedRate);
            _appliedFrequencyHz = Number(SdrConnectProtocol.Properties.CentreFrequency) ?? _appliedFrequencyHz;
        }
        else
        {
            _appliedFrequencyHz = Number(SdrConnectProtocol.Properties.VfoFrequency) ?? _appliedFrequencyHz;
        }

        int rate = StreamRate();
        if (_everStarted)
        {
            // The time since the stream was last heard is lost; the settling after the set-up too.
            _pendingLost += Math.Max(1, (long)((now - _lossFrom).TotalSeconds * rate));
        }

        _discardFrames = Frames(_options.RetuneSettle, rate);
        _discardCounts = _everStarted;
        _pendingTuning = true;
        _everStarted = true;
        _shortfall = 0;
        _lastObserved = now;
        if (_options.WatchRate)
        {
            _rateWatch ??= new IqRateWatch(rate, _options.RateWindow);
            _rateWatch.Restart(now, rate);
        }

        _streamOn = true;
    }

    /// <summary>Whole frames of the stream taken: thrown away if settling, measured, then handed on.</summary>
    private void Deliver(ReadOnlySpan<byte> frames)
    {
        int count = frames.Length / SdrConnectProtocol.FrameBytes;
        DateTimeOffset now = _time.GetUtcNow();
        Interlocked.Exchange(ref _lastFrameTicks, now.UtcTicks);
        Interlocked.Exchange(ref _watchFromTicks, now.UtcTicks);
        _lossFrom = now;
        _sessionFrames += count;
        Measure(count, now);

        if (_discardFrames > 0)
        {
            int drop = (int)Math.Min(_discardFrames, count);
            _discardFrames -= drop;
            if (_discardCounts)
            {
                _pendingLost += drop;
            }

            frames = frames[(drop * SdrConnectProtocol.FrameBytes)..];
            if (frames.IsEmpty)
            {
                return;
            }
        }

        if (_pendingLost > 0)
        {
            _sink.Gap(_pendingLost);
            _pendingLost = 0;
        }

        if (_pendingTuning)
        {
            _pendingTuning = false;
            _sink.Tuning(_appliedFrequencyHz, StreamRate());
        }

        _sink.Frames(frames);
        Interlocked.Add(ref _framesDelivered, frames.Length / SdrConnectProtocol.FrameBytes);
    }

    /// <summary>
    /// The stream against the wall clock. While the rate watch has it short of its rate, the
    /// shortfall is counted as lost, a tenth of a second at a time, netted against the bursts a
    /// network delivers late frames in, so jitter alone counts nothing.
    /// </summary>
    private void Measure(int count, DateTimeOffset now)
    {
        if (_rateWatch is null)
        {
            return;
        }

        int rate = StreamRate();
        switch (_rateWatch.Observe(count, now, readerBehind: false))
        {
            case IqRateVerdict.WentOff:
                Say($"sdrconnect: {_endpoint} is delivering about {_rateWatch.LastMeasuredRate:F0} samples a second, "
                    + $"not the {rate} it should. Frames are being lost on the way: the network link, or this "
                    + "machine, cannot keep up. "
                    + (_options.Stream == SdrConnectStream.Iq
                        ? "The IQ stream needs a wired link; \"sdrconnect\".\"mode\": \"audio\" needs far less."
                        : "Check the link between this machine and SDRconnect.")
                    + " The missing time is marked as gaps.");
                break;
            case IqRateVerdict.CameBack:
                Say($"sdrconnect: {_endpoint} is delivering at {rate} samples a second again");
                _shortfall = 0;
                break;
        }

        if (_rateWatch.Off)
        {
            _shortfall += ((now - _lastObserved).TotalSeconds * rate) - count;
            _shortfall = Math.Max(_shortfall, -rate);
            if (_shortfall >= rate / 10.0)
            {
                _pendingLost += (long)_shortfall;
                _shortfall = 0;
            }
        }

        _lastObserved = now;
    }

    /// <summary>The watchdog, once a second: a stream that has gone silent is dropped so the loop
    /// reconnects; a value asked for that never came is taken as it is.</summary>
    private void Watch()
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        Settle(now);
        if (_connected && (_streamOn || _startPending))
        {
            // From the stream being turned on, so a connection that is set up and then never
            // streams is dropped too.
            var last = new DateTimeOffset(Interlocked.Read(ref _watchFromTicks), TimeSpan.Zero);
            if (now - last >= _options.NoDataTimeout)
            {
                Abort($"SDRconnect sent nothing for {_options.NoDataTimeout.TotalSeconds:F0} s");
            }
        }
    }

    private void Abort(string reason)
    {
        WebSocket? socket;
        lock (_stateGate)
        {
            socket = _socket;
        }

        if (socket is null)
        {
            return;
        }

        _abortReason ??= reason;
        try
        {
            socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Sends what this client wants <paramref name="property"/> to be, then asks for it back.
    /// </summary>
    /// <remarks>
    /// <para>The value is read inside the send gate, so of two sends racing (a retune and a put-back,
    /// or a retune and a reconnect's set-up) the one that goes last carries the latest value, and
    /// SDRconnect ends where this client last asked.</para>
    /// <para>The <c>get_property</c> after the set is there because the document does not say
    /// whether SDRconnect reports a change back to the client that made it. Without a report the
    /// stream would be taken to be where it was, and the setting, at the answer timeout, as not
    /// taken; with the answer, the change is followed either way.</para>
    /// </remarks>
    private async Task SendWantedAsync(string property)
    {
        WebSocket? socket;
        lock (_stateGate)
        {
            socket = _socket;
        }

        if (socket is null)
        {
            return; // between connections: the next set-up sends everything again
        }

        await _sendGate.WaitAsync(_closing.Token).ConfigureAwait(false);
        try
        {
            string? value;
            lock (_stateGate)
            {
                value = Wanted(property);
                if (value is null)
                {
                    return;
                }

                _awaiting[property] = _time.GetUtcNow() + _options.AnswerTimeout;
            }

            await socket.SendAsync(SdrConnectProtocol.Set(property, value), WebSocketMessageType.Text, endOfMessage: true, _closing.Token).ConfigureAwait(false);
            await socket.SendAsync(SdrConnectProtocol.Get(property), WebSocketMessageType.Text, endOfMessage: true, _closing.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendAsync(byte[] message)
    {
        WebSocket? socket;
        lock (_stateGate)
        {
            socket = _socket;
        }

        if (socket is null)
        {
            return; // between connections: the next set-up sends everything again
        }

        await _sendGate.WaitAsync(_closing.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, _closing.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Runs a send from a thread that cannot wait for it: a failure is the receive loop's
    /// to notice, as a dropped connection.</summary>
    private static void Fire(Task send) =>
        _ = send.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private int StreamRate() => _options.Stream == SdrConnectStream.Iq ? Math.Max(_appliedRate, 1) : SdrConnectProtocol.AudioRate;

    private static long Frames(TimeSpan span, int rate) => (long)Math.Round(span.TotalSeconds * rate);

    private double? Number(string property)
    {
        lock (_stateGate)
        {
            return _values.TryGetValue(property, out string? value) ? SdrConnectProtocol.ParseNumber(value) : null;
        }
    }

    private static int? Whole(IReadOnlyDictionary<string, string> report, string property) =>
        report.TryGetValue(property, out string? value) && SdrConnectProtocol.ParseNumber(value) is double number
            ? (int)Math.Round(number)
            : null;

    private void Say(string line) => _options.Journal?.Invoke(line);
}
