using System.Globalization;
using System.Text;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Telemetry;

/// <summary>One frame, as a monitoring system wants it: when, from whom, and how well it read.</summary>
/// <param name="HeardAt">UTC, when the frame was decoded.</param>
/// <param name="Station">Base callsign, SSIDs combined - see <see cref="StationCallsign"/>.</param>
/// <param name="Ssid">The SSID it arrived under, kept as a detail rather than a series key.</param>
/// <param name="Mode">The mode that read it.</param>
/// <param name="SubChannel">The modem that read it.</param>
/// <param name="SnrDb">Signal-to-noise, where the receiver measured one.</param>
/// <param name="FrequencyOffsetHz">How far the sender sat from our centre - the same measurement
/// frequency matching runs on, and the one that shows a drifting reference over days.</param>
/// <param name="CorrectedBytes">Bytes Reed-Solomon repaired: the leading indicator, which moves
/// long before a link starts losing frames outright.</param>
/// <param name="Bytes">Frame length.</param>
public sealed record FrameEvent(
    DateTimeOffset HeardAt,
    string Station,
    string Ssid,
    string Mode,
    int SubChannel,
    double? SnrDb,
    double? FrequencyOffsetHz,
    int? CorrectedBytes,
    int Bytes);

/// <summary>
/// What this station has heard, in a shape a monitoring system can take.
/// </summary>
/// <remarks>
/// <para><b>Two exports, because they answer different questions and one format cannot do both.</b>
/// <see cref="Exposition"/> is Prometheus text: totals and sums, aggregated, which is what
/// alerting and rate() want. <see cref="LineProtocol"/> is InfluxDB line protocol: one point per
/// frame, carrying the moment it was actually heard, which is the only way to plot individual
/// frames as points. A scrape-and-aggregate format fundamentally cannot represent per-event data
/// - it has one sample per series per scrape - so asking it to is asking the wrong question of
/// the right tool.</para>
/// <para><b>Both are pull.</b> Nothing here knows the address, protocol or credentials of any
/// monitoring system, which is what keeps this generic: a station serves what it knows and
/// whoever is interested comes and reads it.</para>
/// <para><b>Only frames whose own check sequence verified are counted.</b> That is not tidiness,
/// it is the difference between a station list and a list of this receiver's bit errors. Of the
/// 77 distinct callsigns the live 40 m station had ever decoded, 45 were heard exactly once and
/// <em>not one of those 45 ever had a valid CRC</em> - they are corruptions (<c>EI0RSI-9</c>,
/// <c>EI0RSA-12</c> and <c>EI0RSE-1</c> are all <c>EI0RSI-1</c> with a bit wrong; <c>7B7BPQ</c>
/// is <c>GB7BPQ</c>). All 21 stations heard twenty times or more had one. Without the gate every
/// bit error mints a series that exists for ever and appears on a dashboard as a station.</para>
/// <para><b>Threading.</b> Frames arrive on the receive path and exports are read by an HTTP
/// listener, so everything here is under one lock. The receive path's work is a dictionary
/// lookup and some additions.</para>
/// </remarks>
public sealed class StationTelemetry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Callsign, string Mode), Station> _stations = [];
    private readonly Queue<FrameEvent> _events = [];
    private readonly TimeProvider _time;
    private readonly int _maxStations;
    private readonly TimeSpan _eventWindow;
    private readonly TimeSpan _stationIdle;
    private long _uncounted;

    /// <param name="time">Clock, injected for tests.</param>
    /// <param name="maxStations">Most stations kept. On reaching it the least recently heard is
    /// dropped - a backstop, since the check-sequence gate is what actually bounds this.</param>
    /// <param name="eventWindow">How long a frame stays in <see cref="LineProtocol"/>. Must
    /// comfortably exceed the scrape interval: a consumer that scrapes more slowly than this
    /// misses frames, and one that scrapes faster sees each frame more than once, which is
    /// harmless (see <see cref="LineProtocol"/>).</param>
    /// <param name="stationIdle">How long a station stays in <see cref="Exposition"/> after its
    /// last frame. A station that has stopped transmitting should stop being a series rather than
    /// hold its last value for ever - see the remarks there.</param>
    public StationTelemetry(
        TimeProvider? time = null,
        int maxStations = 256,
        TimeSpan? eventWindow = null,
        TimeSpan? stationIdle = null)
    {
        _time = time ?? TimeProvider.System;
        _maxStations = maxStations;
        _eventWindow = eventWindow ?? TimeSpan.FromMinutes(5);
        _stationIdle = stationIdle ?? TimeSpan.FromHours(6);
    }

    /// <summary>Frames declined because nothing vouched for them - see the type remarks. A large
    /// number beside a small station list is a receiver working at its limit, which is worth
    /// knowing and is why it is counted rather than silently dropped.</summary>
    public long Uncounted
    {
        get { lock (_lock) { return _uncounted; } }
    }

    /// <summary>
    /// Where the receive loop's keeping-up figures come from (issue #649), published as the
    /// <c>pdn_receive_*</c> series. Null leaves them out: a host that runs no receive loop of its
    /// own, or more than one, has no single answer to give.
    /// </summary>
    public Func<ReceiveRateSnapshot>? ReceiveRate { get; set; }

    /// <summary>Stations currently held.</summary>
    public int StationCount
    {
        get { lock (_lock) { return _stations.Count; } }
    }

    /// <summary>
    /// Records a decoded frame. Called from the receive path.
    /// </summary>
    /// <param name="subChannel">The modem that read it.</param>
    /// <param name="frame">The frame, for its addresses.</param>
    /// <param name="quality">The receiver's own account of the decode.</param>
    public void Record(int subChannel, ReadOnlySpan<byte> frame, FrameQuality quality)
    {
        if (!DecodeConfidence.IsEvidence(quality)
            || !Waterfall.Ax25AddressParser.TryParse(frame, out string source, out _)
            || source.Length == 0)
        {
            lock (_lock)
            {
                _uncounted++;
            }

            return;
        }

        (string callsign, string ssid) = StationCallsign.Split(source);
        DateTimeOffset now = _time.GetUtcNow();
        var heard = new FrameEvent(
            now, callsign, ssid, quality.Mode, subChannel,
            quality.SnrDb, quality.FrequencyOffsetHz, quality.CorrectedBytes, quality.FrameBytes);

        lock (_lock)
        {
            // Keyed by station AND mode. The same callsign heard on two modes is two links -
            // different frequency, different modem, different path budget - and averaging them
            // is averaging two unrelated measurements: on the live 40 m station GB7BPQ reads
            // 15.2 dB on afsk300-il2pc and 12.7 dB on bpsk300-il2pc, and one number covering
            // both describes neither.
            (string Callsign, string Mode) key = (callsign, quality.Mode);
            if (!_stations.TryGetValue(key, out Station? station))
            {
                Evict(now);
                station = new Station(callsign, quality.Mode);
                _stations[key] = station;
            }

            station.Add(heard);
            _events.Enqueue(heard);
            TrimEvents(now);
        }
    }

    /// <summary>
    /// The Prometheus text exposition: totals and sums, for rates and alerting.
    /// </summary>
    /// <remarks>
    /// <para><b>Counters, not gauges, and the reason matters.</b> A last-value gauge for SNR
    /// looks like the obvious thing and is a trap: a station transmitting every few minutes
    /// against a fifteen-second scrape holds its reading across every scrape in between, so a
    /// chart draws a flat line and a long-retention store keeps it for ever. One sample smeared
    /// across an hour reads exactly like a continuous measurement of a quiet channel. A sum and
    /// a count divide into the mean over whatever window is asked for, and produce nothing at
    /// all when nothing was heard, which is the truth:</para>
    /// <code>
    /// rate(pdn_station_snr_db_sum[10m]) / rate(pdn_station_frames_total[10m])
    /// </code>
    /// <para>A <c>_last</c> gauge is published too, for a "right now" readout, and is named so
    /// that anyone building a time series on it can see what they are doing.</para>
    /// <para><b>Frequency offset is signed, so it takes two counters rather than one.</b> A
    /// station below our centre contributes a negative number, a single running sum of them
    /// goes <em>down</em>, and a counter that goes down is a counter that reset: Prometheus
    /// reads the drop as a restart and <c>rate()</c> returns a large positive rate from the
    /// value after it. The live 40 m station is where that showed - seven of its twelve
    /// station-and-mode pairs sat low, so the mean-offset chart read plausibly for the stations
    /// above centre and was nonsense for the ones below, which is the worst way for a metric to
    /// be wrong. So the sums are split by sign, each monotonic, and the mean is the difference
    /// over the count:</para>
    /// <code>
    /// (rate(pdn_station_frequency_offset_above_hz_sum[10m])
    ///  - rate(pdn_station_frequency_offset_below_hz_sum[10m]))
    /// / rate(pdn_station_frames_with_offset_total[10m])
    /// </code>
    /// <para>Splitting rather than reaching for a gauge keeps the property that matters: it
    /// still produces nothing at all over a window in which the station was not heard.</para>
    /// <para><b>Mode is a label on every series, not something to join on.</b> The tidy
    /// Prometheus idiom is an info metric carrying the detail and a
    /// <c>group_left</c> join to bring it in, and it was the wrong choice here for two reasons.
    /// The join is PromQL: InfluxQL has none, so anything reading these through Telegraf and
    /// InfluxDB - which is half of a common setup, both scraping the same endpoint - could not
    /// recover the mode at all. And it is not really a join anyway: one callsign heard on two
    /// modes is two links, over different frequencies through different modems, and a series
    /// spanning both describes neither. <c>pdn_station_info</c> stays, for the sub-channel and
    /// as the list of what is currently heard.</para>
    /// </remarks>
    public string Exposition()
    {
        var text = new StringBuilder();
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();
            Station[] live = [.. _stations.Values
                .Where(s => now - s.LastHeard <= _stationIdle)
                .OrderBy(s => s.Callsign, StringComparer.Ordinal)
                .ThenBy(s => s.Mode, StringComparer.Ordinal)];

            Metric(text, "pdn_station_info", "gauge",
                "1 per station and mode, carrying the sub-channel it was last heard on.");
            foreach (Station s in live)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"pdn_station_info{{station=\"{Escape(s.Callsign)}\",mode=\"{Escape(s.Mode)}\",sub_channel=\"{s.SubChannel}\"}} 1\n");
            }

            Series(text, live, "pdn_station_frames_total", "counter",
                "Frames received from this station whose own check sequence verified.",
                s => s.Frames);
            Series(text, live, "pdn_station_bytes_total", "counter",
                "Bytes in those frames.", s => s.Bytes);
            Series(text, live, "pdn_station_snr_db_sum", "counter",
                "Sum of per-frame SNR. Divide by pdn_station_frames_with_snr_total for the mean.",
                s => s.SnrSum);
            Series(text, live, "pdn_station_frames_with_snr_total", "counter",
                "Frames whose receiver reported an SNR - the divisor for pdn_station_snr_db_sum.",
                s => s.SnrCount);
            Series(text, live, "pdn_station_frequency_offset_above_hz_sum", "counter",
                "Sum of how far this station sat above our centre, counting only the frames "
                + "where it was above. See pdn_station_frequency_offset_below_hz_sum.",
                s => s.OffsetAboveSum);
            Series(text, live, "pdn_station_frequency_offset_below_hz_sum", "counter",
                "The same below our centre, as a positive number. Subtract it from the above "
                + "sum and divide by pdn_station_frames_with_offset_total for the mean offset; "
                + "drift over days is a reference going off.",
                s => s.OffsetBelowSum);
            Series(text, live, "pdn_station_frames_with_offset_total", "counter",
                "Frames whose receiver measured an offset - the divisor for the two offset sums.",
                s => s.OffsetCount);
            Series(text, live, "pdn_station_corrected_bytes_total", "counter",
                "Bytes Reed-Solomon repaired: rises long before a link starts losing frames.",
                s => s.Corrected);
            Series(text, live, "pdn_station_snr_db_last", "gauge",
                "SNR of the most recent frame. A point reading, not a time series - it holds "
                + "its value between transmissions; build charts on the _sum and _total pair.",
                s => s.LastSnr ?? double.NaN);
            Series(text, live, "pdn_station_frequency_offset_hz_last", "gauge",
                "How far the most recent frame sat from our centre, positive above. A point "
                + "reading, not a time series - it holds its value between transmissions; build "
                + "charts on the two offset sums and pdn_station_frames_with_offset_total.",
                s => s.LastOffset ?? double.NaN);

            Metric(text, "pdn_frames_uncounted_total", "counter",
                "Decodes not attributed to any station: no verified check sequence, or no "
                + "readable address. Large beside a small station list means a receiver at its limit.");
            text.Append(CultureInfo.InvariantCulture, $"pdn_frames_uncounted_total {_uncounted}\n");

            Metric(text, "pdn_stations", "gauge", "Stations currently held.");
            text.Append(CultureInfo.InvariantCulture, $"pdn_stations {live.Length}\n");

            if (ReceiveRate?.Invoke() is { } receive)
            {
                AppendReceiveRate(text, receive);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The recent frames as InfluxDB line protocol - one point each, stamped with when it was
    /// actually heard.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a second format.</b> Plotting individual frames as points needs each frame to
    /// carry its own moment. A scrape-and-aggregate format has one sample per series per scrape
    /// and cannot express two frames a second apart; line protocol is timestamped events by
    /// construction, and Telegraf's HTTP input pulls it, so this stays pull like everything
    /// else.</para>
    /// <para><b>Overlapping scrapes are safe.</b> A window is served, not a queue, so nothing is
    /// consumed by being read and a consumer that misses a scrape loses nothing. Reading the same
    /// frame twice is harmless because InfluxDB identifies a point by measurement, tags and
    /// timestamp: a repeated write of an identical point replaces it. Timestamps are unique per
    /// station by construction (frames from one station cannot overlap in time), which is what
    /// makes that hold.</para>
    /// <para><b>Tags are the series key, fields are not.</b> Station, mode and sub-channel are
    /// tags because charts group by them; SSID, SNR, offset and lengths are fields, so a node
    /// cycling SSIDs adds detail rather than series.</para>
    /// </remarks>
    public string LineProtocol()
    {
        var text = new StringBuilder();
        lock (_lock)
        {
            TrimEvents(_time.GetUtcNow());
            foreach (FrameEvent e in _events)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"pdn_frame,station={Tag(e.Station)},mode={Tag(e.Mode)},sub_channel={e.SubChannel}");
                var fields = new List<string> { $"bytes={e.Bytes}i" };
                if (e.Ssid.Length > 0)
                {
                    fields.Add($"ssid=\"{e.Ssid}\"");
                }

                if (e.SnrDb is double snr)
                {
                    fields.Add(string.Create(CultureInfo.InvariantCulture, $"snr_db={snr}"));
                }

                if (e.FrequencyOffsetHz is double offset)
                {
                    fields.Add(string.Create(CultureInfo.InvariantCulture, $"offset_hz={offset}"));
                }

                if (e.CorrectedBytes is int corrected)
                {
                    fields.Add($"corrected_bytes={corrected}i");
                }

                text.Append(CultureInfo.InvariantCulture,
                    $" {string.Join(",", fields)} {e.HeardAt.ToUnixTimeMilliseconds() * 1_000_000L}\n");
            }
        }

        return text.ToString();
    }

    private static void AppendReceiveRate(StringBuilder text, ReceiveRateSnapshot receive)
    {
        Metric(text, "pdn_receive_samples_total", "counter",
            "Samples the audio input delivered to the receive loop, over pdn_receive_seconds_total.");
        text.Append(CultureInfo.InvariantCulture, $"pdn_receive_samples_total {receive.Samples}\n");
        Metric(text, "pdn_receive_seconds_total", "counter",
            "Wall time the receive loop was measured over, leaving out spans while keyed or "
            + "deliberately idle. rate(samples) / rate(seconds) / the sample rate is the share of "
            + "real time reaching the modems.");
        text.Append(CultureInfo.InvariantCulture, $"pdn_receive_seconds_total {receive.Seconds:R}\n");
        Metric(text, "pdn_receive_busy_seconds_total", "counter",
            "Of pdn_receive_seconds_total, the time the loop spent processing rather than waiting "
            + "for audio. Close to it means the loop is the bottleneck.");
        text.Append(CultureInfo.InvariantCulture, $"pdn_receive_busy_seconds_total {receive.BusySeconds:R}\n");
        if (receive.RealTimeRatio is double ratio)
        {
            Metric(text, "pdn_receive_realtime_ratio", "gauge",
                "Share of real time that reached the modems over the last minute; 1 is keeping up.");
            text.Append(CultureInfo.InvariantCulture, $"pdn_receive_realtime_ratio {ratio:R}\n");
        }

        Metric(text, "pdn_receive_behind", "gauge",
            "1 while the station is reported as behind real time (losing received audio), else 0.");
        text.Append(CultureInfo.InvariantCulture, $"pdn_receive_behind {(receive.Behind ? 1 : 0)}\n");
        if (receive.InputLosses is long lost)
        {
            Metric(text, "pdn_receive_input_losses_total", "counter",
                "Times the audio input itself counted audio as lost: capture overruns, missing DAX packets, breaks in a receiver's stream.");
            text.Append(CultureInfo.InvariantCulture, $"pdn_receive_input_losses_total {lost}\n");
        }
    }

    private static void Metric(StringBuilder text, string name, string type, string help)
    {
        text.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} {type}\n");
    }

    private static void Series(
        StringBuilder text, Station[] live, string name, string type, string help,
        Func<Station, double> value)
    {
        Metric(text, name, type, help);
        foreach (Station s in live)
        {
            double v = value(s);
            if (double.IsNaN(v))
            {
                continue;   // never measured; a series that does not exist beats one reading NaN
            }

            text.Append(CultureInfo.InvariantCulture,
                $"{name}{{station=\"{Escape(s.Callsign)}\",mode=\"{Escape(s.Mode)}\"}} "
                + $"{v.ToString("0.###", CultureInfo.InvariantCulture)}\n");
        }
    }

    /// <summary>Prometheus label values escape backslash, quote and newline, and nothing else.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Line protocol tag values escape comma, equals and space.</summary>
    private static string Tag(string value) =>
        value.Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);

    private void TrimEvents(DateTimeOffset now)
    {
        while (_events.Count > 0 && now - _events.Peek().HeardAt > _eventWindow)
        {
            _events.Dequeue();
        }
    }

    private void Evict(DateTimeOffset now)
    {
        if (_stations.Count < _maxStations)
        {
            return;
        }

        (string, string)? oldest = null;
        DateTimeOffset when = DateTimeOffset.MaxValue;
        foreach (KeyValuePair<(string Callsign, string Mode), Station> entry in _stations)
        {
            if (entry.Value.LastHeard < when)
            {
                (when, oldest) = (entry.Value.LastHeard, entry.Key);
            }
        }

        if (oldest is { } key)
        {
            _stations.Remove(key);
        }
    }

    private sealed class Station(string callsign, string mode)
    {
        public string Callsign { get; } = callsign;

        /// <summary>Part of the key, so it never changes for a given series.</summary>
        public string Mode { get; } = mode;

        public int SubChannel { get; private set; }

        public DateTimeOffset LastHeard { get; private set; }

        public long Frames { get; private set; }

        public long Bytes { get; private set; }

        public double SnrSum { get; private set; }

        public long SnrCount { get; private set; }

        /// <summary>Offsets above centre only, so this never decreases - see
        /// <see cref="OffsetBelowSum"/>.</summary>
        public double OffsetAboveSum { get; private set; }

        /// <summary>Offsets below centre, as magnitudes, so this never decreases either.</summary>
        public double OffsetBelowSum { get; private set; }

        public long OffsetCount { get; private set; }

        public long Corrected { get; private set; }

        public double? LastSnr { get; private set; }

        public double? LastOffset { get; private set; }

        public void Add(FrameEvent e)
        {
            Frames++;
            Bytes += e.Bytes;
            SubChannel = e.SubChannel;
            LastHeard = e.HeardAt;
            if (e.SnrDb is double snr)
            {
                SnrSum += snr;
                SnrCount++;
                LastSnr = snr;
            }

            if (e.FrequencyOffsetHz is double offset)
            {
                if (offset >= 0)
                {
                    OffsetAboveSum += offset;
                }
                else
                {
                    OffsetBelowSum -= offset;
                }

                OffsetCount++;
                LastOffset = offset;
            }

            Corrected += e.CorrectedBytes ?? 0;
        }
    }
}
