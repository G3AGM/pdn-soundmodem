namespace Packet.SoundModem.Telemetry;

/// <summary>
/// How much of real time the station's receive loop is keeping up with (issue #649): totals since
/// start and the last minute's verdict, as the metrics endpoint publishes them.
/// </summary>
/// <param name="Samples">Samples delivered by the input over the measured time.</param>
/// <param name="Seconds">Wall time measured, leaving out spans while keyed or deliberately idle.
/// <c>rate(Samples) / rate(Seconds) / sample rate</c> is the share of real time.</param>
/// <param name="BusySeconds">Of <paramref name="Seconds"/>, the time the loop spent processing
/// rather than waiting for audio. Close to <paramref name="Seconds"/> means the loop is the
/// bottleneck.</param>
/// <param name="RealTimeRatio">The last completed window's share of real time; null before the
/// first window closes.</param>
/// <param name="Behind">Whether the station is currently reported as behind real time.</param>
/// <param name="InputLosses">The input's own count of the times it lost audio, where it keeps
/// one; null otherwise.</param>
public readonly record struct ReceiveRateSnapshot(
    long Samples,
    double Seconds,
    double BusySeconds,
    double? RealTimeRatio,
    bool Behind,
    long? InputLosses);
