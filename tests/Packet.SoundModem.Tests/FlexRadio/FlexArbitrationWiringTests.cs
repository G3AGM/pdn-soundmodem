using M0LTE.Flex;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// The daemon's side of arbitrated keying, end to end through <see cref="FlexDevice"/> against
/// <c>flex:mock</c>: the config flag selects the arbitrated PTT, the station name is offered,
/// and a keyup runs the ordered quiet-filter-slice-xmit sequence with the plan's transmit
/// filter re-asserted per keyup. The arbitration semantics themselves are pinned in
/// M0LTE.Flex's own tests; what is pinned here is that the daemon wires them.
/// </summary>
public sealed class FlexArbitrationWiringTests
{
    [Fact]
    public async Task Default_Keying_Is_The_Plain_Ptt()
    {
        await using FlexRuntime runtime = await FlexDevice.OpenAsync(
            "flex:mock", dspRate: 48000, packetBuffer: 3,
            tuning: new FlexTuning(), cancellation: CancellationToken.None);

        runtime.Ptt.Should().BeOfType<FlexPtt>(
            "arbitration is opt-in until the shared-PA hardware probes pass");
    }

    [Fact]
    public async Task Arbitrated_Keying_Reasserts_The_Filter_Slice_And_Xmit_In_Order()
    {
        await using FlexRuntime runtime = await FlexDevice.OpenAsync(
            "flex:mock", dspRate: 48000, packetBuffer: 3,
            tuning: new FlexTuning
            {
                Arbitration = true,
                TransmitFilterHighHz = 5350,
                StationName = "pdn-soundmodem",
                // The mock confirms every keyup, so the wait ends when its interlock status lands.
                // The default 750 ms is a verdict on a real radio; with every thread-pool thread
                // busy elsewhere in the suite the client reads that status later than that, the
                // keyup is judged lost and Key throws (reproduced by starving the pool, #668).
                ArbitrationTimings = new FlexPttArbitrationOptions { ConfirmTimeout = TimeSpan.FromSeconds(30) },
            },
            cancellation: CancellationToken.None);

        runtime.Ptt.Should().BeOfType<FlexArbitratedPtt>();
        runtime.Mock!.CommandLog.Should().Contain("client station pdn-soundmodem");

        runtime.Ptt.Key();
        runtime.Ptt.Unkey();

        // The keyup's global writes land after bring-up, in dependency order: the filter
        // (radio-global, asserted only while quiet), then the TX slice claim, then the key.
        List<string> log = [.. runtime.Mock.CommandLog];
        int bringUpEnd = log.FindLastIndex(c => c.StartsWith("stream create", StringComparison.Ordinal));
        int filter = log.FindIndex(bringUpEnd, c => c == "transmit set filter_high=5350");
        filter.Should().BeGreaterThan(bringUpEnd, "the keyup re-asserts the filter itself");
        int slice = log.FindIndex(filter + 1, c => c.StartsWith("slice set ", StringComparison.Ordinal) && c.EndsWith("tx=1", StringComparison.Ordinal));
        int xmit = log.FindIndex(slice + 1, c => c == "xmit 1");
        slice.Should().BeGreaterThan(filter);
        xmit.Should().BeGreaterThan(slice);
        log.FindIndex(xmit + 1, c => c == "xmit 0").Should().BeGreaterThan(xmit, "the won keyup unkeys");
        log.Count(c => c == "xmit 1").Should().Be(1, "nothing contends with the mock, so the first keyup wins");
    }
}
