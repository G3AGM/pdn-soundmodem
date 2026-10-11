using M0LTE.Dsp;

namespace Packet.SoundModem.Iq;

/// <summary>Knobs for <see cref="IqToFmAudioConverter"/>.</summary>
public sealed class IqToFmAudioOptions
{
    /// <summary>IQ sample rate of the capture (iq48 = 48000, the RSP1 rig = 96000).</summary>
    public int InputRate { get; init; } = 48000;

    /// <summary>Output audio rate - the mode's DSP rate. Must divide <see cref="InputRate"/>.</summary>
    public int OutputRate { get; init; } = 48000;

    /// <summary>Frequency, within the IQ baseband, of the FM carrier (the dial). Set this to
    /// (transmit dial − RX tune frequency) so the carrier sits at 0 Hz before discrimination; a
    /// residual offset appears as a DC term on the recovered audio (and as
    /// the FM deviation meter's mean if measured), which most modems'
    /// AC-coupling tolerates but which the on-air dial-correction nulls anyway.</summary>
    public double DialHz { get; init; }

    /// <summary>Half-bandwidth of an optional complex band-limiting pre-filter about the carrier,
    /// in Hz (0 disables it). It keeps only the FM signal's occupied band before discrimination,
    /// which on a live capture rejects out-of-band thermal noise and adjacent signals. The dry-run
    /// injects its noise as audio before modulation, so there is nothing out of band to reject and
    /// it defaults off - the mod/demod round trip then stays an exact identity.</summary>
    public double RfBandwidthHz { get; init; }

    /// <summary>Taps in the complex pre-filter, when <see cref="RfBandwidthHz"/> is set.</summary>
    public int RfFilterTaps { get; init; } = 257;

    /// <summary>
    /// When set (&gt;0), scale the recovered audio by <c>1/(2·DeviationHz)</c>, mapping a ±this-Hz
    /// instantaneous-frequency excursion to ±0.5. This is a <b>fixed, data-independent</b> scale -
    /// the right one for a ladder, where <see cref="NormalisePeak"/> would let a low-SNR rung's noise
    /// excursion shrink every rung. It also makes the raw output recoverable in Hz (audio × 2 ×
    /// DeviationHz), which is how a burst's achieved peak deviation is read back off it. Takes
    /// precedence over <see cref="NormalisePeak"/>.
    /// </summary>
    public double DeviationHz { get; init; }

    /// <summary>Peak-normalise the recovered audio to this magnitude (0 disables), when
    /// <see cref="DeviationHz"/> is not set. FM carries no amplitude and the demodulator equalises
    /// level, so this is cosmetic / clipping insurance; the signal's shape (hence the decode) is
    /// unchanged by it.</summary>
    public float NormalisePeak { get; init; } = 0.7f;
}

/// <summary>
/// Turns a captured IQ stream (complex baseband centred near the FM carrier) into the real audio a
/// mode's demodulator consumes, by <b>instantaneous-frequency (phase-derivative) discrimination</b>
/// - the FM counterpart of <see cref="M0LTE.Dsp.SsbDemodulator"/>, and the receive half of the FM OTA
/// chain. The FM-native modes (afsk1200, fsk9600, c4fsk, qpsk3600 audio-over-FM) reach the air as
/// frequency modulation, so the harness FM-demodulates the RSP1's IQ here and feeds the result to
/// the mode's own demodulator exactly as the SSB path feeds it single-sideband audio.
///
/// <para>Chain: shift by −DialHz (carrier → 0 Hz) → (optional) complex band-limit about the carrier
/// → discriminate <c>arg(z[n]·conj(z[n-1]))·fs/2π</c> to instantaneous frequency → anti-alias
/// low-pass and decimate to the output rate.</para>
///
/// <para><b>This shares no filter kernel with the transmit-side <c>FmModulator</c> (sm-ota),
/// deliberately</b>, the same discipline the SSB pair keeps: the discriminator is the analytic
/// inverse of the phase integrator, so the round trip recovers the modulating audio, but the tests
/// check the deviation the modulator produced against an independent excursion measurement and the
/// recovered audio against a decode, not merely against each other.</para>
///
/// <para>This is the whole-capture form, for a recording in hand; it allocates per call. A live
/// stream goes through <see cref="StreamingFmDiscriminator"/> inside <see cref="IqReceiver"/>,
/// which forms the same estimate block by block.</para>
/// </summary>
public sealed class IqToFmAudioConverter
{
    private readonly IqToFmAudioOptions _opt;

    /// <summary>Creates the converter; null takes every default.</summary>
    public IqToFmAudioConverter(IqToFmAudioOptions? options = null)
    {
        _opt = options ?? new IqToFmAudioOptions();
        if (_opt.InputRate % _opt.OutputRate != 0)
        {
            throw new ArgumentException(
                $"InputRate {_opt.InputRate} must be a multiple of OutputRate {_opt.OutputRate}");
        }
    }

    /// <summary>Convert interleaved 16-bit stereo IQ (I,Q,I,Q…) as read from a capture WAV.</summary>
    public float[] Convert(ReadOnlySpan<short> interleaved)
    {
        int n = interleaved.Length / 2;
        var i = new double[n];
        var q = new double[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = interleaved[2 * k] / 32768.0;
            q[k] = interleaved[(2 * k) + 1] / 32768.0;
        }

        return Convert(i, q);
    }

    /// <summary>Convert separate I/Q sample arrays (normalised, ~[−1,1]).</summary>
    public float[] Convert(ReadOnlySpan<double> iIn, ReadOnlySpan<double> qIn)
    {
        int n = iIn.Length;
        if (qIn.Length != n)
        {
            throw new ArgumentException("I and Q must be the same length");
        }

        // 1. NCO: shift by −DialHz so the carrier sits at 0 Hz (incremental phasor, periodically
        //    renormalised against drift - the same construction as the SSB converter).
        var re = new double[n];
        var im = new double[n];
        double dPhi = -2.0 * Math.PI * _opt.DialHz / _opt.InputRate;
        double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
        double pr = 1.0, pi = 0.0;
        for (int k = 0; k < n; k++)
        {
            re[k] = (iIn[k] * pr) - (qIn[k] * pi);
            im[k] = (iIn[k] * pi) + (qIn[k] * pr);
            double npr = (pr * wr) - (pi * wi);
            pi = (pr * wi) + (pi * wr);
            pr = npr;
            if ((k & 0x3FFF) == 0x3FFF)
            {
                double m = Math.Sqrt((pr * pr) + (pi * pi));
                pr /= m;
                pi /= m;
            }
        }

        // 2. Optional complex band-limit about the carrier (now at 0 Hz): a real low-pass applied to
        //    each of I and Q keeps only |f| < RfBandwidthHz, rejecting out-of-band capture noise.
        if (_opt.RfBandwidthHz > 0)
        {
            float[] proto = FilterDesign.LowPass(_opt.RfBandwidthHz, _opt.InputRate, _opt.RfFilterTaps);
            re = FirReal(re, proto);
            im = FirReal(im, proto);
        }

        // 3. Discriminate: instantaneous frequency = phase advanced over one sample × fs/2π. This is
        //    the modulating audio (scaled by the modulator's kf); its absolute scale is arbitrary and
        //    the demodulator equalises it, so it is carried through and only peak-normalised at the end.
        var freq = new double[n];
        double scale = _opt.InputRate / (2.0 * Math.PI);
        for (int k = 1; k < n; k++)
        {
            double a = re[k], b = im[k], c = re[k - 1], d = im[k - 1];
            freq[k] = Math.Atan2((b * c) - (a * d), (a * c) + (b * d)) * scale;
        }

        if (n > 1)
        {
            freq[0] = freq[1];
        }

        // 4. Anti-alias low-pass then decimate to the output rate. When the capture is already at the
        //    output rate (every 48 kHz mode dry-run) there is nothing to decimate and the raw
        //    discriminated audio is returned.
        int factor = _opt.InputRate / _opt.OutputRate;
        float[] outp;
        if (factor == 1)
        {
            outp = new float[n];
            for (int k = 0; k < n; k++)
            {
                outp[k] = (float)freq[k];
            }
        }
        else
        {
            var lp = new FirFilter(FilterDesign.LowPass(0.45 * _opt.OutputRate, _opt.InputRate, (8 * factor) + 1));
            int pre = 8 * factor;
            int outN = n / factor;
            outp = new float[outN];
            int oi = 0;
            for (int r = 0; r < n + pre; r++)
            {
                double x = r < n ? freq[r] : 0.0;
                float y = lp.Next((float)x);
                int k = r - pre;
                if (k >= 0 && k % factor == 0 && oi < outN)
                {
                    outp[oi++] = y;
                }
            }
        }

        if (_opt.DeviationHz > 0)
        {
            // Fixed scale: ±DeviationHz → ±0.5. Data-independent, so no rung can rescale another,
            // and the Hz excursion is recoverable as audio × 2 × DeviationHz.
            float g = (float)(1.0 / (2.0 * _opt.DeviationHz));
            for (int k = 0; k < outp.Length; k++)
            {
                outp[k] *= g;
            }
        }
        else if (_opt.NormalisePeak > 0)
        {
            float peak = 0f;
            for (int k = 0; k < outp.Length; k++)
            {
                peak = Math.Max(peak, Math.Abs(outp[k]));
            }

            if (peak > 1e-9f)
            {
                float g = _opt.NormalisePeak / peak;
                for (int k = 0; k < outp.Length; k++)
                {
                    outp[k] *= g;
                }
            }
        }

        return outp;
    }

    /// <summary>Linear-phase real FIR with group-delay compensation, so the filtered signal stays
    /// time-aligned - used for the optional complex pre-filter.</summary>
    private static double[] FirReal(double[] x, float[] taps)
    {
        int n = x.Length;
        int mid = (taps.Length - 1) / 2;
        var y = new double[n];
        for (int k = 0; k < n; k++)
        {
            double acc = 0;
            int baseIdx = k - mid;
            int mLo = Math.Max(0, -baseIdx);
            int mHi = Math.Min(taps.Length, n - baseIdx);
            for (int m = mLo; m < mHi; m++)
            {
                acc += taps[m] * x[baseIdx + m];
            }

            y[k] = acc;
        }

        return y;
    }
}
