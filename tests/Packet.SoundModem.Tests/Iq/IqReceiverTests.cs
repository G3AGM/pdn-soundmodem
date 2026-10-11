using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The IQ receive front end (issue #594): synthetic AFSK, BPSK and GFSK on an offset in a
/// wideband stream decode through each demodulator; two channels from one source decode at once;
/// a gap the source reports is heard as silence of its own length and a retune restarts the chain
/// and follows the dial. All synthetic and in memory: no hardware, no network.
/// </summary>
public sealed class IqReceiverTests
{
    private const double Rf = 144_800_000;

    [Fact]
    public void Afsk1200_On_Usb_Decodes_From_A_2_4_Msps_Source_At_An_Offset()
    {
        const int rate = 2_400_000;
        (float[] audio, byte[] frame, SimModem sim) = Burst("afsk1200", seed: 3);
        float[] iq = IqSynth.Dsb(Scale(IqSynth.Resample(audio, sim.Rate, rate), 0.3f), rate, 25_000);

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [new IqChannelSpec { OffsetHz = 25_000, Demodulation = IqDemodulation.Usb }],
            new IqReceiverOptions { DspRate = sim.Rate });

        receiver.CoarseRate.Should().Be(150_000, "four halvings leave the channel's 31 kHz with room");
        sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched.Should().BeTrue();
    }

    [Fact]
    public void Bpsk300_On_Lsb_Decodes_From_A_Source_Whose_Rate_Is_No_Multiple_Of_The_Dsp_Rate()
    {
        // 250 kS/s halves once to 125 kS/s, and 125 kS/s to 12 kHz is 12/125: the rational path.
        const int rate = 250_000;
        (float[] audio, byte[] frame, SimModem sim) = Burst("bpsk300", seed: 5);
        float[] iq = IqSynth.Dsb(Scale(IqSynth.Resample(audio, sim.Rate, rate), 0.3f), rate, -20_000);

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [new IqChannelSpec { OffsetHz = -20_000, Demodulation = IqDemodulation.Lsb }],
            new IqReceiverOptions { DspRate = sim.Rate });

        receiver.CoarseRate.Should().Be(125_000);
        sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched.Should().BeTrue();
    }

    [Fact]
    public void Gfsk9600_On_Fm_Decodes_From_A_2_048_Msps_Source()
    {
        const int rate = 2_048_000;
        (float[] audio, byte[] frame, SimModem sim) = Burst("fsk9600-il2p", seed: 7);
        float[] iq = FmIq(audio, sim.Rate, rate, 25_000, deviationHz: 2400);

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [IqChannelSpec.ForFmMode("fsk9600-il2p", 25_000)],
            new IqReceiverOptions { DspRate = sim.Rate });

        receiver.Channels[0].ChannelRate.Should().Be(48_000);
        sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched.Should().BeTrue();
    }

    [Fact]
    public void Afsk1200_On_Fm_Decodes_At_A_12_Khz_Dsp_Rate()
    {
        // Discriminated at 48 kHz, then resampled to the 12 kHz the station runs at.
        const int rate = 240_000;
        (float[] audio, byte[] frame, SimModem sim) = Burst("afsk1200", seed: 9);
        float[] iq = FmIq(audio, sim.Rate, rate, -30_000, deviationHz: 3000);

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [IqChannelSpec.ForFmMode("afsk1200", -30_000)],
            new IqReceiverOptions { DspRate = 12_000 });

        receiver.Channels[0].ChannelRate.Should().Be(48_000);
        receiver.Channels[0].SampleRate.Should().Be(12_000);
        sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched.Should().BeTrue();
    }

    [Fact]
    public async Task Two_Channels_From_One_Source_Decode_Concurrently_On_The_Reader_Thread()
    {
        const int rate = 240_000;
        (float[] fmAudio, byte[] fmFrame, SimModem fmSim) = Burst("afsk1200", seed: 11);
        (float[] ssbAudio, byte[] ssbFrame, SimModem ssbSim) = Burst("bpsk300", seed: 13);
        float[] iq = FmIq(fmAudio, fmSim.Rate, rate, 30_000, deviationHz: 3000, amplitude: 0.3);
        float[] ssbWide = Scale(IqSynth.Resample(ssbAudio, ssbSim.Rate, rate), 0.3f);
        if (2 * ssbWide.Length > iq.Length)
        {
            Array.Resize(ref iq, 2 * ssbWide.Length);
        }

        IqSynth.AddDsb(iq, ssbWide, rate, -40_000);

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [
                IqChannelSpec.ForFmMode("afsk1200", 30_000),
                new IqChannelSpec { OffsetHz = -40_000, Demodulation = IqDemodulation.Usb },
            ],
            new IqReceiverOptions { DspRate = 12_000 });

        // Each channel drained by its own consumer, as two stations would.
        Task<float[]> fm = Task.Run(() => Drain(receiver.Channels[0]));
        Task<float[]> ssb = Task.Run(() => Drain(receiver.Channels[1]));
        receiver.Start();
        float[][] heard = await Task.WhenAll(fm, ssb).WaitAsync(TimeSpan.FromSeconds(60));

        fmSim.Decode(heard[0], fmFrame).Matched.Should().BeTrue();
        ssbSim.Decode(heard[1], ssbFrame).Matched.Should().BeTrue();
    }

    [Fact]
    public void A_Gap_Is_Heard_As_Silence_Of_Its_Own_Length_And_Not_Spliced()
    {
        const int rate = 240_000;
        const int dsp = 12_000;
        float[] before = ComplexTone(rate, 26_000, rate, 0.5f); // one second, 1 kHz above the dial
        float[] after = ComplexTone(rate, 26_000, rate, 0.5f);
        long lost = rate / 4;
        var journal = new List<string>();
        var source = new ScriptedIqSource(
        [
            new(before, rate),
            new(after, rate, LostBefore: lost),
        ]);

        using var receiver = new IqReceiver(
            source,
            [new IqChannelSpec { OffsetHz = 25_000, Demodulation = IqDemodulation.Usb }],
            new IqReceiverOptions { DspRate = dsp, Journal = journal.Add });
        float[] audio = IqSynth.Receive(receiver)[0];

        // Time is kept: two seconds of signal and a quarter of a second of loss is 2.25 s of audio.
        audio.Length.Should().BeCloseTo((int)(2.25 * dsp), 4);

        // The loss is a run of silence starting where the first second ended, a quarter of a
        // second long; the tone resumes after it rather than being joined onto the first second.
        (int start, int length) = LongestZeroRun(audio);
        start.Should().BeCloseTo(dsp, 4);
        length.Should().BeCloseTo(dsp / 4, 8);

        receiver.Gaps.Should().Be(1);
        receiver.SamplesLost.Should().Be(lost);
        receiver.Channels[0].Discontinuities.Should().Be(1);
        journal.Should().ContainSingle(l => l.Contains("lost 60000 samples") && l.Contains("not spliced"));
    }

    [Theory]
    [InlineData(IqDemodulation.Usb)]
    [InlineData(IqDemodulation.Fm)]
    public void A_Gap_And_A_Retune_In_The_Same_Block_Keep_Time_And_Count_Once(IqDemodulation demodulation)
    {
        // A source that changes rate and loses a quarter of a second at the same moment: the loss
        // is measured at the old rate (60000 samples at 240 kS/s), and the audio is one second,
        // a quarter of silence, then one second, with the silence exactly where the first ended.
        const int dsp = 12_000;
        var source = new ScriptedIqSource(
        [
            new(ComplexTone(240_000, 26_000, 240_000, 0.5f), 240_000),
            new(ComplexTone(192_000, 26_000, 192_000, 0.5f), 192_000, LostBefore: 60_000),
        ]);
        using var receiver = new IqReceiver(
            source,
            [new IqChannelSpec { OffsetHz = 25_000, Demodulation = demodulation, FmIfBandwidthHz = 8_000 }],
            new IqReceiverOptions { DspRate = dsp });

        float[] audio = IqSynth.Receive(receiver)[0];

        audio.Length.Should().Be(27_000);
        (int start, int length) = LongestZeroRun(audio);
        start.Should().Be(12_000);
        length.Should().BeInRange(3_000, 3_001, "the FM discriminator's first output after a restart is taken against silence");
        receiver.Retunes.Should().Be(1);
        receiver.Gaps.Should().Be(1);
        receiver.Channels[0].Discontinuities.Should().Be(1, "one block, one discontinuity");
    }

    [Fact]
    public void Retune_Lines_Are_Rate_Limited_Like_Gap_Lines()
    {
        const int rate = 48_000;
        var segments = new List<ScriptedIqSource.Segment>();
        for (int k = 0; k < 6; k++)
        {
            segments.Add(new(ComplexTone(rate, 6_000, 4_800, 0.5f), rate, Rf + (k * 1_000)));
        }

        var journal = new List<string>();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var receiver = new IqReceiver(
            new ScriptedIqSource(segments),
            [new IqChannelSpec { OffsetHz = 5_000, Demodulation = IqDemodulation.Usb }],
            new IqReceiverOptions { DspRate = 12_000, Journal = journal.Add, TimeProvider = time, CoverageHz = 15_000 });

        receiver.PumpToEnd();

        receiver.Retunes.Should().Be(5);
        journal.Count(l => l.Contains("retuned")).Should().Be(1, "the rest fall inside the ten seconds");
    }

    [Fact]
    public void A_Gap_Longer_Than_The_Cap_Is_Marked_At_The_Cap()
    {
        const int rate = 48_000;
        var source = new ScriptedIqSource(
        [
            new(ComplexTone(rate, 6_000, rate / 2, 0.5f), rate),
            new(ComplexTone(rate, 6_000, rate / 2, 0.5f), rate, LostBefore: 60L * rate),
        ]);
        var journal = new List<string>();
        using var receiver = new IqReceiver(
            source,
            [new IqChannelSpec { OffsetHz = 5_000, Demodulation = IqDemodulation.Usb }],
            new IqReceiverOptions { DspRate = 12_000, MaxGapSeconds = 2, RingSeconds = 10, Journal = journal.Add });

        float[] audio = IqSynth.Receive(receiver)[0];

        audio.Length.Should().BeCloseTo(3 * 12_000, 4, "one second of signal and two of marked gap");
        journal.Should().Contain(l => l.Contains("marked as 2 s"));
    }

    [Fact]
    public void A_Retune_Restarts_The_Chain_And_An_Anchored_Channel_Keeps_Its_Dial()
    {
        const int rate = 240_000;
        (float[] a, byte[] frameA, SimModem sim) = Burst("afsk1200", seed: 15);
        (float[] b, byte[] frameB, _) = Burst("afsk1200", seed: 17);
        double dial = Rf + 25_000;
        var journal = new List<string>();

        // The source moves 10 kHz up between the two bursts; the signal stays on the dial.
        var source = new ScriptedIqSource(
        [
            new(FmIq(a, sim.Rate, rate, dial - Rf, 3000), rate, Rf),
            new(FmIq(b, sim.Rate, rate, dial - (Rf + 10_000), 3000), rate, Rf + 10_000),
        ]);
        using var receiver = new IqReceiver(
            source,
            [IqChannelSpec.ForFmMode("afsk1200", 25_000)],
            new IqReceiverOptions { DspRate = 12_000, Journal = journal.Add });
        IqReceiverChannel channel = receiver.Channels[0];
        channel.DialHz.Should().Be(dial);

        float[] audio = IqSynth.Receive(receiver)[0];

        receiver.Retunes.Should().Be(1);
        channel.OffsetHz.Should().Be(15_000, "the channel follows its RF dial, not its old offset");
        channel.DialHz.Should().Be(dial);
        channel.Discontinuities.Should().Be(1);
        journal.Should().Contain(l => l.Contains("retuned to 144.810000 MHz"));
        int split = (int)((long)a.Length * 12_000 / sim.Rate);
        sim.Decode(audio.AsSpan(0, split), frameA).Matched.Should().BeTrue();
        sim.Decode(audio.AsSpan(split), frameB).Matched.Should().BeTrue();
    }

    [Fact]
    public void A_Change_Of_Rate_Rebuilds_The_Chain()
    {
        (float[] a, byte[] frameA, SimModem sim) = Burst("afsk1200", seed: 19);
        (float[] b, byte[] frameB, _) = Burst("afsk1200", seed: 21);
        var source = new ScriptedIqSource(
        [
            new(FmIq(a, sim.Rate, 240_000, 20_000, 3000), 240_000),
            new(FmIq(b, sim.Rate, 250_000, 20_000, 3000), 250_000),
        ]);
        using var receiver = new IqReceiver(
            source,
            [IqChannelSpec.ForFmMode("afsk1200", 20_000)],
            new IqReceiverOptions { DspRate = 12_000 });

        float[] audio = IqSynth.Receive(receiver)[0];

        receiver.SourceRate.Should().Be(250_000);
        receiver.CoarseRate.Should().Be(125_000);
        int split = (int)((long)a.Length * 12_000 / sim.Rate);
        sim.Decode(audio.AsSpan(0, split), frameA).Matched.Should().BeTrue();
        sim.Decode(audio.AsSpan(split), frameB).Matched.Should().BeTrue();
    }

    [Fact]
    public void A_Retune_That_Leaves_The_Dial_Out_Of_Band_Silences_The_Channel_Until_One_Brings_It_Back()
    {
        const int rate = 240_000;
        (float[] b, byte[] frameB, SimModem sim) = Burst("afsk1200", seed: 23);
        float[] away = ComplexTone(rate, 0, rate, 0.5f);
        var journal = new List<string>();
        var source = new ScriptedIqSource(
        [
            new(ComplexTone(rate, 25_000, rate / 2, 0.5f), rate, Rf),
            new(away, rate, Rf + 500_000),
            new(FmIq(b, sim.Rate, rate, 25_000, 3000), rate, Rf),
        ]);
        using var receiver = new IqReceiver(
            source,
            [IqChannelSpec.ForFmMode("afsk1200", 25_000)],
            new IqReceiverOptions { DspRate = 12_000, Journal = journal.Add });

        float[] audio = IqSynth.Receive(receiver)[0];

        receiver.Retunes.Should().Be(2);
        receiver.Channels[0].Muted.Should().BeFalse();
        journal.Should().Contain(l => l.Contains("silent") && l.Contains("outside"));
        journal.Should().Contain(l => l.Contains("back in the source's band"));

        // The muted second is silence, and is there: the audio keeps time through it.
        int mutedStart = 6_000;
        audio.AsSpan(mutedStart + 100, 12_000 - 200).ToArray().Should().OnlyContain(v => v == 0f);
        sim.Decode(audio.AsSpan(mutedStart + 12_000), frameB).Matched.Should().BeTrue();
    }

    [Fact]
    public async Task Tuning_A_Channel_Moves_It_Within_The_Band_Without_Touching_The_Source()
    {
        const int rate = 240_000;
        (float[] a, byte[] frameA, SimModem sim) = Burst("afsk1200", seed: 25);
        (float[] b, byte[] frameB, _) = Burst("afsk1200", seed: 27);
        float[] iq = FmIq(a, sim.Rate, rate, 20_000, 3000, amplitude: 0.3);
        float[] other = FmIq(b, sim.Rate, rate, -30_000, 3000, amplitude: 0.3);
        for (int k = 0; k < Math.Min(iq.Length, other.Length); k++)
        {
            iq[k] += other[k];
        }

        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate, Rf),
            [IqChannelSpec.ForFmMode("afsk1200", 20_000)],
            new IqReceiverOptions { DspRate = 12_000, CoverageHz = 40_000 });
        IqReceiverChannel channel = receiver.Channels[0];

        (await channel.TuneAsync(Rf + 200_000, CancellationToken.None))
            .Should().BeFalse("200 kHz out is past what the receiver keeps");
        (await channel.TuneAsync(Rf - 30_000, CancellationToken.None))
            .Should().BeTrue();
        channel.DialHz.Should().Be(Rf - 30_000);

        float[] audio = IqSynth.Receive(receiver)[0];

        channel.OffsetHz.Should().Be(-30_000);
        sim.Decode(audio, frameB).Matched.Should().BeTrue("the channel now hears the signal at -30 kHz");
        sim.Decode(audio, frameA).Matched.Should().BeFalse("and not the one it was first put on");
    }

    [Fact]
    public void A_Drop_To_A_Rate_The_Channel_Filter_Cannot_Run_At_Silences_It_Rather_Than_Stopping_The_Receiver()
    {
        // A 16 kHz IF needs more than a 12 kS/s stream; the receiver must carry on and the
        // channel come back when the rate does.
        (float[] b, byte[] frameB, SimModem sim) = Burst("afsk1200", seed: 31);
        var journal = new List<string>();
        var source = new ScriptedIqSource(
        [
            new(ComplexTone(240_000, 20_000, 24_000, 0.5f), 240_000),
            new(ComplexTone(12_000, 0, 12_000, 0.5f), 12_000),
            new(FmIq(b, sim.Rate, 240_000, 20_000, 3000), 240_000),
        ]);
        using var receiver = new IqReceiver(
            source,
            [new IqChannelSpec { OffsetHz = 20_000, Demodulation = IqDemodulation.Fm, FmIfBandwidthHz = 16_000 }],
            new IqReceiverOptions { DspRate = 12_000, Journal = journal.Add });

        float[] audio = IqSynth.Receive(receiver)[0];

        receiver.Retunes.Should().Be(2);
        journal.Should().Contain(l => l.Contains("silent"));
        audio.AsSpan(1_200 + 100, 12_000 - 200).ToArray().Should().OnlyContain(v => v == 0f, "the muted second keeps its time");
        sim.Decode(audio.AsSpan(1_200 + 12_000), frameB).Matched.Should().BeTrue();
    }

    [Fact]
    public async Task On_A_Source_That_Does_Not_Say_Where_It_Is_Tuned_A_Dial_Is_An_Offset()
    {
        const int rate = 240_000;
        (float[] b, byte[] frameB, SimModem sim) = Burst("afsk1200", seed: 33);
        using var receiver = new IqReceiver(
            new BufferIqSource(FmIq(b, sim.Rate, rate, -30_000, 3000), rate),
            [IqChannelSpec.ForFmMode("afsk1200", 20_000)],
            new IqReceiverOptions { DspRate = 12_000, CoverageHz = 40_000 });
        IqReceiverChannel channel = receiver.Channels[0];
        channel.DialHz.Should().Be(20_000, "with no centre the dial reads as the offset");

        (await channel.TuneAsync(-30_000, CancellationToken.None)).Should().BeTrue();
        float[] audio = IqSynth.Receive(receiver)[0];

        channel.Muted.Should().BeFalse();
        channel.OffsetHz.Should().Be(-30_000);
        sim.Decode(audio, frameB).Matched.Should().BeTrue();
    }

    [Fact]
    public void A_Channel_Outside_The_Source_Band_Is_Refused_With_A_Reason()
    {
        Action build = () => new IqReceiver(
            new BufferIqSource(new float[2000], 48_000),
            [IqChannelSpec.ForFmMode("fsk9600", 20_000)],
            new IqReceiverOptions { DspRate = 48_000 });

        build.Should().Throw<ArgumentException>().WithMessage("*past the 24000 Hz a 48000 S/s source carries*");
    }

    [Fact]
    public void A_Channel_Whose_Rate_Is_Not_A_Multiple_Of_The_Dsp_Rate_Is_Refused()
    {
        Action build = () => new IqReceiver(
            new BufferIqSource(new float[2000], 240_000),
            [new IqChannelSpec { OffsetHz = 0, Demodulation = IqDemodulation.Usb, ChannelRate = 20_000 }],
            new IqReceiverOptions { DspRate = 12_000 });

        build.Should().Throw<ArgumentException>().WithMessage("*not a multiple of the 12000 Hz DSP rate*");
    }

    [Fact]
    public void Usb_And_Lsb_Each_Take_Their_Own_Sideband()
    {
        const int rate = 96_000;
        float[] above = ComplexTone(rate, 10_000 + 1000, rate, 0.5f);
        float Rms(IqDemodulation demodulation)
        {
            using var receiver = new IqReceiver(
                new BufferIqSource(above, rate),
                [new IqChannelSpec { OffsetHz = 10_000, Demodulation = demodulation }],
                new IqReceiverOptions { DspRate = 12_000 });
            return SettledRms(IqSynth.Receive(receiver)[0]);
        }

        float usb = Rms(IqDemodulation.Usb);
        float lsb = Rms(IqDemodulation.Lsb);
        usb.Should().BeGreaterThan(0.2f);
        (20 * Math.Log10(usb / lsb)).Should().BeGreaterThan(50, "a tone above the dial is upper sideband only");
    }

    [Fact]
    public void Swapping_I_And_Q_Mirrors_The_Spectrum()
    {
        const int rate = 96_000;
        float[] below = ComplexTone(rate, -(10_000 + 1000), rate, 0.5f);
        float Rms(bool swap)
        {
            using var receiver = new IqReceiver(
                new BufferIqSource(below, rate),
                [new IqChannelSpec { OffsetHz = 10_000, Demodulation = IqDemodulation.Usb }],
                new IqReceiverOptions { DspRate = 12_000, SwapIq = swap });
            return SettledRms(IqSynth.Receive(receiver)[0]);
        }

        Rms(swap: false).Should().BeLessThan(0.001f, "the tone is on the other side of the centre");
        Rms(swap: true).Should().BeGreaterThan(0.2f, "swapped, it lands 1 kHz above the dial");
    }

    [Fact]
    public void A_Consumer_That_Stops_Draining_Loses_The_Oldest_Audio_And_It_Is_Said()
    {
        const int rate = 48_000;
        var journal = new List<string>();
        using var receiver = new IqReceiver(
            new BufferIqSource(ComplexTone(rate, 6_000, 3 * rate, 0.5f), rate),
            [new IqChannelSpec { OffsetHz = 5_000, Demodulation = IqDemodulation.Usb }],
            new IqReceiverOptions { DspRate = 12_000, RingSeconds = 1, Journal = journal.Add });

        receiver.PumpToEnd();

        IqReceiverChannel channel = receiver.Channels[0];
        channel.DroppedSamples.Should().BeGreaterThan(12_000);
        channel.Discontinuities.Should().BeGreaterThan(0);
        journal.Should().Contain(l => l.Contains("not keeping up"));
        Drain(channel).Length.Should().Be(12_000, "what is left is the newest second");
    }

    [Fact]
    public void The_Streaming_Discriminator_Is_The_Batch_One()
    {
        const int rate = 48_000;
        (float[] audio, _, SimModem sim) = Burst("afsk1200", seed: 29);
        float[] iq = FmIq(audio, sim.Rate, rate, 0, 3000);
        var i = new double[iq.Length / 2];
        var q = new double[iq.Length / 2];
        for (int k = 0; k < i.Length; k++)
        {
            i[k] = iq[2 * k];
            q[k] = iq[(2 * k) + 1];
        }

        float[] batch = new IqToFmAudioConverter(new IqToFmAudioOptions
        {
            InputRate = rate,
            OutputRate = rate,
            DeviationHz = 3000,
        }).Convert(i, q);

        var streaming = new float[i.Length];
        var discriminator = new StreamingFmDiscriminator(rate, 3000);
        int written = 0;
        for (int at = 0; at < iq.Length; at += 2 * 1000)
        {
            int length = Math.Min(2 * 1000, iq.Length - at);
            written += discriminator.Process(iq.AsSpan(at, length), streaming.AsSpan(written));
        }

        written.Should().Be(batch.Length);
        for (int k = 1; k < batch.Length; k++)
        {
            streaming[k].Should().BeApproximately(batch[k], 1e-5f);
        }
    }

    [Fact]
    public void Steady_State_Pumping_Allocates_Nothing()
    {
        const int rate = 240_000;
        float[] iq = ComplexTone(rate, 21_000, 4 * rate, 0.5f);
        using var receiver = new IqReceiver(
            new BufferIqSource(iq, rate),
            [
                new IqChannelSpec { OffsetHz = 20_000, Demodulation = IqDemodulation.Usb },
                new IqChannelSpec { OffsetHz = -20_000, Demodulation = IqDemodulation.Lsb },
                IqChannelSpec.ForFmMode("afsk1200", 20_000),
            ],
            new IqReceiverOptions { DspRate = 12_000, ReadBlockFrames = 4096 });
        var buffer = new float[8192];

        int pumped = 0;
        void PumpAndDrain(int blocks)
        {
            for (int b = 0; b < blocks; b++)
            {
                pumped += receiver.Pump() ? 1 : 0;
                for (int c = 0; c < receiver.Channels.Count; c++)
                {
                    IqReceiverChannel channel = receiver.Channels[c];
                    while (channel.Available > 0)
                    {
                        channel.Read(buffer);
                    }
                }
            }
        }

        PumpAndDrain(20); // warm: JIT, tiering, first-touch
        long before = GC.GetAllocatedBytesForCurrentThread();
        PumpAndDrain(100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        pumped.Should().Be(120, "every block was real IQ");
        allocated.Should().Be(0, "the per-block path runs in buffers sized at construction");
    }

    [Fact]
    public void A_Dc_Clear_Offset_Is_An_Eighth_Of_The_Rate_At_Most_25_Khz()
    {
        IqTuning.DcClearOffsetHz(48_000).Should().Be(6_000);
        IqTuning.DcClearOffsetHz(2_400_000).Should().Be(25_000);
    }

    // ---- helpers ----

    /// <summary>A mode's burst at its DSP rate, with quiet either side for the receiver to
    /// settle into and detect the end in.</summary>
    internal static (float[] Audio, byte[] Frame, SimModem Sim) Burst(string mode, int seed)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(40, seed);
        float[] active = sim.RenderBurst(frame, txDelayMilliseconds: 150);
        float peak = 0;
        foreach (float v in active)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        int lead = sim.Rate * 3 / 10;
        int tail = sim.Rate * 8 / 10;
        var audio = new float[lead + active.Length + tail];
        for (int k = 0; k < active.Length; k++)
        {
            audio[lead + k] = active[k] / peak;
        }

        return (audio, frame, sim);
    }

    /// <summary>Audio normalised to a peak of 1, frequency-modulated to a peak of
    /// <paramref name="deviationHz"/> on <paramref name="offsetHz"/> at <paramref name="iqRate"/>.</summary>
    private static float[] FmIq(
        float[] audio, int audioRate, int iqRate, double offsetHz, double deviationHz, double amplitude = 0.5) =>
        IqSynth.Fm(IqSynth.Resample(audio, audioRate, iqRate), iqRate, offsetHz, deviationHz, amplitude);

    private static float[] ComplexTone(int rate, double hz, int frames, float amplitude)
    {
        var iq = new float[2 * frames];
        double w = 2.0 * Math.PI * hz / rate;
        for (int k = 0; k < frames; k++)
        {
            iq[2 * k] = amplitude * (float)Math.Cos(w * k);
            iq[(2 * k) + 1] = amplitude * (float)Math.Sin(w * k);
        }

        return iq;
    }

    private static float[] Scale(float[] samples, float gain)
    {
        for (int k = 0; k < samples.Length; k++)
        {
            samples[k] *= gain;
        }

        return samples;
    }

    private static float[] Drain(IqReceiverChannel channel)
    {
        var all = new List<float>();
        var buffer = new float[4096];
        while (!channel.Completed)
        {
            int got = channel.Read(buffer);
            all.AddRange(buffer.AsSpan(0, got));
        }

        return [.. all];
    }

    private static float SettledRms(float[] audio)
    {
        int from = audio.Length / 4;
        int to = audio.Length * 3 / 4;
        double sum = 0;
        for (int k = from; k < to; k++)
        {
            sum += audio[k] * (double)audio[k];
        }

        return (float)Math.Sqrt(sum / Math.Max(1, to - from));
    }

    private static (int Start, int Length) LongestZeroRun(float[] audio)
    {
        int bestStart = -1;
        int bestLength = 0;
        int runStart = -1;
        for (int k = 0; k <= audio.Length; k++)
        {
            bool zero = k < audio.Length && audio[k] == 0f;
            if (zero && runStart < 0)
            {
                runStart = k;
            }
            else if (!zero && runStart >= 0)
            {
                if (k - runStart > bestLength)
                {
                    bestLength = k - runStart;
                    bestStart = runStart;
                }

                runStart = -1;
            }
        }

        return (bestStart, bestLength);
    }
}
