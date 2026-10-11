using Packet.SoundModem.Modems;
using Packet.SoundModem.Telemetry;

namespace Packet.SoundModem.Tests.Telemetry;

/// <summary>
/// Publishing what a station hears, in a shape a monitoring system can take.
/// </summary>
/// <remarks>
/// Two exports because they answer different questions: Prometheus text for rates and alerting,
/// InfluxDB line protocol for one point per frame. A scrape-and-aggregate format has one sample
/// per series per scrape and so cannot represent individual frames as points, which is what a
/// scatter plot is.
/// </remarks>
public class StationTelemetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_Receive_Loops_Keeping_Up_Is_Published_When_A_Source_Is_Given()
    {
        var telemetry = new StationTelemetry(new FakeTime(Start))
        {
            ReceiveRate = () => new ReceiveRateSnapshot(
                Samples: 1_440_000, Seconds: 100, BusySeconds: 99.5, RealTimeRatio: 0.3, Behind: true,
                InputLosses: 7),
        };

        string text = telemetry.Exposition();

        text.Should().Contain("pdn_receive_samples_total 1440000\n")
            .And.Contain("pdn_receive_seconds_total 100\n")
            .And.Contain("pdn_receive_busy_seconds_total 99.5\n")
            .And.Contain("pdn_receive_realtime_ratio 0.3\n")
            .And.Contain("pdn_receive_behind 1\n")
            .And.Contain("pdn_receive_input_losses_total 7\n")
            .And.Contain("# TYPE pdn_receive_samples_total counter");
    }

    [Fact]
    public void No_Receive_Series_Without_A_Source_And_No_Ratio_Before_The_First_Window()
    {
        new StationTelemetry(new FakeTime(Start)).Exposition().Should().NotContain("pdn_receive_");

        string early = new StationTelemetry(new FakeTime(Start))
        {
            ReceiveRate = () => new ReceiveRateSnapshot(4800, 0.1, 0.01, null, false, null),
        }.Exposition();

        early.Should().Contain("pdn_receive_behind 0\n")
            .And.NotContain("pdn_receive_realtime_ratio")
            .And.NotContain("pdn_receive_input_losses_total");
    }

    [Fact]
    public void A_Bit_Error_Does_Not_Become_A_Station()
    {
        // The gate that makes this safe to leave running for years. Of the 77 distinct callsigns
        // the live 40 m station had ever decoded, 45 were heard exactly once and NOT ONE of those
        // 45 ever had a valid check sequence - they are corruptions of the regulars (EI0RSI-9,
        // EI0RSA-12 and EI0RSE-1 are all EI0RSI-1 with a bit wrong; 7B7BPQ is GB7BPQ). All 21
        // stations heard twenty times or more had one. Without this, every bit error the receiver
        // ever makes mints a series that exists for ever and appears on a chart as a station.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("GB7BWR", 2), Quality(crcValid: true));
        telemetry.Record(0, Frame("EI0RSI", 9), Quality(crcValid: null, plainIl2p: true));
        telemetry.Record(0, Frame("7B7BPQ", 0), Quality(crcValid: true, monitorOnly: true));

        telemetry.StationCount.Should().Be(1);
        telemetry.Uncounted.Should().Be(2);
        telemetry.Exposition().Should().Contain("GB7BWR").And.NotContain("EI0RSI").And.NotContain("7B7BPQ");
    }

    [Fact]
    public void Every_Ssid_Of_One_Station_Is_One_Series()
    {
        // GB7IOW-1, GB7IOW-2 and GB7IOW are one transmitter, one antenna and one path. A chart
        // that draws them as three series is drawing one signal three times.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("GB7IOW", 1), Quality(crcValid: true, snr: 12));
        telemetry.Record(0, Frame("GB7IOW", 2), Quality(crcValid: true, snr: 14));
        telemetry.Record(0, Frame("GB7IOW", 0), Quality(crcValid: true, snr: 16));

        telemetry.StationCount.Should().Be(1);
        telemetry.Exposition().Should().Contain("pdn_station_frames_total{station=\"GB7IOW\",mode=\"afsk300-il2pc\"} 3");
    }

    [Fact]
    public void The_Ssid_Survives_On_The_Individual_Frame()
    {
        // Combined for the series key, kept as a detail: which SSID answered is a real question,
        // it is just not a question that should fork a chart.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("GB7IOW", 9), Quality(crcValid: true, snr: 12));

        telemetry.LineProtocol().Should().Contain("station=GB7IOW").And.Contain("ssid=\"9\"");
    }

    [Fact]
    public void Snr_Is_Exported_As_A_Sum_And_A_Count_So_The_Mean_Is_Honest_Over_Any_Window()
    {
        // The trap this avoids: a last-value gauge for SNR holds its reading between
        // transmissions, so a station heard once an hour against a fifteen-second scrape draws a
        // flat line across every scrape in between and a long-retention store keeps it for ever.
        // One sample smeared across an hour reads exactly like a continuous measurement. A sum
        // and a count divide into the mean over whatever window is asked for, and produce
        // nothing at all when nothing was heard.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("GB7NOT", 0), Quality(crcValid: true, snr: 10));
        telemetry.Record(0, Frame("GB7NOT", 0), Quality(crcValid: true, snr: 20));
        telemetry.Record(0, Frame("GB7NOT", 0), Quality(crcValid: true, snr: null));

        string text = telemetry.Exposition();
        text.Should().Contain("pdn_station_snr_db_sum{station=\"GB7NOT\",mode=\"afsk300-il2pc\"} 30");
        text.Should().Contain("pdn_station_frames_with_snr_total{station=\"GB7NOT\",mode=\"afsk300-il2pc\"} 2");
        text.Should().Contain("pdn_station_frames_total{station=\"GB7NOT\",mode=\"afsk300-il2pc\"} 3");
        text.Should().Contain("pdn_station_snr_db_last{station=\"GB7NOT\",mode=\"afsk300-il2pc\"} 20",
            "a point reading is still worth having, named so nobody charts it by accident");
    }

    [Fact]
    public void One_Callsign_On_Two_Modes_Is_Two_Links_And_Two_Series()
    {
        // Not a presentation choice. A station heard on two modes is reaching us over two
        // frequencies through two modems with two path budgets, and one series spanning both
        // describes neither: on the live 40 m station GB7BPQ reads 15.2 dB on afsk300-il2pc and
        // 12.7 dB on bpsk300-il2pc, and the single number was the average of two unrelated
        // measurements. Mode is on the series rather than on an info metric to join to, because
        // InfluxQL - half of a common setup, both halves scraping the same endpoint - has no
        // join and could not recover it at all.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("GB7BPQ", 0), Quality(crcValid: true, snr: 15.2, mode: "afsk300-il2pc"));
        telemetry.Record(2, Frame("GB7BPQ", 0), Quality(crcValid: true, snr: 12.7, mode: "bpsk300-il2pc"));

        string text = telemetry.Exposition();
        text.Should().Contain("pdn_station_snr_db_sum{station=\"GB7BPQ\",mode=\"afsk300-il2pc\"} 15.2");
        text.Should().Contain("pdn_station_snr_db_sum{station=\"GB7BPQ\",mode=\"bpsk300-il2pc\"} 12.7");
        telemetry.StationCount.Should().Be(2, "two links, and the SSID rule does not merge them");
    }

    [Fact]
    public void A_Station_That_Has_Stopped_Transmitting_Stops_Being_A_Series()
    {
        // Rather than holding its last reading for ever. Prometheus marks a vanished series
        // stale, which is the truth: we do not know what that station is doing.
        var time = new FakeTime(Start);
        var telemetry = new StationTelemetry(time, stationIdle: TimeSpan.FromHours(6));

        telemetry.Record(0, Frame("PD1HBL", 8), Quality(crcValid: true, snr: 15));
        telemetry.Exposition().Should().Contain("PD1HBL");

        time.Advance(TimeSpan.FromHours(7));
        telemetry.Exposition().Should().NotContain("PD1HBL");
        telemetry.Exposition().Should().Contain("pdn_stations 0");
    }

    [Fact]
    public void Each_Frame_Is_Its_Own_Point_With_The_Moment_It_Was_Heard()
    {
        // The whole reason for the second format. Three frames a minute apart are three points
        // at three times - which is what a scatter plot of individual frames needs, and what one
        // sample per series per scrape cannot express.
        var time = new FakeTime(Start);
        var telemetry = new StationTelemetry(time);

        for (int i = 0; i < 3; i++)
        {
            telemetry.Record(2, Frame("GB7OXF", 2), Quality(crcValid: true, snr: 10 + i));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        string[] points = telemetry.LineProtocol().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        points.Should().HaveCount(3);
        points.Should().OnlyContain(p => p.StartsWith("pdn_frame,station=GB7OXF", StringComparison.Ordinal));

        long[] stamps = [.. points.Select(
            p => long.Parse(p.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture))];
        stamps.Should().BeInAscendingOrder();
        (stamps[1] - stamps[0]).Should().Be(60_000_000_000L, "a minute, in nanoseconds");
    }

    [Fact]
    public void The_Frame_Feed_Is_A_Window_So_Overlapping_Scrapes_Are_Safe()
    {
        // A window is served, not a queue: nothing is consumed by being read, so a collector
        // that misses a scrape loses nothing and one that scrapes twice sees some frames twice.
        // That is harmless because InfluxDB identifies a point by measurement, tags and
        // timestamp - a repeated write of an identical point replaces it.
        var time = new FakeTime(Start);
        var telemetry = new StationTelemetry(time, eventWindow: TimeSpan.FromMinutes(5));

        telemetry.Record(0, Frame("GB7BPQ", 0), Quality(crcValid: true, snr: 15));

        telemetry.LineProtocol().Should().Be(telemetry.LineProtocol(), "reading does not consume");

        time.Advance(TimeSpan.FromMinutes(6));
        telemetry.LineProtocol().Should().BeEmpty("and the window bounds it");
    }

    [Fact]
    public void The_Station_List_Is_Capped_And_Sheds_The_Least_Recently_Heard()
    {
        var time = new FakeTime(Start);
        var telemetry = new StationTelemetry(time, maxStations: 3);

        foreach (string call in new[] { "AAAAAA", "BBBBBB", "CCCCCC" })
        {
            telemetry.Record(0, Frame(call, 0), Quality(crcValid: true));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        telemetry.Record(0, Frame("DDDDDD", 0), Quality(crcValid: true));

        telemetry.StationCount.Should().Be(3);
        telemetry.Exposition().Should().Contain("DDDDDD").And.NotContain("station=\"AAAAAA\"");
    }

    [Fact]
    public void A_Station_Below_Our_Centre_Never_Makes_A_Counter_Go_Backwards()
    {
        // Frequency offset is signed, and a counter that decreases is a counter Prometheus reads
        // as having reset: rate() then returns a large positive number from the value after the
        // drop. On the live 40 m station seven of twelve station-and-mode pairs sat low, so a
        // single signed sum charted plausibly for the stations above centre and was nonsense for
        // the ones below. Split by sign, both halves only ever go up.
        var time = new FakeTime(Start);
        var telemetry = new StationTelemetry(time);

        telemetry.Record(2, Frame("GB7BPQ", 0), Quality(crcValid: true, offset: -18.5));
        string first = telemetry.Exposition();
        telemetry.Record(2, Frame("GB7BPQ", 0), Quality(crcValid: true, offset: -21.5));
        telemetry.Record(2, Frame("GB7BPQ", 0), Quality(crcValid: true, offset: 4));

        string second = telemetry.Exposition();
        Value(first, "pdn_station_frequency_offset_below_hz_sum").Should().Be(18.5);
        Value(second, "pdn_station_frequency_offset_below_hz_sum").Should().Be(40);
        Value(second, "pdn_station_frequency_offset_above_hz_sum").Should().Be(4);
        Value(second, "pdn_station_frames_with_offset_total").Should().Be(3);

        // Which is what a dashboard divides out: (above - below) / count = (4 - 40) / 3 = -12 Hz.
        ((Value(second, "pdn_station_frequency_offset_above_hz_sum")
            - Value(second, "pdn_station_frequency_offset_below_hz_sum"))
            / Value(second, "pdn_station_frames_with_offset_total"))
            .Should().BeApproximately(-12, 0.001);
    }

    [Fact]
    public void Every_Station_Publishes_Its_Latest_Offset_Beside_Its_Latest_Snr()
    {
        // The "right now, how far off is that station" readout, matching the SNR one. A point
        // reading rather than a series, and named so that anyone charting it can see that.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(2, Frame("GB7OXF", 0), Quality(crcValid: true, snr: 14.7, offset: -2.9));
        telemetry.Record(2, Frame("EI0RSI", 1), Quality(crcValid: true, snr: 18, offset: 2.2));

        string text = telemetry.Exposition();
        text.Should().Contain("# TYPE pdn_station_frequency_offset_hz_last gauge");
        text.Should().Contain(
            "pdn_station_frequency_offset_hz_last{station=\"GB7OXF\",mode=\"afsk300-il2pc\"} -2.9");
        text.Should().Contain(
            "pdn_station_frequency_offset_hz_last{station=\"EI0RSI\",mode=\"afsk300-il2pc\"} 2.2");
    }

    [Fact]
    public void A_Station_Whose_Offset_Was_Never_Measured_Reads_As_Unmeasured_Not_As_Zero()
    {
        // A single decoder measures no carrier offset, so its stations have none. Nothing may
        // report that as "dead on frequency": the last-reading gauge is simply absent, and the
        // divisor is zero, so the mean divides to no point at all rather than to 0 Hz.
        var telemetry = new StationTelemetry(new FakeTime(Start));

        telemetry.Record(0, Frame("PD4R", 0), Quality(crcValid: true, snr: 20.6));

        string text = telemetry.Exposition();
        text.Should().Contain("pdn_station_snr_db_last{station=\"PD4R\"");
        text.Should().NotContain("pdn_station_frequency_offset_hz_last{station=\"PD4R\"");
        Value(text, "pdn_station_frames_with_offset_total").Should().Be(0);
    }

    [Fact]
    public void The_Exposition_Is_Well_Formed_Prometheus_Text()
    {
        // Every metric carries HELP and TYPE, and every value parses as a number. A scraper that
        // cannot parse this reports nothing and says nothing about why.
        var telemetry = new StationTelemetry(new FakeTime(Start));
        telemetry.Record(0, Frame("GB7BWR", 2), Quality(crcValid: true, snr: 16.4, offset: -3.5, corrected: 2));

        string[] lines = telemetry.Exposition().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
            {
                declared.Add(line.Split(' ')[2]);
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            declared.Should().Contain(line.Split('{', ' ')[0], "every series needs a TYPE line");
            double.TryParse(
                line[(line.LastIndexOf(' ') + 1)..],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out _).Should().BeTrue($"'{line}' must end in a number");
        }

        lines.Count(l => l.StartsWith("# HELP", StringComparison.Ordinal)).Should().BeGreaterThan(5);
    }

    /// <summary>The value of the one series with this name, as a scraper would read it.</summary>
    private static double Value(string exposition, string metric) =>
        double.Parse(
            exposition.Split('\n')
                .Single(l => l.StartsWith(metric + "{", StringComparison.Ordinal))
                .Split(' ')[^1],
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An AX.25 UI frame from a station, which is all this needs.</summary>
    private static byte[] Frame(string callsign, int ssid)
    {
        var frame = new List<byte>();
        foreach (char c in "APDN  ")
        {
            frame.Add((byte)(c << 1));
        }

        frame.Add(0xE0);
        foreach (char c in callsign.PadRight(6))
        {
            frame.Add((byte)(c << 1));
        }

        frame.Add((byte)(0x61 | (ssid << 1)));
        frame.Add(0x03);
        frame.Add(0xF0);
        return [.. frame];
    }

    private static FrameQuality Quality(
        bool? crcValid = null, bool plainIl2p = false, bool monitorOnly = false,
        double? snr = null, double? offset = null, int? corrected = null,
        string mode = "afsk300-il2pc") =>
        new(mode, 20, corrected, crcValid, offset, PlainIl2p: plainIl2p,
            MonitorOnly: monitorOnly, SnrDb: snr);

    /// <summary>A clock the test moves by hand. No test here may decide anything by the wall
    /// clock: a station's idle window is hours and a suite cannot wait for one.</summary>
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
