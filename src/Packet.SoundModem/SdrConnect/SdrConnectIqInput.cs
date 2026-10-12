using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.SdrConnect;

/// <summary>
/// A station's receive audio from SDRconnect's IQ stream: an <see cref="SdrConnectIqSource"/>
/// feeding the IQ front end (<see cref="IqReceiver"/>) with one channel at the station's dial,
/// so the filter and the demodulator are this station's, as on every IQ device.
/// </summary>
/// <remarks>
/// <para><b>Where the radio is pointed.</b> A little below the dial
/// (<see cref="IqTuning.DcClearOffsetHz"/>, 25 kHz at SDRconnect's rates), so the LO's DC spike
/// stays out of the channel. The channel is then placed against the LO SDRconnect reports, not
/// the one asked for.</para>
/// <para><b>Retuning.</b> <see cref="TuneAsync"/> moves the channel within the band the front end
/// keeps when it can; otherwise it retunes the radio.</para>
/// </remarks>
public sealed class SdrConnectIqInput : IAudioInput, IReceiverTuner, IDisposable
{
    /// <summary>The rate asked for unless told otherwise: the lowest an RSP runs at without
    /// SDRconnect decimating, and 64 Mbit/s on the wire.</summary>
    public const int DefaultSampleRate = 2_000_000;

    private readonly SdrConnectIqSource _source;
    private readonly IqReceiver _receiver;
    private readonly IqReceiverChannel _channel;
    private int _disposed;

    private SdrConnectIqInput(SdrConnectIqSource source, IqReceiver receiver)
    {
        _source = source;
        _receiver = receiver;
        _channel = receiver.Channels[0];
        _source.Lost += reason => Lost?.Invoke(reason);
    }

    /// <summary>The source.</summary>
    public SdrConnectIqSource Source => _source;

    /// <summary>The front end.</summary>
    public IqReceiver Receiver => _receiver;

    /// <inheritdoc />
    public int SampleRate => _channel.SampleRate;

    /// <inheritdoc />
    public double DialHz => _channel.DialHz;

    /// <summary>Whether the station should expect audio; see <see cref="SdrConnectClient.SessionLive"/>.</summary>
    public bool SessionLive => _source.SessionLive;

    /// <summary>The channel's running count of places the audio is not continuous.</summary>
    public long Discontinuities => _channel.Discontinuities;

    /// <summary>Raised once when SDRconnect has delivered nothing for too long; the input has
    /// stopped.</summary>
    public event Action<string>? Lost;

    /// <summary>Where the LO is put for <paramref name="dialHz"/> at <paramref name="sampleRate"/>:
    /// below the dial by <see cref="IqTuning.DcClearOffsetHz"/>.</summary>
    public static double CentreFor(double dialHz, int sampleRate) => dialHz - IqTuning.DcClearOffsetHz(sampleRate);

    /// <summary>Connects to SDRconnect and starts the front end, with one channel at
    /// <paramref name="dialHz"/>.</summary>
    /// <param name="endpoint">SDRconnect.</param>
    /// <param name="source">The connection's settings; the LO is replaced by
    /// <see cref="CentreFor"/>, and the rate defaults to <see cref="DefaultSampleRate"/>.</param>
    /// <param name="channel">The channel's demodulation and filter; its offset is replaced by
    /// where <paramref name="dialHz"/> falls against the LO SDRconnect reports.</param>
    /// <param name="frontEnd">The front end's settings.</param>
    /// <param name="dialHz">The dial, Hz.</param>
    /// <param name="cancellation">Cancels the connect.</param>
    /// <exception cref="IOException">SDRconnect could not be reached.</exception>
    /// <exception cref="InvalidOperationException">SDRconnect answered but will not do.</exception>
    /// <exception cref="ArgumentException">The channel cannot run at the rate and LO SDRconnect
    /// reported.</exception>
    public static Task<SdrConnectIqInput> OpenAsync(
        SdrConnectEndpoint endpoint, SdrConnectOptions source, IqChannelSpec channel,
        IqReceiverOptions frontEnd, double dialHz, CancellationToken cancellation) =>
        OpenAsync(endpoint, source, channel, frontEnd, dialHz, connect: null, cancellation);

    /// <summary>The same, over sockets from <paramref name="connect"/>.</summary>
    internal static async Task<SdrConnectIqInput> OpenAsync(
        SdrConnectEndpoint endpoint, SdrConnectOptions source, IqChannelSpec channel,
        IqReceiverOptions frontEnd, double dialHz, Func<CancellationToken, Task<WebSocket>>? connect,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(frontEnd);
        int rate = source.SampleRate ?? DefaultSampleRate;
        source = source with { SampleRate = rate, CentreFrequencyHz = CentreFor(dialHz, rate), Stream = SdrConnectStream.Iq };

        var iq = new SdrConnectIqSource(endpoint, source, connect);
        IqReceiver? receiver = null;
        try
        {
            await iq.OpenAsync(cancellation).ConfigureAwait(false);
            IqChannelSpec placed = channel with { OffsetHz = dialHz - iq.CentreFrequencyHz };
            receiver = new IqReceiver(iq, [placed], frontEnd);
            var input = new SdrConnectIqInput(iq, receiver);
            receiver.Start();
            return input;
        }
        catch
        {
            iq.Dispose();
            receiver?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination) => _channel.Read(destination);

    /// <inheritdoc />
    /// <remarks>
    /// Within the band the front end keeps, and clear of the DC spike, the channel moves and the
    /// radio does not. Otherwise the radio is retuned to put the dial where it was put at the
    /// start, and the answer is true.
    /// </remarks>
    public async Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        bool reaches = await _channel.TuneAsync(dialHz, cancellation).ConfigureAwait(false);
        double target = _source.TargetCentreHz;
        double offset = dialHz - target;
        double dcClear = IqTuning.DcClearOffsetHz(_source.SampleRate);
        if (reaches && Math.Abs(target - _source.CentreFrequencyHz) < 1 && Math.Abs(offset) >= dcClear / 2)
        {
            return true;
        }

        _source.Tune(CentreFor(dialHz, _source.SampleRate));
        return true;
    }

    /// <summary>Closes the connection and the front end.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Dispose();
        _receiver.Dispose();
    }
}
