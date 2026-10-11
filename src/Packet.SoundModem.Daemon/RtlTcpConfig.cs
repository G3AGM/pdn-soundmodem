using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Settings used only when Device is <c>rtltcp:HOST[:PORT]</c>: an RTL-SDR dongle behind an
/// rtl_tcp server, or OpenWebRX's rtl_tcp-compatible IQ port. Ignored for every other device.
/// Where to listen comes from the band plan, as for any receiver.
/// </summary>
/// <remarks>
/// The station demodulates: the IQ front end's filter and demodulator, so the SSB and FM keys
/// here are this station's own filter, not something asked of the server.
/// </remarks>
public sealed class RtlTcpConfig
{
    /// <summary>
    /// True for a server that takes no orders: OpenWebRX's rtl_tcp-compatible port. Nothing is
    /// sent to it, and <see cref="CentreFrequency"/> and <see cref="SampleRate"/> say what its
    /// profile is pinned to. False (the default) tunes the dongle from the dial.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>The read-only server's centre frequency, Hz, as its profile sets it. Required with
    /// <see cref="ReadOnly"/>, refused without it.</summary>
    public double? CentreFrequency { get; set; }

    /// <summary>The sample rate, complex samples a second. Controlled: the rate asked for,
    /// default 1024000. Read-only: required, the rate the profile is pinned to.</summary>
    public int? SampleRate { get; set; }

    /// <summary>Tuner gain to ask for, dB; null (the default) for the tuner's own automatic
    /// gain. Controlled only.</summary>
    public double? GainDb { get; set; }

    /// <summary>Frequency correction to ask for, parts per million. Default 0. Controlled
    /// only.</summary>
    public int? Ppm { get; set; }

    /// <summary>Turn on the RTL2832's own digital AGC. Default false. Controlled only.</summary>
    public bool? RtlAgc { get; set; }

    /// <summary>Seconds a connected server may send nothing before it is dropped and reconnected.
    /// Default 5.</summary>
    public double? NoDataSeconds { get; set; }

    /// <summary>Bottom of this station's SSB filter, Hz from the dial. Default 150.</summary>
    public double? SsbLowHz { get; set; }

    /// <summary>Top of this station's SSB filter, Hz from the dial. Default 3450.</summary>
    public double? SsbHighHz { get; set; }

    /// <summary>FM: this station's IF bandwidth, Hz, whole width about the carrier. Default
    /// 16000.</summary>
    public double? FmIfBandwidthHz { get; set; }

    /// <summary>FM: the peak deviation heard as full scale, Hz. Default 3000.</summary>
    public double? FmDeviationHz { get; set; }

    /// <summary>Linear gain on the demodulated audio. Default 1.0.</summary>
    public double? Gain { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}
