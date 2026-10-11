using Packet.SoundModem.Modems;
using System.Globalization;

namespace Packet.SoundModem.Ota;

/// <summary>
/// <c>sm-ota sim</c> - the pure-software BER-vs-SNR baseline: render a burst for any
/// <c>ModemCatalog</c> mode, push it through calibrated AWGN or a Watterson fading channel at a
/// known SNR, decode it, and tally frame/packet success. No radio, no capture, no self-calibrating
/// noise lead-in - the SNR is injected directly, so a run is deterministic and fast.
/// </summary>
/// <remarks>
/// This is the sim half of the campaign methodology the MS110D <c>ladder</c> drives over the air:
/// the same <see cref="Packet.SoundModem.Tests.Channel.WattersonChannel"/> rig, the same 3&#160;kHz
/// SNR convention, generalized off the MS110D-specific path so it covers the FreeDV datac OFDM
/// family (and, through the <c>IModem</c> seam, any other catalogue mode). It produces the
/// AWGN / Poor / Good waterfalls a Phase-B on-air readiness call rests on.
/// </remarks>
internal static class SimCommand
{
    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a is null || a.Has("help") || !a.Has("mode"))
        {
            Console.Error.WriteLine("""
                sm-ota sim --mode <catalogue-mode> --snr <a,b,c> [options]

                Renders bursts, injects a channel at a known SNR (3 kHz noise bandwidth), decodes,
                and tallies frame/packet success - the software BER-vs-SNR baseline.

                  --mode <name>        ModemCatalog mode (e.g. freedv-datac0 … freedv-datac14).
                                       Any catalogue mode works at the frame layer. ARDOP
                                       (a session TNC, not a catalogue mode) is driven as
                                       ardop:<FrameName>, e.g. ardop:4FSK.500.100 - one
                                       full-capacity even data frame per burst at the native
                                       12 kHz, scored payload byte-exact; --frame-bytes and
                                       --layer do not apply (the type fixes the payload).
                  --snr <a,b,c>        SNR rungs in dB (3 kHz), ascending
                  --channel <list>     awgn|good|moderate|poor|fm-mic|fm-data, comma-separated
                                       (default awgn). The two fm-* channels put an FM mode
                                       through a real FM link (modulate, noise on the carrier,
                                       limit, discriminate) rather than the linear SSB path;
                                       there --snr means carrier-to-noise ratio in the receiver
                                       IF bandwidth, which is NOT the SSB modes' SNR3k and must
                                       not be compared with it. fm-mic is a microphone/speaker
                                       path (emphasis, 300-3000 Hz); fm-data is a data port
                                       (flat, wide). Deviation comes from the mode, per
                                       docs/dev/mode-modulation-reference.md
                  --cfo <list>         carrier-offset Hz, comma-separated sweep axis (default 0)
                  --impulse <rate>     measured-calibrated QRN, broadband crashes per minute
                                       (0 off; the 40 m campaign's anchors: 120 = ordinary
                                       summer evening, 220 = evening/sunrise peak)
                  --detector <name>    coherent|differential|mlse override for bpsk*/qpsk*
                                       modes (mlse is bpsk-only - rx-roadmap workstream 5)
                  --detector2 <name>   ensemble second detector for the bpsk banks: every
                                       diversity position gets a branch under each detector
                                       and the bank delivers the union (workstream 2)
                  --centre <Hz>        audio-centre override for the modes that take one,
                                       including ms110d-*/freedv-* via the shift decorator -
                                       the A/B instrument for the moved-modem BER gate
                  --layer frame|packet frame = full IL2P+CRC frame through the IModem surface
                                       (the deployment metric, any mode). packet = one raw datac
                                       packet through the library's own DatacReceiver, scored on
                                       CRC + post-LDPC coded BER (the FreeDV cross-check; datac
                                       modes only). Default frame.
                  --bursts <n>         independent trials per point (default 100)
                  --levels <a,b,c>     absolute input-level scales in dB (default 0). At a fixed
                                       SNR this is the level-invariance probe: signal and noise
                                       scale together so the SNR is unchanged; a level-sensitive
                                       front end (no input AGC) degrades as the level falls. Runs
                                       as an inner axis at every SNR rung.
                  --frame-bytes <n>    AX.25 frame size, frame layer (default 60)
                  --txdelay <ms>       flag/preamble run-in before each frame (default 0). The fast
                                       classic-HDLC FSK detectors (fsk9600/c4fsk9600/c4fsk19200)
                                       cannot lock onto 2 opening flags; pass ~150 to characterise
                                       them. 0 keeps the datac/MS110D baseline byte-identical
                  --rate <Hz>          DSP rate. Default 8000 for freedv-* (engine-native, the rate
                                       FreeDV's own figures are measured at), else DspRateFor(mode).
                                       Pass 48000 to exercise the ×6/÷6 deployment path.
                  --bridged            build the mode as a station's channel does at --rate: at its
                                       own rate behind the rate bridge when --rate is faster (#648)
                  --adpcm              put the receive audio through an OpenWebRX receiver's
                                       4-bit IMA ADPCM round trip before the modem hears it
                                       (#599): brought to a level, coded, decoded. Frame layer,
                                       12 kHz modes only; A/B it against the same run without
                  --adpcm-level <dBFS> the RMS level the round trip codes at (default -20)
                  --seed <n>           first burst seed (default 1)
                  --workers <n>        bounded parallelism (default 4 - shared box)
                  --csv <path>         write one row per point
                  --quiet              suppress the per-point progress lines

                Every point carries its own fade/noise realisation derived from the burst seed, so a
                point reproduces from (mode, channel, SNR, seed, count) alone.
                """);
            return a is null ? 2 : 0;
        }

        string mode = a.Req("mode");
        double[] snrs = a.Req("snr").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        SimChannelKind[] channels = a.Str("channel", "awgn").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(SimChannel.Parse).ToArray();
        SimLayer layer = a.Str("layer", "frame").StartsWith('p') ? SimLayer.Packet : SimLayer.Frame;
        double[] levels = a.Str("levels", "0").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        double[] cfos = a.Str("cfo", "0").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        int bursts = a.Int("bursts", 100);
        int frameBytes = a.Int("frame-bytes", 60);
        int? rate = a.Has("rate") ? a.Int("rate", 8000) : null;
        int firstSeed = a.Int("seed", 1);
        int workers = a.Int("workers", 4);
        int txDelayMs = a.Int("txdelay", 0);
        PskDetector? detector = a.Str("detector", null) is { } d
            ? d.StartsWith('d') ? PskDetector.Differential
                : d.StartsWith('m') ? PskDetector.Mlse
                : PskDetector.Coherent
            : null;
        double impulseRate = a.Dbl("impulse", 0);
        PskDetector? detector2 = a.Str("detector2", null) is { } d2
            ? d2.StartsWith('d') ? PskDetector.Differential
                : d2.StartsWith('m') ? PskDetector.Mlse
                : PskDetector.Coherent
            : null;
        double? centreHz = a.Has("centre") ? a.Dbl("centre", 0) : null;
        bool bridged = a.Has("bridged");
        bool adpcm = a.Has("adpcm");
        double adpcmLevelArg = a.Dbl("adpcm-level", AdpcmRoundTrip.DefaultLevelDbfs);
        double? adpcmLevel = adpcm ? adpcmLevelArg : null;
        bool quiet = a.Has("quiet");
        string? csvPath = a.Str("csv", null);

        // Every flag this command recognises has now been read at least once (including the
        // optional ones above, via Has/Str(null)) - so any key still unread here is a flag the
        // caller passed that sim never looked at: mistyped, or not supported by this build. That
        // must fail now, before a single burst runs, or the run reports numbers for whatever
        // experiment the defaults happened to describe rather than the one asked for.
        a.RejectUnknown("sim");

        // Native 8 kHz is the right default for the datac family: it is codec2's own rate and what
        // FreeDV's published operating points are measured at. ARDOP is hard 12 kHz. Other modes
        // take their catalogue rate.
        int effectiveRate = rate ?? (mode.StartsWith("freedv-", StringComparison.Ordinal)
            ? 8000
            : ArdopFrameProbe.IsArdopMode(mode)
                ? ArdopFrameProbe.Rate
                : Packet.SoundModem.Modems.ModemCatalog.DspRateFor(mode));
        int? rateArg = layer == SimLayer.Packet ? null : effectiveRate;

        void Log(string m)
        {
            if (!quiet)
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {m}");
            }
        }

        Log($"sim {mode} layer={layer} rate={(layer == SimLayer.Packet ? 8000 : effectiveRate)} "
            + $"bursts={bursts} frameBytes={frameBytes} workers={workers}");
        Log($"channels: {string.Join(',', channels)}  snrs: {string.Join(',', snrs)}"
            + (adpcmLevel is double l ? $"  adpcm round trip at {l:0.#} dBFS" : ""));

        bool levelScan = levels.Length > 1 || levels[0] != 0;
        var rows = new List<SimPointResult>();
        foreach (SimChannelKind kind in channels)
        {
            var levelZeroCurve = new List<SimPointResult>();
            foreach (double snr in snrs)
            {
                foreach (double level in levels)
                {
                    foreach (double cfo in cfos)
                    {
                    SimPointResult r = SimBench.RunPoint(
                        mode, rateArg, layer, kind, snr, bursts, frameBytes, firstSeed, workers, level,
                        txDelayMs, cfo, detector, centreHz, detector2, impulseRate, bridged, adpcmLevel);
                    rows.Add(r);
                    if (level == 0)
                    {
                        levelZeroCurve.Add(r);
                    }

                    (double lo, double hi) = r.SuccessCi;
                    string lvl = levelScan ? $" lvl {level,+5:+0.0;-0.0} dB" : "";
                    string cfoTag = cfos.Length > 1 || cfos[0] != 0 ? $" cfo {cfo,+6:+0.0;-0.0} Hz" : "";
                    Log($"  {kind,-4} {snr,6:+0.0;-0.0} dB{lvl}{cfoTag}  ok {r.Successes,4}/{r.Trials}  "
                        + $"succ {r.SuccessRate,5:P0} [{lo:P0}..{hi:P0}]  "
                        + $"FER {r.Fer:0.000}  codedBER {Fmt(r.CodedBer)}  margin {r.Margin:0.0}");
                    }
                }
            }

            if (levelZeroCurve.Count > 1)
            {
                double knee = SimBench.Threshold(levelZeroCurve, 0.5);
                double knee90 = SimBench.Threshold(levelZeroCurve, 0.9);
                Log($"  {kind} threshold: 50% at {Knee(knee)} dB, 90% at {Knee(knee90)} dB");
            }
        }

        if (levelScan)
        {
            ReportLevelInvariance(rows);
        }

        Report(mode, layer, levelScan, rows);

        if (csvPath is not null)
        {
            WriteCsv(csvPath, effectiveRate, layer, frameBytes, rows);
            Console.Error.WriteLine($"wrote {csvPath}");
        }

        return 0;
    }

    private static void Report(string mode, SimLayer layer, bool levelScan, IReadOnlyList<SimPointResult> rows)
    {
        Console.WriteLine();
        Console.WriteLine($"=== sim {mode} ({layer}) ===");
        bool cfoScan = rows.Any(r => r.CfoHz != 0);
        string lvlHead = levelScan ? $" {"level",6}" : "";
        string cfoHead = cfoScan ? $" {"cfoHz",6}" : "";
        Console.WriteLine($"{"channel",-7} {"SNR",6}{lvlHead}{cfoHead} {"ok/N",9} {"succ%",6} {"95% CI",13} "
            + $"{"FER",7} {"codedBER",10} {"margin",7}");
        foreach (SimPointResult r in rows)
        {
            (double lo, double hi) = r.SuccessCi;
            string lvl = levelScan ? $" {r.LevelDb,6:+0.0;-0.0}" : "";
            string cfoCol = cfoScan ? $" {r.CfoHz,6:+0;-0;0}" : "";
            Console.WriteLine($"{r.Channel,-7} {r.SnrDb,6:+0.0;-0.0}{lvl}{cfoCol} {r.Successes,4}/{r.Trials,-4} "
                + $"{r.SuccessRate,6:P0} {$"{lo:P0}..{hi:P0}",13} {r.Fer,7:0.000} "
                + $"{Fmt(r.CodedBer),10} {r.Margin,7:0.0}");
        }
    }

    /// <summary>
    /// The level-invariance verdict: for each (channel, SNR) with a level scan, the spread of the
    /// success rate across the level axis. A level-robust receiver holds flat - max−min ≈ 0 across
    /// the whole non-clipping range; a level-sensitive one (the WN2 lesson: an un-normalised front
    /// end) sags at low level even though the SNR never moved. Clipping at high positive level is a
    /// separate, expected effect and is flagged, not counted against invariance.
    /// </summary>
    private static void ReportLevelInvariance(IReadOnlyList<SimPointResult> rows)
    {
        Console.WriteLine();
        Console.WriteLine("=== level-invariance probe (fixed SNR, absolute level scanned) ===");
        Console.WriteLine($"{"channel",-7} {"SNR",6} {"levels",7} {"succ min..max",16} {"verdict",10}");
        foreach (IGrouping<(SimChannelKind, double), SimPointResult> g in rows
                     .GroupBy(r => (r.Channel, r.SnrDb)))
        {
            var scan = g.OrderBy(r => r.LevelDb).ToList();
            double min = scan.Min(r => r.SuccessRate);
            double max = scan.Max(r => r.SuccessRate);
            // Invariant when every level holds within a burst-count-worth of the best - a couple of
            // trials of scatter is sampling noise, not level sensitivity.
            double slack = 3.0 / Math.Max(1, scan[0].Trials);
            string verdict = max - min <= slack ? "INVARIANT" : "LEVEL-DEP";
            Console.WriteLine($"{g.Key.Item1,-7} {g.Key.Item2,6:+0.0;-0.0} {scan.Count,7} "
                + $"{$"{min:P0}..{max:P0}",16} {verdict,10}");
        }
    }

    private static void WriteCsv(
        string path, int rate, SimLayer layer, int frameBytes, IReadOnlyList<SimPointResult> rows)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("mode,channel,layer,rate,frameBytes,snrDb,levelDb,cfoHz,trials,successes,successRate,"
            + "ciLo,ciHi,fer,codedBer,margin");
        foreach (SimPointResult r in rows)
        {
            (double lo, double hi) = r.SuccessCi;
            w.WriteLine(string.Join(',',
                r.Mode, r.Channel, r.Layer, rate, frameBytes,
                F(r.SnrDb), F(r.LevelDb), F(r.CfoHz), r.Trials, r.Successes, F(r.SuccessRate), F(lo), F(hi),
                F(r.Fer), F(r.CodedBer), F(r.Margin)));
        }
    }

    private static string Knee(double v) => double.IsNaN(v) ? "-" : v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);

    private static string Fmt(double v) => double.IsNaN(v) ? "-"
        : v == 0 ? "0"
        : v.ToString("0.00E+00", CultureInfo.InvariantCulture);

    private static string F(double v) => double.IsNaN(v) ? ""
        : v.ToString("G6", CultureInfo.InvariantCulture);
}
