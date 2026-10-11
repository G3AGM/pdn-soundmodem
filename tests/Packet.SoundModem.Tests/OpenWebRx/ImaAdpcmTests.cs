using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Tests.OpenWebRx;

/// <summary>
/// The ADPCM an OpenWebRX receiver may send its audio as, and the sync framing it wraps it in.
/// </summary>
/// <remarks>
/// The two hashes were taken on 2026-10-10 from a one-off cross-check, not from this code: the
/// signal below encoded by a line-for-line Python port of csdr's <c>AdpcmEncoder(sync=true)</c>
/// (<c>src/lib/adpcm.cpp</c>, the server's encoder), and that stream decoded under node by
/// OpenWebRX's own browser decoder (<c>ImaAdpcmCodec.decodeWithSync</c> from
/// <c>htdocs/lib/AudioEngine.js</c>, OpenWebRX 1.2.2 and OpenWebRX+ 1.2.126, fed in 4096- and
/// 777-byte pieces; all four runs gave the same samples). So the encoder here writes the bytes
/// the server writes, and the decoder hears what the browser hears.
/// </remarks>
public class ImaAdpcmTests
{
    /// <summary>SHA-256 of csdr's encoding of <see cref="Signal"/>(10000).</summary>
    private const string CsdrStreamSha256 = "f6aa021fb865bde1e6f9a192e79675b7b72481b86e30420a6adca9eeb302279c";

    /// <summary>SHA-256 of the browser decoder's samples from the second frame on, as 16-bit
    /// little-endian. The first frame is left out: the browser decodes its first code on a step
    /// of 0 rather than the header's, an off-by-a-few the next sync header puts right.</summary>
    private const string BrowserDecodeFromFrameTwoSha256 = "5b6be71828df136245ec358bcfdc563a2c67f7ca5003362ece5b54836bce35f2";

    /// <summary>A triangle and some integer noise: deterministic in any language, and busy enough
    /// to walk the step index up and down the table.</summary>
    private static short[] Signal(int count)
    {
        var samples = new short[count];
        long x = 12345;
        for (int i = 0; i < count; i++)
        {
            x = ((x * 1103515245) + 12345) & 0x7FFFFFFF;
            int noise = (int)((x >> 16) & 0x0FFF) - 2048;
            int tri = (i * 97) % 20000;
            tri = tri < 10000 ? tri : 20000 - tri;
            samples[i] = (short)(tri - 5000 + noise);
        }

        return samples;
    }

    private static byte[] Encode(short[] samples)
    {
        var encoder = new ImaAdpcmSyncEncoder();
        var bytes = new byte[ImaAdpcmSyncEncoder.MaxBytesFor(samples.Length)];
        int written = encoder.Encode(samples, bytes);
        return bytes[..written];
    }

    private static short[] Decode(byte[] stream, int piece)
    {
        var decoder = new ImaAdpcmSyncDecoder();
        var output = new List<short>();
        var buffer = new short[ImaAdpcmSyncDecoder.MaxSamplesFor(piece)];
        for (int i = 0; i < stream.Length; i += piece)
        {
            int length = Math.Min(piece, stream.Length - i);
            int got = decoder.Decode(stream.AsSpan(i, length), buffer);
            output.AddRange(buffer.AsSpan(0, got).ToArray());
        }

        return [.. output];
    }

    private static string Sha256(ReadOnlySpan<short> samples) =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(samples)));

    [Fact]
    public void The_Encoder_Writes_The_Bytes_The_OpenWebRx_Server_Writes()
    {
        byte[] stream = Encode(Signal(10000));

        stream.Length.Should().Be(5040, "5000 bytes of codes in five frames, each behind an 8-byte header");
        Convert.ToHexStringLower(SHA256.HashData(stream)).Should().Be(CsdrStreamSha256);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(777)]
    [InlineData(1)]
    [InlineData(3)]
    public void The_Decoder_Hears_What_The_Browser_Hears_However_The_Stream_Is_Cut(int piece)
    {
        short[] decoded = Decode(Encode(Signal(10000)), piece);

        decoded.Length.Should().Be(10000);
        Sha256(decoded.AsSpan(2 * ImaAdpcmSyncDecoder.FrameBytes)).Should().Be(BrowserDecodeFromFrameTwoSha256);
    }

    [Fact]
    public void A_Round_Trip_Tracks_The_Signal_To_Within_A_Few_Steps()
    {
        short[] signal = Signal(24000);
        short[] decoded = Decode(Encode(signal), 4096);

        double error = 0;
        double power = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            power += (double)signal[i] * signal[i];
            error += (double)(signal[i] - decoded[i]) * (signal[i] - decoded[i]);
        }

        double snrDb = 10 * Math.Log10(power / error);
        snrDb.Should().BeGreaterThan(12, "4-bit IMA ADPCM on a busy signal");
    }

    [Fact]
    public void Nothing_Is_Decoded_Before_The_First_Sync_Word()
    {
        byte[] stream = Encode(Signal(4000));
        byte[] joined = [.. new byte[] { 0x12, 0x34, (byte)'S', (byte)'Y', 0x00 }, .. stream];

        short[] decoded = Decode(joined, 64);

        decoded.Should().Equal(Decode(stream, 64));
    }

    [Fact]
    public void A_Lost_Byte_Costs_No_More_Than_Its_Frame()
    {
        short[] signal = Signal(10000);
        byte[] stream = Encode(signal);
        short[] clean = Decode(stream, 512);

        // A byte gone from the middle of the second frame: that frame's tail is decoded off by a
        // nibble, the count then runs a byte into the third frame's header, and the search finds
        // the fourth frame's sync word.
        int lost = 8 + ImaAdpcmSyncDecoder.FrameBytes + 8 + 300;
        byte[] damaged = [.. stream[..lost], .. stream[(lost + 1)..]];
        short[] decoded = Decode(damaged, 512);

        int fromFourth = 3 * 2 * ImaAdpcmSyncDecoder.FrameBytes;
        decoded[^(clean.Length - fromFourth)..].Should().Equal(clean[fromFourth..]);
    }

    [Fact]
    public void A_Sync_Word_After_A_Stray_S_Is_Still_Found()
    {
        byte[] stream = Encode(Signal(4000));
        byte[] joined = [(byte)'S', .. stream];

        Decode(joined, 2).Should().Equal(Decode(stream, 2));
    }

    [Fact]
    public void An_Odd_Sample_Is_Held_Until_The_Next_Call()
    {
        short[] signal = Signal(5001);
        var encoder = new ImaAdpcmSyncEncoder();
        var bytes = new byte[ImaAdpcmSyncEncoder.MaxBytesFor(signal.Length) * 2];
        int written = encoder.Encode(signal.AsSpan(0, 2501), bytes);
        written += encoder.Encode(signal.AsSpan(2501), bytes.AsSpan(written));

        byte[] whole = Encode(signal[..5000]);
        bytes[..written].Should().Equal(whole);
    }
}
