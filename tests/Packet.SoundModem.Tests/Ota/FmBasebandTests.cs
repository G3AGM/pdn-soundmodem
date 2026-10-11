using AwesomeAssertions;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Iq;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The FM baseband DSP: the software modulator sets a peak deviation, the deviation meter measures
/// it back, and the discriminator recovers the modulating audio - the three primitives the FM OTA
/// path rests on, each checked against an independent expectation rather than only against each other.
/// </summary>
public class FmBasebandTests
{
    private const int Rate = 48000;

    [Theory]
    [InlineData(1200)]
    [InlineData(2400)]
    [InlineData(3000)]
    [InlineData(5000)]
    public void Modulating_a_full_scale_tone_at_a_deviation_measures_that_deviation(double devHz)
    {
        // The round-trip the calibration depends on: a full-scale (±1) tone modulated at a stated
        // peak deviation must measure back at that deviation, so the on-air tool can be trusted to
        // set the drive to a mode's target.
        float[] audio = Sine(1000, amplitude: 1.0, seconds: 0.25, Rate);
        float[] iq = new FmModulator(new FmModulatorOptions
        {
            InputRate = Rate,
            OutputRate = Rate,
            PeakDeviationHz = devHz,
            ReferenceAmplitude = 1.0,
            Amplitude = 1.0,
        }).Modulate(audio);

        FmDeviation dev = FmDeviationMeter.Measure(iq, Rate);

        dev.PeakHz.Should().BeApproximately(devHz, devHz * 0.02,
            "a full-scale tone at reference amplitude deviates the carrier by exactly the target");
        dev.MeanHz.Should().BeApproximately(0, 15, "balanced modulation has no residual carrier offset");
    }

    [Fact]
    public void The_deviation_scales_with_the_audio_drive()
    {
        // Deviation is linear in drive: half the amplitude is half the deviation. This is exactly the
        // knob the on-air calibration turns (audio-amplitude) to hit the target.
        const double devHz = 4000;
        float[] full = Sine(1000, amplitude: 1.0, seconds: 0.2, Rate);
        float[] half = Sine(1000, amplitude: 0.5, seconds: 0.2, Rate);

        var mod = new FmModulator(new FmModulatorOptions
        {
            InputRate = Rate,
            OutputRate = Rate,
            PeakDeviationHz = devHz,
            ReferenceAmplitude = 1.0,
            Amplitude = 1.0,
        });

        FmDeviationMeter.Measure(mod.Modulate(full), Rate).PeakHz.Should().BeApproximately(devHz, devHz * 0.02);
        FmDeviationMeter.Measure(mod.Modulate(half), Rate).PeakHz
            .Should().BeApproximately(devHz / 2, devHz * 0.02, "half the drive is half the deviation");
    }

    [Fact]
    public void Upsampling_modulation_preserves_the_target_deviation()
    {
        // A 12 kHz-DSP mode (afsk1200) is upsampled to the baseband rate inside the modulator; the
        // deviation must survive the resample. Measured over the steady middle, away from the
        // upsampler's edge transient.
        const double devHz = 3000;
        float[] audio = Sine(1000, amplitude: 1.0, seconds: 0.25, 12000);
        float[] iq = new FmModulator(new FmModulatorOptions
        {
            InputRate = 12000,
            OutputRate = 48000,
            PeakDeviationHz = devHz,
            ReferenceAmplitude = 1.0,
            Amplitude = 1.0,
        }).Modulate(audio);

        int complex = iq.Length / 2;
        int from = (int)(0.2 * complex);
        int len = (int)(0.6 * complex);
        FmDeviation dev = FmDeviationMeter.Measure(iq.AsSpan(2 * from, 2 * len), 48000);

        dev.PeakHz.Should().BeApproximately(devHz, devHz * 0.05,
            "the upsample must not change the peak deviation the modulator sets");
    }

    [Fact]
    public void The_deviation_meter_reads_the_residual_carrier_offset_as_the_mean()
    {
        // A pure carrier offset (no modulation) reads as a mean equal to the offset and a peak equal
        // to it too - the calibration reads this to know a capture is not tuned to the carrier.
        float[] iq = Carrier(offsetHz: 700, seconds: 0.2, Rate);

        FmDeviation dev = FmDeviationMeter.Measure(iq, Rate);

        dev.MeanHz.Should().BeApproximately(700, 5);
        dev.PeakHz.Should().BeApproximately(700, 5);
    }

    [Fact]
    public void Discriminating_the_modulated_tone_recovers_its_frequency_and_shape()
    {
        // Mod then discriminate is an identity on the audio (up to scale): the recovered signal is a
        // scaled copy of the modulating tone. The discriminator's fixed ±deviation→±0.5 scale makes
        // the copy exactly half the drive amplitude.
        float[] audio = Sine(1500, amplitude: 0.8, seconds: 0.25, Rate);
        float[] iq = new FmModulator(new FmModulatorOptions
        {
            InputRate = Rate,
            OutputRate = Rate,
            PeakDeviationHz = 3000,
            ReferenceAmplitude = 1.0,
            Amplitude = 1.0,
        }).Modulate(audio);

        (double[] i, double[] q) = Split(iq);
        float[] rec = new IqToFmAudioConverter(new IqToFmAudioOptions
        {
            InputRate = Rate,
            OutputRate = Rate,
            DeviationHz = 3000,
        }).Convert(i, q);

        // Recovered = kf·audio / (2·DeviationHz) = 3000·(0.8 sin) / 6000 = 0.4 sin.
        int from = rec.Length / 5;
        int to = rec.Length * 4 / 5;
        ZeroCrossFrequency(rec.AsSpan(from, to - from), Rate).Should().BeApproximately(1500, 30,
            "the recovered audio is the modulating tone");
        Peak(rec.AsSpan(from, to - from)).Should().BeApproximately(0.4, 0.03,
            "the ±deviation→±0.5 scale makes the recovered copy half the drive amplitude");
    }

    private static float[] Sine(double freqHz, double amplitude, double seconds, int rate)
    {
        int n = (int)(seconds * rate);
        var s = new float[n];
        for (int k = 0; k < n; k++)
        {
            s[k] = (float)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * k / rate));
        }

        return s;
    }

    /// <summary>A pure complex carrier at a fixed offset - a constant instantaneous frequency.</summary>
    private static float[] Carrier(double offsetHz, double seconds, int rate)
    {
        int n = (int)(seconds * rate);
        var iq = new float[2 * n];
        for (int k = 0; k < n; k++)
        {
            double ph = 2.0 * Math.PI * offsetHz * k / rate;
            iq[2 * k] = (float)Math.Cos(ph);
            iq[(2 * k) + 1] = (float)Math.Sin(ph);
        }

        return iq;
    }

    private static (double[] I, double[] Q) Split(float[] iq)
    {
        int n = iq.Length / 2;
        var i = new double[n];
        var q = new double[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = iq[2 * k];
            q[k] = iq[(2 * k) + 1];
        }

        return (i, q);
    }

    private static double Peak(ReadOnlySpan<float> x)
    {
        double p = 0;
        for (int k = 0; k < x.Length; k++)
        {
            p = Math.Max(p, Math.Abs(x[k]));
        }

        return p;
    }

    /// <summary>Fundamental frequency of a clean tone by sign-change counting.</summary>
    private static double ZeroCrossFrequency(ReadOnlySpan<float> x, int rate)
    {
        int crossings = 0;
        for (int k = 1; k < x.Length; k++)
        {
            if ((x[k - 1] <= 0 && x[k] > 0) || (x[k - 1] >= 0 && x[k] < 0))
            {
                crossings++;
            }
        }

        double seconds = x.Length / (double)rate;
        return crossings / (2.0 * seconds);
    }
}
