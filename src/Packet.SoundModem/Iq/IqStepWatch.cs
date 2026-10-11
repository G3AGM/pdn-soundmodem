namespace Packet.SoundModem.Iq;

/// <summary>
/// Notices when an IQ stream's character changes suddenly and stays changed: its level, its
/// spectral tilt, or its DC offset. A hint, for a read-only source, that the server may have
/// switched to another band at the same sample rate, which nothing in the stream can say.
/// </summary>
/// <remarks>
/// <para><b>What it measures.</b> Each window (one second by default) gives three numbers: the
/// mean power in dB; the lag-one autocorrelation over the power, a complex number whose angle is
/// the spectrum's centre of mass and whose size is how far from flat it is; and the mean (the DC
/// offset) over the RMS. A direct-conversion tuner's DC offset and its noise shape move with its
/// frequency and gain, and a different band brings a different noise floor, so a profile switch
/// moves at least one of these. A packet burst or an operator keying up nearby can move them too,
/// which is why the change has to hold for several windows, consistent with itself, before it is
/// said; and why it is only ever said, never acted on.</para>
/// <para><b>Not a detector.</b> A switch to a band that looks the same is not seen, and a long
/// strong signal arriving can be taken for a switch. The source reports it in the journal for an
/// operator to look into; it does not mute the station or restart its filters, because a false
/// alarm there would cost the frame being received.</para>
/// <para>Original. Allocation-free.</para>
/// </remarks>
public sealed class IqStepWatch
{
    private const int WarmUpWindows = 3;
    private const double Adapt = 0.1;

    private readonly int _windowSamples;
    private readonly double _levelStepDb;
    private readonly double _shapeStep;
    private readonly int _holdWindows;

    // The window being measured.
    private long _n;
    private double _sumP;
    private double _sumI;
    private double _sumQ;
    private double _r1Re;
    private double _r1Im;
    private float _prevI;
    private float _prevQ;
    private bool _havePrev;

    // The reference: what the stream has looked like.
    private int _windows;
    private Features _reference;

    // A change seen and not yet held long enough.
    private Features _candidate;
    private int _candidateRun;

    /// <param name="sampleRate">The stream's rate, complex samples a second.</param>
    /// <param name="windowSeconds">Each measurement's length.</param>
    /// <param name="levelStepDb">A level change that counts, dB.</param>
    /// <param name="shapeStep">A change in the tilt or the DC offset that counts, as a distance
    /// between the normalised complex numbers (each at most 1).</param>
    /// <param name="holdWindows">How many consecutive windows a change has to hold.</param>
    public IqStepWatch(int sampleRate, double windowSeconds = 1, double levelStepDb = 6, double shapeStep = 0.25, int holdWindows = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(holdWindows, 1);
        _windowSamples = Math.Max(1, (int)(sampleRate * windowSeconds));
        _levelStepDb = levelStepDb;
        _shapeStep = shapeStep;
        _holdWindows = holdWindows;
    }

    /// <summary>Steps seen since construction.</summary>
    public long Steps { get; private set; }

    /// <summary>The last step, in words, or null.</summary>
    public string? LastStep { get; private set; }

    /// <summary>Forgets everything: the stream is known to have changed (a retune this client
    /// made, a reconnect), so what follows is the new reference.</summary>
    public void Restart()
    {
        _windows = 0;
        _candidateRun = 0;
        ResetWindow();
        _havePrev = false;
    }

    /// <summary>Takes a block of interleaved <c>I, Q</c>; true when it completes a step.</summary>
    public bool Feed(ReadOnlySpan<float> iq)
    {
        bool stepped = false;
        int pairs = iq.Length / 2;
        for (int k = 0; k < pairs; k++)
        {
            float i = iq[2 * k];
            float q = iq[(2 * k) + 1];
            _sumP += (i * i) + (q * q);
            _sumI += i;
            _sumQ += q;
            if (_havePrev)
            {
                // z[n] conj(z[n-1])
                _r1Re += (i * _prevI) + (q * _prevQ);
                _r1Im += (q * _prevI) - (i * _prevQ);
            }

            _prevI = i;
            _prevQ = q;
            _havePrev = true;
            if (++_n >= _windowSamples)
            {
                stepped |= CloseWindow();
            }
        }

        return stepped;
    }

    private bool CloseWindow()
    {
        double n = _n;
        double p = _sumP / n;
        var window = new Features
        {
            LevelDb = 10 * Math.Log10(p + 1e-20),
            TiltRe = p > 0 ? _r1Re / (n * p) : 0,
            TiltIm = p > 0 ? _r1Im / (n * p) : 0,
            DcRe = p > 0 ? _sumI / n / Math.Sqrt(p) : 0,
            DcIm = p > 0 ? _sumQ / n / Math.Sqrt(p) : 0,
        };
        ResetWindow();

        if (_windows < WarmUpWindows)
        {
            _reference = _windows == 0 ? window : Features.Mix(_reference, window, 1.0 / (_windows + 1));
            _windows++;
            return false;
        }

        if (!Differs(window, _reference, 1))
        {
            _reference = Features.Mix(_reference, window, Adapt);
            _candidateRun = 0;
            return false;
        }

        // Off the reference. The same new state as last time, or a fresh one?
        if (_candidateRun > 0 && !Differs(window, _candidate, 0.5))
        {
            _candidate = Features.Mix(_candidate, window, 1.0 / (_candidateRun + 1));
            _candidateRun++;
        }
        else
        {
            _candidate = window;
            _candidateRun = 1;
        }

        if (_candidateRun < _holdWindows)
        {
            return false;
        }

        Steps++;
        LastStep = $"level {_candidate.LevelDb - _reference.LevelDb:+0.0;-0.0} dB, "
            + $"tilt moved {Distance(_candidate.TiltRe - _reference.TiltRe, _candidate.TiltIm - _reference.TiltIm):F2}, "
            + $"DC moved {Distance(_candidate.DcRe - _reference.DcRe, _candidate.DcIm - _reference.DcIm):F2}";
        _reference = _candidate;
        _candidateRun = 0;
        return true;
    }

    private bool Differs(in Features a, in Features b, double scale) =>
        Math.Abs(a.LevelDb - b.LevelDb) > _levelStepDb * scale
        || Distance(a.TiltRe - b.TiltRe, a.TiltIm - b.TiltIm) > _shapeStep * scale
        || Distance(a.DcRe - b.DcRe, a.DcIm - b.DcIm) > _shapeStep * scale;

    private static double Distance(double re, double im) => Math.Sqrt((re * re) + (im * im));

    private void ResetWindow()
    {
        _n = 0;
        _sumP = 0;
        _sumI = 0;
        _sumQ = 0;
        _r1Re = 0;
        _r1Im = 0;
    }

    private struct Features
    {
        public double LevelDb;
        public double TiltRe;
        public double TiltIm;
        public double DcRe;
        public double DcIm;

        public static Features Mix(in Features old, in Features fresh, double weight) => new()
        {
            LevelDb = old.LevelDb + (weight * (fresh.LevelDb - old.LevelDb)),
            TiltRe = old.TiltRe + (weight * (fresh.TiltRe - old.TiltRe)),
            TiltIm = old.TiltIm + (weight * (fresh.TiltIm - old.TiltIm)),
            DcRe = old.DcRe + (weight * (fresh.DcRe - old.DcRe)),
            DcIm = old.DcIm + (weight * (fresh.DcIm - old.DcIm)),
        };
    }
}
