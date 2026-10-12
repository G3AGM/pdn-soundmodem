using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace Packet.SoundModem.SdrConnect;

/// <summary>One JSON message of the SDRconnect WebSocket API, either way.</summary>
/// <param name="EventType">The <c>event_type</c> field.</param>
/// <param name="Property">The <c>property</c> field; empty where it does not apply.</param>
/// <param name="Value">The <c>value</c> field; empty where it does not apply.</param>
/// <param name="Device">The <c>device</c> field (<c>primary</c> or <c>secondary</c>), or null
/// when absent, which the API takes as the primary.</param>
public readonly record struct SdrConnectMessage(string EventType, string Property, string Value, string? Device)
{
    /// <summary>Whether the message is about the primary device (absent means primary).</summary>
    public bool IsPrimary => Device is null || Device.Equals("primary", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The SDRconnect WebSocket API's wire format, from SDRplay's published "SDRconnect WebSocket API"
/// document, version 1.0.3: the JSON envelope, the event and property names this station uses,
/// and the binary messages' type word and sample layouts.
/// </summary>
/// <remarks>
/// <para><b>Text.</b> Every JSON message is one object of string fields: <c>event_type</c>,
/// <c>property</c>, <c>value</c> and an optional <c>device</c>. Values are strings even where
/// the property is a number or a boolean ("101000000", "true").</para>
/// <para><b>Binary.</b> A 2-byte little-endian type, then the payload. Type 1 is the primary
/// device's demodulated audio, signed 16-bit stereo at 48 kHz (L, R, L, R); type 2 its IQ,
/// signed 16-bit interleaved (I, Q, I, Q). Both come in 4-byte frames: one stereo sample or one
/// complex sample. Neither carries a counter or a timestamp.</para>
/// <para>Original, from the document; no SDRplay code was read for it or copied.</para>
/// </remarks>
public static class SdrConnectProtocol
{
    /// <summary>SDRconnect's WebSocket port unless its operator changed it.</summary>
    public const int DefaultPort = 5454;

    /// <summary>The demodulated audio's rate, Hz, as the document states it.</summary>
    public const int AudioRate = 48_000;

    /// <summary>Bytes in one frame of either stream: one stereo sample or one I,Q pair.</summary>
    public const int FrameBytes = 4;

    /// <summary>Bytes of the type word at the start of every binary message.</summary>
    public const int TypeBytes = 2;

    /// <summary>Binary type: the primary device's audio.</summary>
    public const ushort PrimaryAudio = 1;

    /// <summary>Binary type: the primary device's IQ.</summary>
    public const ushort PrimaryIq = 2;

    /// <summary>
    /// What a signed 16-bit sample is divided by to put full scale at 1. Whether SDRconnect uses
    /// the whole 16 bits for its IQ, or a smaller range, the document does not say; the first run
    /// against a real instance settles it, and only the level, not the decoding, depends on it.
    /// </summary>
    public const float S16FullScale = 32768f;

    /// <summary>Client and server event names.</summary>
    public static class Events
    {
        /// <summary>Client: set a property.</summary>
        public const string SetProperty = "set_property";

        /// <summary>Client: ask for a property's value.</summary>
        public const string GetProperty = "get_property";

        /// <summary>Client: turn the IQ stream on or off.</summary>
        public const string IqStreamEnable = "iq_stream_enable";

        /// <summary>Client: turn the demodulated audio stream on or off.</summary>
        public const string AudioStreamEnable = "audio_stream_enable";

        /// <summary>Client: turn the spectrum stream on or off.</summary>
        public const string SpectrumEnable = "spectrum_enable";

        /// <summary>Client: start or stop the selected device streaming.</summary>
        public const string DeviceStreamEnable = "device_stream_enable";

        /// <summary>Client: select the device by serial number.</summary>
        public const string SelectedDeviceSerial = "selected_device_serial";

        /// <summary>Client: select the device by display name.</summary>
        public const string SelectedDeviceName = "selected_device_name";

        /// <summary>Server: a property changed.</summary>
        public const string PropertyChanged = "property_changed";

        /// <summary>Server: the answer to a <see cref="GetProperty"/>.</summary>
        public const string GetPropertyResponse = "get_property_response";
    }

    /// <summary>The property names this station reads or sets.</summary>
    public static class Properties
    {
        /// <summary>Hardware LO, Hz (unsigned 64-bit).</summary>
        public const string CentreFrequency = "device_center_frequency";

        /// <summary>Hardware sample rate, Hz (double).</summary>
        public const string SampleRate = "device_sample_rate";

        /// <summary>The VFO, Hz (unsigned 64-bit): where the demodulator listens.</summary>
        public const string VfoFrequency = "device_vfo_frequency";

        /// <summary>RF gain as a state index.</summary>
        public const string LnaState = "lna_state";

        /// <summary>The lowest <see cref="LnaState"/>; read only.</summary>
        public const string LnaStateMin = "lna_state_min";

        /// <summary>The highest <see cref="LnaState"/>; read only.</summary>
        public const string LnaStateMax = "lna_state_max";

        /// <summary>The VFO's filter bandwidth, Hz.</summary>
        public const string FilterBandwidth = "filter_bandwidth";

        /// <summary>The demodulator: AM, USB, LSB, CW, SAM, NFM or WFM.</summary>
        public const string Demodulator = "demodulator";

        /// <summary>The widest filter the demodulator takes, Hz; read only.</summary>
        public const string DemodMaxBandwidth = "demod_max_bandwidth";

        /// <summary>Whether the device is streaming; read only.</summary>
        public const string Started = "started";

        /// <summary>Whether this client may control the hardware; read only.</summary>
        public const string CanControl = "can_control";

        /// <summary>The audio volume, percent.</summary>
        public const string AudioVolumePercent = "audio_volume_percent";

        /// <summary>The audio limiters.</summary>
        public const string AudioLimiters = "audio_limiters";

        /// <summary>The audio filters.</summary>
        public const string AudioFilter = "audio_filter";

        /// <summary>The squelch.</summary>
        public const string SquelchEnable = "squelch_enable";

        /// <summary>The audio AGC.</summary>
        public const string AgcEnable = "agc_enable";

        /// <summary>The noise reduction.</summary>
        public const string NoiseReductionEnable = "noise_reduction_enable";

        /// <summary>NFM de-emphasis.</summary>
        public const string NfmDeemphasisEnable = "nfm_deemphasis_enable";

        /// <summary>The SSB demodulator's low cut, Hz.</summary>
        public const string SsbLowCut = "ssb_lowcut_frequency";

        /// <summary>The antennas there are, comma-separated; read only.</summary>
        public const string ValidAntennas = "valid_antennas";

        /// <summary>The antenna in use.</summary>
        public const string ActiveAntenna = "active_antenna";

        /// <summary>The devices there are, comma-separated display names; read only.</summary>
        public const string ValidDevices = "valid_devices";

        /// <summary>The device in use, by display name; read only.</summary>
        public const string ActiveDevice = "active_device";

        /// <summary>The API's version, e.g. "1.0.3"; read only.</summary>
        public const string ApiVersion = "api_version";
    }

    /// <summary>A client message as UTF-8 JSON, ready to send as one text message.</summary>
    public static byte[] Encode(string eventType, string property, string value)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event_type", eventType);
            writer.WriteString("property", property);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>A <c>set_property</c> message.</summary>
    public static byte[] Set(string property, string value) => Encode(Events.SetProperty, property, value);

    /// <summary>A <c>get_property</c> message.</summary>
    public static byte[] Get(string property) => Encode(Events.GetProperty, property, "");

    /// <summary>One of the stream switches (<c>iq_stream_enable</c> and the like).</summary>
    public static byte[] Enable(string eventType, bool on) => Encode(eventType, "", on ? "true" : "false");

    /// <summary>A frequency or other whole number as SDRconnect writes it.</summary>
    public static string Number(double value) =>
        Math.Round(value).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>Reads a JSON text message; false if it is not one this API sends.</summary>
    /// <remarks>Lenient about the value's JSON type: a number or a boolean is taken as its text, so
    /// a server that sends <c>"value": 100</c> is understood as well as one that sends
    /// <c>"value": "100"</c>.</remarks>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out SdrConnectMessage message)
    {
        message = default;
        string? eventType = null;
        string property = "";
        string value = "";
        string? device = null;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString()!;
                if (!reader.Read())
                {
                    return false;
                }

                string? text = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => Raw(reader.ValueSpan),
                    JsonTokenType.True => "true",
                    JsonTokenType.False => "false",
                    JsonTokenType.Null => null,
                    _ => Skip(ref reader),
                };
                switch (name)
                {
                    case "event_type":
                        eventType = text;
                        break;
                    case "property":
                        property = text ?? "";
                        break;
                    case "value":
                        value = text ?? "";
                        break;
                    case "device":
                        device = text;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(eventType))
        {
            return false;
        }

        message = new SdrConnectMessage(eventType, property, value, device);
        return true;

        static string Raw(ReadOnlySpan<byte> raw) => System.Text.Encoding.UTF8.GetString(raw);

        static string? Skip(ref Utf8JsonReader reader)
        {
            reader.Skip();
            return null;
        }
    }

    /// <summary>
    /// Whether a text message is one a client reads: any <c>get_property_response</c> (they come
    /// only when asked for), and a <c>property_changed</c> about one of <paramref name="followed"/>
    /// (UTF-8 property names). Reads without allocating, so the pushes it passes over, of whatever
    /// SDRconnect keeps changing and however often it says so, cost nothing.
    /// </summary>
    public static bool IsFollowed(ReadOnlySpan<byte> utf8, byte[][] followed)
    {
        ArgumentNullException.ThrowIfNull(followed);
        bool response = false;
        bool push = false;
        bool about = false;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isEvent = reader.ValueTextEquals("event_type"u8);
                bool isProperty = !isEvent && reader.ValueTextEquals("property"u8);
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    if (isEvent)
                    {
                        response = reader.ValueTextEquals(Events.GetPropertyResponse);
                        push = reader.ValueTextEquals(Events.PropertyChanged);
                    }
                    else if (isProperty)
                    {
                        foreach (byte[] name in followed)
                        {
                            about |= reader.ValueTextEquals(name);
                        }
                    }
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return response || (push && about);
    }

    /// <summary>
    /// Text from SDRconnect (a device name, an antenna) or about it (an exception's message) made
    /// safe to print on a station's console: anything outside printable ASCII becomes '?'.
    /// </summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                var clean = new System.Text.StringBuilder(text.Length);
                foreach (char d in text)
                {
                    clean.Append(d is < ' ' or > '~' ? '?' : d);
                }

                return clean.ToString();
            }
        }

        return text;
    }

    /// <summary>A frequency or rate as SDRconnect reports it ("2000000", "2000000.0"), or null
    /// when it is not a number.</summary>
    public static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
            ? parsed
            : null;

    /// <summary>A boolean as SDRconnect reports it, or null.</summary>
    public static bool? ParseBool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
        : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>The type word of a binary message's first two bytes.</summary>
    public static ushort BinaryType(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadUInt16LittleEndian(header);

    /// <summary>
    /// Whole IQ frames (signed 16-bit I then Q, little-endian) to interleaved floats with full
    /// scale at 1 (<paramref name="fullScale"/>). <paramref name="output"/> takes two floats per frame.
    /// </summary>
    public static void ConvertIq(ReadOnlySpan<byte> frames, Span<float> output, float fullScale = S16FullScale)
    {
        int samples = frames.Length / 2;
        float scale = 1f / fullScale;
        for (int k = 0; k < samples; k++)
        {
            output[k] = BinaryPrimitives.ReadInt16LittleEndian(frames.Slice(2 * k, 2)) * scale;
        }
    }

    /// <summary>
    /// Whole stereo audio frames (signed 16-bit L then R, little-endian) to the left channel as
    /// floats with full scale at 1. <paramref name="output"/> takes one float per frame.
    /// </summary>
    public static void ConvertLeft(ReadOnlySpan<byte> frames, Span<float> output)
    {
        int count = frames.Length / FrameBytes;
        for (int k = 0; k < count; k++)
        {
            output[k] = BinaryPrimitives.ReadInt16LittleEndian(frames.Slice(FrameBytes * k, 2)) / S16FullScale;
        }
    }
}
