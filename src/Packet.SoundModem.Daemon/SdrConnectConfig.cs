using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Settings used only when Device is <c>sdrconnect:HOST[:PORT]</c>: SDRplay's SDRconnect, GUI or
/// headless, over its WebSocket API. Ignored for every other device. Where to listen comes from
/// the band plan, as for any receiver.
/// </summary>
/// <remarks>
/// Two streams. <c>"iq"</c> (the default) takes the raw IQ and demodulates it here, so the SSB and
/// FM keys are this station's own filter, as on <c>rtltcp:</c>. <c>"audio"</c> takes SDRconnect's
/// own demodulated audio, so they are the filter SDRconnect is asked for.
/// </remarks>
public sealed class SdrConnectConfig
{
    /// <summary><c>"iq"</c> (the default): SDRconnect's IQ, demodulated here; wants a wired link.
    /// <c>"audio"</c>: SDRconnect's own demodulated audio; suits Wi-Fi.</summary>
    public string? Mode { get; set; }

    /// <summary>The radio's sample rate to ask SDRconnect for, samples a second. IQ: default
    /// 2000000. Audio: SDRconnect's own unless set. Whatever SDRconnect reports is used.</summary>
    public int? SampleRate { get; set; }

    /// <summary>The RF gain state (SDRconnect's LNA state) to ask for; SDRconnect's own unless set.</summary>
    public int? LnaState { get; set; }

    /// <summary>The antenna to select, by the name SDRconnect gives it; SDRconnect's own unless set.</summary>
    public string? Antenna { get; set; }

    /// <summary>Which radio, by serial number, when SDRconnect has more than one; not with
    /// <see cref="DeviceName"/>.</summary>
    public string? DeviceSerial { get; set; }

    /// <summary>Which radio, by SDRconnect's display name for it; not with <see cref="DeviceSerial"/>.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Seconds a connected SDRconnect may send nothing before it is dropped and
    /// reconnected. Default 5.</summary>
    public double? NoDataSeconds { get; set; }

    /// <summary>Bottom of the SSB filter, Hz from the dial. Default 150.</summary>
    public double? SsbLowHz { get; set; }

    /// <summary>Top of the SSB filter, Hz from the dial. Default 3450.</summary>
    public double? SsbHighHz { get; set; }

    /// <summary>FM: the IF bandwidth, Hz, whole width about the carrier. Default 16000 for IQ,
    /// 12500 for audio.</summary>
    public double? FmIfBandwidthHz { get; set; }

    /// <summary>FM, IQ only: the peak deviation heard as full scale, Hz. Default 3000.</summary>
    public double? FmDeviationHz { get; set; }

    /// <summary>Linear gain on the demodulated audio. Default 1.0.</summary>
    public double? Gain { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}
