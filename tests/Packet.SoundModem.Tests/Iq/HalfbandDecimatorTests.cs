using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The front end's coarse stage: each halfband keeps its protected band flat and rejects what
/// would fold into it by the stated figure, measured on tones rather than taken from the design;
/// the cascade is planned to the rate the issue's arithmetic says.
/// </summary>
public sealed class HalfbandDecimatorTests
{
    [Theory]
    [InlineData(2_400_000, 49_000)] // the first stage off a dongle: short
    [InlineData(300_000, 49_000)]   // the last stage on the way to 150 kS/s: the long one
    public void A_Tone_In_The_Protected_Band_Comes_Through_At_Unity(int rate, double protectedHz)
    {
        var stage = new HalfbandDecimator(rate, protectedHz);
        foreach (double hz in (ReadOnlySpan<double>)[0, 0.5 * protectedHz, -0.9 * protectedHz, protectedHz])
        {
            double gainDb = 20 * Math.Log10(Rms(stage, rate, hz) / 0.5);
            gainDb.Should().BeApproximately(0, 0.01, "at {0} Hz", hz);
            stage.Reset();
        }
    }

    [Theory]
    [InlineData(2_400_000, 49_000)]
    [InlineData(300_000, 49_000)]
    [InlineData(96_000, 16_000)]
    public void What_Would_Fold_Into_The_Protected_Band_Is_Down_By_The_Stated_Attenuation(int rate, double protectedHz)
    {
        var stage = new HalfbandDecimator(rate, protectedHz, stopbandAttenuationDb: 80);
        int output = rate / 2;
        // From the stopband edge (output rate - protected) up to the input Nyquist: each folds onto
        // a frequency inside the protected band.
        foreach (double hz in (ReadOnlySpan<double>)[output - protectedHz, output - (0.5 * protectedHz), output, -(output - (0.2 * protectedHz)), rate / 2.0 - 1])
        {
            double gainDb = 20 * Math.Log10(Rms(stage, rate, hz) / 0.5);
            gainDb.Should().BeLessThan(-80, "a tone at {0} Hz lands in the protected band once halved", hz);
            stage.Reset();
        }
    }

    [Fact]
    public void A_Dongle_With_A_Channel_25_Khz_Out_Comes_Down_To_150_Ksps_In_Four_Stages()
    {
        // An FM channel at 48 kHz on a 25 kHz offset reaches 25 + 24 = 49 kHz from the centre.
        var coarse = new CoarseDecimator(2_400_000, 49_000, maxBlockFrames: 8192);

        coarse.Stages.Should().HaveCount(4);
        coarse.OutputRate.Should().Be(150_000);
        coarse.ProtectedHz.Should().Be(49_000);
        coarse.MacsPerInputSample.Should().BeLessThan(30, "the coarse stage is what makes 2.4 MS/s affordable");
    }

    [Fact]
    public void A_Source_Already_Narrow_Passes_Through_Untouched()
    {
        var coarse = new CoarseDecimator(48_000, 11_000, maxBlockFrames: 1024);
        float[] iq = [0.1f, 0.2f, 0.3f, 0.4f];

        coarse.Stages.Should().BeEmpty();
        coarse.OutputRate.Should().Be(48_000);
        coarse.ProtectedHz.Should().Be(24_000, "with no stage, the whole band comes through");
        coarse.Process(iq).ToArray().Should().Equal(iq);
    }

    [Fact]
    public void Splitting_A_Stream_Into_Blocks_Differently_Does_Not_Change_The_Output()
    {
        const int rate = 2_400_000;
        float[] iq = Tone(rate, 12_345, 50_001);
        float[] whole = Decimate(new CoarseDecimator(rate, 49_000, 60_000), iq, iq.Length / 2);
        float[] pieces = Decimate(new CoarseDecimator(rate, 49_000, 60_000), iq, 777);

        pieces.Should().Equal(whole);
    }

    private static float[] Decimate(CoarseDecimator coarse, float[] iq, int blockFrames)
    {
        var output = new List<float>();
        for (int at = 0; at < iq.Length; at += 2 * blockFrames)
        {
            output.AddRange(coarse.Process(iq.AsSpan(at, Math.Min(2 * blockFrames, iq.Length - at))).ToArray());
        }

        return [.. output];
    }

    /// <summary>The RMS magnitude of the stage's output for a 0.5 amplitude complex tone, over the
    /// settled part.</summary>
    private static double Rms(HalfbandDecimator stage, int rate, double hz)
    {
        float[] iq = Tone(rate, hz, 40_000);
        var output = new float[2 * stage.MaxOutput(iq.Length / 2)];
        int produced = stage.Process(iq, output);
        int skip = stage.Length;
        double sum = 0;
        for (int k = skip; k < produced; k++)
        {
            sum += (output[2 * k] * (double)output[2 * k]) + (output[(2 * k) + 1] * (double)output[(2 * k) + 1]);
        }

        return Math.Sqrt(sum / (produced - skip));
    }

    private static float[] Tone(int rate, double hz, int frames)
    {
        var iq = new float[2 * frames];
        for (int k = 0; k < frames; k++)
        {
            double phase = 2.0 * Math.PI * hz * k / rate;
            iq[2 * k] = 0.5f * (float)Math.Cos(phase);
            iq[(2 * k) + 1] = 0.5f * (float)Math.Sin(phase);
        }

        return iq;
    }
}
