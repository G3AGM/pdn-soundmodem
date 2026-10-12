namespace Packet.SoundModem.SdrConnect;

/// <summary>Which of SDRconnect's streams a station takes.</summary>
public enum SdrConnectStream
{
    /// <summary>The raw IQ at the device's sample rate (binary type 2), demodulated here by the
    /// IQ front end, so the filter and the demodulator are this station's.</summary>
    Iq,

    /// <summary>SDRconnect's own demodulated audio, 48 kHz stereo (binary type 1), of which the
    /// left channel is taken; the filter and the demodulator are SDRconnect's.</summary>
    Audio,
}

/// <summary>Settings for a connection to SDRconnect (<see cref="SdrConnectClient"/>).</summary>
/// <remarks>
/// Several of these stand in for things the API document does not say and that only a real
/// SDRconnect can answer (the IQ scale, how long a retune takes to reach the stream, how the
/// filter bandwidth relates to the low cut). They are here, with the document's or a cautious
/// value as the default, so what the first run finds can be applied as a setting.
/// </remarks>
public sealed record SdrConnectOptions
{
    /// <summary>The stream taken.</summary>
    public SdrConnectStream Stream { get; init; } = SdrConnectStream.Iq;

    /// <summary>The hardware LO to ask for, Hz.</summary>
    public required double CentreFrequencyHz { get; init; }

    /// <summary>The device sample rate to ask for, Hz, or null to leave SDRconnect's. Whatever
    /// SDRconnect then reports is the rate used.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Audio stream: the VFO to ask for, Hz (the dial: the suppressed carrier for SSB,
    /// the channel centre for FM).</summary>
    public double? VfoFrequencyHz { get; init; }

    /// <summary>Audio stream: the demodulator, <c>USB</c>, <c>LSB</c> or <c>NFM</c>.</summary>
    public string Demodulator { get; init; } = "USB";

    /// <summary>Audio stream: the filter bandwidth to ask for, Hz, or null to leave SDRconnect's.</summary>
    public double? FilterBandwidthHz { get; init; }

    /// <summary>Audio stream, SSB: the low cut to ask for, Hz, or null to leave SDRconnect's.</summary>
    public double? SsbLowCutHz { get; init; }

    /// <summary>The RF gain state to ask for, or null to leave SDRconnect's.</summary>
    public int? LnaState { get; init; }

    /// <summary>The antenna to ask for by name, or null to leave SDRconnect's.</summary>
    public string? Antenna { get; init; }

    /// <summary>The device to select by serial number, or null for whichever SDRconnect has.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>The device to select by display name, or null for whichever SDRconnect has.</summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// What a signed 16-bit IQ sample is divided by to put full scale at 1. The document does not
    /// say what range SDRconnect's IQ uses; 32768 assumes all of it.
    /// </summary>
    public float IqFullScale { get; init; } = SdrConnectProtocol.S16FullScale;

    /// <summary>How long a connect may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long SDRconnect may take to answer the questions asked at a connect, and to
    /// report a setting it was sent before the report is taken as its answer.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a streaming connection may deliver nothing before it is dropped and reconnected,
    /// the time it was gone reported as lost samples. Either stream is tens of kilobytes a second
    /// or more, so a few seconds of nothing is a hung server or a dead link, not a quiet band.
    /// </summary>
    public TimeSpan NoDataTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long SDRconnect may go on delivering nothing, unreachable or connected and
    /// silent, before the client gives up and raises <see cref="SdrConnectClient.Lost"/>.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The first wait between reconnect attempts; it doubles to
    /// <see cref="ReconnectDelayCap"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan ReconnectDelayCap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much of the stream to throw away after SDRconnect reports a new tuning, counted as
    /// lost: the stream carries no marker of where a change took effect, so what follows the
    /// report for this long may be either side of it.
    /// </summary>
    public TimeSpan RetuneSettle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether to measure the stream's rate against the wall clock, which is the only way
    /// to see samples SDRconnect or the link did not deliver: its frames carry no counter.</summary>
    public bool WatchRate { get; init; } = true;

    /// <summary>The rate watch's window.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The socket's receive buffer, bytes.</summary>
    public int ReceiveBufferBytes { get; init; } = 65_536;

    /// <summary>Where the client's lines go (the start-up report, drops, foreign retunes), or null.</summary>
    public Action<string>? Journal { get; init; }

    /// <summary>The clock for waits, timeouts and the rate watch.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Where a client's stream goes: the IQ source's buffer, or the audio input's.</summary>
/// <remarks>Called on the client's receive loop only, one call at a time.</remarks>
internal interface ISdrConnectSink
{
    /// <summary>Whole 4-byte frames of the stream taken.</summary>
    void Frames(ReadOnlySpan<byte> frames);

    /// <summary><paramref name="frames"/> frames are missing before the next ones.</summary>
    void Gap(long frames);

    /// <summary>The next frames are at a new tuning: the LO and rate for IQ, the VFO for audio.</summary>
    void Tuning(double frequencyHz, int sampleRate);

    /// <summary>The client has stopped for good.</summary>
    void End();
}
