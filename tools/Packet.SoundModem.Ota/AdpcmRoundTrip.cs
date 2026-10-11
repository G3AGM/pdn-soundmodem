using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Ota;

/// <summary>
/// What an OpenWebRX receiver does to audio on its way to a listener whose receiver operator has
/// chosen ADPCM: brought to a level, quantised to 16 bits, coded to 4-bit IMA ADPCM in sync
/// frames, sent, and decoded by the listener. The simulator puts a burst through this between the
/// channel and the modem (<c>sm-ota sim --adpcm</c>), so what the compression costs each mode is a
/// measurement rather than a guess (#599).
/// </summary>
/// <remarks>
/// <para>The level stands in for the receiver's AGC, which this does not model beyond the level
/// it leaves the audio at: the whole burst, signal and noise, is scaled to an RMS of
/// <c>levelDbfs</c> relative to full scale. IMA ADPCM adapts its step to the signal, so its
/// cost hardly moves with level until the audio is quiet enough to sit on the smallest step, or
/// loud enough to clip.</para>
/// <para>The coding runs at the receiver's 12 kHz, so it applies to modes that run there.</para>
/// </remarks>
internal static class AdpcmRoundTrip
{
    /// <summary>The level the round trip leaves audio at unless told otherwise, dBFS RMS.</summary>
    public const double DefaultLevelDbfs = -20;

    /// <summary>
    /// Returns <paramref name="audio"/> as a listener would hear it through the receiver.
    /// </summary>
    /// <param name="audio">The receiver's audio, at <paramref name="rate"/>.</param>
    /// <param name="rate">Must be the receiver's 12 kHz.</param>
    /// <param name="levelDbfs">The RMS level the audio is brought to before coding.</param>
    public static float[] Apply(float[] audio, int rate, double levelDbfs)
    {
        if (rate != OpenWebRxProtocol.AudioRate)
        {
            throw new ArgumentException(
                $"the ADPCM round trip runs at the receiver's {OpenWebRxProtocol.AudioRate} Hz, and this "
                + $"mode runs at {rate} Hz", nameof(rate));
        }

        double power = 0;
        foreach (float sample in audio)
        {
            power += (double)sample * sample;
        }

        double rms = Math.Sqrt(power / Math.Max(1, audio.Length));
        double scale = rms > 0 ? Math.Pow(10, levelDbfs / 20) * 32768 / rms : 0;
        var pcm = new short[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(Math.Round(audio[i] * scale), short.MinValue, short.MaxValue);
        }

        var encoder = new ImaAdpcmSyncEncoder();
        var stream = new byte[ImaAdpcmSyncEncoder.MaxBytesFor(pcm.Length)];
        int bytes = encoder.Encode(pcm, stream);

        var decoder = new ImaAdpcmSyncDecoder();
        var decoded = new short[ImaAdpcmSyncDecoder.MaxSamplesFor(bytes)];
        int samples = decoder.Decode(stream.AsSpan(0, bytes), decoded);

        // An odd final sample stays in the encoder, as it would until the next block; pad it so
        // the burst keeps its length.
        var output = new float[audio.Length];
        for (int i = 0; i < Math.Min(samples, output.Length); i++)
        {
            output[i] = decoded[i] / 32768f;
        }

        return output;
    }
}
