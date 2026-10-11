using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// The FM ladders through the IQ front end with the stream cut to an RTL dongle's unsigned bytes
/// (issue #598), beside the same front end on float IQ: what eight bits cost, at a sensible gain
/// and at too little.
/// </summary>
/// <remarks>
/// <para>The channel is <see cref="IqFrontEndBench"/>'s: white noise on the carrier at the stated
/// CNR in the IF bandwidth, nothing else. The dongle arm scales the whole stream (noise and all, as
/// a dongle's gain does) to an RMS per axis that is a fraction of full scale, rounds and clips to a
/// byte, and turns the bytes back to floats with the rtl_tcp source's own table. 0.2 of full scale
/// is a dongle with its gain set so the noise sits well clear of the bottom and peaks rarely clip;
/// 0.02 is one with its gain far too low, the noise only a few steps of the converter.</para>
/// <para>The smoke rows block CI and run on a 250 kS/s source (the lowest rate a dongle runs at
/// that the coarse stage handles cheaply); the full ladder (<c>SM_MASK_GATE=1</c>) runs at
/// 2.4 MS/s and is the table quoted in the PR.</para>
/// </remarks>
public sealed class RtlTcpLadderTests(ITestOutputHelper output)
{
    private const double GoodGain = 0.2;
    private const double LowGain = 0.02;

    // Measured 2026-10-11, N=20, seed 1, 250 kS/s, carrier 25 kHz off centre, 0.2 of full scale:
    // the same rows and floors as the float front end's smoke rows.
    [Theory]
    [InlineData("afsk1200-il2p", 12.0, 17, 20)]
    [InlineData("fsk9600-il2p", 15.0, 17, 20)]
    public void Fm_Through_Dongle_Bytes_Smoke_Mask_Holds(string mode, double cnrDb, int floor, int bursts)
    {
        int heard = IqFrontEndBench.RunPoint(
            mode, cnrDb, IqFmArm.FrontEnd, bursts, sourceRate: 250_000, dongleRms: GoodGain);
        output.WriteLine($"{mode} dongle bytes {cnrDb:+0.0} dB CNR: {heard}/{bursts}");

        heard.Should().BeGreaterThanOrEqualTo(
            floor,
            "the {0} dongle-bytes row at {1} dB CNR is measured reality; a miss this deep is a "
            + "receive-path regression, and it moves only with a mode-validation.md entry",
            mode, cnrDb);
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
    public void Fm_Ladder_Through_Dongle_Bytes_Against_Float_IQ(string mode, double[] cnrs)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1",
            "set SM_MASK_GATE=1 for the dongle-bytes FM ladder - the receive-path A/B instrument");

        const int bursts = 50;
        output.WriteLine($"{mode}, N={bursts}, seed 1, front end at 2.4 MS/s with the carrier 25 kHz off centre");
        output.WriteLine($"| CNR dB | float IQ | bytes, RMS {GoodGain} of full scale | bytes, RMS {LowGain} of full scale |");
        output.WriteLine("|---:|---:|---:|---:|");
        foreach (double cnr in cnrs)
        {
            int floats = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, sourceRate: 2_400_000);
            int good = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, sourceRate: 2_400_000, dongleRms: GoodGain);
            int low = IqFrontEndBench.RunPoint(mode, cnr, IqFmArm.FrontEnd, bursts, sourceRate: 2_400_000, dongleRms: LowGain);
            output.WriteLine($"| {cnr:+0;-0} | {floats}/{bursts} | {good}/{bursts} | {low}/{bursts} |");

            // Four binomial sigma at the worst (p = 0.5): a dongle at a sensible gain that loses
            // more than that against float IQ at any rung is losing real sensitivity to its bytes.
            good.Should().BeGreaterThanOrEqualTo(
                floats - 14,
                "eight bits at a sensible gain should cost {0} next to nothing at {1} dB CNR",
                mode, cnr);
        }
    }
}
