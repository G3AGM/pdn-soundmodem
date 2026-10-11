using Packet.SoundModem.Telemetry;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Compares the audio the receive loop is handed with the wall clock, and says when the station
/// falls behind real time and when it catches up again.
/// </summary>
/// <remarks>
/// <para><b>Why (issue #649).</b> GB7RDG's receive loop ran at about 0.3x real time for five days
/// (#648). The Flex input dropped the other 70 % of the audio and nothing said so: no journal
/// line, no metric, no warning on the page. The waterfall crawled and Listen chopped, and the
/// only place the loss showed was the raw-capture chunks, each holding 300 s of samples and
/// taking 16 minutes of wall clock to fill. A station that cannot keep up looks, from outside,
/// exactly like one on a quiet band.</para>
/// <para><b>What is measured.</b> Per <c>Read</c>, the samples delivered and the wall time since
/// the previous one, and of that time how much the loop spent processing rather than waiting in
/// <c>Read</c>. Over a window the first two give the share of real time that reached the
/// station; the third says why it fell short. Busy share over delivered share is the processing
/// the loop needed per second of audio. Audio lost on the way in leaves a loop that keeps up with
/// what does arrive, so that figure stays under one; a loop that is itself the bottleneck needs
/// more than a second per second, and whatever buffer the device has overflows. The two need
/// different fixes, so the line says which.</para>
/// <para>Busy share alone is not the test, though it was the first one: a thread starved of CPU
/// (a cgroup quota, a contended host) is descheduled inside <c>Read</c> as well as outside it,
/// so its busy share reads low. Measured on GB7RDG with a 15 % quota on 2026-10-10: busy 82 %,
/// 57 % delivered, which the first cut blamed on the network. 0.82 / 0.57 is 1.44 seconds per
/// second, the loop's fault, which it was.</para>
/// <para><b>What is left out.</b> Spans while this station is keyed (a keyed radio's receive
/// stream is not a measurement of anything) and while the host says the session is idle on
/// purpose (an on-demand receiver nobody is watching). Short dropouts inside a window do count:
/// they are lost audio. Long ones are the dead-feed watches' business, and they fire first.</para>
/// <para>Wall time comes from the station's <see cref="TimeProvider"/>, so the watch is testable
/// on a fake clock. The cumulative totals are read by the metrics endpoint from another thread
/// and are written with <see cref="Interlocked"/>; the window state belongs to the receive loop.</para>
/// </remarks>
internal sealed class RealTimeWatch
{
    /// <summary>Below this share of real time over a window, the station is behind.</summary>
    internal const double BehindBelow = 0.95;

    /// <summary>At or above this share over a window, a station that was behind has caught up.
    /// Higher than <see cref="BehindBelow"/> so a station hovering at the line says it once.</summary>
    internal const double RecoveredAt = 0.99;

    /// <summary>At or above this much processing per second of delivered audio, the loop is the
    /// bottleneck. A little under one, for the time a starved thread loses inside <c>Read</c>.</summary>
    internal const double BottleneckSecondsPerSecond = 0.95;

    /// <summary>How much measured wall time one verdict is taken over.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly int _sampleRate;
    private readonly Func<long>? _inputLosses;

    private long _readStarted;
    private long _lastReadEnded;

    // The current window, receive loop only.
    private long _windowSamples;
    private long _windowTicks;
    private long _windowBusyTicks;

    // Cumulative, for the metrics endpoint.
    private long _totalSamples;
    private long _totalTicks;
    private long _totalBusyTicks;
    private long _lastRatioMillionths = -1;
    private int _behind;

    private long _lossesAtWindowStart;

    /// <param name="time">The station's clock.</param>
    /// <param name="sampleRate">The input's sample rate: what real time means in samples.</param>
    /// <param name="inputLosses">The input's own count of the times it lost audio
    /// (<c>StationOptions.InputLossCount</c>); null for a device that cannot say.</param>
    internal RealTimeWatch(TimeProvider time, int sampleRate, Func<long>? inputLosses = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _time = time;
        _sampleRate = sampleRate;
        _inputLosses = inputLosses;
        _lossesAtWindowStart = inputLosses?.Invoke() ?? 0;
    }

    /// <summary>Call immediately before <c>Read</c>.</summary>
    internal void ReadStarting() => _readStarted = _time.GetTimestamp();

    /// <summary>
    /// Call immediately after <c>Read</c> returns, with what it delivered and whether the span
    /// since the previous read is to be left out (keyed, or deliberately idle).
    /// </summary>
    internal void ReadEnded(int samples, bool leaveOut)
    {
        long now = _time.GetTimestamp();
        long previous = _lastReadEnded;
        _lastReadEnded = now;

        // The first read has no span before it to measure, and a left-out span is dropped whole:
        // its samples and its time together, so the ratio of what remains is unchanged.
        if (previous == 0 || leaveOut)
        {
            return;
        }

        long ticks = now - previous;
        long busy = Math.Clamp(_readStarted - previous, 0, ticks);
        _windowSamples += samples;
        _windowTicks += ticks;
        _windowBusyTicks += busy;
        Interlocked.Add(ref _totalSamples, samples);
        Interlocked.Add(ref _totalTicks, ticks);
        Interlocked.Add(ref _totalBusyTicks, busy);
    }

    /// <summary>
    /// Closes the window once it holds <see cref="Window"/> of measured time, and returns the line
    /// to journal when the station has just fallen behind or just caught up; null otherwise.
    /// </summary>
    internal string? Poll()
    {
        double seconds = Seconds(_windowTicks);
        if (seconds < Window.TotalSeconds)
        {
            return null;
        }

        double ratio = _windowSamples / (seconds * _sampleRate);
        double busy = Seconds(_windowBusyTicks) / seconds;
        long lostNow = _inputLosses?.Invoke() ?? 0;
        long lost = lostNow - _lossesAtWindowStart;
        _lossesAtWindowStart = lostNow;
        _windowSamples = 0;
        _windowTicks = 0;
        _windowBusyTicks = 0;
        Interlocked.Exchange(ref _lastRatioMillionths, (long)Math.Round(ratio * 1_000_000));

        bool wasBehind = Volatile.Read(ref _behind) == 1;
        if (!wasBehind && ratio < BehindBelow)
        {
            Volatile.Write(ref _behind, 1);
            return BehindLine(ratio, busy, seconds, lost);
        }

        if (wasBehind && ratio >= RecoveredAt)
        {
            Volatile.Write(ref _behind, 0);
            // Over 100 % is a backlog the input had queued being worked off.
            return $"receive: caught up - {Pct(ratio)} of real time over the last {seconds:F0} s, "
                + $"the loop busy {Pct(busy)} of it"
                + (ratio > 1.05 ? " (working off audio the input had queued, so some frames arrive late)" : "");
        }

        return null;
    }

    /// <summary>Whether the last verdict was "behind".</summary>
    internal bool Behind => Volatile.Read(ref _behind) == 1;

    /// <summary>The totals so far and the last window's verdict, for the metrics endpoint.</summary>
    internal ReceiveRateSnapshot Snapshot()
    {
        long ratio = Interlocked.Read(ref _lastRatioMillionths);
        return new ReceiveRateSnapshot(
            Interlocked.Read(ref _totalSamples),
            Seconds(Interlocked.Read(ref _totalTicks)),
            Seconds(Interlocked.Read(ref _totalBusyTicks)),
            ratio < 0 ? null : ratio / 1_000_000.0,
            Behind,
            _inputLosses?.Invoke());
    }

    private double Seconds(long ticks) => ticks / (double)_time.TimestampFrequency;

    /// <summary>A share as a whole percentage, the same in every culture: "P0" puts a space, or a
    /// non-breaking one, before the sign depending on the machine's locale, and the journal is
    /// ASCII.</summary>
    private static string Pct(double share) =>
        (share * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private string BehindLine(double ratio, double busy, double seconds, long lost)
    {
        // "Lost or late": an input with a deep buffer queues what the loop cannot take, and hands
        // it over late once the loop catches up (measured on a Flex: 192 % of real time for the
        // minute after a CPU squeeze ended). Which share is which is the input's business.
        string head = $"receive: BEHIND real time - {Pct(ratio)} of the audio reached the modems "
            + $"over the last {seconds:F0} s; the other {Pct(1 - ratio)} is lost or queued behind "
            + "(frames missed or late, the waterfall slow, Listen chopped)";
        // Processing needed per second of the audio that did arrive; see the type remarks.
        double perSecond = ratio > 0 ? busy / ratio : 0;
        string why = perSecond >= BottleneckSecondsPerSecond
            ? $". The receive loop was busy {Pct(busy)} of that time, about "
                + $"{perSecond.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} s "
                + "of work per second of audio: it is the bottleneck. The modems on this channel "
                + "want more CPU than this station is getting - fewer or cheaper modems, a faster "
                + "machine, or less else running on it"
            : $". The receive loop was busy only {Pct(busy)} of that time and kept up with what "
                + "arrived, so the audio went missing before it got here: look at the network, the "
                + "radio or the device";
        string counted = lost > 0
            ? $". The input itself counted {lost} loss{(lost == 1 ? "" : "es")} of audio in that window"
            : "";
        return head + why + counted;
    }
}
