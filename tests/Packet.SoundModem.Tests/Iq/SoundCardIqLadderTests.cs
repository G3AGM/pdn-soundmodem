using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The SSB ladders through sound-card IQ (issue #596): the card, its balance correction and the
/// front end are a new receive path for the HF modes, so 300 baud BPSK and 1200 baud AFSK are
/// laddered through it beside the same audio decoded as it is.
/// </summary>
/// <remarks>
/// <para>The axis is SNR in 3 kHz, as the Watterson masks state it, on the AWGN path; the noise is
/// in the audio before it is put on the IQ (see <see cref="SoundCardIqBench"/>), so the arms meet
/// the same noise and differ only in the receive path. These rows pin the path, not a radio.</para>
/// <para>The smoke rows block CI; the full ladder (<c>SM_MASK_GATE=1</c>) prints all three arms
/// side by side and is the table quoted in the PR.</para>
/// </remarks>
public sealed class SoundCardIqLadderTests(ITestOutputHelper output)
{
    // Measured 2026-10-11, N=20, seed 1, the image arm. Both rows sit on the flat a dB or two above
    // the knee of the full ladder (N=50: bpsk300 44/50 at -5 dB, afsk1200 34/50 at +6 dB), where
    // the audio arm is 50/50 too.
    [Theory]
    [InlineData("bpsk300", -3.0, 18, 20)]  // measured 20/20
    [InlineData("afsk1200", 8.0, 18, 20)]  // measured 20/20
    public void Card_Smoke_Mask_Holds(string mode, double snrDb, int floor, int bursts)
    {
        int heard = SoundCardIqBench.RunPoint(mode, snrDb, CardArm.CorrectedCardWithImage, bursts);
        output.WriteLine($"{mode} corrected card with image {snrDb:+0.0;-0.0} dB: {heard}/{bursts}");

        heard.Should().BeGreaterThanOrEqualTo(
            floor,
            "the {0} sound-card IQ row at {1} dB is measured reality; a miss this deep is a "
            + "receive-path regression, and it moves only with a mode-validation.md entry",
            mode, snrDb);
    }

    public static TheoryData<string, double[]> Ladders => new()
    {
        { "bpsk300", [-8, -6, -5, -4, -3, -2, 0] },
        { "afsk1200", [4, 6, 8, 10, 12, 15] },
    };

    [Theory]
    [MemberData(nameof(Ladders))]
    public void Card_Ladder_Against_The_Audio_Path(string mode, double[] snrs)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1",
            "set SM_MASK_GATE=1 for the sound-card IQ ladder - the receive-path A/B instrument");

        const int bursts = 50;
        output.WriteLine($"{mode}, N={bursts}, seed 1, AWGN, USB 6 kHz above the centre of 48 kS/s IQ");
        output.WriteLine("| SNR dB (3 kHz) | audio | matched card | 0.5 dB / 3 deg card, corrected, 34 dB signal on the image | DC and hum | channel on the centre | DC and hum, channel on the centre |");
        output.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
        foreach (double snr in snrs)
        {
            int audio = SoundCardIqBench.RunPoint(mode, snr, CardArm.Audio, bursts);
            int card = SoundCardIqBench.RunPoint(mode, snr, CardArm.Card, bursts);
            int image = SoundCardIqBench.RunPoint(mode, snr, CardArm.CorrectedCardWithImage, bursts);
            int hum = SoundCardIqBench.RunPoint(mode, snr, CardArm.CardWithDcAndHum, bursts);
            int centre = SoundCardIqBench.RunPoint(mode, snr, CardArm.CardAtCentre, bursts);
            int humCentre = SoundCardIqBench.RunPoint(mode, snr, CardArm.CardWithDcAndHumAtCentre, bursts);
            output.WriteLine(
                $"| {snr:+0;-0} | {audio}/{bursts} | {card}/{bursts} | {image}/{bursts} | {hum}/{bursts} "
                + $"| {centre}/{bursts} | {humCentre}/{bursts} |");

            // Four binomial sigma at the worst (p = 0.5): a path that loses more than that against
            // the audio at any rung costs real sensitivity.
            card.Should().BeGreaterThanOrEqualTo(audio - 14, "the card must hear {0} about as well as the audio at {1} dB", mode, snr);
            image.Should().BeGreaterThanOrEqualTo(audio - 14, "the corrected card must keep the image out at {0} dB", snr);
            hum.Should().BeGreaterThanOrEqualTo(
                audio - 14, "at the default offset the card's DC and hum must stay out of the channel at {0} dB", snr);

            // The two centre arms are measured, not held: they are what the default offset avoids.
        }
    }
}
