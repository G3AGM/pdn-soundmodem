using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The FM ladders through the IQ front end (issue #594): the front end is a new receive path for
/// 1200 baud AFSK and 9600 baud GFSK, so each is laddered through it against the batch
/// discriminator the FM ladders were built on, at the same carrier-to-noise ratio in the same IF
/// bandwidth.
/// </summary>
/// <remarks>
/// <para>The axis is CNR in the IF bandwidth, as the FM mask tier states it, but the channel is
/// not that tier's <c>fm-data</c> link model: white noise on the carrier and nothing else (see
/// <see cref="IqFrontEndBench"/>). These rows pin the front end, not a radio, and are not
/// comparable with the <c>Fm_Smoke_Mask_Holds</c> rows.</para>
/// <para>The smoke rows block CI and run on a 240 kS/s source to stay cheap; the full ladder
/// (<c>SM_MASK_GATE=1</c>) runs the 2.4 MS/s dongle case, prints both arms side by side, and is
/// the table quoted in the PR.</para>
/// </remarks>
public sealed class IqFrontEndFmLadderTests(ITestOutputHelper output)
{
    // Measured 2026-10-11, N=20, seed 1, 240 kS/s, carrier 25 kHz off centre. Both rows sit on the
    // flat, a couple of dB above their knees in the full ladder, for the reason the FM mask tier
    // gives: a point on a cliff measures the machine's floating point as much as the receiver.
    [Theory]
    [InlineData("afsk1200-il2p", 12.0, 17, 20)] // measured 20/20
    [InlineData("fsk9600-il2p", 15.0, 17, 20)]  // measured 20/20
    public void Fm_Front_End_Smoke_Mask_Holds(string mode, double cnrDb, int floor, int bursts)
    {
        int heard = IqFrontEndBench.RunPoint(mode, cnrDb, IqFmArm.FrontEnd, bursts, sourceRate: 240_000);
        output.WriteLine($"{mode} front end {cnrDb:+0.0} dB CNR: {heard}/{bursts}");

        heard.Should().BeGreaterThanOrEqualTo(
            floor,
            "the {0} front-end row at {1} dB CNR is measured reality; a miss this deep is a "
            + "receive-path regression, and it moves only with a mode-validation.md entry",
            mode, cnrDb);
    }

    /// <summary>
    /// The <c>iqpipe:</c> device (issue #597) is the front end behind a format conversion, so its
    /// one new step is quantisation: each pipe format against the floats, at 240 kS/s (an RTL-SDR's
    /// low range) with the capture carried at -16 dBFS. Prints the table quoted in the PR.
    /// </summary>
    [Theory]
    [InlineData("afsk1200-il2p", new double[] { 4, 6, 8, 10, 12 })]
    [InlineData("fsk9600-il2p", new double[] { 6, 8, 10, 12, 15 })]
    public void Fm_Front_End_Ladder_Through_Each_Pipe_Format(string mode, double[] cnrs)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1",
            "set SM_MASK_GATE=1 for the pipe formats' FM ladder - the iqpipe: receive-path A/B");

        const int bursts = 50;
        const int rate = 240_000;
        output.WriteLine($"{mode}, N={bursts}, seed 1, front end at {rate} S/s with the carrier 25 kHz off centre, pipe formats at {IqFrontEndBench.PipeRms} RMS");
        output.WriteLine("| CNR dB | floats | cf32 | cs16 | cs8 | cu8 |");
        output.WriteLine("|---:|---:|---:|---:|---:|---:|");
        foreach (double cnr in cnrs)
        {
            int floats = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, rate);
            var heard = new List<int>();
            foreach (IqSampleFormat format in new[] { IqSampleFormat.Cf32, IqSampleFormat.Cs16, IqSampleFormat.Cs8, IqSampleFormat.Cu8 })
            {
                heard.Add(IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, rate, pipeFormat: format));
            }

            output.WriteLine($"| {cnr:+0;-0} | {floats}/{bursts} | {string.Join(" | ", heard.Select(h => $"{h}/{bursts}"))} |");
            heard.Should().OnlyContain(
                h => h >= floats - 14,
                "carrying {0} through a pipe format must cost no sensitivity at {1} dB CNR", mode, cnr);
        }
    }

    public static TheoryData<string, double[]> Ladders => new()
    {
        { "afsk1200-il2p", [4, 6, 8, 10, 12, 15] },
        { "afsk1200", [4, 6, 8, 10, 12, 15] },
        { "fsk9600-il2p", [6, 8, 10, 12, 15, 18] },
        { "fsk9600", [6, 8, 10, 12, 15, 18] },
    };

    [Theory]
    [MemberData(nameof(Ladders))]
    public void Fm_Front_End_Ladder_Against_The_Reference_Discriminator(string mode, double[] cnrs)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1",
            "set SM_MASK_GATE=1 for the front end's FM ladder - the receive-path A/B instrument");

        const int bursts = 50;
        output.WriteLine($"{mode}, N={bursts}, seed 1, front end at 2.4 MS/s with the carrier 25 kHz off centre");
        double spacingIf = IqChannelSpec.IfBandwidthForSpacing(
            Packet.SoundModem.Modems.FmModeProfiles.For(mode)!.ChannelSpacingHz);
        double occupancyIf = IqChannelSpec.ForFmMode(mode, 0).FmIfBandwidthHz;
        output.WriteLine($"| CNR dB | reference ({spacingIf / 1000:F0} kHz IF) | front end ({occupancyIf / 1000:F0} kHz IF) | front end ({spacingIf / 1000:F0} kHz IF) |");
        output.WriteLine("|---:|---:|---:|---:|");
        foreach (double cnr in cnrs)
        {
            int reference = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.Reference, bursts, sourceRate: 48_000);
            int frontEnd = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, sourceRate: 2_400_000);
            int narrow = IqFrontEndBench.RunPoint(
                mode, cnr, IqFmArm.FrontEnd, bursts, sourceRate: 2_400_000, frontEndIfHz: spacingIf);
            output.WriteLine($"| {cnr:+0;-0} | {reference}/{bursts} | {frontEnd}/{bursts} | {narrow}/{bursts} |");

            // Four binomial sigma at the worst (p = 0.5): a front end that loses more than that
            // against the reference at any rung costs real sensitivity.
            frontEnd.Should().BeGreaterThanOrEqualTo(
                reference - 14,
                "the front end must hear {0} about as well as the reference discriminator at {1} dB CNR",
                mode, cnr);
        }
    }
}
