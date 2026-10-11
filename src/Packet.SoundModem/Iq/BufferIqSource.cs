using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Iq;

/// <summary>
/// An <see cref="IIqSource"/> that replays a fixed in-memory buffer of interleaved
/// <c>I, Q</c> floats, block by block - the offline/test source (and a natural fit for
/// replaying a recorded IQ capture). Not for hot-path production streaming; a real transport
/// implements <see cref="IIqSource"/> directly.
/// </summary>
public sealed class BufferIqSource : IIqSource
{
    private readonly float[] _iq;
    private int _offset;

    /// <summary>Creates a source over <paramref name="interleaved"/> (<c>I, Q, I, Q, …</c>).</summary>
    /// <param name="interleaved">Interleaved complex samples; length must be even.</param>
    /// <param name="sampleRate">Complex sample rate in Hz.</param>
    /// <param name="centreFrequencyHz">RF centre the IQ is tuned to (default 0 = unspecified).</param>
    public BufferIqSource(float[] interleaved, int sampleRate, double centreFrequencyHz = 0)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if ((interleaved.Length & 1) != 0)
        {
            throw new ArgumentException("interleaved IQ length must be even (I,Q pairs)", nameof(interleaved));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _iq = interleaved;
        SampleRate = sampleRate;
        CentreFrequencyHz = centreFrequencyHz;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public double CentreFrequencyHz { get; }

    /// <inheritdoc />
    /// <remarks>Always 0: a buffer has one tuning.</remarks>
    public long TuningGeneration => 0;

    /// <inheritdoc />
    /// <remarks>Always 0: a buffer cannot lose samples.</remarks>
    public long SamplesLost => 0;

    /// <inheritdoc />
    public int Read(Span<float> interleaved)
    {
        int remaining = _iq.Length - _offset;
        if (remaining <= 0)
        {
            return 0;
        }

        // Keep the returned count even (whole I,Q pairs).
        int take = Math.Min(interleaved.Length & ~1, remaining);
        _iq.AsSpan(_offset, take).CopyTo(interleaved);
        _offset += take;
        return take;
    }
}
