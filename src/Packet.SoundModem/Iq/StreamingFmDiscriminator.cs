namespace Packet.SoundModem.Iq;

/// <summary>
/// The block-at-a-time FM discriminator: complex baseband with the carrier at 0 Hz in,
/// instantaneous frequency out, one output per input, with nothing else done to it. No
/// de-emphasis and no audio filter: what a data port should hand a 9600 baud modem and what a
/// rig's speaker or data port rarely does.
/// </summary>
/// <remarks>
/// <para>The same estimator as <see cref="IqToFmAudioConverter"/>, the batch form the FM ladders
/// were built on: <c>f[n] = arg(z[n] * conj(z[n-1])) * fs / 2 pi</c>, the phase advanced over one
/// sample. It is independent of the signal's amplitude, which is why an FM receive path needs no
/// AGC. A test holds the two to the same output.</para>
/// <para><b>Scale.</b> With a deviation set, plus and minus that many Hz come out as plus and
/// minus 0.5: a fixed, data-independent scale, so no burst can change the level another is heard
/// at, and the deviation is recoverable as <c>output * 2 * deviation</c>. With none set, the
/// output is in Hz.</para>
/// <para>Textbook (the polar discriminator); no ported code. Zero allocation.</para>
/// </remarks>
public sealed class StreamingFmDiscriminator
{
    private readonly double _scale;
    private float _previousI;
    private float _previousQ;

    /// <summary>Creates a discriminator.</summary>
    /// <param name="sampleRate">Complex sample rate in, Hz.</param>
    /// <param name="deviationHz">The peak deviation that maps to plus and minus 0.5, Hz, or 0 for
    /// output in Hz.</param>
    public StreamingFmDiscriminator(int sampleRate, double deviationHz = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(deviationHz);
        SampleRate = sampleRate;
        DeviationHz = deviationHz;
        double hzPerRadian = sampleRate / (2.0 * Math.PI);
        _scale = deviationHz > 0 ? hzPerRadian / (2.0 * deviationHz) : hzPerRadian;
    }

    /// <summary>Complex sample rate in, and real rate out, Hz.</summary>
    public int SampleRate { get; }

    /// <summary>The deviation that maps to plus and minus 0.5, Hz; 0 for output in Hz.</summary>
    public double DeviationHz { get; }

    /// <summary>Discriminates a block of interleaved <c>I, Q</c>; returns the samples written to
    /// <paramref name="output"/>, one per input frame.</summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        int frames = interleaved.Length / 2;
        float pi = _previousI;
        float pq = _previousQ;
        for (int k = 0; k < frames; k++)
        {
            float a = interleaved[2 * k];
            float b = interleaved[(2 * k) + 1];

            // z[n] * conj(z[n-1]) = (a + jb)(c - jd).
            double re = ((double)a * pi) + ((double)b * pq);
            double im = ((double)b * pi) - ((double)a * pq);
            output[k] = (float)(Math.Atan2(im, re) * _scale);
            pi = a;
            pq = b;
        }

        _previousI = pi;
        _previousQ = pq;
        return frames;
    }

    /// <summary>Forgets the previous sample: the next output is taken against silence, which reads
    /// as 0 Hz.</summary>
    public void Reset()
    {
        _previousI = 0;
        _previousQ = 0;
    }
}
