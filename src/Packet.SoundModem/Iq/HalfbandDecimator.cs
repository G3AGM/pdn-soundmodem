namespace Packet.SoundModem.Iq;

/// <summary>
/// Halves the rate of a complex (interleaved <c>I, Q</c>) stream with a halfband low-pass, keeping
/// a stated band about DC free of aliases. One stage of <see cref="CoarseDecimator"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a halfband.</b> A low-pass cut at a quarter of the input rate has every other
/// coefficient exactly zero (except the centre one), so a decimate-by-two costs about a quarter of
/// its length in multiply-accumulates per output. The early stages of a cascade from a wideband
/// SDR rate run where only a narrow band about DC has to survive, so their transition band is
/// enormous and they need only a handful of taps; the cost lands on the last stage, which runs at
/// the lowest rate.</para>
/// <para><b>What is protected.</b> Decimating by two folds the band from <c>out/2</c> to
/// <c>in/2</c> back over the band below it. Only content landing inside plus and minus
/// <c>protectedHz</c> matters, and that comes from the band starting at
/// <c>outputRate - protectedHz</c>, which is where the stopband starts. So the filter is flat to
/// <c>protectedHz</c> and attenuates by the stated figure from <c>outputRate - protectedHz</c>
/// up.</para>
/// <para><b>Design.</b> Textbook: a sinc cut at a quarter of the input rate under a symmetric
/// Kaiser window, sized by Kaiser's length formula (Oppenheim and Schafer, 7.6) with a few dB of
/// margin so the stated attenuation holds as measured. The length is chosen so that the end taps
/// fall on odd offsets, where the sinc is not zero. No ported code. Zero allocation after
/// construction: a doubled history keeps the newest window contiguous without a wrap.</para>
/// </remarks>
public sealed class HalfbandDecimator
{
    /// <summary>Kaiser's length formula is an estimate; designing a little beyond the stated
    /// attenuation keeps the measured figure on the right side of it.</summary>
    private const double DesignMarginDb = 3;

    private readonly float[] _oddTaps; // h at offsets 1, 3, 5, ... K (symmetric about the centre)
    private readonly float _centreTap;
    private readonly float[] _historyI;
    private readonly float[] _historyQ;
    private readonly int _length;
    private readonly int _half;
    private int _write;
    private bool _odd;

    /// <summary>Designs one decimate-by-two stage.</summary>
    /// <param name="inputRate">Complex sample rate in, Hz. Must be even.</param>
    /// <param name="protectedHz">The half-width of the band about DC kept free of aliases and
    /// flat, Hz. Must be below a quarter of <paramref name="inputRate"/>.</param>
    /// <param name="stopbandAttenuationDb">Attenuation of what would alias into the protected
    /// band, dB (20 to 110).</param>
    public HalfbandDecimator(int inputRate, double protectedHz, double stopbandAttenuationDb = 80)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 2);
        if ((inputRate & 1) != 0)
        {
            throw new ArgumentException($"inputRate ({inputRate}) must be even to halve", nameof(inputRate));
        }

        int outputRate = inputRate / 2;
        if (!(protectedHz > 0 && protectedHz < outputRate / 2.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(protectedHz), protectedHz, $"must be above 0 and below {outputRate / 2.0} Hz (a quarter of the input rate)");
        }

        if (!(stopbandAttenuationDb >= 20 && stopbandAttenuationDb <= 110))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopbandAttenuationDb), stopbandAttenuationDb, "must be from 20 to 110 dB");
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        ProtectedHz = protectedHz;
        StopbandAttenuationDb = stopbandAttenuationDb;

        // Kaiser: N - 1 = (A - 7.95) / (14.36 * transition / rate).
        double design = stopbandAttenuationDb + DesignMarginDb;
        double transition = (outputRate - (2 * protectedHz)) / inputRate;
        int k = (int)Math.Ceiling((design - 7.95) / (14.36 * transition) / 2.0);
        if (k < 1)
        {
            k = 1;
        }

        if ((k & 1) == 0)
        {
            k++; // end taps on an odd offset, where sin(pi n / 2) is not zero
        }

        _half = k;
        _length = (2 * k) + 1;
        double beta = KaiserBeta(design);
        double i0Beta = BesselI0(beta);

        // h[n] = sin(pi n / 2) / (pi n) * w[n], h[0] = 1/2; even n other than 0 are exactly zero.
        var odd = new double[(k + 1) / 2];
        double sum = 0.5;
        for (int j = 0; j < odd.Length; j++)
        {
            int n = (2 * j) + 1;
            double ratio = (double)n / k;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - (ratio * ratio)))) / i0Beta;
            odd[j] = Math.Sin(Math.PI * n / 2.0) / (Math.PI * n) * window;
            sum += 2 * odd[j];
        }

        // Unity gain at DC.
        _centreTap = (float)(0.5 / sum);
        _oddTaps = new float[odd.Length];
        for (int j = 0; j < odd.Length; j++)
        {
            _oddTaps[j] = (float)(odd[j] / sum);
        }

        _historyI = new float[2 * _length];
        _historyQ = new float[2 * _length];
    }

    /// <summary>Complex sample rate in, Hz.</summary>
    public int InputRate { get; }

    /// <summary>Complex sample rate out, Hz: half the input rate.</summary>
    public int OutputRate { get; }

    /// <summary>The half-width of the band kept flat and alias-free, Hz.</summary>
    public double ProtectedHz { get; }

    /// <summary>The stated attenuation of anything that would alias into the protected band, dB.</summary>
    public double StopbandAttenuationDb { get; }

    /// <summary>The filter's length in taps, zeros included.</summary>
    public int Length => _length;

    /// <summary>Multiply-accumulates per output, per component: the non-zero taps, using the
    /// symmetry.</summary>
    public int MacsPerOutput => _oddTaps.Length + 1;

    /// <summary>The filter's group delay, in input samples.</summary>
    public int DelayInputSamples => _half;

    /// <summary>Most frames <see cref="Process"/> can write for <paramref name="inputFrames"/>
    /// input frames.</summary>
    public int MaxOutput(int inputFrames) => (inputFrames / 2) + 1;

    /// <summary>Decimates a block of interleaved <c>I, Q</c>; returns the frames written to
    /// <paramref name="output"/> (twice that many floats). Blocks may be any length, and splitting
    /// a stream into blocks differently does not change the output.</summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        int frames = interleaved.Length / 2;
        int written = 0;
        int length = _length;
        int half = _half;
        float centre = _centreTap;
        float[] taps = _oddTaps;
        float[] hi = _historyI;
        float[] hq = _historyQ;

        for (int f = 0; f < frames; f++)
        {
            float i = interleaved[2 * f];
            float q = interleaved[(2 * f) + 1];
            hi[_write] = i;
            hi[_write + length] = i;
            hq[_write] = q;
            hq[_write + length] = q;
            if (++_write == length)
            {
                _write = 0;
            }

            // Keep one output in two: on the second sample of each pair.
            _odd = !_odd;
            if (_odd)
            {
                continue;
            }

            // The newest `length` samples, oldest first, are hi[_write .. _write + length).
            ReadOnlySpan<float> wi = hi.AsSpan(_write, length);
            ReadOnlySpan<float> wq = hq.AsSpan(_write, length);
            float accI = centre * wi[half];
            float accQ = centre * wq[half];
            for (int j = 0; j < taps.Length; j++)
            {
                int n = (2 * j) + 1;
                float t = taps[j];
                accI += t * (wi[half - n] + wi[half + n]);
                accQ += t * (wq[half - n] + wq[half + n]);
            }

            output[2 * written] = accI;
            output[(2 * written) + 1] = accQ;
            written++;
        }

        return written;
    }

    /// <summary>Forgets all history, as if newly constructed: for a discontinuity in the input.</summary>
    public void Reset()
    {
        Array.Clear(_historyI);
        Array.Clear(_historyQ);
        _write = 0;
        _odd = false;
    }

    /// <summary>Kaiser's beta for an attenuation in dB.</summary>
    private static double KaiserBeta(double attenuationDb) =>
        attenuationDb > 50 ? 0.1102 * (attenuationDb - 8.7)
        : attenuationDb >= 21 ? (0.5842 * Math.Pow(attenuationDb - 21, 0.4)) + (0.07886 * (attenuationDb - 21))
        : 0;

    /// <summary>The zeroth-order modified Bessel function of the first kind, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double quarterSquare = x * x / 4;
        for (int m = 1; m < 200; m++)
        {
            term *= quarterSquare / (m * (double)m);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}

/// <summary>
/// The front end's first stage: a cascade of <see cref="HalfbandDecimator"/>s that takes a
/// wideband IQ stream down to the lowest rate that still carries every channel, before any
/// per-channel work is done. Shared by every channel of one source.
/// </summary>
/// <remarks>
/// It halves while the rate allows (an even rate) and while the halved rate still leaves the
/// protected band a stopband of at least its own width (output rate at least three times the
/// protected half-width), so the last stage stays short. A 2.4 MS/s dongle with a channel 25 kHz
/// off centre comes down to 150 kS/s in four stages for about ten multiply-accumulates per
/// input sample in all; a 48 kS/s sound card, already narrow, passes through untouched.
/// </remarks>
public sealed class CoarseDecimator
{
    /// <summary>How many times the protected half-width the output rate must be, at least.</summary>
    public const double MinimumRateOverProtected = 3.0;

    private readonly HalfbandDecimator[] _stages;
    private readonly float[][] _scratch;

    /// <summary>Plans the cascade.</summary>
    /// <param name="inputRate">Complex sample rate in, Hz.</param>
    /// <param name="protectedHz">The half-width of the band about DC every channel lies in, Hz.</param>
    /// <param name="maxBlockFrames">The most frames one <see cref="Process"/> call will be handed.</param>
    /// <param name="stopbandAttenuationDb">Each stage's alias attenuation, dB.</param>
    public CoarseDecimator(int inputRate, double protectedHz, int maxBlockFrames, double stopbandAttenuationDb = 80)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockFrames, 1);
        if (!(protectedHz > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(protectedHz), protectedHz, "must be above 0");
        }

        var stages = new List<HalfbandDecimator>();
        int rate = inputRate;
        while ((rate & 1) == 0 && rate / 2 >= MinimumRateOverProtected * protectedHz)
        {
            stages.Add(new HalfbandDecimator(rate, protectedHz, stopbandAttenuationDb));
            rate /= 2;
        }

        _stages = [.. stages];
        _scratch = new float[_stages.Length][];
        int frames = maxBlockFrames;
        for (int s = 0; s < _stages.Length; s++)
        {
            frames = _stages[s].MaxOutput(frames);
            _scratch[s] = new float[2 * frames];
        }

        InputRate = inputRate;
        OutputRate = rate;
        ProtectedHz = _stages.Length > 0 ? protectedHz : inputRate / 2.0;
        MaxBlockFrames = maxBlockFrames;
    }

    /// <summary>Complex sample rate in, Hz.</summary>
    public int InputRate { get; }

    /// <summary>Complex sample rate out, Hz.</summary>
    public int OutputRate { get; }

    /// <summary>The half-width of the band that comes through flat and alias-free, Hz: the band
    /// asked for when there is at least one stage, else the whole input band.</summary>
    public double ProtectedHz { get; }

    /// <summary>The most frames one <see cref="Process"/> call takes.</summary>
    public int MaxBlockFrames { get; }

    /// <summary>The stages, first (fastest) to last.</summary>
    public IReadOnlyList<HalfbandDecimator> Stages => _stages;

    /// <summary>The overall rate reduction, a power of two.</summary>
    public int Factor => InputRate / OutputRate;

    /// <summary>Multiply-accumulates per input sample, both components, over every stage.</summary>
    public double MacsPerInputSample
    {
        get
        {
            double total = 0;
            double share = 1;
            foreach (HalfbandDecimator stage in _stages)
            {
                share /= 2;
                total += 2 * stage.MacsPerOutput * share;
            }

            return total;
        }
    }

    /// <summary>Most frames <see cref="Process"/> can produce from <paramref name="inputFrames"/>.</summary>
    public int MaxOutput(int inputFrames)
    {
        int frames = inputFrames;
        foreach (HalfbandDecimator stage in _stages)
        {
            frames = stage.MaxOutput(frames);
        }

        return frames;
    }

    /// <summary>
    /// Decimates a block. Returns the decimated block, which is <paramref name="interleaved"/>
    /// itself when there are no stages and otherwise lives in scratch owned by this instance,
    /// valid until the next call.
    /// </summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length / 2 > MaxBlockFrames)
        {
            throw new ArgumentException(
                $"block of {interleaved.Length / 2} frames is over the {MaxBlockFrames} planned for", nameof(interleaved));
        }

        ReadOnlySpan<float> current = interleaved;
        for (int s = 0; s < _stages.Length; s++)
        {
            int produced = _stages[s].Process(current, _scratch[s]);
            current = _scratch[s].AsSpan(0, 2 * produced);
        }

        return current;
    }

    /// <summary>Forgets all history in every stage.</summary>
    public void Reset()
    {
        foreach (HalfbandDecimator stage in _stages)
        {
            stage.Reset();
        }
    }
}
