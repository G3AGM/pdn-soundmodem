using System.Net.WebSockets;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.SdrConnect;

/// <summary>Settings for an <see cref="SdrConnectAudioInput"/>'s channel.</summary>
public sealed record SdrConnectAudioChannel
{
    /// <summary>The demodulator: <c>USB</c>, <c>LSB</c> or <c>NFM</c>.</summary>
    public required string Demodulator { get; init; }

    /// <summary>SSB: the passband's bottom, Hz from the dial.</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>SSB: the passband's top, Hz from the dial.</summary>
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>FM: the filter's whole width about the carrier, Hz.</summary>
    public double FmBandwidthHz { get; init; } = 12_500;

    /// <summary>Linear gain on the audio.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>The rate the station reads at, Hz; SDRconnect's 48 kHz is brought to it.</summary>
    public required int OutputRate { get; init; }

    /// <summary>
    /// The filter bandwidth SDRconnect is asked for. The document gives <c>filter_bandwidth</c>
    /// and <c>ssb_lowcut_frequency</c> but not how the two combine, so for SSB the top of the
    /// passband is asked for as the bandwidth: read as a width from the low cut, the passband is
    /// a little wider than wanted; read as the top edge, it is exact. Neither loses a modem.
    /// </summary>
    internal double FilterBandwidthHz =>
        Demodulator.Equals("NFM", StringComparison.OrdinalIgnoreCase) ? FmBandwidthHz : SsbHighHz;
}

/// <summary>
/// A station's receive audio from SDRconnect's own demodulator: the primary device's 48 kHz
/// stereo audio, its left channel, brought to the station's DSP rate.
/// </summary>
/// <remarks>
/// <para><b>SDRconnect's audio, not ours.</b> As with the <c>openwebrx:</c> device, the
/// demodulator and its filter are the far end's. What this input can do it does: it sets the
/// demodulator, the VFO and the filter, and turns off SDRconnect's AGC, noise reduction, squelch,
/// limiters and audio filters, so what arrives is the demodulator's output and nothing more. The
/// stream is about 1.5 Mbit/s, so this suits a Wi-Fi link that the IQ stream would not.</para>
/// <para><b>Where the radio is pointed.</b> The LO a little below the dial, as on the IQ stream,
/// and the VFO on the dial. <see cref="TuneAsync"/> moves only the VFO while the dial stays well
/// inside the band SDRconnect has, and the LO too when it does not.</para>
/// <para><b>Gaps</b> are counted as on the OpenWebRX device (<see cref="Discontinuities"/>): a
/// reconnect, a retune, samples lost on the way, or the buffer filling.</para>
/// <para>Original, from SDRplay's published SDRconnect WebSocket API document, version 1.0.3.</para>
/// </remarks>
public sealed class SdrConnectAudioInput : IAudioInput, IReceiverTuner, ISdrConnectSink, IDisposable
{
    private readonly SdrConnectClient _client;
    private readonly SdrConnectAudioChannel _channel;
    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly float[] _left;
    private readonly float[] _resampled;
    private readonly RationalResampler? _resampler;
    private int _head;
    private int _tail;
    private int _count;
    private bool _ended;
    private bool _everDelivered;
    private long _dropped;
    private long _holes;
    private long _lossAtRead;
    private int _disposed;

    private SdrConnectAudioInput(
        SdrConnectEndpoint endpoint, SdrConnectOptions options, SdrConnectAudioChannel channel,
        Func<CancellationToken, Task<WebSocket>>? connect)
    {
        _channel = channel;
        _client = new SdrConnectClient(endpoint, options, this, connect);
        _client.Lost += reason => Lost?.Invoke(reason);

        // Eight seconds of slack, as on the other web receivers: there so a GC pause or a busy box
        // costs latency rather than samples.
        _ring = new float[channel.OutputRate * 8];
        int frames = (options.ReceiveBufferBytes / SdrConnectProtocol.FrameBytes) + 1;
        _left = new float[frames];
        if (channel.OutputRate != SdrConnectProtocol.AudioRate)
        {
            _resampler = new RationalResampler(new RationalResamplerOptions
            {
                InputRate = SdrConnectProtocol.AudioRate,
                OutputRate = channel.OutputRate,
            });
            _resampled = new float[_resampler.MaxOutput(frames)];
        }
        else
        {
            _resampled = _left;
        }
    }

    /// <summary>SDRconnect's address.</summary>
    public SdrConnectEndpoint Endpoint => _client.Endpoint;

    /// <summary>The connection.</summary>
    internal SdrConnectClient Client => _client;

    /// <summary>What SDRconnect said at the connect, property by property.</summary>
    public IReadOnlyDictionary<string, string> Report => _client.Report;

    /// <inheritdoc />
    public int SampleRate => _channel.OutputRate;

    /// <inheritdoc />
    public double DialHz => _client.TargetVfoHz ?? 0;

    /// <summary>Whether the station should expect audio; see <see cref="SdrConnectClient.SessionLive"/>.</summary>
    public bool SessionLive => _client.SessionLive;

    /// <summary>Samples dropped because nothing drained the buffer in time.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>
    /// A running count that moves whenever the audio handed out has a hole in it: every sample
    /// dropped because the buffer filled, plus one for each reconnect, retune or loss reported
    /// before the audio that follows it. As of the last <see cref="Read"/> that delivered audio;
    /// the daemon marks the block after a move as following lost audio.
    /// </summary>
    public long Discontinuities => Interlocked.Read(ref _lossAtRead);

    /// <summary>Raised once when SDRconnect has delivered nothing for too long; the input has
    /// stopped.</summary>
    public event Action<string>? Lost;

    /// <summary>Connects to SDRconnect, sets its demodulator to the dial and starts the audio.</summary>
    /// <param name="endpoint">SDRconnect.</param>
    /// <param name="options">The connection's settings; the stream, LO, VFO, demodulator and
    /// filter are replaced from <paramref name="channel"/> and <paramref name="dialHz"/>.</param>
    /// <param name="channel">The demodulator, its passband and the output rate.</param>
    /// <param name="dialHz">The dial, Hz.</param>
    /// <param name="cancellation">Cancels the connect.</param>
    /// <exception cref="IOException">SDRconnect could not be reached.</exception>
    /// <exception cref="InvalidOperationException">SDRconnect answered but will not do.</exception>
    public static Task<SdrConnectAudioInput> OpenAsync(
        SdrConnectEndpoint endpoint, SdrConnectOptions options, SdrConnectAudioChannel channel,
        double dialHz, CancellationToken cancellation) =>
        OpenAsync(endpoint, options, channel, dialHz, connect: null, cancellation);

    /// <summary>The same, over sockets from <paramref name="connect"/>.</summary>
    internal static async Task<SdrConnectAudioInput> OpenAsync(
        SdrConnectEndpoint endpoint, SdrConnectOptions options, SdrConnectAudioChannel channel,
        double dialHz, Func<CancellationToken, Task<WebSocket>>? connect, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channel);
        bool fm = channel.Demodulator.Equals("NFM", StringComparison.OrdinalIgnoreCase);
        options = options with
        {
            Stream = SdrConnectStream.Audio,
            CentreFrequencyHz = CentreFor(dialHz, options.SampleRate),
            VfoFrequencyHz = dialHz,
            Demodulator = channel.Demodulator.ToUpperInvariant(),
            FilterBandwidthHz = channel.FilterBandwidthHz,
            SsbLowCutHz = fm ? null : channel.SsbLowHz,
        };

        var input = new SdrConnectAudioInput(endpoint, options, channel, connect);
        try
        {
            await input._client.OpenAsync(cancellation).ConfigureAwait(false);
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>Where the LO is put for <paramref name="dialHz"/>: below the dial, as on the IQ
    /// stream, by what suits <paramref name="sampleRate"/> (or the IQ stream's default).</summary>
    public static double CentreFor(double dialHz, int? sampleRate) =>
        dialHz - IqTuning.DcClearOffsetHz(sampleRate ?? SdrConnectIqInput.DefaultSampleRate);

    /// <inheritdoc />
    /// <remarks>Blocks briefly for samples and returns 0 if none arrive, which is what the
    /// daemon's receive loop expects of a device with nothing to say.</remarks>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            while (_count == 0)
            {
                if (_ended || !Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100)))
                {
                    return 0;
                }
            }

            Interlocked.Exchange(ref _lossAtRead, _dropped + _holes);
            int take = Math.Min(destination.Length, _count);
            int first = Math.Min(take, _ring.Length - _tail);
            _ring.AsSpan(_tail, first).CopyTo(destination);
            _ring.AsSpan(0, take - first).CopyTo(destination[first..]);
            _tail = (_tail + take) % _ring.Length;
            _count -= take;
            return take;
        }
    }

    /// <inheritdoc />
    /// <remarks>The VFO alone while the dial stays a filter's width inside the band SDRconnect
    /// has; the LO as well otherwise. True either way: SDRconnect can be put anywhere.</remarks>
    public Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        int rate = Math.Max(_client.ReportedRate, 1);
        double centre = _client.TargetCentreHz;
        double room = (rate / 2.0) - (2 * _channel.FilterBandwidthHz);
        double offset = dialHz - centre;
        bool inside = Math.Abs(offset) <= room && Math.Abs(offset) >= IqTuning.DcClearOffsetHz(rate) / 2;
        _client.TuneVfo(dialHz, inside ? null : CentreFor(dialHz, rate));
        return Task.FromResult(true);
    }

    /// <summary>Closes the connection.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.Dispose();
        End();
    }

    /// <inheritdoc />
    void ISdrConnectSink.Frames(ReadOnlySpan<byte> frames)
    {
        int count = frames.Length / SdrConnectProtocol.FrameBytes;
        SdrConnectProtocol.ConvertLeft(frames, _left);
        int produced = _resampler?.Process(_left.AsSpan(0, count), _resampled) ?? count;
        float gain = _channel.Gain;
        if (gain != 1f)
        {
            for (int k = 0; k < produced; k++)
            {
                _resampled[k] *= gain;
            }
        }

        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            int room = _ring.Length - _count;
            int keep = Math.Min(produced, room);
            if (keep < produced)
            {
                _dropped += produced - keep;
            }

            int first = Math.Min(keep, _ring.Length - _head);
            _resampled.AsSpan(0, first).CopyTo(_ring.AsSpan(_head));
            _resampled.AsSpan(first, keep - first).CopyTo(_ring);
            _head = (_head + keep) % _ring.Length;
            _count += keep;
            _everDelivered |= keep > 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <inheritdoc />
    void ISdrConnectSink.Gap(long frames) => Hole();

    /// <inheritdoc />
    void ISdrConnectSink.Tuning(double frequencyHz, int sampleRate) => Hole();

    /// <inheritdoc />
    void ISdrConnectSink.End() => End();

    /// <summary>What follows is not continuous with what came before; nothing is said before the
    /// first audio of all.</summary>
    private void Hole()
    {
        _resampler?.Reset();
        lock (_gate)
        {
            if (_everDelivered)
            {
                _holes++;
            }
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }
    }
}
