using M0LTE.Dsp;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Synthetic IQ for the front end's tests and its FM ladder: audio placed on an RF offset in a
/// complex stream at any source rate, as an SDR would deliver it.
/// </summary>
internal static class IqSynth
{
    /// <summary>Resamples real audio from <paramref name="fromRate"/> to <paramref name="toRate"/>,
    /// flushing the filter so the tail is not lost, and trimming the filter's delay so sample 0
    /// stays sample 0.</summary>
    public static float[] Resample(ReadOnlySpan<float> audio, int fromRate, int toRate)
    {
        if (fromRate == toRate)
        {
            return audio.ToArray();
        }

        var resampler = new RationalResampler(new RationalResamplerOptions
        {
            InputRate = fromRate,
            OutputRate = toRate,
            // Synthesis only: the audio band is all that has to come through, and images far
            // out do no harm because the receiver's filters remove them.
            StopbandAttenuationDb = 80,
        });
        int tail = (int)Math.Ceiling(resampler.DelayInputSamples) + 2;
        var padded = new float[audio.Length + tail];
        audio.CopyTo(padded);
        var output = new float[resampler.MaxOutput(padded.Length)];
        int produced = resampler.Process(padded, output);
        int delay = (int)Math.Round(resampler.DelayOutputSamples);
        int length = Math.Min(produced - delay, (int)((long)audio.Length * toRate / fromRate));
        return output.AsSpan(delay, Math.Max(0, length)).ToArray();
    }

    /// <summary>Frequency-modulates real audio already at <paramref name="rate"/> onto a carrier
    /// at <paramref name="offsetHz"/>, as interleaved I, Q: <paramref name="hzPerUnit"/> of
    /// deviation per unit of audio.</summary>
    public static float[] Fm(ReadOnlySpan<float> audio, int rate, double offsetHz, double hzPerUnit, double amplitude)
    {
        var iq = new float[2 * audio.Length];
        double phase = 0;
        double carrier = 2.0 * Math.PI * offsetHz / rate;
        double step = 2.0 * Math.PI * hzPerUnit / rate;
        for (int n = 0; n < audio.Length; n++)
        {
            phase += carrier + (step * audio[n]);
            phase = Math.IEEERemainder(phase, 2.0 * Math.PI);
            iq[2 * n] = (float)(amplitude * Math.Cos(phase));
            iq[(2 * n) + 1] = (float)(amplitude * Math.Sin(phase));
        }

        return iq;
    }

    /// <summary>Places real audio at <paramref name="rate"/> on <paramref name="offsetHz"/> as a
    /// double-sideband signal: its upper sideband is the USB signal, its lower the LSB one.</summary>
    public static float[] Dsb(ReadOnlySpan<float> audio, int rate, double offsetHz)
    {
        var iq = new float[2 * audio.Length];
        AddDsb(iq, audio, rate, offsetHz);
        return iq;
    }

    /// <summary>Adds a double-sideband signal into an existing stream.</summary>
    public static void AddDsb(Span<float> iq, ReadOnlySpan<float> audio, int rate, double offsetHz)
    {
        double w = 2.0 * Math.PI * offsetHz / rate;
        int n = Math.Min(audio.Length, iq.Length / 2);
        for (int k = 0; k < n; k++)
        {
            double phase = w * k;
            iq[2 * k] += (float)(audio[k] * Math.Cos(phase));
            iq[(2 * k) + 1] += (float)(audio[k] * Math.Sin(phase));
        }
    }

    /// <summary>Adds complex white Gaussian noise of total power <paramref name="power"/>
    /// (E|n|^2) per sample.</summary>
    public static void AddNoise(Span<float> iq, double power, int seed)
    {
        var random = new Random(seed);
        double sigma = Math.Sqrt(power / 2);
        for (int k = 0; k < iq.Length; k++)
        {
            // Box-Muller, one draw per component.
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            iq[k] += (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }

    /// <summary>
    /// Puts a stream through what an RTL dongle and rtl_tcp do to it: scaled so the RMS on each
    /// axis is <paramref name="rmsOfFullScale"/> of full scale (the dongle's gain), clipped and
    /// rounded to unsigned bytes (its 8-bit converter), and turned back to floats with
    /// <see cref="Packet.SoundModem.RtlTcp.RtlTcpProtocol.Convert"/>, as the rtl_tcp source does.
    /// </summary>
    public static void ThroughDongleBytes(Span<float> iq, double rmsOfFullScale)
    {
        double sum = 0;
        foreach (float v in iq)
        {
            sum += v * (double)v;
        }

        double scale = rmsOfFullScale / Math.Sqrt(sum / Math.Max(1, iq.Length));
        var bytes = new byte[iq.Length];
        for (int k = 0; k < iq.Length; k++)
        {
            bytes[k] = (byte)Math.Clamp(Math.Round((iq[k] * scale * 127.5) + 127.5), 0, 255);
        }

        Packet.SoundModem.RtlTcp.RtlTcpProtocol.Convert(bytes, iq);
    }

    /// <summary>Pumps a receiver over a whole source, draining every channel as it goes, and
    /// returns each channel's audio.</summary>
    public static float[][] Receive(IqReceiver receiver)
    {
        var outputs = new List<float>[receiver.Channels.Count];
        for (int c = 0; c < outputs.Length; c++)
        {
            outputs[c] = [];
        }

        var buffer = new float[16384];
        bool more = true;
        while (more)
        {
            more = receiver.Pump();
            for (int c = 0; c < outputs.Length; c++)
            {
                // Only what is there: an empty Read waits for audio, which pumped is never coming.
                while (receiver.Channels[c].Available > 0)
                {
                    int got = receiver.Channels[c].Read(buffer);
                    outputs[c].AddRange(buffer.AsSpan(0, got));
                }
            }
        }

        var result = new float[outputs.Length][];
        for (int c = 0; c < outputs.Length; c++)
        {
            result[c] = [.. outputs[c]];
        }

        return result;
    }
}

/// <summary>Which receiver a front-end FM ladder point is heard through.</summary>
internal enum IqFmArm
{
    /// <summary>The FM ladders' own batch discriminator (<see cref="IqToFmAudioConverter"/>) on a
    /// 48 kS/s capture centred on the carrier, IF-filtered to the same bandwidth: the reference.</summary>
    Reference,

    /// <summary>The front end (<see cref="IqReceiver"/>) on a wideband capture with the carrier
    /// <see cref="IqFrontEndBench.OffsetHz"/> off its centre.</summary>
    FrontEnd,
}

/// <summary>
/// The FM ladder through the IQ front end: a mode's burst frequency-modulated at its deviation,
/// carrier-to-noise set in its IF bandwidth, heard through either the reference discriminator or
/// the front end, scored on whether the frame came back.
/// </summary>
/// <remarks>
/// <para>The noise is white across the whole capture at the density that puts the stated CNR in
/// the IF bandwidth (8 kHz on a 12.5 kHz channel, 16 kHz on a 25 kHz one, as the FM link model
/// states its CNR), so the two arms meet the same noise density and differ only in the receiver.
/// It is not the <c>fm-data</c> link model: there is no limiter, no deviation error and no audio
/// shaping here, because what is under test is the front end against the discriminator the ladders
/// already trust, not a radio.</para>
/// <para>Deterministic in (mode, CNR, first seed, bursts, source rate).</para>
/// </remarks>
internal static class IqFrontEndBench
{
    /// <summary>The level a capture is carried through a pipe format at, RMS per component of full
    /// scale: about -16 dBFS, where a dongle's gain would put a noise-dominated band, with peaks
    /// four to five standard deviations from clipping.</summary>
    public const double PipeRms = 0.15;

    /// <summary>Where the carrier sits from the source's centre: the DC-clear offset a 2.4 MS/s
    /// dongle is given.</summary>
    public const double OffsetHz = 25000;

    private const int ReferenceRate = 48000;
    private const double Amplitude = 0.5;

    /// <summary>Scores <paramref name="bursts"/> bursts of <paramref name="mode"/> at
    /// <paramref name="cnrDb"/> through <paramref name="arm"/>; returns the frames recovered.</summary>
    /// <param name="frontEndIfHz">The front end's IF, Hz, or null for what
    /// <see cref="IqChannelSpec.ForFmMode"/> gives (the mode's published occupancy). The CNR and the
    /// reference's pre-filter stay at the channel spacing's IF either way, as the link model states
    /// them.</param>
    /// <param name="dongleRms">Null for float IQ. Otherwise the front end hears the stream as an
    /// RTL dongle would send it over rtl_tcp: scaled so its RMS on each axis is this fraction of
    /// full scale, cut to unsigned bytes, and turned back to floats by the rtl_tcp source's own
    /// table. Front end only.</param>
    /// <param name="pipeFormat">For the front end: the format the capture is carried in, as an
    /// <c>iqpipe:</c> device would read it (scaled to <see cref="PipeRms"/> a component, quantised,
    /// clipped at full scale, and read back by <see cref="IqSampleFormats.Convert"/>), or null for
    /// the floats as made.</param>
    public static int RunPoint(
        string mode, double cnrDb, IqFmArm arm, int bursts, int sourceRate, int firstSeed = 1, int workers = 4,
        double? frontEndIfHz = null, double? dongleRms = null, IqSampleFormat? pipeFormat = null)
    {
        FmModeProfile profile = FmModeProfiles.For(mode)
            ?? throw new ArgumentException($"'{mode}' is not an FM mode", nameof(mode));
        double deviation = profile.PeakDeviationHz;
        double ifBandwidth = IqChannelSpec.IfBandwidthForSpacing(profile.ChannelSpacingHz);
        int successes = 0;
        Parallel.For(
            0, bursts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) },
            () => 0,
            (b, _, local) => local + (RunBurst(mode, cnrDb, arm, sourceRate, deviation, ifBandwidth, frontEndIfHz, dongleRms, pipeFormat, firstSeed + b) ? 1 : 0),
            local => Interlocked.Add(ref successes, local));
        return successes;
    }

    /// <summary>The capture as an <c>iqpipe:</c> device would hear it in <paramref name="format"/>:
    /// scaled to <see cref="PipeRms"/>, written as that format's integers (rounded, clipped) and
    /// read back through the device's own conversion.</summary>
    public static float[] ThroughPipe(float[] iq, IqSampleFormat format)
    {
        double sum = 0;
        foreach (float v in iq)
        {
            sum += v * (double)v;
        }

        double scale = PipeRms / Math.Sqrt(sum / Math.Max(1, iq.Length));
        var bytes = new byte[iq.Length * IqSampleFormats.BytesPerPair(format) / 2];
        for (int k = 0; k < iq.Length; k++)
        {
            double x = iq[k] * scale;
            switch (format)
            {
                case IqSampleFormat.Cu8:
                    bytes[k] = (byte)Math.Clamp(Math.Round((x * 127.5) + 127.5), 0, 255);
                    break;
                case IqSampleFormat.Cs8:
                    bytes[k] = (byte)(sbyte)Math.Clamp(Math.Round(x * 128), -128, 127);
                    break;
                case IqSampleFormat.Cs16:
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                        bytes.AsSpan(2 * k), (short)Math.Clamp(Math.Round(x * 32768), -32768, 32767));
                    break;
                default:
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 * k), (float)x);
                    break;
            }
        }

        var heard = new float[iq.Length];
        IqSampleFormats.Convert(format, bytes, heard);
        return heard;
    }

    private static bool RunBurst(
        string mode, double cnrDb, IqFmArm arm, int sourceRate, double deviation, double ifBandwidth,
        double? frontEndIfHz, double? dongleRms, IqSampleFormat? pipeFormat, int seed)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(60, seed);
        float[] active = sim.RenderBurst(frame, txDelayMilliseconds: 150);

        // Lead-in and lead-out of unmodulated carrier, as SimChannel.ApplyFm gives: what a key-up
        // gives a receiver, and room for the end of the burst to be detected.
        int leadIn = sim.Rate / 2;
        int leadOut = (int)(sim.Rate * 1.2);
        var audio = new float[leadIn + active.Length + leadOut];
        active.CopyTo(audio.AsSpan(leadIn));

        // The signal deviates to exactly the mode's target at its peak, as the FM ladder drives it.
        float peak = 0;
        foreach (float v in active)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        double hzPerUnit = deviation / Math.Max(peak, 1e-6f);
        int rate = arm == IqFmArm.Reference ? ReferenceRate : sourceRate;
        double offset = arm == IqFmArm.Reference ? 0 : OffsetHz;
        float[] iq = IqSynth.Fm(IqSynth.Resample(audio, sim.Rate, rate), rate, offset, hzPerUnit, Amplitude);

        // CNR in the IF bandwidth: carrier power over the noise power that falls in it.
        double noisePower = Amplitude * Amplitude * rate / (ifBandwidth * Math.Pow(10, cnrDb / 10));
        IqSynth.AddNoise(iq, noisePower, seed + 7_000_000);
        if (dongleRms is double rms && arm == IqFmArm.FrontEnd)
        {
            IqSynth.ThroughDongleBytes(iq, rms);
        }

        if (pipeFormat is IqSampleFormat format && arm == IqFmArm.FrontEnd)
        {
            iq = ThroughPipe(iq, format);
        }

        float[] heard;
        if (arm == IqFmArm.Reference)
        {
            int n = iq.Length / 2;
            var i = new double[n];
            var q = new double[n];
            for (int k = 0; k < n; k++)
            {
                i[k] = iq[2 * k];
                q[k] = iq[(2 * k) + 1];
            }

            heard = new IqToFmAudioConverter(new IqToFmAudioOptions
            {
                InputRate = ReferenceRate,
                OutputRate = sim.Rate,
                RfBandwidthHz = ifBandwidth / 2,
                DeviationHz = deviation,
            }).Convert(i, q);
        }
        else
        {
            IqChannelSpec spec = IqChannelSpec.ForFmMode(mode, OffsetHz);
            if (frontEndIfHz is double frontEndIf)
            {
                spec = spec with { FmIfBandwidthHz = frontEndIf };
            }
            using var receiver = new IqReceiver(
                new BufferIqSource(iq, sourceRate),
                [spec],
                new IqReceiverOptions { DspRate = sim.Rate });
            heard = IqSynth.Receive(receiver)[0];
        }

        return sim.Decode(heard, frame).Matched;
    }
}
