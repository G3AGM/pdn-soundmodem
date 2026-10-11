using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The <c>"iqpipe"</c> section: what the raw IQ of an <c>iqpipe:</c> device is, and for
/// <c>"device": "iqpipe:"</c> the command that makes it. Ignored for every other device.
/// </summary>
/// <remarks>
/// A pipe says nothing about itself, so everything the front end needs is stated here: the
/// layout of the bytes, the rate, and the RF frequency the IQ is centred on. Where the station
/// points the channel comes from the band plan, as for every self-tuning device.
/// </remarks>
public sealed class IqPipeConfig
{
    /// <summary>
    /// The command whose standard output is the IQ, for <c>"device": "iqpipe:"</c>: run through
    /// <c>/bin/sh -c</c>, restarted with backoff when it exits. <c>{frequency}</c> and
    /// <c>{rate}</c> are replaced with the centre and the rate in whole hertz; with
    /// <c>{frequency}</c> in it the station chooses the centre itself and may move it.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>How the bytes are laid out: <c>cu8</c> (rtl_sdr), <c>cs8</c>, <c>cs16</c> or
    /// <c>cf32</c>. Required.</summary>
    public string? Format { get; set; }

    /// <summary>The complex sample rate, samples per second. Required, and trusted: the station
    /// says so in the journal when the feed delivers at another.</summary>
    public int? Rate { get; set; }

    /// <summary>
    /// The RF frequency the IQ is centred on, Hz. Required unless the command takes
    /// <c>{frequency}</c>, in which case the default is the dial less
    /// <c>IqTuning.DcClearOffsetHz</c> (an eighth of the rate, at most 25 kHz), so the channel is
    /// kept off the device's DC spike.
    /// </summary>
    public double? CentreFrequency { get; set; }

    /// <summary>Swap I and Q, for a device that delivers them the other way round (its spectrum
    /// comes out mirrored). Default false.</summary>
    public bool SwapIq { get; set; }

    /// <summary>Linear gain on the demodulated audio. Default 1.</summary>
    public double? Gain { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}
