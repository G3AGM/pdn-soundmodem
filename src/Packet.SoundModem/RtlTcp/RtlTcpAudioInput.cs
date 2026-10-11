using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.RtlTcp;

/// <summary>
/// A station's receive audio from an rtl_tcp server: an <see cref="RtlTcpIqSource"/> feeding the
/// IQ front end (<see cref="IqReceiver"/>) with one channel at the station's dial. The source is a
/// transport and nothing more; the filter and the demodulator are the front end's.
/// </summary>
/// <remarks>
/// <para><b>Where the dongle is pointed.</b> On a controlled server the dongle is tuned a little
/// below the dial (<see cref="IqTuning.DcClearOffsetHz"/>, 25 kHz at the usual rates) so its DC
/// spike stays out of the channel. On a read-only one it is wherever its operator put it, and the
/// dial has to lie inside the band that reaches.</para>
/// <para><b>Retuning.</b> <see cref="TuneAsync"/> moves the channel within the band the front end
/// keeps when it can; otherwise, on a controlled server, it retunes the dongle. On a read-only
/// server it can only move within the band, and answers false outside it.</para>
/// </remarks>
public sealed class RtlTcpAudioInput : IAudioInput, IReceiverTuner, IDisposable
{
    private readonly RtlTcpIqSource _source;
    private readonly IqReceiver _receiver;
    private readonly IqReceiverChannel _channel;
    private readonly double _dcClearHz;
    private int _disposed;

    private RtlTcpAudioInput(RtlTcpIqSource source, IqReceiver receiver, double dcClearHz)
    {
        _source = source;
        _receiver = receiver;
        _channel = receiver.Channels[0];
        _dcClearHz = dcClearHz;
        _source.Lost += reason => Lost?.Invoke(reason);
    }

    /// <summary>The source.</summary>
    public RtlTcpIqSource Source => _source;

    /// <summary>The front end.</summary>
    public IqReceiver Receiver => _receiver;

    /// <summary>The one channel.</summary>
    public IqReceiverChannel Channel => _channel;

    /// <inheritdoc />
    public int SampleRate => _channel.SampleRate;

    /// <inheritdoc />
    public double DialHz => _channel.DialHz;

    /// <summary>Whether the station should expect audio; see <see cref="RtlTcpIqSource.SessionLive"/>.</summary>
    public bool SessionLive => _source.SessionLive;

    /// <summary>The channel's running count of places the audio is not continuous.</summary>
    public long Discontinuities => _channel.Discontinuities;

    /// <summary>Raised once when the server has been unreachable for too long; the input has
    /// stopped.</summary>
    public event Action<string>? Lost;

    /// <summary>
    /// Where a controlled dongle is tuned for <paramref name="dialHz"/> at
    /// <paramref name="sampleRate"/>: below the dial by <see cref="IqTuning.DcClearOffsetHz"/>.
    /// </summary>
    public static double CentreFor(double dialHz, int sampleRate) => dialHz - IqTuning.DcClearOffsetHz(sampleRate);

    /// <summary>
    /// Connects to the server and starts the front end, with one channel at
    /// <paramref name="dialHz"/>.
    /// </summary>
    /// <param name="endpoint">The server.</param>
    /// <param name="source">The source's settings. On a controlled server
    /// <see cref="RtlTcpSourceOptions.CentreFrequencyHz"/> is replaced by
    /// <see cref="CentreFor"/>; on a read-only one it is the profile's centre.</param>
    /// <param name="channel">The channel's demodulation and filter; its offset is replaced by
    /// where <paramref name="dialHz"/> falls.</param>
    /// <param name="frontEnd">The front end's settings.</param>
    /// <param name="dialHz">The dial, Hz.</param>
    /// <param name="cancellation">Cancels the connect.</param>
    /// <exception cref="ArgumentException">The dial is outside the band a read-only server's
    /// profile reaches, or the channel cannot run as specified.</exception>
    /// <exception cref="IOException">The server could not be reached or closed at once.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The server could not be reached.</exception>
    public static async Task<RtlTcpAudioInput> OpenAsync(
        RtlTcpEndpoint endpoint, RtlTcpSourceOptions source, IqChannelSpec channel,
        IqReceiverOptions frontEnd, double dialHz, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(frontEnd);
        double dcClear = IqTuning.DcClearOffsetHz(source.SampleRate);
        if (!source.ReadOnly)
        {
            source = source with { CentreFrequencyHz = CentreFor(dialHz, source.SampleRate) };
        }

        IqChannelSpec placed = channel with { OffsetHz = dialHz - source.CentreFrequencyHz };

        // The band is checked before anything is opened, so a dial a read-only profile does not
        // reach is said as that and not as a network error.
        var rtl = new RtlTcpIqSource(endpoint, source);
        IqReceiver? receiver = null;
        try
        {
            receiver = new IqReceiver(rtl, [placed], frontEnd);
            await rtl.OpenAsync(cancellation).ConfigureAwait(false);
            var input = new RtlTcpAudioInput(rtl, receiver, dcClear);
            receiver.Start();
            return input;
        }
        catch
        {
            rtl.Dispose();
            receiver?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination) => _channel.Read(destination);

    /// <inheritdoc />
    /// <remarks>
    /// Within the band the front end keeps, and clear of the DC spike, the channel moves and the
    /// dongle does not. Otherwise a controlled dongle is retuned to put the dial where it was put
    /// at the start, and the answer is true; a read-only one cannot be, and the answer is the
    /// channel's.
    /// </remarks>
    public async Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        bool reaches = await _channel.TuneAsync(dialHz, cancellation).ConfigureAwait(false);
        if (_source.ReadOnly)
        {
            return reaches;
        }

        // The channel's answer is against where the source is now; with a retune of the source
        // still on its way that is not where it will be, so the source is retuned again.
        double target = _source.TargetCentreHz;
        double offset = dialHz - target;
        if (reaches && target == _source.CentreFrequencyHz && Math.Abs(offset) >= _dcClearHz / 2)
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
