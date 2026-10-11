namespace Packet.SoundModem.Iq;

/// <summary>
/// A two-channel 16-bit capture stream, as <see cref="SoundCardIqSource"/> needs it: a sound card's
/// left and right inputs, which on an IQ receiver are I and Q. ALSA on Linux
/// (<see cref="Packet.SoundModem.Channel.AlsaStereoCapture"/>); a WASAPI one for an in-process
/// Windows host would implement the same.
/// </summary>
public interface IStereoCapture : IDisposable
{
    /// <summary>The capture rate, frames (left, right pairs) per second.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Overruns so far: places where the card had audio and nobody took it. An increase seen after
    /// a <see cref="Read"/> means the hole lies immediately before the frames it returned.
    /// </summary>
    long Overruns { get; }

    /// <summary>The card's buffer, frames: about what an overrun throws away. 0 where unknown.</summary>
    int BufferFrames { get; }

    /// <summary>
    /// Reads interleaved <c>left, right</c> frames into <paramref name="interleaved"/>. Blocks
    /// until at least one frame is there. Stops short at an overrun rather than reading across it,
    /// so the frames returned are never either side of a hole.
    /// </summary>
    /// <returns>Frames read (not samples), or 0 only when the stream is closing.</returns>
    /// <exception cref="InvalidOperationException">The device failed and could not be brought
    /// back.</exception>
    int Read(Span<short> interleaved);
}
