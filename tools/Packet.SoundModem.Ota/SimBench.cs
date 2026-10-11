using Packet.SoundModem.Modems;
using M0LTE.Fm;
namespace Packet.SoundModem.Ota;

/// <summary>Which layer of the stack a sim point scores.</summary>
internal enum SimLayer
{
    /// <summary>The full <see cref="SimModem"/> path: an IL2P+CRC AX.25 frame through the mode's
    /// <c>IModem</c>, scored on whether the frame came back - the deployment reality, and the
    /// generalization that drives any catalogue mode.</summary>
    Frame,

    /// <summary>The raw datac packet through <see cref="DatacPacketProbe"/>, scored on the packet's
    /// own CRC and post-LDPC coded BER - the layer FreeDV publishes its operating points in.</summary>
    Packet,
}

/// <summary>One waterfall point: everything one (mode, channel, layer, SNR, level) cell measured.</summary>
/// <param name="Trials">Bursts run.</param>
/// <param name="Successes">Frames matched (frame layer) or packets CRC-OK (packet layer).</param>
/// <param name="BitErrors">Coded bit errors - a lost frame/packet counts all its bits wrong.</param>
/// <param name="TotalBits">Bits compared.</param>
/// <param name="Margin">Frame layer: mean FEC-corrected bytes on matched frames. Packet layer: mean
/// LDPC parity checks satisfied - a soft distance-from-the-cliff indicator either way.</param>
/// <param name="LevelDb">Absolute input-level scale applied to the post-channel signal+noise before
/// the receiver, in dB. 0 is the nominal level; the SNR is unchanged (signal and noise scale
/// together) - the level-invariance axis. A level-sensitive front end degrades here at fixed SNR.</param>
internal sealed record SimPointResult(
    string Mode, SimChannelKind Channel, SimLayer Layer, double SnrDb,
    int Trials, int Successes, long BitErrors, long TotalBits, double Margin, double LevelDb = 0, double CfoHz = 0)
{
    /// <summary>Fraction of bursts that delivered - the number FreeDV's "N/100" is quoted as.</summary>
    public double SuccessRate => Trials == 0 ? 0 : (double)Successes / Trials;

    /// <summary>Frame/packet error rate.</summary>
    public double Fer => 1 - SuccessRate;

    /// <summary>Coded bit-error rate (lost bursts saturate it toward 0.5). At the frame layer this
    /// tracks the FER - IL2P+CRC is all-or-nothing - so read the FER there; the packet layer's coded
    /// BER is the smoothly-degrading post-LDPC number.</summary>
    public double CodedBer => TotalBits == 0 ? double.NaN : (double)BitErrors / TotalBits;

    /// <summary>Wilson 95&#160;% confidence interval on the success rate.</summary>
    public (double Lo, double Hi) SuccessCi => SimStats.Wilson(Successes, Trials);
}

/// <summary>Runs the generate → channel → score loop over a grid, with bounded parallelism.</summary>
/// <remarks>
/// <para>Each burst is a fully independent trial - its own payload, its own fade and noise
/// realisation from its seed - so bursts parallelise cleanly and a point reproduces from
/// (mode, channel, SNR, first-seed, count). Parallelism is bounded by the caller because the box is
/// a shared 16&#160;GB LXC that may be running another heavy sim battery; each in-flight burst holds
/// a burst's worth of audio and channel scratch, so a handful of workers is the safe envelope.</para>
/// </remarks>
internal static class SimBench
{
    /// <summary>Scores one (mode, channel, layer, SNR, level) cell over <paramref name="bursts"/>
    /// trials.</summary>
    /// <param name="levelDb">Absolute level scale on the post-channel signal+noise (0 = nominal).
    /// The SNR is unchanged; this is the level-invariance axis.</param>
    /// <param name="adpcmLevelDbfs">When set, the post-channel audio goes through an OpenWebRX
    /// receiver's ADPCM round trip at this RMS level before the modem hears it
    /// (<see cref="AdpcmRoundTrip"/>). Frame layer, 12 kHz modes only.</param>
    public static SimPointResult RunPoint(
        string mode, int? rate, SimLayer layer, SimChannelKind kind, double snrDb,
        int bursts, int frameBytes, int firstSeed, int workers, double levelDb = 0,
        int txDelayMs = 0, double cfoHz = 0, PskDetector? detector = null, double? centreHz = null,
        PskDetector? secondDetector = null, double impulseRatePerMinute = 0, bool bridged = false,
        double? adpcmLevelDbfs = null)
    {
        if (adpcmLevelDbfs is not null && (layer != SimLayer.Frame || ArdopFrameProbe.IsArdopMode(mode)))
        {
            throw new ArgumentException("--adpcm applies to the frame layer's catalogue modes only");
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) };
        float levelScale = (float)Math.Pow(10, levelDb / 20.0);
        int successes = 0;
        long bitErrors = 0;
        long totalBits = 0;
        double marginSum = 0;
        object gate = new();

        Parallel.For(
            0, bursts, options,
            () => new Acc(),
            (i, _, acc) =>
            {
                int seed = firstSeed + i;
                if (ArdopFrameProbe.IsArdopMode(mode))
                {
                    // ARDOP is a session TNC, not a catalogue IModem, so it takes the
                    // dedicated-probe path (the DatacPacketProbe pattern) regardless of
                    // the requested layer; the score is frame-layer semantics: payload
                    // byte-exact or the burst's bits count wrong.
                    var probe = new ArdopFrameProbe(mode);
                    ArdopProbeResult r = probe.Run(seed, kind, snrDb, levelScale, cfoHz,
                        impulseRatePerMinute);
                    int bits = probe.PayloadBytes * 8;
                    acc.Successes += r.Matched ? 1 : 0;
                    acc.BitErrors += r.Matched ? 0 : bits;
                    acc.TotalBits += bits;
                    acc.Margin += r.Quality;
                }
                else if (layer == SimLayer.Packet)
                {
                    var probe = new DatacPacketProbe(mode);
                    PacketResult r = probe.Run(seed, kind, snrDb, levelScale);
                    acc.Successes += r.CrcOk ? 1 : 0;
                    acc.BitErrors += r.BitErrors;
                    acc.TotalBits += r.PayloadBits;
                    acc.Margin += r.ParityChecks;
                }
                else
                {
                    var sm = new SimModem(mode, rate)
                    {
                        Bridged = bridged,
                        Options = new ModemOptions(CentreFrequencyHz: centreHz, Detector: detector,
                            SecondDetector: secondDetector),
                    };
                    byte[] frame = SimModem.Frame(frameBytes, seed);
                    float[] active = sm.RenderBurst(frame, txDelayMs);
                    // An FM mode meets a real FM link - noise on the carrier, then a limiter and a
                    // discriminator - rather than the linear SSB path, and its axis is CNR in the
                    // IF bandwidth. The two channels are not interchangeable and neither are their
                    // numbers, so the mode picks the physics rather than the caller.
                    float[] rx = FmModes.IsFmChannel(kind)
                        ? SimChannel.ApplyFm(active, sm.Rate, kind, mode, snrDb, seed + 3_000_000)
                        : SimChannel.Apply(active, sm.Rate, kind, snrDb, seed + 3_000_000, cfoHz: cfoHz,
                            impulseRatePerMinute: impulseRatePerMinute);
                    ScaleInPlace(rx, levelScale);
                    if (adpcmLevelDbfs is double adpcmLevel)
                    {
                        rx = AdpcmRoundTrip.Apply(rx, sm.Rate, adpcmLevel);
                    }

                    SimDecode d = sm.Decode(rx, frame);
                    int bits = frameBytes * 8;
                    acc.Successes += d.Matched ? 1 : 0;
                    acc.BitErrors += d.Matched ? 0 : bits;
                    acc.TotalBits += bits;
                    acc.Margin += d.CorrectedBytes;
                }

                return acc;
            },
            acc =>
            {
                lock (gate)
                {
                    successes += acc.Successes;
                    bitErrors += acc.BitErrors;
                    totalBits += acc.TotalBits;
                    marginSum += acc.Margin;
                }
            });

        double margin = bursts > 0 ? marginSum / bursts : 0;
        return new SimPointResult(
            mode, kind, layer, snrDb, bursts, successes, bitErrors, totalBits, margin, levelDb, cfoHz);
    }

    private static void ScaleInPlace(float[] samples, float scale)
    {
        if (scale == 1f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
        }
    }

    /// <summary>The SNR (dB) where the success rate crosses <paramref name="target"/>, by linear
    /// interpolation between the bracketing points - the waterfall "knee". <see cref="double.NaN"/>
    /// if the curve never crosses (all points above, or all below, the target).</summary>
    public static double Threshold(IReadOnlyList<SimPointResult> curve, double target = 0.5)
    {
        // Points must be in ascending SNR order; find the lowest crossing from below to above.
        for (int i = 1; i < curve.Count; i++)
        {
            double p0 = curve[i - 1].SuccessRate;
            double p1 = curve[i].SuccessRate;
            if (p0 < target && p1 >= target)
            {
                double s0 = curve[i - 1].SnrDb;
                double s1 = curve[i].SnrDb;
                double t = (target - p0) / (p1 - p0);
                return s0 + t * (s1 - s0);
            }
        }

        return double.NaN;
    }

    private struct Acc
    {
        public int Successes;
        public long BitErrors;
        public long TotalBits;
        public double Margin;
    }
}

/// <summary>Binomial statistics for the waterfalls.</summary>
internal static class SimStats
{
    /// <summary>Wilson score interval for a binomial proportion - well-behaved at the 0/1 tails a
    /// waterfall lives in, unlike the normal approximation (which gives [0,0] at zero errors and
    /// pretends to certainty).</summary>
    public static (double Lo, double Hi) Wilson(int successes, int trials, double z = 1.96)
    {
        if (trials == 0)
        {
            return (0, 1);
        }

        double p = (double)successes / trials;
        double z2 = z * z;
        double denom = 1 + z2 / trials;
        double centre = p + z2 / (2.0 * trials);
        double margin = z * Math.Sqrt((p * (1 - p) / trials) + (z2 / (4.0 * trials * trials)));
        return ((centre - margin) / denom, (centre + margin) / denom);
    }
}
