using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Telemetry;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The real-time watch (issue #649): a station whose receive loop cannot keep up, or whose audio
/// goes missing before it arrives, says so once, says which of the two it is, and says so again
/// when it has caught up. Every case drives a fake clock through 100 ms reads.
/// </summary>
public class RealTimeWatchTests
{
    private const int Rate = 48000;
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Turns the loop for <paramref name="duration"/>: each 100 ms read delivers
    /// <paramref name="share"/> of a block, and the loop spends <paramref name="busy"/> of the
    /// block processing before it reads again.
    /// </summary>
    private static List<string> Run(
        FakeTimeProvider clock, RealTimeWatch watch, TimeSpan duration, double share, double busy,
        bool leaveOut = false)
    {
        var lines = new List<string>();
        int samples = (int)Math.Round(Rate * Block.TotalSeconds * share);
        for (TimeSpan t = TimeSpan.Zero; t < duration; t += Block)
        {
            clock.Advance(Block * busy);
            watch.ReadStarting();
            clock.Advance(Block * (1 - busy));
            watch.ReadEnded(samples, leaveOut);
            if (watch.Poll() is string line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    [Fact]
    public void A_Station_Keeping_Up_Says_Nothing()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        Run(clock, watch, TimeSpan.FromMinutes(5), share: 1, busy: 0.3).Should().BeEmpty();

        watch.Behind.Should().BeFalse();
        watch.Snapshot().RealTimeRatio.Should().BeApproximately(1, 0.01);
    }

    [Fact]
    public void A_Loop_That_Cannot_Keep_Up_Is_Called_The_Bottleneck_Once_And_Then_Caught_Up_Once()
    {
        // GB7RDG on 2026-10-10: busy the whole time, 30 % of the audio reaching the modems.
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        List<string> behind = Run(clock, watch, TimeSpan.FromMinutes(5), share: 0.3, busy: 1);

        behind.Should().ContainSingle("it is said once, not every minute it stays true")
            .Which.Should().Contain("BEHIND").And.Contain("30%").And.Contain("bottleneck");
        watch.Behind.Should().BeTrue();

        List<string> recovered = Run(clock, watch, TimeSpan.FromMinutes(5), share: 1, busy: 0.3);

        recovered.Should().ContainSingle().Which.Should().StartWith("receive: caught up");
        watch.Behind.Should().BeFalse();
    }

    [Fact]
    public void Audio_Missing_While_The_Loop_Waits_Is_Blamed_On_The_Path_Not_The_Loop()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        List<string> lines = Run(clock, watch, TimeSpan.FromMinutes(2), share: 0.8, busy: 0.2);

        lines.Should().ContainSingle().Which.Should()
            .Contain("busy only 20%").And.Contain("network").And.NotContain("bottleneck");
    }

    [Fact]
    public void A_Loop_Starved_Of_Cpu_Is_Still_Called_The_Bottleneck_Though_Its_Busy_Share_Reads_Low()
    {
        // Measured on GB7RDG under a 15 % CPU quota: the thread is descheduled inside Read too, so
        // it read busy 82 % with 57 % delivered. That is 1.4 s of work per second of audio.
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        List<string> lines = Run(clock, watch, TimeSpan.FromMinutes(1.1), share: 0.57, busy: 0.82);

        lines.Should().ContainSingle().Which.Should()
            .Contain("bottleneck").And.Contain("1.4 s of work per second").And.NotContain("network");
    }

    [Fact]
    public void No_Audio_At_All_Is_Blamed_On_The_Path_Not_On_An_Infinitely_Slow_Loop()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        List<string> lines = Run(clock, watch, TimeSpan.FromMinutes(1.1), share: 0, busy: 0.01);

        lines.Should().ContainSingle().Which.Should()
            .Contain("network").And.NotContain("bottleneck").And.NotContain("Infinity");
    }

    [Fact]
    public void No_Verdict_Is_Taken_Before_A_Minute_Has_Been_Measured()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        Run(clock, watch, TimeSpan.FromSeconds(50), share: 0.3, busy: 1).Should().BeEmpty();

        watch.Snapshot().RealTimeRatio.Should().BeNull("no window has closed yet");
    }

    [Fact]
    public void Spans_Left_Out_While_Keyed_Count_For_Nothing()
    {
        // A keyed Flex delivers nothing worth measuring, or nothing at all: neither is lost audio.
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        Run(clock, watch, TimeSpan.FromMinutes(5), share: 0, busy: 0, leaveOut: true).Should().BeEmpty();

        watch.Snapshot().Seconds.Should().Be(0);
        Run(clock, watch, TimeSpan.FromMinutes(2), share: 1, busy: 0.3).Should().BeEmpty();
    }

    [Fact]
    public void The_Inputs_Own_Loss_Count_Is_Reported_With_The_Verdict()
    {
        var clock = new FakeTimeProvider();
        long lost = 0;
        var watch = new RealTimeWatch(clock, Rate, () => lost);

        lost = 42;
        List<string> lines = Run(clock, watch, TimeSpan.FromMinutes(1.1), share: 0.5, busy: 0.1);

        lines.Should().ContainSingle().Which.Should().Contain("counted 42 losses of audio");
        watch.Snapshot().InputLosses.Should().Be(42);
    }

    [Fact]
    public void The_Totals_Add_Up_For_The_Metrics_Endpoint()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate);

        Run(clock, watch, TimeSpan.FromSeconds(10), share: 0.5, busy: 0.25);

        ReceiveRateSnapshot snapshot = watch.Snapshot();
        // The first read only starts the clock, so 99 of the 100 reads are measured.
        snapshot.Samples.Should().Be(99 * 2400);
        snapshot.Seconds.Should().BeApproximately(9.9, 1e-6);
        snapshot.BusySeconds.Should().BeApproximately(9.9 * 0.25, 1e-6);
        snapshot.InputLosses.Should().BeNull("this input keeps no count");
    }

    [Fact]
    public void Every_Line_Is_Printable_Ascii()
    {
        var clock = new FakeTimeProvider();
        var watch = new RealTimeWatch(clock, Rate, () => 3);
        var lines = Run(clock, watch, TimeSpan.FromMinutes(2), share: 0.3, busy: 1);
        lines.AddRange(Run(clock, watch, TimeSpan.FromMinutes(2), share: 1, busy: 0.1));
        lines.AddRange(Run(clock, watch, TimeSpan.FromMinutes(2), share: 0.5, busy: 0.1));

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.All(c => c >= ' ' && c <= '~'));
    }
}
