using AwesomeAssertions;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The two watches a read-only IQ source keeps on a server that cannot say it changed: the rate
/// against the wall clock, and the stream's character.
/// </summary>
public class IqProfileWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_Stream_At_Its_Rate_Is_Never_Called_Off()
    {
        var watch = new IqRateWatch(2_400_000);
        watch.Restart(T0);

        IqRateVerdict[] verdicts = Feed(watch, T0, seconds: 60, rate: 2_400_000 * 1.01);

        verdicts.Should().AllBeEquivalentTo(IqRateVerdict.None);
        watch.Off.Should().BeFalse();
        watch.LastMeasuredRate.Should().BeApproximately(2_424_000, 2_424);
    }

    [Fact]
    public void A_Stream_That_Switches_Rate_Is_Called_Off_After_Two_Windows_And_Back_After_Two_More()
    {
        var watch = new IqRateWatch(2_400_000);
        watch.Restart(T0);
        Feed(watch, T0, seconds: 20, rate: 2_400_000);

        IqRateVerdict[] off = Feed(watch, T0.AddSeconds(20), seconds: 20, rate: 2_048_000);
        int wentOff = Array.IndexOf(off, IqRateVerdict.WentOff);
        wentOff.Should().BeGreaterThan(-1);
        // Ten reads a second: two five-second windows, the first of which may straddle the switch.
        wentOff.Should().BeLessThanOrEqualTo(150);
        watch.Off.Should().BeTrue();
        watch.LastMeasuredRate.Should().BeApproximately(2_048_000, 20_000);

        IqRateVerdict[] back = Feed(watch, T0.AddSeconds(40), seconds: 20, rate: 2_400_000);
        back.Should().Contain(IqRateVerdict.CameBack);
        watch.Off.Should().BeFalse();
    }

    [Fact]
    public void One_Odd_Window_Moves_Nothing()
    {
        var watch = new IqRateWatch(1_000_000);
        watch.Restart(T0);
        Feed(watch, T0, seconds: 20, rate: 1_000_000);

        // A stall: two seconds at half rate, inside one window, then right again.
        IqRateVerdict[] verdicts = [
            .. Feed(watch, T0.AddSeconds(20), seconds: 2, rate: 500_000),
            .. Feed(watch, T0.AddSeconds(22), seconds: 20, rate: 1_000_000),
        ];

        verdicts.Should().AllBeEquivalentTo(IqRateVerdict.None);
    }

    [Fact]
    public void A_Window_In_Which_The_Reader_Was_Behind_Proves_Nothing()
    {
        var watch = new IqRateWatch(1_000_000);
        watch.Restart(T0);

        IqRateVerdict[] verdicts = Feed(watch, T0, seconds: 60, rate: 600_000, readerBehind: true);

        verdicts.Should().AllBeEquivalentTo(IqRateVerdict.None);
        watch.Off.Should().BeFalse();
    }

    [Fact]
    public void The_Settle_After_A_Restart_Is_Not_Measured()
    {
        var watch = new IqRateWatch(1_000_000, window: TimeSpan.FromSeconds(1), settle: TimeSpan.FromSeconds(3));
        watch.Restart(T0);

        // A server flushing its buffer at ten times the rate for the first two seconds.
        Feed(watch, T0, seconds: 2, rate: 10_000_000).Should().AllBeEquivalentTo(IqRateVerdict.None);
        Feed(watch, T0.AddSeconds(2), seconds: 10, rate: 1_000_000).Should().AllBeEquivalentTo(IqRateVerdict.None);
        watch.Off.Should().BeFalse();
    }

    [Fact]
    public void Steady_Noise_With_Short_Bursts_Is_No_Step()
    {
        const int rate = 100_000;
        var watch = new IqStepWatch(rate);
        var random = new Random(1);
        bool stepped = false;
        for (int second = 0; second < 30; second++)
        {
            // A strong burst for one second in every ten: a packet, not a band change.
            double amplitude = second % 10 == 5 ? 0.4 : 0.1;
            stepped |= watch.Feed(Noise(random, rate, amplitude, dc: 0));
        }

        stepped.Should().BeFalse();
        watch.Steps.Should().Be(0);
    }

    [Fact]
    public void A_Level_That_Steps_And_Stays_Is_A_Step()
    {
        const int rate = 100_000;
        var watch = new IqStepWatch(rate);
        var random = new Random(2);
        for (int second = 0; second < 10; second++)
        {
            watch.Feed(Noise(random, rate, 0.1, dc: 0)).Should().BeFalse();
        }

        int when = -1;
        for (int second = 0; second < 10 && when < 0; second++)
        {
            if (watch.Feed(Noise(random, rate, 0.3, dc: 0)))
            {
                when = second;
            }
        }

        when.Should().Be(2, "the third second at the new level holds it");
        watch.LastStep.Should().Contain("level +9.5 dB");

        // The new level is the reference now: staying there is not another step.
        for (int second = 0; second < 10; second++)
        {
            watch.Feed(Noise(random, rate, 0.3, dc: 0)).Should().BeFalse();
        }

        watch.Steps.Should().Be(1);
    }

    [Fact]
    public void A_DC_Offset_That_Appears_And_Stays_Is_A_Step_At_The_Same_Level()
    {
        const int rate = 100_000;
        var watch = new IqStepWatch(rate);
        var random = new Random(3);
        for (int second = 0; second < 10; second++)
        {
            watch.Feed(Noise(random, rate, 0.1, dc: 0));
        }

        bool stepped = false;
        for (int second = 0; second < 5; second++)
        {
            stepped |= watch.Feed(Noise(random, rate, 0.1, dc: 0.05));
        }

        stepped.Should().BeTrue();
        watch.LastStep.Should().Contain("DC moved");
    }

    [Fact]
    public void A_Restart_Forgets_The_Reference()
    {
        const int rate = 100_000;
        var watch = new IqStepWatch(rate);
        var random = new Random(4);
        for (int second = 0; second < 10; second++)
        {
            watch.Feed(Noise(random, rate, 0.1, dc: 0));
        }

        watch.Restart();
        for (int second = 0; second < 10; second++)
        {
            watch.Feed(Noise(random, rate, 0.5, dc: 0)).Should().BeFalse("a retune this client made is not a step");
        }
    }

    /// <summary>Ten observations a second for <paramref name="seconds"/> at
    /// <paramref name="rate"/>, from <paramref name="start"/>.</summary>
    private static IqRateVerdict[] Feed(
        IqRateWatch watch, DateTimeOffset start, double seconds, double rate, bool readerBehind = false)
    {
        int steps = (int)(seconds * 10);
        var verdicts = new IqRateVerdict[steps];
        for (int k = 0; k < steps; k++)
        {
            verdicts[k] = watch.Observe((long)(rate / 10), start.AddMilliseconds(100 * (k + 1)), readerBehind);
        }

        return verdicts;
    }

    /// <summary>One second of complex Gaussian noise of RMS <paramref name="amplitude"/> per
    /// axis, with <paramref name="dc"/> added to I.</summary>
    private static float[] Noise(Random random, int rate, double amplitude, double dc)
    {
        var iq = new float[2 * rate];
        for (int k = 0; k < iq.Length; k++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            iq[k] = (float)((amplitude * g) + ((k & 1) == 0 ? dc : 0));
        }

        return iq;
    }
}
