using Packet.SoundModem.Iq;
using M0LTE.Ofdm;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The digital down-converter: tuned to one channel in a wideband complex-IQ stream, it recovers
/// that channel's tone at full amplitude and rejects tones in the other channels. Everything here
/// runs on synthetic in-memory IQ - no hardware, no network.
/// </summary>
public sealed class DigitalDownconverterTests
{
    [Fact]
    public void Downconverter_recovers_its_own_tone_and_rejects_the_neighbours()
    {
        const int iqRate = 96000;
        const int decimation = 8; // -> 12 kHz DSP
        const int taps = 256;
        const float amp = 0.5f;
        const int samples = 24000;
        double[] offsets = [20000, 0, -30000];

        for (int i = 0; i < offsets.Length; i++)
        {
            // Selection: tune to a channel carrying only its own tone -> recovered at full amplitude.
            float[] own = ComplexTone(iqRate, offsets[i], amp, samples);
            (float dc, float rms) = MeasureBaseband(
                new DigitalDownconverter(iqRate, offsets[i], decimation, taps), own);
            dc.Should().BeApproximately(amp, amp * 0.05f);
            rms.Should().BeApproximately(amp, amp * 0.05f);

            // Rejection: the same tuning must strongly suppress a tone in any other channel.
            for (int j = 0; j < offsets.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                float[] other = ComplexTone(iqRate, offsets[j], amp, samples);
                (_, float leakRms) = MeasureBaseband(
                    new DigitalDownconverter(iqRate, offsets[i], decimation, taps), other);
                leakRms.Should().BeLessThan(amp * 0.02f); // > ~34 dB isolation
            }
        }
    }

    // ---- helpers ----

    private static float[] ComplexTone(int rate, double offsetHz, float amp, int samples)
    {
        var iq = new float[samples * 2];
        double w = 2.0 * Math.PI * offsetHz / rate;
        for (int k = 0; k < samples; k++)
        {
            iq[2 * k] = amp * (float)Math.Cos(w * k);
            iq[(2 * k) + 1] = amp * (float)Math.Sin(w * k);
        }

        return iq;
    }

    /// <summary>Runs the DDC and returns (DC-component magnitude, per-sample RMS magnitude) over
    /// the settled region - DC ≈ amplitude when a tone lands at baseband, RMS ≈ residual level
    /// otherwise.</summary>
    private static (float Dc, float Rms) MeasureBaseband(DigitalDownconverter ddc, float[] iq)
    {
        var outp = new Cf[ddc.MaxOutput(iq.Length)];
        int n = ddc.Process(iq, outp);
        int skip = Math.Min(200, n / 4); // let the FIR fill
        double sumRe = 0, sumIm = 0, sumSq = 0;
        int count = 0;
        for (int i = skip; i < n; i++)
        {
            sumRe += outp[i].Re;
            sumIm += outp[i].Im;
            sumSq += outp[i].Cnorm();
            count++;
        }

        if (count == 0)
        {
            return (0, 0);
        }

        double dc = Math.Sqrt(((sumRe / count) * (sumRe / count)) + ((sumIm / count) * (sumIm / count)));
        double rms = Math.Sqrt(sumSq / count);
        return ((float)dc, (float)rms);
    }
}
