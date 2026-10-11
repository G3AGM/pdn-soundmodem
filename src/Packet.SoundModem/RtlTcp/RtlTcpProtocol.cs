using System.Buffers.Binary;

namespace Packet.SoundModem.RtlTcp;

/// <summary>The tuner chip an rtl_tcp server says its dongle has, as its header numbers them.</summary>
public enum RtlTcpTuner : uint
{
    /// <summary>Not known, or a server that does not say (a compatible port on something that
    /// is not a dongle).</summary>
    Unknown = 0,

    /// <summary>Elonics E4000.</summary>
    E4000 = 1,

    /// <summary>Fitipower FC0012.</summary>
    Fc0012 = 2,

    /// <summary>Fitipower FC0013.</summary>
    Fc0013 = 3,

    /// <summary>FCI FC2580.</summary>
    Fc2580 = 4,

    /// <summary>Rafael Micro R820T or R820T2, the commonest.</summary>
    R820T = 5,

    /// <summary>Rafael Micro R828D.</summary>
    R828D = 6,
}

/// <summary>What an rtl_tcp server says about its dongle as the connection opens.</summary>
/// <param name="Tuner">The tuner chip.</param>
/// <param name="GainCount">How many gain steps the tuner has.</param>
public readonly record struct RtlTcpHeader(RtlTcpTuner Tuner, uint GainCount)
{
    /// <summary>The tuner as a name for the journal.</summary>
    public string TunerName => Tuner switch
    {
        RtlTcpTuner.E4000 => "E4000",
        RtlTcpTuner.Fc0012 => "FC0012",
        RtlTcpTuner.Fc0013 => "FC0013",
        RtlTcpTuner.Fc2580 => "FC2580",
        RtlTcpTuner.R820T => "R820T",
        RtlTcpTuner.R828D => "R828D",
        _ => $"tuner type {(uint)Tuner}",
    };
}

/// <summary>The commands an rtl_tcp client sends, by their wire number.</summary>
public enum RtlTcpCommand : byte
{
    /// <summary>Tune the dongle; the parameter is the centre frequency in Hz.</summary>
    SetFrequency = 0x01,

    /// <summary>Set the sample rate; the parameter is complex samples a second.</summary>
    SetSampleRate = 0x02,

    /// <summary>Tuner gain mode: 0 for the tuner's own automatic gain, 1 for manual.</summary>
    SetGainMode = 0x03,

    /// <summary>Manual tuner gain, in tenths of a dB; the tuner takes its nearest step.</summary>
    SetGain = 0x04,

    /// <summary>Frequency correction, parts per million, signed.</summary>
    SetFrequencyCorrection = 0x05,

    /// <summary>The RTL2832's own digital AGC: 1 on, 0 off.</summary>
    SetAgcMode = 0x08,
}

/// <summary>
/// The rtl_tcp wire format: the twelve-byte header a server opens with, the five-byte commands a
/// client sends, and the unsigned 8-bit IQ that follows.
/// </summary>
/// <remarks>
/// <para>Implemented from the protocol's description (as osmocom's rtl-sdr documents and every
/// rtl_tcp client speaks it), not from rtl-sdr's code. The header is the ASCII magic
/// <c>RTL0</c>, then the tuner type and the tuner's gain-step count as 32-bit big-endian
/// integers. A command is one byte of command number and a 32-bit big-endian parameter. After
/// the header the server sends samples and nothing else: I then Q, one unsigned byte each, 127.5
/// being zero.</para>
/// <para>The protocol has no replies and no framing: a command is never acknowledged, and a change
/// it makes reaches the stream at a place the client cannot see. Nor can a server say that its
/// tuning changed for some other reason, which is why a read-only port has to be watched (see
/// <see cref="Packet.SoundModem.Iq.IqRateWatch"/>).</para>
/// </remarks>
public static class RtlTcpProtocol
{
    /// <summary>The header's length in bytes.</summary>
    public const int HeaderLength = 12;

    /// <summary>A command's length in bytes.</summary>
    public const int CommandLength = 5;

    /// <summary>rtl_tcp's own port.</summary>
    public const int DefaultPort = 1234;

    /// <summary>The magic the header starts with: <c>RTL0</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "RTL0"u8;

    /// <summary>
    /// Reads a header from the first <see cref="HeaderLength"/> bytes of a stream, or answers
    /// null when they do not start with the magic (a server that sends samples from its first
    /// byte).
    /// </summary>
    public static RtlTcpHeader? ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength || !bytes[..4].SequenceEqual(Magic))
        {
            return null;
        }

        return new RtlTcpHeader(
            (RtlTcpTuner)BinaryPrimitives.ReadUInt32BigEndian(bytes[4..8]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]));
    }

    /// <summary>Writes a header, as a server would: for tests and fakes.</summary>
    public static void WriteHeader(Span<byte> destination, RtlTcpHeader header)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], (uint)header.Tuner);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], header.GainCount);
    }

    /// <summary>Encodes one command into <paramref name="destination"/>'s first five bytes.</summary>
    public static void Encode(Span<byte> destination, RtlTcpCommand command, uint parameter)
    {
        destination[0] = (byte)command;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..5], parameter);
    }

    /// <summary>Encodes a command whose parameter is signed (the frequency correction).</summary>
    public static void Encode(Span<byte> destination, RtlTcpCommand command, int parameter) =>
        Encode(destination, command, unchecked((uint)parameter));

    /// <summary>Reads one command back, as a server would: for tests and fakes.</summary>
    public static (RtlTcpCommand Command, uint Parameter) Decode(ReadOnlySpan<byte> bytes) =>
        ((RtlTcpCommand)bytes[0], BinaryPrimitives.ReadUInt32BigEndian(bytes[1..5]));

    /// <summary>
    /// Whether a dongle can run at <paramref name="sampleRate"/>: the RTL2832 takes 225001 to
    /// 300000 and 900001 to 3200000 samples a second, and above 2.56 MS/s it starts dropping
    /// samples on most hosts.
    /// </summary>
    public static bool IsDongleRate(int sampleRate) =>
        sampleRate is (> 225000 and <= 300000) or (> 900000 and <= 3200000);

    /// <summary>
    /// The float each unsigned byte stands for: (b - 127.5) / 127.5, so 0 and 255 are full scale
    /// and the midpoint is zero. A table, so converting a block is a lookup per byte.
    /// </summary>
    public static ReadOnlySpan<float> SampleTable => Table;

    private static readonly float[] Table = BuildTable();

    private static float[] BuildTable()
    {
        var table = new float[256];
        for (int b = 0; b < 256; b++)
        {
            table[b] = (float)((b - 127.5) / 127.5);
        }

        return table;
    }

    /// <summary>Converts unsigned 8-bit IQ bytes to floats, one for one.</summary>
    public static void Convert(ReadOnlySpan<byte> bytes, Span<float> destination)
    {
        ReadOnlySpan<float> table = Table;
        for (int k = 0; k < bytes.Length; k++)
        {
            destination[k] = table[bytes[k]];
        }
    }
}
