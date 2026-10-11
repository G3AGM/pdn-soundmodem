using AwesomeAssertions;
using Packet.SoundModem.Ota;
using Packet.SoundModem.Iq;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The FM §E2 ladder, verified end to end with no radio in the loop - the FM counterpart of
/// <see cref="OfdmLadderPassTests"/>. Renders an FM ladder exactly as it would go to the DAX FM
/// transmitter, lays it out as an IQ capture would record it, FM-discriminates it back and scores it
/// with the mode's own demodulator, so the whole chain (render → channel → FM-mod at the target
/// deviation → FM-demod → decode) is proved before any power is applied.
/// </summary>
/// <remarks>The load-bearing assertion is the one every ladder rests on: the SNR the scorer
/// <em>measures</em> from each burst's own transmitted noise lead-in matches the SNR the rig was
/// <em>asked</em> to inject.</remarks>
public class FmLadderPassTests
{
    private const int CaptureRate = 48000;

    private static FmLadderPassOptions Options() => new()
    {
        OutputRate = CaptureRate,
        FrameBytes = 40,
        // A shorter lead-in than the live default keeps the test capture small while still covering
        // the scorer's 2 s noise window plus its guard from within the injected noise.
        LeadInSeconds = 3.0,
        LeadOutSeconds = 0.5,
    };

    /// <summary>Lays rendered points out as a capture would hold them, with quiet between, FM-
    /// discriminates the IQ back to the mode's DSP-rate audio and returns it with each burst's
    /// active-start time.</summary>
    private static (float[] Audio, double[] Starts) LayOutAndConvert(
        FmLadderPass pass, IReadOnlyList<FmRenderedPoint> points, double gapSeconds = 1.5, int seed = 4)
    {
        var random = new Random(seed);
        var iq = new List<short>();
        var starts = new List<double>();

        void Quiet(double seconds)
        {
            int frames = (int)(seconds * CaptureRate);
            for (int k = 0; k < frames; k++)
            {
                iq.Add((short)random.Next(-40, 40));
                iq.Add((short)random.Next(-40, 40));
            }
        }

        Quiet(gapSeconds);
        foreach (FmRenderedPoint point in points)
        {
            starts.Add((iq.Count / 2.0 / CaptureRate) + point.LeadInSeconds);
            foreach (float v in point.Iq)
            {
                iq.Add((short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue));
            }

            Quiet(gapSeconds);
        }

        float[] audio = new IqToFmAudioConverter(new IqToFmAudioOptions
        {
            InputRate = CaptureRate,
            OutputRate = pass.DspRate,
            DialHz = 0,
            DeviationHz = pass.TargetDeviationHz,
        }).Convert(iq.ToArray().AsSpan());

        return (audio, starts.ToArray());
    }

    private static FmCampaignManifest Manifest(
        string mode, FmLadderPass pass, IReadOnlyList<FmLadderPoint> plan,
        IReadOnlyList<FmRenderedPoint> rendered, double[] starts) =>
        new(
            Name: "test",
            Mode: mode,
            DspRate: pass.DspRate,
            TargetDeviationHz: pass.TargetDeviationHz,
            OffsetHz: 0,
            CaptureRate: CaptureRate,
            Bursts:
            [
                .. plan.Select((p, k) => new FmCampaignBurst(
                    p.Mode, p.Seed, p.SnrDb, p.Channel, 40, starts[k], rendered[k].BurstSeconds)),
            ],
            ModemRevision: "test",
            WrittenUtc: DateTimeOffset.UtcNow,
            Radio: "none (test)",
            FrequencyMHz: "18.106500",
            RfPower: null,
            PassAudioGain: pass.AudioGain);

    private static (FmCaptureScore Score, FmLadderPass Pass) RunLadder(string mode, double[] snrs, int seed)
    {
        IReadOnlyList<FmLadderPoint> plan = FmLadderPass.Plan(mode, snrs, repeats: 1, firstSeed: seed);
        var pass = new FmLadderPass(Options());
        IReadOnlyList<FmRenderedPoint> rendered = pass.Render(plan);
        (float[] audio, double[] starts) = LayOutAndConvert(pass, rendered);
        FmCaptureScore score = new FmBurstScorer(audio, pass.DspRate, CaptureRate, pass.TargetDeviationHz)
            .Score(Manifest(mode, pass, plan, rendered, starts));
        return (score, pass);
    }

    [Theory]
    [InlineData("afsk1200", 30.0)]
    [InlineData("afsk1200", 20.0)]
    [InlineData("fsk9600", 30.0)]
    [InlineData("qpsk3600", 30.0)]
    public void The_snr_the_scorer_measures_is_the_snr_the_rig_injected(string mode, double snrDb)
    {
        // The load-bearing one. Each rung carries its own noise lead-in on the air so the receiver
        // measures what was delivered; this proves the two agree through the real FM mod/demod round
        // trip, discriminator and scorer.
        (FmCaptureScore score, _) = RunLadder(mode, [snrDb], seed: 31);

        score.Bursts.Should().ContainSingle();
        score.Bursts[0].Snr.Should().NotBeNull();
        score.Bursts[0].Snr!.SnrDb.Should().BeApproximately(snrDb, 1.5,
            "the transmitted noise lead-in exists so the receiver measures the delivered SNR "
            + "rather than trusting the requested one");
    }

    // The deviation the ladder actually drives, which is FmModeProfiles' recommendation and only
    // differs from NinoTNC's published figure where we have a measured reason. qpsk3600 is that
    // case: Nino publishes 5000 Hz, which is 200 % of full deviation for the 12.5 kHz channel he
    // puts the mode on, and we transmit 2500 because it measures +0.4 dB at the knee and takes
    // Carson bandwidth from 15.4 to 10.4 kHz. This row failing is the correct behaviour if the
    // recommendation moves and nobody re-measured the hardware ladder.
    [Theory]
    [InlineData("afsk1200", 3000)]
    [InlineData("fsk9600", 2400)]
    [InlineData("qpsk3600", 2500)]
    [InlineData("c4fsk9600", 2500)]
    public void A_whole_fm_ladder_decodes_clean_and_at_the_target_deviation(string mode, double targetDevHz)
    {
        // The rehearsal: several healthy rungs, laid out as a capture, scored in one pass. Every burst
        // must recover its frame, its measured SNR must track the request, and its peak deviation must
        // land near the mode's target - the calibration the FM path exists to verify.
        double[] snrs = [40, 30];
        (FmCaptureScore score, FmLadderPass pass) = RunLadder(mode, snrs, seed: 41);

        pass.TargetDeviationHz.Should().Be(targetDevHz);
        score.Bursts.Should().HaveCount(snrs.Length);
        score.Bursts.Should().OnlyContain(b => b.Decoded, "every healthy rung must decode its frame");
        for (int k = 0; k < snrs.Length; k++)
        {
            score.Bursts[k].Snr!.SnrDb.Should().BeApproximately(snrs[k], 1.5,
                "measured SNR must track the request across the whole ladder");
        }

        // The near-clean top rung's peak deviation is the signal's, which the drive was calibrated to
        // the target - within the FSK transition overshoot / worst-point reference margin.
        score.Bursts[0].PeakDeviationHz.Should().BeApproximately(targetDevHz, targetDevHz * 0.2,
            "the top rung's peak deviation is the signal deviating to its calibrated target");
    }

    [Fact]
    public void The_scorer_reports_the_cliff_rather_than_papering_over_it()
    {
        // A scorer that always passes is worthless. A rung far below the cliff must be caught: the
        // frame is lost and its noise-dominated deviation runs far past the target.
        (FmCaptureScore score, _) = RunLadder("fsk9600", [40, -15], seed: 55);

        score.Bursts[0].Decoded.Should().BeTrue("40 dB is well above the cliff");
        score.Bursts[1].Decoded.Should().BeFalse("−15 dB is well below the cliff - the burst is lost");
    }

    [Fact]
    public void The_plan_visits_every_rung_before_repeating_any_with_unique_seeds()
    {
        IReadOnlyList<FmLadderPoint> plan = FmLadderPass.Plan("fsk9600", [10, 5, 0], repeats: 3);

        plan.Should().HaveCount(9);
        plan.Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Skip(3).Take(3).Select(p => p.SnrDb).Should().Equal(10, 5, 0);
        plan.Select(p => p.Seed).Should().OnlyHaveUniqueItems(
            "a repeated rung must be a different payload and channel realisation");
    }

    [Theory]
    [InlineData("ms110d-wn6")]
    [InlineData("freedv-datac0")]
    [InlineData("bpsk300")]
    public void A_non_fm_mode_is_rejected_before_any_rendering(string mode)
    {
        Action plan = () => FmLadderPass.Plan(mode, [10], repeats: 1);
        plan.Should().Throw<ArgumentException>("the FM ladder only carries the FM-native modes");
    }
}
