using Packet.SoundModem.Iq;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Ota;

/// <summary>What one FM burst scored to.</summary>
/// <param name="Index">Position in the pass.</param>
/// <param name="Mode">The mode.</param>
/// <param name="StartSeconds">Where the active burst began in the capture.</param>
/// <param name="AskedSnrDb">SNR the rig was asked to inject - beside the measured one.</param>
/// <param name="Decoded">Whether the mode's demodulator emitted the sent frame bit-exact.</param>
/// <param name="FramesDecoded">Frames the receiver emitted from the burst window (0, 1, or more).</param>
/// <param name="Snr">The delivered SNR measured from the burst against its own noise lead-in.</param>
/// <param name="PeakDeviationHz">The achieved peak FM deviation measured over the burst window - the
/// signal's deviation at high SNR, the signal-plus-noise excursion at low SNR.</param>
/// <param name="CorrectedBytes">FEC-repaired bytes on the matched frame (a channel-stress floor).</param>
internal sealed record FmBurstScore(
    int Index,
    string Mode,
    double StartSeconds,
    double AskedSnrDb,
    bool Decoded,
    int FramesDecoded,
    SnrEstimate? Snr,
    double PeakDeviationHz,
    int CorrectedBytes);

/// <summary>Everything a scored FM pass measured.</summary>
/// <param name="AudioSeconds">Length of the discriminated audio.</param>
/// <param name="Bursts">One row per scheduled burst, in order.</param>
internal sealed record FmCaptureScore(double AudioSeconds, IReadOnlyList<FmBurstScore> Bursts);

/// <summary>
/// Scores a captured (or rehearsed) FM pass - the FM counterpart of <see cref="OfdmBurstScorer"/>.
/// The capture's IQ is FM-demodulated to the mode's DSP-rate audio with
/// <see cref="IqToFmAudioConverter"/> (the discriminator scaled so ±target-deviation → ±0.5, a
/// fixed scale so no rung rescales another), then each burst is scored in its own window: a fresh
/// receiver is driven through <see cref="SimModem.Decode"/> exactly as the software waterfall drives
/// it (an unambiguous 0/1 frame result per burst), the delivered SNR is measured against the burst's
/// own transmitted noise lead-in with <see cref="SnrEstimator"/>, and the achieved peak deviation is
/// read back off the discriminated audio. Nominal SNR is never trusted: the injected figure is the
/// request, this is the measurement, and the two are compared.
/// </summary>
internal sealed class FmBurstScorer
{
    // Recovered audio has been through the discriminator's decimation low-pass (when the capture
    // rate exceeds the DSP rate), so its noise occupies that band - the SNR estimator must be told,
    // or it over-subtracts noise at the bottom of the ladder. At a ×1 factor there is no decimation
    // and the noise fills the whole band.
    private const double OccupiedLowHz = 50;

    // Leading/trailing audio around the nominal burst for the decoder's acquisition and end-of-burst.
    private const double DecodePreRollSeconds = 0.3;
    private const double DecodePostRollSeconds = 0.5;

    // The SNR signal window is inset from the burst edges; the noise window sits before the burst.
    // Kept small so short FSK bursts (a fast baud makes even a preamble-plus-frame burst brief) still
    // leave a usable stationary-power window between the insets.
    private const double SnrInsetSeconds = 0.02;
    private const double NoiseGuardSeconds = 0.3;
    private const double NoiseWindowSeconds = 2.0;

    private readonly float[] _audio;
    private readonly int _dspRate;
    private readonly int _captureRate;
    private readonly double _targetDeviationHz;

    /// <summary>Builds a scorer over already-discriminated audio (the offline/test seam). The audio
    /// must be scaled so ±<paramref name="targetDeviationHz"/> maps to ±0.5, as
    /// <see cref="FromCapture"/> produces it.</summary>
    public FmBurstScorer(float[] audio, int dspRate, int captureRate, double targetDeviationHz)
    {
        _audio = audio;
        _dspRate = dspRate;
        _captureRate = captureRate;
        _targetDeviationHz = targetDeviationHz;
    }

    /// <summary>Reads a capture WAV's IQ, FM-discriminates it to the mode's DSP-rate audio and builds
    /// a scorer over it.</summary>
    /// <param name="capturePath">The 2-channel IQ capture.</param>
    /// <param name="dialHz">Down-shift that lands the FM carrier at 0&#160;Hz (0 when tuned to it).</param>
    /// <param name="dspRate">The mode's DSP audio rate.</param>
    /// <param name="targetDeviationHz">The mode's target peak deviation - the discriminator's fixed
    /// scale and the factor the achieved-deviation read-back multiplies back by.</param>
    public static FmBurstScorer FromCapture(
        string capturePath, double dialHz, int dspRate, double targetDeviationHz)
    {
        using var reader = new PcmWavReader(capturePath);
        if (reader.Channels != 2)
        {
            throw new InvalidDataException($"expected a 2-channel IQ capture, found {reader.Channels}");
        }

        if (reader.SampleRate % dspRate != 0)
        {
            throw new InvalidDataException(
                $"capture rate {reader.SampleRate} Hz is not an integer multiple of the {dspRate} Hz "
                + "DSP rate - the discriminator's decimation needs one");
        }

        // Read the whole capture, then discriminate in one pass - the reference-style whole-array
        // path (a ladder pass is minutes, not the hour-long campaign the streaming SSB converter
        // exists for). A live pass captured at 48 kHz keeps this comfortably in memory.
        var samples = new short[reader.FrameCount * 2];
        int off = 0;
        int got;
        while ((got = reader.ReadFrames(samples.AsSpan(off))) > 0)
        {
            off += got * 2;
        }

        float[] audio = new IqToFmAudioConverter(new IqToFmAudioOptions
        {
            InputRate = reader.SampleRate,
            OutputRate = dspRate,
            DialHz = dialHz,
            DeviationHz = targetDeviationHz,
        }).Convert(samples.AsSpan(0, off));

        return new FmBurstScorer(audio, dspRate, reader.SampleRate, targetDeviationHz);
    }

    /// <summary>Scores every burst the manifest describes.</summary>
    public FmCaptureScore Score(FmCampaignManifest manifest)
    {
        var rows = new List<FmBurstScore>(manifest.Bursts.Count);
        for (int index = 0; index < manifest.Bursts.Count; index++)
        {
            rows.Add(ScoreBurst(index, manifest.Bursts[index]));
        }

        return new FmCaptureScore(_audio.Length / (double)_dspRate, rows);
    }

    private FmBurstScore ScoreBurst(int index, FmCampaignBurst burst)
    {
        int startSample = (int)Math.Round(burst.StartSeconds * _dspRate);
        int burstSamples = (int)Math.Round(burst.BurstSeconds * _dspRate);
        byte[] frame = SimModem.Frame(burst.FrameBytes, burst.Seed);

        (bool decoded, int frames, int corrected) = Decode(burst.Mode, startSample, burstSamples, frame);
        SnrEstimate? snr = MeasureSnr(startSample, burstSamples);
        double peakDev = MeasurePeakDeviation(startSample, burstSamples);

        return new FmBurstScore(
            index, burst.Mode, burst.StartSeconds, burst.SnrDb, decoded, frames, snr, peakDev, corrected);
    }

    private (bool Decoded, int Frames, int Corrected) Decode(
        string mode, int startSample, int burstSamples, byte[] frame)
    {
        int from = Math.Max(0, startSample - (int)(DecodePreRollSeconds * _dspRate));
        int to = Math.Min(
            _audio.Length, startSample + burstSamples + (int)(DecodePostRollSeconds * _dspRate));
        if (to - from <= 0)
        {
            return (false, 0, 0);
        }

        var sm = new SimModem(mode, _dspRate);
        SimDecode d = sm.Decode(_audio.AsSpan(from, to - from), frame);
        return (d.Matched, d.FramesDecoded, d.CorrectedBytes);
    }

    /// <summary>Reads the achieved peak deviation off the discriminated audio: it was scaled so
    /// ±target → ±0.5, so peak |audio| × 2 × target is the excursion in Hz.</summary>
    private double MeasurePeakDeviation(int startSample, int burstSamples)
    {
        int from = Math.Max(0, startSample);
        int to = Math.Min(_audio.Length, startSample + burstSamples);
        double peak = 0;
        for (int k = from; k < to; k++)
        {
            peak = Math.Max(peak, Math.Abs(_audio[k]));
        }

        return peak * 2.0 * _targetDeviationHz;
    }

    private SnrEstimate? MeasureSnr(int startSample, int burstSamples)
    {
        int inset = (int)(SnrInsetSeconds * _dspRate);
        int burstFrom = startSample + inset;
        int burstTo = Math.Min(_audio.Length, startSample + burstSamples - inset);

        int noiseTo = startSample - (int)(NoiseGuardSeconds * _dspRate);
        int noiseFrom = Math.Max(0, noiseTo - (int)(NoiseWindowSeconds * _dspRate));

        if (burstTo - burstFrom < 1024 || noiseTo - noiseFrom < 1024)
        {
            return null;
        }

        // At a ×1 capture-to-DSP factor there is no decimation and the recovered noise fills the
        // whole band; above ×1 the discriminator's decimation low-pass caps it at 0.45·DSP.
        bool decimated = _captureRate != _dspRate;
        double occupiedLow = decimated ? OccupiedLowHz : 0;
        double occupiedHigh = decimated ? 0.45 * _dspRate : 0;

        return SnrEstimator.Estimate(
            _audio.AsSpan(burstFrom, burstTo - burstFrom),
            _audio.AsSpan(noiseFrom, noiseTo - noiseFrom),
            _dspRate,
            occupiedLowHz: occupiedLow,
            occupiedHighHz: occupiedHigh);
    }
}
