// SPDX-License-Identifier: AGPL-3.0-or-later
// The sync framing follows OpenWebRX (AGPL-3.0-or-later); see LICENSING.md.

using System.Buffers.Binary;

namespace Packet.SoundModem.OpenWebRx;

/// <summary>
/// The IMA ADPCM step and index tables, and the one-nibble update both directions share.
/// </summary>
/// <remarks>
/// Written from the IMA ADPCM description (IMA Digital Audio Focus and Technical Working
/// Groups, "Recommended Practices for Enhancing Digital Audio Compatibility in Multimedia
/// Systems", 1992): 89 step sizes, a 16-entry index adjustment, and a difference built from
/// the step in eighths. The tables are the standard's, which every implementation shares.
/// </remarks>
internal static class ImaAdpcm
{
    /// <summary>The 89 quantiser step sizes.</summary>
    internal static ReadOnlySpan<short> StepTable =>
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    /// <summary>How each 4-bit code moves the step index.</summary>
    internal static ReadOnlySpan<sbyte> IndexTable => [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    /// <summary>The highest step index.</summary>
    internal const int MaxIndex = 88;

    /// <summary>
    /// Applies one 4-bit code to the predictor and the step index, and returns the new sample.
    /// </summary>
    internal static short Apply(int code, ref int predictor, ref int index)
    {
        int step = StepTable[index];
        int difference = step >> 3;
        if ((code & 1) != 0)
        {
            difference += step >> 2;
        }

        if ((code & 2) != 0)
        {
            difference += step >> 1;
        }

        if ((code & 4) != 0)
        {
            difference += step;
        }

        if ((code & 8) != 0)
        {
            difference = -difference;
        }

        predictor = Math.Clamp(predictor + difference, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[code & 0x0F], 0, MaxIndex);
        return (short)predictor;
    }
}

/// <summary>
/// The OpenWebRX audio stream's ADPCM: IMA ADPCM, two samples a byte with the low nibble first,
/// cut into frames that each start with the word <c>SYNC</c> and the coder's state, so a client
/// that joins mid-stream or loses a byte finds its place again.
/// </summary>
/// <remarks>
/// <para>The frame, as OpenWebRX's server writes it and its browser client reads it: the four
/// ASCII bytes <c>SYNC</c>, the step index and the predictor as two little-endian 16-bit
/// integers, then 1001 bytes of codes (2002 samples), then the next <c>SYNC</c>. Taken from
/// OpenWebRX's <c>htdocs/lib/AudioEngine.js</c> (<c>ImaAdpcmCodec.decodeWithSync</c>, both
/// OpenWebRX 1.2 and OpenWebRX+ 1.2, which count the same 1001 bytes in different ways); the
/// count is the one the server's encoder produces, which emits the next sync word after its
/// counter of 1000 has run down past zero.</para>
/// <para>Frames do not line up with WebSocket messages, so this is a state machine fed one
/// message at a time: a sync word or a header split across two messages is picked up where it
/// left off. That is one place this differs from the browser client, which drops a header split
/// that way and decodes the frame on the previous frame's state.</para>
/// <para>Nothing is decoded before the first sync word, because nothing before it says what the
/// coder's state is. A byte lost in transit costs the rest of its frame at most: the count runs
/// out, the next four bytes are not <c>SYNC</c>, and the search resumes.</para>
/// </remarks>
public sealed class ImaAdpcmSyncDecoder
{
    /// <summary>The sync word every frame starts with.</summary>
    internal static ReadOnlySpan<byte> SyncWord => "SYNC"u8;

    /// <summary>Bytes of codes between one sync header and the next sync word.</summary>
    internal const int FrameBytes = 1001;

    private const int HeaderBytes = 4;

    private readonly byte[] _header = new byte[HeaderBytes];
    private Phase _phase = Phase.Searching;
    private int _matched;
    private int _headerFill;
    private int _remaining;
    private int _predictor;
    private int _index;

    private enum Phase
    {
        Searching,
        Header,
        Data,
    }

    /// <summary>Sync words found since construction or the last <see cref="Reset"/>.</summary>
    public long SyncsFound { get; private set; }

    /// <summary>The most samples <see cref="Decode"/> can produce from <paramref name="bytes"/>
    /// bytes of stream.</summary>
    public static int MaxSamplesFor(int bytes) => bytes * 2;

    /// <summary>Forgets the stream: the next sample decoded follows the next sync word.</summary>
    public void Reset()
    {
        _phase = Phase.Searching;
        _matched = 0;
        _headerFill = 0;
        _remaining = 0;
        _predictor = 0;
        _index = 0;
        SyncsFound = 0;
    }

    /// <summary>
    /// Decodes one message's worth of the stream into <paramref name="destination"/>, which must
    /// hold <see cref="MaxSamplesFor"/> of the input's length.
    /// </summary>
    /// <returns>The samples written.</returns>
    public int Decode(ReadOnlySpan<byte> source, Span<short> destination)
    {
        if (destination.Length < MaxSamplesFor(source.Length))
        {
            throw new ArgumentException(
                $"{source.Length} bytes can decode to {MaxSamplesFor(source.Length)} samples, more than "
                + $"the {destination.Length} the destination holds", nameof(destination));
        }

        int written = 0;
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            switch (_phase)
            {
                case Phase.Searching:
                    if (b == SyncWord[_matched])
                    {
                        if (++_matched == SyncWord.Length)
                        {
                            _matched = 0;
                            _headerFill = 0;
                            _phase = Phase.Header;
                        }
                    }
                    else
                    {
                        // "SSYNC" still holds a sync word: a mismatched S starts a new match.
                        _matched = b == SyncWord[0] ? 1 : 0;
                    }

                    break;

                case Phase.Header:
                    _header[_headerFill++] = b;
                    if (_headerFill == HeaderBytes)
                    {
                        _index = Math.Clamp(
                            (int)BinaryPrimitives.ReadInt16LittleEndian(_header), 0, ImaAdpcm.MaxIndex);
                        _predictor = BinaryPrimitives.ReadInt16LittleEndian(_header.AsSpan(2));
                        _remaining = FrameBytes;
                        SyncsFound++;
                        _phase = Phase.Data;
                    }

                    break;

                default:
                    destination[written++] = ImaAdpcm.Apply(b & 0x0F, ref _predictor, ref _index);
                    destination[written++] = ImaAdpcm.Apply(b >> 4, ref _predictor, ref _index);
                    if (--_remaining == 0)
                    {
                        _phase = Phase.Searching;
                    }

                    break;
            }
        }

        return written;
    }
}

/// <summary>
/// The other direction of <see cref="ImaAdpcmSyncDecoder"/>: what an OpenWebRX server does to
/// its audio before sending it. Here so the simulator can put a modem's audio through the same
/// round trip a web receiver's listener gets, and so the decoder can be tested against a stream
/// built the way the server builds it.
/// </summary>
/// <remarks>
/// The quantiser is the IMA description's: the sign, then three magnitude bits by successive
/// comparison against the step, a half and a quarter of it. A sync header is written ahead of
/// every frame of <see cref="ImaAdpcmSyncDecoder.FrameBytes"/> bytes, carrying the state the
/// frame starts from, as OpenWebRX's server does.
/// </remarks>
public sealed class ImaAdpcmSyncEncoder
{
    private int _predictor;
    private int _index;
    private int _untilSync;
    private short? _pending;

    /// <summary>The most bytes <see cref="Encode"/> can write for <paramref name="samples"/>
    /// samples, sync headers included.</summary>
    public static int MaxBytesFor(int samples)
    {
        int bytes = (samples + 1) / 2;
        int frames = (bytes / ImaAdpcmSyncDecoder.FrameBytes) + 1;
        return bytes + (frames * 8);
    }

    /// <summary>
    /// Encodes <paramref name="samples"/>. An odd sample is held over to pair with the first of
    /// the next call, since a byte carries two.
    /// </summary>
    /// <returns>The bytes written.</returns>
    public int Encode(ReadOnlySpan<short> samples, Span<byte> destination)
    {
        int written = 0;
        int i = 0;
        while (true)
        {
            short first;
            if (_pending is short held)
            {
                if (i >= samples.Length)
                {
                    break;
                }

                first = held;
                _pending = null;
            }
            else
            {
                if (i >= samples.Length)
                {
                    break;
                }

                first = samples[i++];
            }

            if (i >= samples.Length)
            {
                _pending = first;
                break;
            }

            short second = samples[i++];
            if (_untilSync == 0)
            {
                ImaAdpcmSyncDecoder.SyncWord.CopyTo(destination[written..]);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(written + 4)..], (short)_index);
                BinaryPrimitives.WriteInt16LittleEndian(destination[(written + 6)..], (short)_predictor);
                written += 8;
                _untilSync = ImaAdpcmSyncDecoder.FrameBytes;
            }

            int low = EncodeSample(first);
            int high = EncodeSample(second);
            destination[written++] = (byte)(low | (high << 4));
            _untilSync--;
        }

        return written;
    }

    private int EncodeSample(short sample)
    {
        int difference = sample - _predictor;
        int step = ImaAdpcm.StepTable[_index];
        int code = 0;
        if (difference < 0)
        {
            code = 8;
            difference = -difference;
        }

        if (difference >= step)
        {
            code |= 4;
            difference -= step;
        }

        step >>= 1;
        if (difference >= step)
        {
            code |= 2;
            difference -= step;
        }

        step >>= 1;
        if (difference >= step)
        {
            code |= 1;
        }

        // The decoder's own update, so the two stay in step by construction.
        ImaAdpcm.Apply(code, ref _predictor, ref _index);
        return code;
    }
}
