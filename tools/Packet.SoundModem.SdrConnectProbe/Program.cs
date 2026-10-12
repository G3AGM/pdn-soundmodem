// sdrconnect-probe: run on the PC that runs SDRconnect. It asks SDRconnect's WebSocket API what
// it is and measures how it streams, then puts back everything it changed and writes a zip to
// send back. What it is for, and what each section answers, is #699. Written from SDRplay's
// published SDRconnect WebSocket API 1.0.3 document; no SDRplay code is used.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text;
using Packet.SoundModem.SdrConnectProbe;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string host = "127.0.0.1";
int port = 5454;
bool wait = true;
bool readOnly = false;
foreach (string arg in args)
{
    switch (arg)
    {
        case "--no-wait":
            wait = false;
            break;
        case "--no-changes":
            readOnly = true;
            break;
        case "-h" or "--help" or "/?":
            Console.WriteLine("""
                sdrconnect-probe [HOST[:PORT]] [--no-changes] [--no-wait]

                Run it on the PC that runs SDRconnect, with SDRconnect open and a radio selected.
                It takes about three minutes, changes the sample rate, demodulator and audio
                settings while it measures, puts them all back, and never changes the frequency.
                It writes a zip to your Desktop to send back.

                  HOST[:PORT]    SDRconnect's WebSocket (default 127.0.0.1:5454)
                  --no-changes   only read and listen; change nothing
                  --no-wait      do not wait for Enter at the end
                """);
            return 0;
        default:
            int colon = arg.LastIndexOf(':');
            if (colon > 0 && int.TryParse(arg[(colon + 1)..], out int p))
            {
                host = arg[..colon];
                port = p;
            }
            else
            {
                host = arg;
            }

            break;
    }
}

string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
string baseDir = Directory.Exists(desktop) ? desktop : Directory.GetCurrentDirectory();
string workDir = Path.Combine(Path.GetTempPath(), $"sdrconnect-probe-{stamp}");
Directory.CreateDirectory(workDir);
string zipPath = Path.Combine(baseDir, $"sdrconnect-probe-{stamp}.zip");

var report = new StringBuilder();
using var log = new StreamWriter(Path.Combine(workDir, "messages.log"), false, new UTF8Encoding(false)) { AutoFlush = true };

void Say(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

void Note(string line) => report.AppendLine(line);

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine("== " + title);
    report.AppendLine().AppendLine("== " + title);
}

Console.WriteLine("sdrconnect-probe for pdn-soundmodem (#699). This takes about three minutes.");
Console.WriteLine("Please leave SDRconnect alone until it says it has finished.");
Section("Environment");
Say($"probe run {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}, target {host}:{port}, {(readOnly ? "read-only" : "may change settings")}");
Say($"os: {Environment.OSVersion}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}, {Environment.ProcessorCount} cpus");

int[] sdrconnectPids = SdrConnectProcesses(Say);
CheckListeners(port, sdrconnectPids, Say, Note);
FirewallRules(Note);

Section("Connecting");
await using var link = new SdrConnectLink(log);
var changed = new Dictionary<string, string>(StringComparer.Ordinal);
bool controlled = false;
bool startedByUs = false;
try
{
    try
    {
        await link.ConnectAsync(new Uri($"ws://{host}:{port}/"), CancellationToken.None);
        Say($"connected to ws://{host}:{port}/");
    }
    catch (Exception e)
    {
        Say($"FAILED to connect to ws://{host}:{port}/: {Describe(e)}");
        Say("Is SDRconnect running on this PC? Is its WebSocket API turned on, and on which port?");
        return Finish(1);
    }

    await Task.Delay(2000);
    Note($"messages pushed in the first 2 s without asking: see messages.log");

    Section("Properties");
    string[] properties =
    [
        "api_version", "active_device", "valid_devices", "can_control", "started", "overload",
        "device_center_frequency", "device_sample_rate", "device_vfo_frequency", "demodulator",
        "filter_bandwidth", "demod_max_bandwidth", "lna_state", "lna_state_min", "lna_state_max",
        "valid_antennas", "active_antenna", "agc_enable", "agc_threshold", "audio_volume_percent",
        "audio_mute", "audio_limiters", "audio_filter", "squelch_enable", "squelch_threshold",
        "noise_reduction_enable", "noise_reduction_strength", "nfm_deemphasis_enable",
        "am_lowcut_frequency", "ssb_lowcut_frequency", "nfm_lowcut_frequency", "signal_power",
        "signal_snr", "spectrum_ref_level", "spectrum_base", "wfm_stereo_enable", "rds_enable",
    ];
    var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (string property in properties)
    {
        string? value = link.Open ? await link.GetAsync(property, TimeSpan.FromSeconds(2)) : null;
        snapshot[property] = value;
        Say($"{property,-26} {value ?? "(no answer in 2 s)"}");
    }

    controlled = !readOnly && string.Equals(snapshot["can_control"], "true", StringComparison.OrdinalIgnoreCase);
    if (!readOnly && !controlled)
    {
        Say("can_control is not true, so nothing will be changed; the streams are measured as they are.");
    }

    async Task<string?> Set(string property, string value)
    {
        if (!controlled || !link.Open)
        {
            return null;
        }

        if (!changed.ContainsKey(property))
        {
            changed[property] = link.Property(property) ?? snapshot.GetValueOrDefault(property) ?? "";
        }

        (string? now, bool sawChange) = await link.SetAsync(property, value, TimeSpan.FromSeconds(3));
        Note($"  set {property}={value}: now {now ?? "(no answer)"}, property_changed {(sawChange ? "seen" : "NOT seen")}");
        return now;
    }

    if (string.Equals(snapshot["started"], "false", StringComparison.OrdinalIgnoreCase) && controlled)
    {
        Section("Starting the radio");
        await link.SendAsync("device_stream_enable", "", "true");
        startedByUs = true;
        await Task.Delay(3000);
        Say($"started is now {await link.GetAsync("started", TimeSpan.FromSeconds(2)) ?? "(no answer)"}");
    }

    Section("IQ at the current rate");
    double rateNow = ParseRate(link.Property("device_sample_rate"));
    await link.SendAsync("iq_stream_enable", "", "true");
    await Task.Delay(1000);
    link.Capture(2, (long)Math.Min(8_000_000, Math.Max(rateNow, 48_000) * 4 / 2));
    Measured iq = await Measure(link, 2, 5);
    byte[] iqCapture = link.EndCapture();
    Report(iq, rateNow, "IQ", Say);
    Say($"other binary types seen meanwhile: {string.Join(", ", iq.Types)}");
    if (iqCapture.Length > 0)
    {
        WriteWav(Path.Combine(workDir, "iq-current-rate.wav"), (int)Math.Max(1, rateNow), iqCapture);
        Say($"kept {iqCapture.Length / 4} IQ pairs in iq-current-rate.wav");
    }

    if (controlled)
    {
        Section("Sample rates");
        Say("asked      reported   measured S/s  frame bytes (min..max)");
        double[] candidates = [62_500, 125_000, 250_000, 500_000, 1_000_000, 2_000_000, 3_000_000, 6_000_000, 8_000_000, 10_000_000];
        var accepted = new List<double>();
        foreach (double asked in candidates)
        {
            if (!link.Open)
            {
                break;
            }

            string? now = await Set("device_sample_rate", asked.ToString("F0", CultureInfo.InvariantCulture));
            await Task.Delay(500);
            Measured m = await Measure(link, 2, 2);
            double reported = ParseRate(now);
            if (Math.Abs(reported - asked) < 1)
            {
                accepted.Add(asked);
            }

            Say($"{asked,-10:F0} {now ?? "-",-10} {m.PairsPerSecond,12:F0}  {(m.Stats.Messages == 0 ? "no IQ" : $"{m.Stats.MinBytes}..{m.Stats.MaxBytes}")}");
        }

        Say($"rates SDRconnect reported back exactly as asked: {(accepted.Count == 0 ? "none" : string.Join(", ", accepted))}");

        Section("Falling behind");
        double stallRate = accepted.Contains(2_000_000) ? 2_000_000 : accepted.Count > 0 ? accepted[^1] : rateNow;
        await Set("device_sample_rate", stallRate.ToString("F0", CultureInfo.InvariantCulture));
        await Task.Delay(1000);
        Measured before = await Measure(link, 2, 3);
        Say($"at {stallRate:F0} S/s, before: {before.PairsPerSecond:F0} pairs/s");
        link.ResetStats();
        double pausedAt = link.Elapsed;
        link.Pause();
        await Task.Delay(5000);
        link.Resume();
        var series = new List<string>();
        long lastPairs = 0;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            long pairs = link.Stats(2).Pairs;
            series.Add($"{pairs - lastPairs}");
            lastPairs = pairs;
        }

        double window = link.Elapsed - pausedAt;
        Say($"stopped reading for 5 s, then read for 10 s: {lastPairs} pairs in {window:F1} s, "
            + $"{lastPairs / (stallRate * window) * 100:F1}% of what the rate says should exist");
        Say($"pairs per 0.5 s after resuming: {string.Join(" ", series)}");
        Say(link.Open ? "the socket stayed open" : $"the socket ENDED: {link.Ended}");
        Say("(about 100% means SDRconnect buffered and caught up; well under means it dropped; a burst then normal shows the buffer size)");

        Section("Does the audio AGC touch the IQ?");
        foreach (string agc in (string[])["false", "true", "false"])
        {
            await Set("agc_enable", agc);
            await Task.Delay(1000);
            Measured m = await Measure(link, 2, 2);
            Say($"agc_enable {agc,-5}: IQ rms {Db(m.RmsDbfsA)} / {Db(m.RmsDbfsB)} dBFS, {m.PairsPerSecond:F0} pairs/s");
        }

        if (changed.TryGetValue("device_sample_rate", out string? originalRate))
        {
            await Set("device_sample_rate", originalRate);
        }
    }

    await link.SendAsync("iq_stream_enable", "", "false");
    await Task.Delay(500);

    Section("Audio");
    await link.SendAsync("audio_stream_enable", "", "true");
    await Task.Delay(1000);
    foreach (string demod in controlled ? (string[])["USB", "NFM"] : [link.Property("demodulator") ?? "as set"])
    {
        if (controlled)
        {
            await Set("demodulator", demod);
            await Task.Delay(1000);
        }

        link.Capture(1, 48_000 * 4 * 4);
        Measured a = await Measure(link, 1, 4);
        byte[] audio = link.EndCapture();
        Say($"demodulator {demod}:");
        Report(a, 48_000, "audio", Say);
        Say($"  left equals right in {(a.Stats.Pairs == 0 ? 0 : 100.0 * a.Stats.EqualPairs / a.Stats.Pairs):F1}% of frames");
        if (audio.Length > 0)
        {
            WriteWav(Path.Combine(workDir, $"audio-{demod.ToLowerInvariant().Replace(' ', '-')}.wav"), 48_000, audio);
        }
    }

    if (controlled)
    {
        Section("Which audio settings reach the WebSocket audio?");
        await Set("demodulator", "USB");
        foreach ((string property, string value) in ((string, string)[])[
            ("agc_enable", "false"), ("agc_enable", "true"), ("agc_enable", "false"),
            ("audio_volume_percent", "100"), ("audio_volume_percent", "10"), ("audio_volume_percent", "100"),
            ("audio_mute", "true"), ("audio_mute", "false"),
            ("noise_reduction_enable", "false"), ("squelch_enable", "false"),
            ("audio_limiters", "false"), ("audio_filter", "false")])
        {
            await Set(property, value);
            await Task.Delay(1000);
            Measured m = await Measure(link, 1, 2);
            Say($"{property}={value,-6}: audio rms {Db(m.RmsDbfsA)} dBFS, {m.PairsPerSecond:F0} frames/s");
        }
    }

    await link.SendAsync("audio_stream_enable", "", "false");
    return Finish(0);
}
catch (Exception e)
{
    Say($"the probe failed: {Describe(e)}");
    return Finish(2);
}

int Finish(int code)
{
    if (changed.Count > 0 || startedByUs)
    {
        Section("Putting settings back");
        foreach ((string property, string value) in changed)
        {
            try
            {
                if (value.Length == 0)
                {
                    Say($"{property}: SDRconnect never said what it was, so it is left as the probe set it. Please check it in SDRconnect.");
                    continue;
                }

                if (link.Open)
                {
                    link.SendAsync("set_property", property, value).GetAwaiter().GetResult();
                    Thread.Sleep(300);
                }

                Say($"{property} -> {value}");
            }
            catch (Exception e)
            {
                Say($"could not put {property} back to {value}: {e.Message}. Please set it by hand in SDRconnect.");
            }
        }

        if (startedByUs && link.Open)
        {
            link.SendAsync("device_stream_enable", "", "false").GetAwaiter().GetResult();
            Say("radio stopped again, as it was");
        }
    }

    Section("Done");
    if (link.Ended is not null)
    {
        Say($"the connection ended at {link.EndedAt:F1} s: {link.Ended}");
    }

    File.WriteAllText(Path.Combine(workDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
    log.Flush();
    log.Dispose();
    try
    {
        File.Delete(zipPath);
        ZipFile.CreateFromDirectory(workDir, zipPath, CompressionLevel.Optimal, false);
        Console.WriteLine();
        Console.WriteLine("Finished. Please send this file back:");
        Console.WriteLine("  " + zipPath);
    }
    catch (Exception e)
    {
        Console.WriteLine($"Could not write the zip ({e.Message}). The files are in {workDir}");
    }

    if (wait)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    return code;
}

static async Task<Measured> Measure(SdrConnectLink link, int type, double seconds)
{
    link.ResetStats();
    double from = link.Elapsed;
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    double window = link.Elapsed - from;
    return new Measured(link.Stats(type), window, link.TypesSeen());
}

static void Report(Measured m, double expectedRate, string what, Action<string> say)
{
    TypeStats s = m.Stats;
    if (s.Messages == 0)
    {
        say($"  NO {what} messages in {m.Window:F1} s");
        return;
    }

    say($"  {s.Messages} messages in {m.Window:F1} s ({s.Messages / m.Window:F1}/s), payload {s.MinBytes}..{s.MaxBytes} bytes, "
        + $"{s.Bytes * 8 / m.Window / 1e6:F2} Mbit/s{(s.OddLengths > 0 ? $", {s.OddLengths} not a whole number of pairs" : "")}");
    say($"  {m.PairsPerSecond:F0} pairs/s measured against {expectedRate:F0} expected ({m.PairsPerSecond / Math.Max(1, expectedRate) * 100:F1}%)");
    say($"  rms {m.RmsDbfsA:F1} / {m.RmsDbfsB:F1} dBFS, peak {s.PeakA} / {s.PeakB} of 32767, "
        + $"mean {s.SumA / Math.Max(1, s.Pairs):F1} / {s.SumB / Math.Max(1, s.Pairs):F1}");
}

static string Db(double db) => double.IsNaN(db) ? "(no data)" : db.ToString("F1", CultureInfo.InvariantCulture);

static double ParseRate(string? s) =>
    double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

static string Describe(Exception e)
{
    var b = new StringBuilder(e.GetType().Name + ": " + e.Message);
    for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
    {
        b.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
    }

    return b.ToString();
}

static void WriteWav(string path, int rate, byte[] interleaved16)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8);
    w.Write(36 + interleaved16.Length);
    w.Write("WAVEfmt "u8);
    w.Write(16);
    w.Write((short)1);
    w.Write((short)2);
    w.Write(rate);
    w.Write(rate * 4);
    w.Write((short)4);
    w.Write((short)16);
    w.Write("data"u8);
    w.Write(interleaved16.Length);
    w.Write(interleaved16);
}

static int[] SdrConnectProcesses(Action<string> say)
{
    var pids = new List<int>();
    foreach (Process p in Process.GetProcesses())
    {
        if (!p.ProcessName.Contains("sdrconnect", StringComparison.OrdinalIgnoreCase) || p.Id == Environment.ProcessId)
        {
            continue;
        }

        pids.Add(p.Id);
        string detail;
        try
        {
            ProcessModule? main = p.MainModule;
            detail = $"{main?.FileName}, version {main?.FileVersionInfo.FileVersion} ({main?.FileVersionInfo.ProductVersion})";
        }
        catch (Exception e)
        {
            detail = $"(details not readable: {e.Message})";
        }

        say($"process: {p.ProcessName} pid {p.Id}, {detail}");
    }

    if (pids.Count == 0)
    {
        say("process: NO process with SDRconnect in its name is running on this PC");
    }

    return [.. pids];
}

static void CheckListeners(int port, int[] pids, Action<string> say, Action<string> note)
{
    try
    {
        var on = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToList();
        say(on.Count == 0
            ? $"listening on port {port}: NOTHING"
            : $"listening on port {port}: {string.Join(", ", on)}");
        if (on.Any(e => e.Address.Equals(System.Net.IPAddress.Any) || e.Address.Equals(System.Net.IPAddress.IPv6Any)))
        {
            say("  -> other machines can reach it, if the Windows firewall lets them");
        }
        else if (on.Count > 0)
        {
            say("  -> only this PC can reach it");
        }
    }
    catch (Exception e)
    {
        say($"listening on port {port}: could not tell ({e.Message})");
    }

    // Every port SDRconnect listens on, in case the WebSocket is not where the document says.
    string netstat = Run("netstat", "-ano -p TCP");
    foreach (string line in netstat.Split('\n'))
    {
        string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length >= 5 && f[3] == "LISTENING" && int.TryParse(f[4], out int pid) && pids.Contains(pid))
        {
            say($"SDRconnect (pid {pid}) listens on {f[1]}");
        }
    }

    note("");
    note(Run("netsh", "advfirewall show currentprofile state").Trim());
}

static void FirewallRules(Action<string> note)
{
    string all = Run("netsh", "advfirewall firewall show rule name=all verbose");
    note("");
    note("firewall rules naming SDRconnect or port 5454:");
    int found = 0;
    foreach (string block in all.Replace("\r", "").Split("\n\n"))
    {
        if (block.Contains("sdrconnect", StringComparison.OrdinalIgnoreCase)
            || block.Contains(" 5454", StringComparison.Ordinal))
        {
            note(block.Trim());
            note("");
            found++;
        }
    }

    if (found == 0)
    {
        note("(none)");
    }
}

static string Run(string file, string arguments)
{
    try
    {
        using Process p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return output;
    }
    catch (Exception e)
    {
        return $"({file} {arguments} failed: {e.Message})";
    }
}

/// <summary>One measurement window over one binary type.</summary>
internal sealed record Measured(TypeStats Stats, double Window, int[] Types)
{
    public double PairsPerSecond => Window > 0 ? Stats.Pairs / Window : 0;

    public double RmsDbfsA => Dbfs(Stats.SumSqA);

    public double RmsDbfsB => Dbfs(Stats.SumSqB);

    private double Dbfs(double sumSq) =>
        Stats.Pairs == 0 ? double.NaN : 10 * Math.Log10(Math.Max(sumSq / Stats.Pairs, 1e-12) / (32768.0 * 32768.0));
}
