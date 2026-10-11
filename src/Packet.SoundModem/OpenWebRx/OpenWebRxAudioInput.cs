using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Rig;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.OpenWebRx;

/// <summary>
/// A live OpenWebRX or OpenWebRX+ receiver as an <see cref="IAudioInput"/>: the receiver's own
/// demodulated audio, 12 kHz from its narrow demodulators, delivered at the channel's DSP rate.
/// </summary>
/// <remarks>
/// <para><b>Receive only.</b> As with the <c>ubersdr:</c> device, nothing at the far end of the
/// socket transmits, and the daemon says so rather than pretending.</para>
/// <para><b>The receiver's audio, not ours.</b> An UberSDR gives IQ, so the filter and the AGC
/// there are this program's. OpenWebRX gives audio: its demodulator, its passband (this client
/// asks for one and the server applies it), its AGC, and its compression, which is the
/// receiver operator's choice and can be 4-bit ADPCM. The simulator measures what the ADPCM
/// costs each mode (<c>sm-ota sim --adpcm</c>); the AGC it cannot model, which is one reason
/// the on-air legs matter.</para>
/// <para><b>Sessions end and are picked up again</b> on <see cref="UberSdrReconnectPolicy"/>'s
/// ladder, the same one the UberSDR device uses: a session that delivered real audio is
/// followed by a one-second breath, a receiver that drops us at once by an escalating wait, and
/// a <c>backoff</c> ("Too many clients", or on OpenWebRX+ "Client address banned") by the long
/// ladder that never gives up, because only time lifts those. A receiver unreachable for
/// <see cref="UberSdrAudioInput.ReconnectGiveUpAfter"/> raises <see cref="Lost"/>.</para>
/// <para><b>Tuning</b> stays here, behind the device: the dial is set when the input opens, and
/// <see cref="TuneAsync"/> moves it while it runs, on the open session and on every later one.
/// See <see cref="OpenWebRxConversation"/> for how a dial outside the receiver's band is met.</para>
/// </remarks>
public sealed class OpenWebRxAudioInput : IAudioInput, IReceiverTuner, IDisposable
{
    private const string UserAgent = "pdn-soundmodem (openwebrx: receive device)";

    /// <summary>How long a new session may take to answer the handshake and say where it is.</summary>
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long into a session before a profile is asked for. OpenWebRX+ scores a profile
    /// change against the client by how soon it follows the last one, counting the connect as
    /// one, and bans an address that scores too high; ten seconds scores nothing.</summary>
    internal static readonly TimeSpan ProfileAskDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long a started demodulator may stay silent before the session is given up as
    /// one that will not deliver.</summary>
    internal static readonly TimeSpan FirstAudioTimeout = TimeSpan.FromSeconds(30);

    private readonly OpenWebRxEndpoint _endpoint;
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly Action<string>? _journal;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly object _conversationGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly float[] _ring;
    private readonly int _factor;
    private readonly int _guardSamples;
    private readonly float _gain;
    private readonly int _outputRate;
    private readonly TaskCompletionSource<string?> _firstVerdict =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OpenWebRxTuning _tuning;
    private OpenWebRxConversation? _conversation;
    private WebSocket? _socket;
    private int _head;
    private int _tail;
    private int _count;
    private long _dropped;
    private long _droppedReported;
    private long _published;
    private bool _ended;
    private bool _sessionLive;
    private int _disposed;
    private Task? _pump;

    private OpenWebRxAudioInput(
        OpenWebRxEndpoint endpoint,
        OpenWebRxTuning tuning,
        Func<CancellationToken, Task<WebSocket>> connect,
        Action<string>? journal,
        TimeProvider time)
    {
        _endpoint = endpoint;
        _tuning = tuning;
        _connect = connect;
        _journal = journal;
        _time = time;
        _outputRate = tuning.OutputRate;
        _factor = tuning.OutputRate / OpenWebRxProtocol.AudioRate;
        _guardSamples = (int)((long)tuning.StartupGuardMs * OpenWebRxProtocol.AudioRate / 1000);
        _gain = tuning.Gain;

        // Eight seconds of slack, as on the UberSDR device: there so a GC pause or a busy box
        // costs latency rather than samples.
        _ring = new float[tuning.OutputRate * 8];
    }

    /// <summary>Raised once when the receiver has been unreachable for
    /// <see cref="UberSdrAudioInput.ReconnectGiveUpAfter"/>, with a sentence saying so.</summary>
    public event Action<string>? Lost;

    /// <summary>The receiver this streams from.</summary>
    public OpenWebRxEndpoint Endpoint => _endpoint;

    /// <summary>What the receiver said it is at start-up, e.g. <c>openwebrx v1.2.2</c>, or null
    /// when it refused us before saying.</summary>
    public string? Server { get; private set; }

    /// <summary>The receiver's name and location, as its operator set them, or null.</summary>
    public string? ReceiverDescription { get; private set; }

    /// <summary>Whether the receiver's audio is ADPCM, as it said at start-up.</summary>
    public bool Adpcm { get; private set; }

    /// <summary>The band the receiver was on at start-up, in words.</summary>
    public string? Band { get; private set; }

    /// <summary>Why the receiver refused us at start-up ("Too many clients"), or null when it
    /// did not. The station comes up anyway and the input keeps asking.</summary>
    public string? RefusedAtStartup { get; private set; }

    /// <inheritdoc />
    public int SampleRate => _outputRate;

    /// <summary>Samples dropped because nothing drained the buffer in time.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>
    /// True while a session is open and has delivered audio, so a stop is a hung stream; false
    /// between sessions, and while a new one is getting going, when quiet is deliberate or is
    /// this input's own to time out. See <see cref="UberSdrAudioInput.SessionLive"/>.
    /// </summary>
    public bool SessionLive => Volatile.Read(ref _sessionLive);

    /// <inheritdoc />
    public double DialHz
    {
        get
        {
            lock (_conversationGate)
            {
                return _tuning.FrequencyHz;
            }
        }
    }

    /// <summary>
    /// Connects to <paramref name="endpoint"/> and starts streaming. The first session's
    /// handshake, and the receiver saying where it is, happen here, so a wrong address, a server
    /// that is not OpenWebRX, a profile it does not offer or a dial its band does not reach is
    /// an error at start-up rather than a silence later.
    /// </summary>
    /// <exception cref="InvalidOperationException">The receiver cannot serve this tuning, or did
    /// not answer as an OpenWebRX receiver; the message is written for an operator.</exception>
    public static Task<OpenWebRxAudioInput> OpenAsync(
        OpenWebRxEndpoint endpoint,
        OpenWebRxTuning tuning,
        Action<string>? journal,
        CancellationToken cancellation,
        TimeProvider? time = null) =>
        OpenAsync(endpoint, tuning, ct => ConnectAsync(endpoint, ct), journal, cancellation, time);

    /// <summary>The same, over a socket from <paramref name="connect"/>; the tests hand it one end
    /// of a pair whose other end is a fake receiver.</summary>
    internal static async Task<OpenWebRxAudioInput> OpenAsync(
        OpenWebRxEndpoint endpoint,
        OpenWebRxTuning tuning,
        Func<CancellationToken, Task<WebSocket>> connect,
        Action<string>? journal,
        CancellationToken cancellation,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (tuning.OutputRate % OpenWebRxProtocol.AudioRate != 0)
        {
            throw new InvalidOperationException(
                $"an OpenWebRX receiver delivers {OpenWebRxProtocol.AudioRate} Hz audio, which this "
                + $"channel's {tuning.OutputRate} Hz DSP rate is not a whole multiple of. The 12 kHz "
                + "and 48 kHz mode families both are.");
        }

        WebSocket first;
        try
        {
            first = await connect(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
        {
            throw new InvalidOperationException(
                $"cannot reach the OpenWebRX receiver at {endpoint.WebSocketUri}: {e.Message}", e);
        }

        var input = new OpenWebRxAudioInput(endpoint, tuning, connect, journal, time ?? TimeProvider.System);
        try
        {
            input._pump = Task.Run(() => input.PumpAsync(first), CancellationToken.None);
            // A little longer than the session's own clock, so the session's more exact account of
            // what it was waiting for is the one that is given.
            Task timeout = Task.Delay(StartupTimeout + TimeSpan.FromSeconds(5), input._time, cancellation);
            if (await Task.WhenAny(input._firstVerdict.Task, timeout).ConfigureAwait(false) != input._firstVerdict.Task)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    $"{endpoint} did not say where it is listening within {(StartupTimeout + TimeSpan.FromSeconds(5)).TotalSeconds:F0} s "
                    + "of connecting. Is that an OpenWebRX receiver's address, as you would open it in a browser?");
            }

            if (await input._firstVerdict.Task.ConfigureAwait(false) is string refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            return input;
        }
        catch
        {
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
    public async Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        WebSocket? socket;
        OpenWebRxConversation? conversation;
        lock (_conversationGate)
        {
            _tuning = _tuning with { FrequencyHz = (long)Math.Round(dialHz) };
            socket = _socket;
            conversation = _conversation;
        }

        if (conversation is null)
        {
            return true; // between sessions: the next one opens on the new dial
        }

        bool reaches = true;
        await ReactAsync(socket, () =>
        {
            OpenWebRxReaction reaction = conversation.Retune(_tuning);
            reaches = !conversation.OutOfBand;
            return reaction;
        }, cancellation).ConfigureAwait(false);
        return reaches;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
            // Shutdown: the pump's own failures have already been journalled.
        }

        _firstVerdict.TrySetResult("the input was closed before the receiver answered");
        _stopping.Dispose();
    }

    private static async Task<WebSocket> ConnectAsync(OpenWebRxEndpoint endpoint, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", UserAgent);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(endpoint.WebSocketUri, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            socket.Dispose();
            throw new WebSocketException($"no answer from {endpoint.WebSocketUri} within 15 s");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>How a session ended, for the reconnect ladder and the journal.</summary>
    private enum SessionEnd
    {
        Closed,
        Refused,
        Faulted,
        Cancelled,
    }

    /// <summary>The reconnecting receive loop. Runs until disposed.</summary>
    private async Task PumpAsync(WebSocket first)
    {
        CancellationToken cancellation = _stopping.Token;
        WebSocket? socket = first;
        var policy = new UberSdrReconnectPolicy();
        long? downSince = null;
        bool firstSession = true;

        while (!cancellation.IsCancellationRequested)
        {
            if (socket is null)
            {
                try
                {
                    socket = await _connect(cancellation).ConfigureAwait(false);
                    downSince = null;
                    Write($"reconnected to {_endpoint}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    downSince ??= _time.GetTimestamp();
                    TimeSpan down = _time.GetElapsedTime(downSince.Value);
                    if (down > UberSdrAudioInput.ReconnectGiveUpAfter)
                    {
                        RaiseLost(
                            $"{_endpoint} has been unreachable for {down.TotalMinutes:F0} minutes "
                            + $"({e.Message}). Stopping so the service restarts and tries again.");
                        return;
                    }

                    TimeSpan wait = policy.After(UberSdrReconnectOutcome.Transient);
                    Write($"{_endpoint} unreachable ({e.Message}); retrying in {wait.TotalSeconds:F0}s");
                    if (!await WaitAsync(wait).ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }
            }

            long publishedBefore = Interlocked.Read(ref _published);
            long openedAt = _time.GetTimestamp();
            (SessionEnd end, string? why) = await RunSessionAsync(socket, firstSession, cancellation)
                .ConfigureAwait(false);
            firstSession = false;
            Volatile.Write(ref _sessionLive, false);
            lock (_conversationGate)
            {
                _socket = null;
            }

            socket.Dispose();
            socket = null;
            if (end == SessionEnd.Cancelled || cancellation.IsCancellationRequested)
            {
                break;
            }

            long delivered = Interlocked.Read(ref _published) - publishedBefore;
            bool healthy = delivered >= 10L * _outputRate;
            TimeSpan lasted = _time.GetElapsedTime(openedAt);
            TimeSpan pause;
            if (end == SessionEnd.Refused)
            {
                pause = policy.After(UberSdrReconnectOutcome.Refused);
                Write($"{_endpoint} is refusing us for now ({why}); asking again in {pause.TotalMinutes:F0} min");
            }
            else if (healthy && end == SessionEnd.Closed)
            {
                pause = policy.After(UberSdrReconnectOutcome.Healthy);
                Write($"the stream from {_endpoint} ended{(why is null ? "" : $" ({why})")}; reconnecting");
            }
            else
            {
                pause = policy.After(UberSdrReconnectOutcome.ShortSession);
                Write($"the session ended after {lasted.TotalMilliseconds:F0} ms with "
                    + $"{delivered * 1000 / _outputRate} ms of audio ({why ?? "the receiver closed the stream"}); "
                    + $"backing off {pause.TotalSeconds:F0}s before reconnecting to {_endpoint}");
            }

            if (!await WaitAsync(pause).ConfigureAwait(false))
            {
                break;
            }
        }

        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }

        async Task<bool> WaitAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, _time, cancellation).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>One session: the handshake, the receiver's account of itself, then audio until
    /// the socket closes.</summary>
    private async Task<(SessionEnd End, string? Why)> RunSessionAsync(
        WebSocket socket, bool firstSession, CancellationToken cancellation)
    {
        long sessionOpened = _time.GetTimestamp();
        OpenWebRxConversation conversation;
        lock (_conversationGate)
        {
            conversation = new OpenWebRxConversation(_tuning);
            _conversation = conversation;
            _socket = socket;
        }

        var decoder = new ImaAdpcmSyncDecoder();
        Upsampler? upsampler = _factor > 1 ? new Upsampler(_outputRate, _factor) : null;
        var accumulator = new ArrayBufferWriter<byte>(64 * 1024);
        byte[] receive = ArrayPool<byte>.Shared.Rent(64 * 1024);
        short[] pcm = [];
        float[] audio = [];
        float[] upsampled = [];
        int guardLeft = _guardSamples;
        bool adpcm = false;
        bool audioFlowing = false;
        bool awaitingAudio = false;
        int carry = -1;

        // The session's own clock: first for the receiver to say where it is, then for the
        // started demodulator to deliver. Running out closes the session, so a receiver that
        // never delivers costs a short session rather than a hung one. Stopped while the
        // receiver's band does not reach the dial, which is quiet on purpose.
        using var silent = new CancellationTokenSource(StartupTimeout, _time);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation, silent.Token);

        try
        {
            foreach (string message in OpenWebRxConversation.Opening())
            {
                await SendAsync(socket, message, session.Token).ConfigureAwait(false);
            }

            while (true)
            {
                accumulator.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receive, session.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string? refusal = conversation.Refusal;
                        if (firstSession && !_firstVerdict.Task.IsCompleted)
                        {
                            Decide(conversation, refusal is null
                                ? $"{_endpoint} closed the connection before saying where it is listening"
                                : null);
                        }

                        return refusal is null
                            ? (SessionEnd.Closed, conversation.Fault)
                            : (SessionEnd.Refused, refusal);
                    }

                    accumulator.Write(receive.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(accumulator.WrittenSpan);
                    bool mayAskProfile = _time.GetElapsedTime(sessionOpened) >= ProfileAskDelay;
                    await ReactAsync(socket, () => conversation.OnText(text, mayAskProfile), session.Token)
                        .ConfigureAwait(false);
                    if (conversation.Adpcm != adpcm)
                    {
                        adpcm = conversation.Adpcm;
                        decoder.Reset();
                    }

                    if (conversation.Refusal is string refusal)
                    {
                        if (firstSession)
                        {
                            Decide(conversation, null);
                        }

                        return (SessionEnd.Refused, refusal);
                    }

                    if (conversation.Fault is string fault)
                    {
                        if (firstSession && !_firstVerdict.Task.IsCompleted)
                        {
                            Decide(conversation, $"{_endpoint}: {fault}");
                        }

                        return (SessionEnd.Faulted, fault);
                    }

                    if (conversation.OutOfBand)
                    {
                        silent.CancelAfter(Timeout.InfiniteTimeSpan);
                        awaitingAudio = false;
                        Volatile.Write(ref _sessionLive, false);
                    }
                    else if (conversation.Listening && !audioFlowing && !awaitingAudio)
                    {
                        silent.CancelAfter(FirstAudioTimeout);
                        awaitingAudio = true;
                    }

                    if (firstSession && !_firstVerdict.Task.IsCompleted)
                    {
                        if (conversation.Listening)
                        {
                            Decide(conversation, null);
                        }
                        else if (conversation.OutOfBand && (_tuning.Profile is not null || conversation.Profiles is not null))
                        {
                            Decide(conversation, OutOfBandAtStartup(conversation));
                        }
                    }

                    continue;
                }

                ReadOnlySpan<byte> message = accumulator.WrittenSpan;
                if (message.Length < 2 || message[0] != OpenWebRxProtocol.AudioMessage)
                {
                    continue; // the waterfall, or wide-FM audio
                }

                bool listening;
                lock (_conversationGate)
                {
                    listening = conversation.Listening;
                }

                if (!listening)
                {
                    // Audio from before the demodulator was placed, or from a band that has
                    // moved away from the dial: not this station's signal, so not delivered, and
                    // quiet on purpose rather than a hung stream.
                    Volatile.Write(ref _sessionLive, false);
                    continue;
                }

                ReadOnlySpan<byte> payload = message[1..];
                int samples;
                if (adpcm)
                {
                    int most = ImaAdpcmSyncDecoder.MaxSamplesFor(payload.Length);
                    if (pcm.Length < most)
                    {
                        pcm = new short[most];
                    }

                    samples = decoder.Decode(payload, pcm);
                }
                else
                {
                    if (pcm.Length < (payload.Length / 2) + 1)
                    {
                        pcm = new short[(payload.Length / 2) + 1];
                    }

                    samples = 0;
                    int i = 0;
                    if (carry >= 0 && payload.Length > 0)
                    {
                        pcm[samples++] = (short)(carry | (payload[0] << 8));
                        carry = -1;
                        i = 1;
                    }

                    for (; i + 1 < payload.Length; i += 2)
                    {
                        pcm[samples++] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i, 2));
                    }

                    if (i < payload.Length)
                    {
                        carry = payload[i];
                    }
                }

                if (samples == 0)
                {
                    continue;
                }

                if (!audioFlowing)
                {
                    audioFlowing = true;
                    silent.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                if (!Volatile.Read(ref _sessionLive))
                {
                    Volatile.Write(ref _sessionLive, true);
                }

                int skip = Math.Min(guardLeft, samples);
                guardLeft -= skip;
                if (skip == samples)
                {
                    continue;
                }

                int count = samples - skip;
                if (audio.Length < count)
                {
                    audio = new float[count];
                }

                for (int s = 0; s < count; s++)
                {
                    audio[s] = pcm[skip + s] / 32768f;
                }

                if (upsampler is null)
                {
                    Publish(audio.AsSpan(0, count));
                }
                else
                {
                    int needed = upsampler.OutputLength(count);
                    if (upsampled.Length < needed)
                    {
                        upsampled = new float[needed];
                    }

                    upsampler.Process(audio.AsSpan(0, count), upsampled.AsSpan(0, needed));
                    Publish(upsampled.AsSpan(0, needed));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return (SessionEnd.Cancelled, null);
        }
        catch (OperationCanceledException)
        {
            string why = awaitingAudio
                ? $"no audio within {FirstAudioTimeout.TotalSeconds:F0} s of starting the demodulator"
                : $"the receiver did not say where it is listening within {StartupTimeout.TotalSeconds:F0} s";
            if (firstSession && !_firstVerdict.Task.IsCompleted)
            {
                Decide(conversation, $"{_endpoint}: {why}");
            }

            return (SessionEnd.Faulted, why);
        }
        catch (Exception e) when (e is WebSocketException or IOException or InvalidOperationException)
        {
            if (firstSession && !_firstVerdict.Task.IsCompleted)
            {
                Decide(conversation, $"the connection to {_endpoint} failed before it said where it is listening: {e.Message}");
            }

            return (SessionEnd.Closed, e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receive);
            lock (_conversationGate)
            {
                _conversation = null;
            }
        }
    }

    /// <summary>The start-up verdict: null to carry on, else why the station cannot start.</summary>
    private void Decide(OpenWebRxConversation conversation, string? refusal)
    {
        Server = conversation.Server;
        ReceiverDescription = conversation.Description;
        Adpcm = conversation.Adpcm;
        Band = conversation.CentreHz is null ? null : conversation.BandDescription;
        RefusedAtStartup = conversation.Refusal;
        _firstVerdict.TrySetResult(refusal);
    }

    private string OutOfBandAtStartup(OpenWebRxConversation conversation) =>
        $"{_endpoint} is on {conversation.BandDescription}, which does not reach the dial "
        + $"{conversation.Tuning.FrequencyHz / 1e6:F6} MHz"
        + (_tuning.Profile is null
            ? $". Choose the profile that does with \"openwebrx\": {{ \"profile\": \"...\" }}; it offers "
              + $"{conversation.ProfileList}."
            : $", even on the profile asked for. It offers {conversation.ProfileList}.");

    /// <summary>
    /// Works out what to send and sends it, holding the send gate across both, so that what the
    /// conversation records as sent reaches the server in the order it was decided.
    /// </summary>
    private async Task ReactAsync(
        WebSocket? socket, Func<OpenWebRxReaction> decide, CancellationToken cancellation)
    {
        await _sendGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            OpenWebRxReaction reaction;
            lock (_conversationGate)
            {
                reaction = decide();
            }

            foreach (string line in reaction.Lines)
            {
                Write(line);
            }

            if (socket is null)
            {
                return;
            }

            foreach (string message in reaction.Send)
            {
                try
                {
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellation)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return; // the session is going; the next one is opened on the same tuning
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendAsync(WebSocket socket, string message, CancellationToken cancellation)
    {
        await _sendGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void Publish(ReadOnlySpan<float> samples)
    {
        Interlocked.Add(ref _published, samples.Length);
        long dropped = 0;
        lock (_gate)
        {
            foreach (float sample in samples)
            {
                if (_count == _ring.Length)
                {
                    if (++_tail == _ring.Length)
                    {
                        _tail = 0;
                    }

                    _count--;
                    dropped++;
                }

                _ring[_head] = sample * _gain;
                if (++_head == _ring.Length)
                {
                    _head = 0;
                }

                _count++;
            }

            Monitor.PulseAll(_gate);
        }

        if (dropped == 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _dropped, dropped);
        if (total - Interlocked.Read(ref _droppedReported) >= _outputRate)
        {
            Interlocked.Exchange(ref _droppedReported, total);
            Write($"WARNING - dropped {total} samples ({total / (double)_outputRate:F1} s) because the "
                + "receive buffer filled. The machine is not keeping up with the stream.");
        }
    }

    private void Write(string sentence) => _journal?.Invoke($"openwebrx: {sentence}");

    private void RaiseLost(string reason)
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }

        Lost?.Invoke(reason);
    }
}
