// SPDX-License-Identifier: AGPL-3.0-or-later
// The message shapes follow OpenWebRX (AGPL-3.0-or-later); see LICENSING.md.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.OpenWebRx;

/// <summary>
/// The OpenWebRX receiver WebSocket, as its own browser client speaks it: the messages this
/// client sends, and what it reads out of the ones it is sent.
/// </summary>
/// <remarks>
/// <para>Implemented from the OpenWebRX sources, AGPL-3.0-or-later like this repository:
/// the client side from <c>htdocs/openwebrx.js</c> (<c>on_ws_opened</c>, <c>on_ws_recv</c>,
/// <c>open_websocket</c>) and <c>htdocs/lib/Demodulator.js</c> (<c>Demodulator.start</c> and
/// <c>set</c>), the server side from <c>owrx/connection.py</c>
/// (<c>HandshakeMessageHandler</c>, <c>OpenWebRxReceiverClient.handleTextMessage</c>) and
/// <c>owrx/dsp.py</c> (<c>DspManager</c>'s property validators and defaults). Read at
/// jketterl/openwebrx 1.2.2 and luarvique/openwebrx (OpenWebRX+) 1.2.126, which speak the same
/// protocol for everything here.</para>
/// <para>The conversation: the client opens <c>ws/</c> beside the receiver's page and sends a
/// handshake line, the server answers with its own and its version, then a stream of JSON text
/// messages (<c>config</c> with the SDR's centre and sample rate and the audio compression,
/// <c>profiles</c>, <c>receiver_details</c>, and many this client has no use for) and binary
/// messages whose first byte is their type: 1 the waterfall, 2 audio, 3 the secondary
/// waterfall, 4 wide-FM audio. The client asks for audio at a rate with
/// <c>connectionproperties</c>, sets the demodulator with <c>dspcontrol</c> parameters, and
/// starts it with a <c>dspcontrol</c> start. Audio is 16-bit little-endian, or
/// <see cref="ImaAdpcmSyncDecoder"/>'s ADPCM where the server's <c>audio_compression</c> says
/// <c>adpcm</c>, which is the server's setting and not the client's.</para>
/// <para>Only keys both servers validate are ever sent. A property a server does not know is
/// an error there rather than something it ignores, so OpenWebRX+'s noise reduction, for one,
/// is left at its default (off) rather than turned off explicitly.</para>
/// </remarks>
public static class OpenWebRxProtocol
{
    /// <summary>The audio rate this client asks for. It is what the browser client asks for on
    /// a 48 kHz sound card, and what the narrow demodulators run at.</summary>
    public const int AudioRate = 12000;

    /// <summary>The rate asked for wide-FM audio. Never used for packet, but the browser client
    /// always sends one, so this does too.</summary>
    internal const int HdAudioRate = 48000;

    /// <summary>The squelch level the browser client uses for "open": the bottom of its slider.</summary>
    internal const int OpenSquelch = -150;

    /// <summary>The binary message type that carries the narrow demodulators' audio.</summary>
    internal const byte AudioMessage = 2;

    /// <summary>The handshake line. The server reads only its <c>type</c>.</summary>
    internal const string Handshake = "SERVER DE CLIENT client=pdn-soundmodem type=receiver";

    /// <summary>The start of the server's answer to the handshake.</summary>
    internal const string HandshakeReplyPrefix = "CLIENT DE SERVER";

    /// <summary>Asks for audio at <see cref="AudioRate"/>.</summary>
    internal static string ConnectionProperties() =>
        new JsonObject
        {
            ["type"] = "connectionproperties",
            ["params"] = new JsonObject
            {
                ["output_rate"] = AudioRate,
                ["hd_output_rate"] = HdAudioRate,
            },
        }.ToJsonString();

    /// <summary>Sets the demodulator: where it listens, through what, with the squelch open.</summary>
    /// <param name="offsetHz">The dial's offset from the SDR's centre frequency.</param>
    /// <param name="tuning">The demodulator and passband.</param>
    internal static string DspParameters(long offsetHz, OpenWebRxTuning tuning) =>
        new JsonObject
        {
            ["type"] = "dspcontrol",
            ["params"] = new JsonObject
            {
                ["low_cut"] = tuning.LowCutHz,
                ["high_cut"] = tuning.HighCutHz,
                ["offset_freq"] = offsetHz,
                ["mod"] = tuning.Modulation,
                ["squelch_level"] = OpenSquelch,
            },
        }.ToJsonString();

    /// <summary>Starts the demodulator.</summary>
    internal static string DspStart() =>
        new JsonObject { ["type"] = "dspcontrol", ["action"] = "start" }.ToJsonString();

    /// <summary>Moves the receiver to another profile. This moves it for every listener on that
    /// SDR, so it is asked at most once a session.</summary>
    internal static string SelectProfile(string profileId) =>
        new JsonObject
        {
            ["type"] = "selectprofile",
            ["params"] = new JsonObject { ["profile"] = profileId },
        }.ToJsonString();

    /// <summary>
    /// The offset from the SDR's centre that puts the dial where it is wanted, or null when the
    /// passband would not fit inside what the SDR is receiving.
    /// </summary>
    internal static long? OffsetFor(long centreHz, long sampleRate, OpenWebRxTuning tuning)
    {
        long offset = tuning.FrequencyHz - centreHz;
        long half = sampleRate / 2;
        long low = offset + Math.Min(tuning.LowCutHz, tuning.HighCutHz);
        long high = offset + Math.Max(tuning.LowCutHz, tuning.HighCutHz);
        return low >= -half && high <= half ? offset : null;
    }

    /// <summary>Reads one text message from the server.</summary>
    internal static OpenWebRxMessage Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith(HandshakeReplyPrefix, StringComparison.Ordinal))
        {
            string? server = null;
            string? version = null;
            foreach (string field in text[HandshakeReplyPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = field.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = field[..equals];
                string setting = field[(equals + 1)..];
                if (key == "server")
                {
                    server = setting;
                }
                else if (key == "version")
                {
                    version = setting;
                }
            }

            return new OpenWebRxMessage.HandshakeReply(server, version);
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return new OpenWebRxMessage.Other(null);
        }

        if (json is null)
        {
            return new OpenWebRxMessage.Other(null);
        }

        string? type = StringOf(json["type"]);
        JsonNode? value = json["value"];
        return type switch
        {
            "config" when value is JsonObject config => ParseConfig(config),
            "profiles" when value is JsonArray profiles => new OpenWebRxMessage.Profiles(
                [.. profiles.OfType<JsonObject>()
                    .Select(p => new OpenWebRxProfile(StringOf(p["id"]) ?? "", StringOf(p["name"]) ?? ""))
                    .Where(p => p.Id.Length > 0)]),
            "receiver_details" when value is JsonObject details => new OpenWebRxMessage.ReceiverDetails(
                Describe(details)),
            "backoff" => new OpenWebRxMessage.Backoff(StringOf(json["reason"]) ?? "the receiver is busy"),
            "sdr_error" => new OpenWebRxMessage.SdrError(StringOf(value) ?? "the SDR failed"),
            "demodulator_error" => new OpenWebRxMessage.DemodulatorError(StringOf(value) ?? "the demodulator failed"),
            "log_message" => new OpenWebRxMessage.LogMessage(StringOf(value) ?? ""),
            _ => new OpenWebRxMessage.Other(type),
        };
    }

    private static OpenWebRxMessage.Config ParseConfig(JsonObject config) =>
        new(
            CentreHz: LongOf(config["center_freq"]),
            SampleRate: LongOf(config["samp_rate"]),
            AudioCompression: StringOf(config["audio_compression"]),
            SdrId: StringOf(config["sdr_id"]),
            ProfileId: StringOf(config["profile_id"]),
            MaxClients: LongOf(config["max_clients"]));

    /// <summary>The receiver's name and location, as its operator set them, on one line.</summary>
    private static string? Describe(JsonObject details)
    {
        var parts = new List<string>();
        foreach (string field in (string[])["receiver_name", "receiver_location"])
        {
            if (StringOf(details[field]) is { Length: > 0 } text)
            {
                parts.Add(Ascii(text));
            }
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Text the receiver's operator wrote, reduced to printable ASCII for the journal.</summary>
    internal static string Ascii(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString().Trim();
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static long? LongOf(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out long whole))
        {
            return whole;
        }

        if (value.TryGetValue(out double real) && double.IsFinite(real))
        {
            return (long)Math.Round(real);
        }

        return value.TryGetValue(out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : null;
    }
}

/// <summary>One profile an OpenWebRX receiver offers: an SDR and a band setting on it.</summary>
/// <param name="Id">The id, <c>sdr|profile</c>.</param>
/// <param name="Name">The name the page's list shows.</param>
public readonly record struct OpenWebRxProfile(string Id, string Name);

/// <summary>One text message from an OpenWebRX server, as far as this client cares.</summary>
internal abstract record OpenWebRxMessage
{
    /// <summary>The answer to the handshake.</summary>
    public sealed record HandshakeReply(string? Server, string? Version) : OpenWebRxMessage;

    /// <summary>Some of the receiver's configuration; each field is null where this message
    /// did not carry it, since the server sends changes as they happen.</summary>
    public sealed record Config(
        long? CentreHz, long? SampleRate, string? AudioCompression, string? SdrId, string? ProfileId,
        long? MaxClients) : OpenWebRxMessage;

    /// <summary>The profiles the receiver offers.</summary>
    public sealed record Profiles(IReadOnlyList<OpenWebRxProfile> All) : OpenWebRxMessage;

    /// <summary>Who runs the receiver and where.</summary>
    public sealed record ReceiverDetails(string? Description) : OpenWebRxMessage;

    /// <summary>The server will not serve this client now ("Too many clients", or on
    /// OpenWebRX+ "Client address banned"), and closes the socket.</summary>
    public sealed record Backoff(string Reason) : OpenWebRxMessage;

    /// <summary>The receiver has no working SDR.</summary>
    public sealed record SdrError(string Message) : OpenWebRxMessage;

    /// <summary>The demodulator could not be set up as asked.</summary>
    public sealed record DemodulatorError(string Message) : OpenWebRxMessage;

    /// <summary>A line the server wants its listeners to see.</summary>
    public sealed record LogMessage(string Message) : OpenWebRxMessage;

    /// <summary>Anything else, which this client reads past.</summary>
    public sealed record Other(string? Type) : OpenWebRxMessage;
}
