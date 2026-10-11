using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Ota;

/// <summary>
/// What a sound card carrying IQ does to a signal: single sideband placed on a dial in a complex
/// stream, and the card's own gain and phase mismatch and 16-bit quantisation on the way in.
/// </summary>
internal static class CardSynth
{
    private const int HilbertTaps = 255;

    /// <summary>
    /// Adds real audio at <paramref name="rate"/> to <paramref name="iq"/> as a single-sideband
    /// signal with its dial at <paramref name="dialHz"/>: above the dial for USB, mirrored below it
    /// for LSB. The audio is delayed by the Hilbert filter's 127 samples.
    /// </summary>
    public static void AddSsb(Span<float> iq, ReadOnlySpan<float> audio, int rate, double dialHz, bool lsb, double amplitude)
    {
        const int mid = HilbertTaps / 2;
        var h = new double[HilbertTaps];
        for (int k = 0; k < HilbertTaps; k++)
        {
            int m = k - mid;
            double window = 0.42 - (0.5 * Math.Cos(2 * Math.PI * k / (HilbertTaps - 1)))
                + (0.08 * Math.Cos(4 * Math.PI * k / (HilbertTaps - 1)));
            h[k] = m % 2 == 0 ? 0 : 2 / (Math.PI * m) * window;
        }

        int n = Math.Min(audio.Length, iq.Length / 2);
        for (int k = 0; k < n; k++)
        {
            double re = k >= mid ? audio[k - mid] : 0;
            double im = 0;
            for (int j = 1; j < HilbertTaps; j += 2)
            {
                int at = k - j;
                if (at >= 0)
                {
                    im += h[j] * audio[at];
                }
            }

            if (lsb)
            {
                im = -im;
            }

            double w = 2 * Math.PI * dialHz * k / rate;
            double c = Math.Cos(w);
            double s = Math.Sin(w);
            iq[2 * k] += (float)(amplitude * ((re * c) - (im * s)));
            iq[(2 * k) + 1] += (float)(amplitude * ((re * s) + (im * c)));
        }
    }

    /// <summary>
    /// What a card with Q <paramref name="gainDb"/> louder than I and <paramref name="phaseDegrees"/>
    /// off quadrature makes of ideal IQ, as interleaved 16-bit pairs: for <c>I = cos t</c> it gives
    /// <c>Q = g sin(t + phi)</c>.
    /// </summary>
    public static short[] Card(ReadOnlySpan<float> iq, double gainDb, double phaseDegrees)
    {
        double g = Math.Pow(10, gainDb / 20);
        double phi = phaseDegrees * Math.PI / 180;
        var pcm = new short[iq.Length];
        for (int k = 0; k + 1 < iq.Length; k += 2)
        {
            pcm[k] = Pcm(iq[k]);
            pcm[k + 1] = Pcm(g * ((iq[k + 1] * Math.Cos(phi)) + (iq[k] * Math.Sin(phi))));
        }

        return pcm;
    }

    private static short Pcm(double v) =>
        (short)Math.Clamp(Math.Round(v * 32768), short.MinValue, short.MaxValue);
}

/// <summary>A card that plays back a recording of 16-bit pairs: the bench's stand-in for ALSA.</summary>
internal sealed class RecordedStereoCapture(int sampleRate, short[] pcm) : IStereoCapture
{
    private int _at;

    /// <inheritdoc/>
    public int SampleRate { get; } = sampleRate;

    /// <inheritdoc/>
    public long Overruns => 0;

    /// <inheritdoc/>
    public int BufferFrames => 0;

    /// <inheritdoc/>
    public int Read(Span<short> interleaved)
    {
        int frames = Math.Min(interleaved.Length / 2, (pcm.Length - _at) / 2);
        if (frames <= 0)
        {
            return 0;
        }

        pcm.AsSpan(_at, 2 * frames).CopyTo(interleaved);
        _at += 2 * frames;
        return frames;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>Which receiver a sound-card IQ ladder point is heard through.</summary>
internal enum CardArm
{
    /// <summary>The post-channel audio decoded as it is: what an SSB radio's audio output into a
    /// sound card gives. The reference.</summary>
    Audio,

    /// <summary>The same audio as USB at 48 kS/s on a dial 6 kHz above the IQ centre, through a
    /// perfectly matched 16-bit card, <see cref="SoundCardIqSource"/> and the front end.</summary>
    Card,

    /// <summary>As <see cref="Card"/>, on a card 0.5 dB and 3 degrees out, corrected by the
    /// source, with a signal 34 dB stronger than the wanted one on the channel's mirror: what the
    /// correction is for.</summary>
    CorrectedCardWithImage,
}

/// <summary>
/// The SSB ladder through sound-card IQ (issue #596): a mode's burst through the sim channel at an
/// SNR in 3 kHz, then heard as audio or through the sound-card IQ path, scored on whether the frame
/// came back.
/// </summary>
/// <remarks>
/// <para>The noise is added by <see cref="SimChannel.Apply"/> to the audio before it is put on the
/// IQ, so every arm meets the same noise in the channel's passband and the arms differ only in the
/// receive path: the transport, the balance correction, the 16-bit card and the front end's filters
/// and demodulator. Outside the channel the IQ is clean but for the image arm's interferer.</para>
/// <para>Deterministic in (mode, SNR, arm, first seed, bursts).</para>
/// </remarks>
internal static class SoundCardIqBench
{
    /// <summary>The card's rate: the G90's.</summary>
    public const int CardRate = 48000;

    /// <summary>Where the dial sits from the IQ centre: <see cref="IqTuning.DcClearOffsetHz"/> at 48 kS/s.</summary>
    public const double DialOffsetHz = 6000;

    private const double WantedAmplitude = 0.01;
    private const double InterfererAmplitude = 0.5;
    private const double GainImbalanceDb = 0.5;
    private const double PhaseImbalanceDegrees = 3.0;

    /// <summary>Scores <paramref name="bursts"/> bursts of <paramref name="mode"/> at
    /// <paramref name="snrDb"/> (3 kHz) through <paramref name="arm"/>; returns the frames recovered.</summary>
    public static int RunPoint(string mode, double snrDb, CardArm arm, int bursts, int firstSeed = 1, int workers = 4)
    {
        int successes = 0;
        Parallel.For(
            0, bursts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) },
            () => 0,
            (b, _, local) => local + (RunBurst(mode, snrDb, arm, firstSeed + b) ? 1 : 0),
            local => Interlocked.Add(ref successes, local));
        return successes;
    }

    private static bool RunBurst(string mode, double snrDb, CardArm arm, int seed)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(60, seed);
        float[] audio = SimChannel.Apply(sim.RenderBurst(frame, txDelayMilliseconds: 150), sim.Rate, SimChannelKind.Awgn, snrDb, seed);
        if (arm == CardArm.Audio)
        {
            return sim.Decode(audio, frame).Matched;
        }

        float[] wanted = Normalised(IqSynth.Resample(audio, sim.Rate, CardRate));
        var iq = new float[2 * wanted.Length];
        CardSynth.AddSsb(iq, wanted, CardRate, DialOffsetHz, lsb: false, WantedAmplitude);
        bool image = arm == CardArm.CorrectedCardWithImage;
        if (image)
        {
            // Another station of the same kind, on the mirror of the channel: LSB on the mirror of
            // the dial lands exactly where the wanted USB signal's image would.
            var other = new SimModem(mode);
            byte[] otherFrame = SimModem.Frame(60, seed + 5_000_000);
            float[] otherAudio = SimChannel.Apply(
                other.RenderBurst(otherFrame, txDelayMilliseconds: 150), other.Rate, SimChannelKind.Awgn, 30, seed + 5_000_000);
            CardSynth.AddSsb(
                iq, Normalised(IqSynth.Resample(otherAudio, other.Rate, CardRate)), CardRate, -DialOffsetHz, lsb: true,
                InterfererAmplitude);
        }

        short[] pcm = image
            ? CardSynth.Card(iq, GainImbalanceDb, PhaseImbalanceDegrees)
            : CardSynth.Card(iq, 0, 0);
        using var source = new SoundCardIqSource(new RecordedStereoCapture(CardRate, pcm), new SoundCardIqOptions
        {
            GainImbalanceDb = image ? GainImbalanceDb : 0,
            PhaseImbalanceDegrees = image ? PhaseImbalanceDegrees : 0,
        });
        using var receiver = new IqReceiver(
            source,
            // Gain back to the audio arm's level: the wanted signal sits 40 dB down on the card to
            // leave the image arm's interferer room, and afsk1200's decoder hears a burst about
            // 2 dB worse at that level than at full scale (measured 2026-10-11: 15/50 against
            // 34/50 at +6 dB), which is a level matter for the operator's "gain", not the path.
            [new IqChannelSpec { OffsetHz = DialOffsetHz, Demodulation = IqDemodulation.Usb, Gain = (float)(1 / WantedAmplitude) }],
            new IqReceiverOptions { DspRate = sim.Rate });
        return sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched;
    }

    /// <summary>Scaled to a peak of 1, so the card's level is set by the amplitude alone.</summary>
    private static float[] Normalised(float[] audio)
    {
        float peak = 1e-9f;
        foreach (float v in audio)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        for (int k = 0; k < audio.Length; k++)
        {
            audio[k] /= peak;
        }

        return audio;
    }
}
