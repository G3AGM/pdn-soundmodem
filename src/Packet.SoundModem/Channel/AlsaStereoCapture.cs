using Packet.SoundModem.Audio;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Channel;

/// <summary>
/// An ALSA stereo capture stream for <see cref="SoundCardIqSource"/>: the card's left and right
/// inputs, interleaved, 16 bits each, with an overrun ending a read rather than being read across.
/// </summary>
/// <remarks>
/// The same <see cref="AlsaPcm"/> as <see cref="AlsaAudioInput"/>, with the same half-second
/// buffer and the same recovery; the differences are two channels where that asks for one, and
/// <see cref="AlsaPcm.ReadUntilOverrun"/> where that reads across an overrun, because an IQ
/// consumer has to be told where a gap is and an audio one does not.
/// </remarks>
public sealed class AlsaStereoCapture : IStereoCapture
{
    private readonly AlsaPcm _pcm;

    /// <summary>Opens a two-channel capture stream on <paramref name="device"/>.</summary>
    /// <param name="device">ALSA device name.</param>
    /// <param name="sampleRate">Capture rate.</param>
    /// <param name="bufferMicroseconds">How long the reader may be away before the card overruns;
    /// see <see cref="AlsaAudioInput"/> for why half a second.</param>
    public AlsaStereoCapture(string device, int sampleRate, int bufferMicroseconds = 500_000)
    {
        _pcm = AlsaPcm.Open(
            device, AlsaPcm.Direction.Capture, channels: 2, sampleRate, bufferMicroseconds);
        SampleRate = sampleRate;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public long Overruns => _pcm.Xruns;

    /// <inheritdoc />
    public int BufferFrames => _pcm.BufferFrames;

    /// <summary>The buffer the card gave us, in milliseconds (0 if it would not say).</summary>
    public int BufferMilliseconds => _pcm.BufferMilliseconds;

    /// <summary>The period the card gave us, in milliseconds (0 if it would not say).</summary>
    public int PeriodMilliseconds => _pcm.PeriodMilliseconds;

    /// <summary>Why the buffer asked for was not the one used, or null when it was.</summary>
    public string? ConfigurationWarning => _pcm.ConfigurationWarning;

    /// <inheritdoc />
    public int Read(Span<short> interleaved) => _pcm.ReadUntilOverrun(interleaved[..(interleaved.Length & ~1)]);

    /// <inheritdoc />
    public void Dispose() => _pcm.Dispose();
}
