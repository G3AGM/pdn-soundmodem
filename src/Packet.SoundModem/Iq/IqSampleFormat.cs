using System.Buffers.Binary;

namespace Packet.SoundModem.Iq;

/// <summary>How a stream of raw IQ is laid out: interleaved <c>I, Q</c>, little-endian where
/// a sample is more than a byte. The names are the ones <c>rx_sdr -F</c> and <c>csdr</c> use.</summary>
public enum IqSampleFormat
{
    /// <summary>Unsigned 8-bit, offset binary, centred on 127.5: what <c>rtl_sdr</c> and an
    /// <c>rtl_tcp</c> server send.</summary>
    Cu8,

    /// <summary>Signed 8-bit (a HackRF's native format).</summary>
    Cs8,

    /// <summary>Signed 16-bit little-endian.</summary>
    Cs16,

    /// <summary>32-bit IEEE float little-endian, full scale -1 to 1.</summary>
    Cf32,
}

/// <summary>Reading <see cref="IqSampleFormat"/>s into the interleaved floats an
/// <c>IIqSource</c> hands out, full scale -1 to 1 on each axis.</summary>
/// <remarks>
/// <para>CU8 maps 0 to -1 and 255 to +1 (<c>b / 127.5 - 1</c>), as csdr's
/// <c>convert_u8_f</c> does, so a dongle's IQ has no DC offset of the conversion's own. CS8 and
/// CS16 divide by 128 and 32768, so the most negative code is exactly -1. CF32 is taken as it
/// comes.</para>
/// <para>Original; the mappings are the conventional ones and copy no code.</para>
/// </remarks>
public static class IqSampleFormats
{
    /// <summary>The spellings <see cref="TryParse"/> takes, for messages.</summary>
    public const string Spellings = "cu8, cs8, cs16 or cf32";

    /// <summary>Bytes in one complex sample (one I and one Q) of <paramref name="format"/>.</summary>
    public static int BytesPerPair(IqSampleFormat format) => format switch
    {
        IqSampleFormat.Cu8 or IqSampleFormat.Cs8 => 2,
        IqSampleFormat.Cs16 => 4,
        IqSampleFormat.Cf32 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not an IQ format"),
    };

    /// <summary>Reads a format's name, in any case: <c>cu8</c>, <c>cs8</c>, <c>cs16</c> or
    /// <c>cf32</c>.</summary>
    public static bool TryParse(string? text, out IqSampleFormat format)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "cu8":
                format = IqSampleFormat.Cu8;
                return true;
            case "cs8":
                format = IqSampleFormat.Cs8;
                return true;
            case "cs16":
                format = IqSampleFormat.Cs16;
                return true;
            case "cf32":
                format = IqSampleFormat.Cf32;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>The name <see cref="TryParse"/> reads back, lower case.</summary>
    public static string Name(IqSampleFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>
    /// Converts the whole pairs in <paramref name="bytes"/> into interleaved floats in
    /// <paramref name="interleaved"/>, as many as both hold.
    /// </summary>
    /// <returns>The number of floats written (twice the pairs).</returns>
    public static int Convert(IqSampleFormat format, ReadOnlySpan<byte> bytes, Span<float> interleaved)
    {
        int perPair = BytesPerPair(format);
        int pairs = Math.Min(bytes.Length / perPair, interleaved.Length / 2);
        int values = 2 * pairs;
        switch (format)
        {
            case IqSampleFormat.Cu8:
                for (int k = 0; k < values; k++)
                {
                    interleaved[k] = (bytes[k] / 127.5f) - 1f;
                }

                break;
            case IqSampleFormat.Cs8:
                for (int k = 0; k < values; k++)
                {
                    interleaved[k] = (sbyte)bytes[k] / 128f;
                }

                break;
            case IqSampleFormat.Cs16:
                for (int k = 0; k < values; k++)
                {
                    interleaved[k] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(2 * k, 2)) / 32768f;
                }

                break;
            case IqSampleFormat.Cf32:
                for (int k = 0; k < values; k++)
                {
                    interleaved[k] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(4 * k, 4));
                }

                break;
        }

        return values;
    }
}
