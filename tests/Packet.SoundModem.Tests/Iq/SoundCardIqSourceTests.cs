using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// Sound-card IQ (issue #596): a card's left and right inputs as an IQ source. The channel
/// mapping and swap, the balance correction measured as image rejection on a tone, the balance the
/// source reads off a band of noise, overruns reported as gaps, a centre moved by a rig, and a burst
/// decoded end to end through the front end. A scripted card; no hardware.
/// </summary>
public sealed class SoundCardIqSourceTests
{
    private const int Rate = 48000;

    [Fact]
    public void The_Left_Input_Is_I_And_The_Right_Is_Q_At_Full_Scale_One()
    {
        var card = new ScriptedCapture(Rate, [16384, -8192, -32768, 32767]);
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions());

        var iq = new float[4];
        source.Read(iq).Should().Be(4);

        iq.Should().Equal(0.5f, -0.25f, -1f, 32767f / 32768f);
    }

    [Fact]
    public void Swapped_The_Right_Input_Is_I()
    {
        var card = new ScriptedCapture(Rate, [16384, -8192]);
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions { SwapIq = true });

        var iq = new float[2];
        source.Read(iq);

        iq.Should().Equal(-0.25f, 0.5f);
    }

    [Theory]
    [InlineData(0.5, 3.0)]
    [InlineData(-1.0, -5.0)]
    [InlineData(0.2, 1.0)]
    public void The_Balance_Correction_Takes_A_Tones_Image_Down_From_What_The_Mismatch_Leaves(
        double gainDb, double phaseDegrees)
    {
        // A tone 6 kHz above the centre on a card whose Q is gainDb louder and phaseDegrees off.
        short[] pcm = Tone(6000, gainDb, phaseDegrees, seconds: 0.5);

        double uncorrected = ImageRejection(pcm, new SoundCardIqOptions(), 6000);
        double corrected = ImageRejection(
            pcm, new SoundCardIqOptions { GainImbalanceDb = gainDb, PhaseImbalanceDegrees = phaseDegrees }, 6000);

        uncorrected.Should().BeApproximately(
            new IqBalance(gainDb, phaseDegrees).ImageRejectionDb, 1.0,
            "the image an uncorrected mismatch leaves is what the formula says");
        corrected.Should().BeGreaterThan(
            60, "corrected, the image is down to the 16-bit card's own floor");
    }

    [Fact]
    public void A_Swapped_Card_Puts_A_Tone_On_The_Other_Side_Of_The_Centre()
    {
        short[] pcm = Tone(6000, 0, 0, seconds: 0.25);

        double straight = ImageRejection(pcm, new SoundCardIqOptions(), 6000);
        double swapped = ImageRejection(pcm, new SoundCardIqOptions { SwapIq = true }, 6000);

        straight.Should().BeGreaterThan(60);
        swapped.Should().BeLessThan(-60, "swapping I and Q mirrors the spectrum about the centre");
    }

    [Fact]
    public void The_Balance_Read_Off_A_Band_Of_Noise_Is_The_Cards_Mismatch_And_Is_Said_Once()
    {
        short[] pcm = Noise(0.7, 2.0, seconds: 3, seed: 11);
        var lines = new List<string>();
        var card = new ScriptedCapture(Rate, pcm);
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions
        {
            BalanceReportSeconds = 2,
            GainImbalanceDb = 0.7,
            PhaseImbalanceDegrees = 2.0,
            Journal = lines.Add,
        });

        var iq = new float[2 * 4096];
        while (source.Read(iq) > 0)
        {
        }

        IqBalance measured = source.MeasuredBalance!;
        measured.GainDb.Should().BeApproximately(0.7, 0.05);
        measured.PhaseDegrees.Should().BeApproximately(2.0, 0.3);
        lines.Should().ContainSingle().Which.Should().MatchRegex(
            @"Q is \+0\.(69|70|71|72) dB against I and \+(1\.9|2\.0)\d degrees off quadrature");
        lines[0].Should().Contain(
            "more than 60 dB with the configured +0.70 dB and +2.00 degrees",
            "the line says what the correction leaves");
    }

    [Fact]
    public void An_Overrun_Is_A_Gap_Before_The_Block_After_It_Measured_By_The_Time_Away()
    {
        var clock = new FakeTimeProvider();
        var card = new ScriptedCapture(Rate, new short[2 * 48000]) { Clock = clock };
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions { TimeProvider = clock });
        var iq = new float[2 * 4800];

        source.Read(iq).Should().Be(2 * 4800);
        source.SamplesLost.Should().Be(0);

        // The reader is away for 300 ms; the card overruns, and the next read is the 100 ms
        // after the restart.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        card.OverrunBeforeNextRead = true;
        source.Read(iq).Should().Be(2 * 4800);

        source.SamplesLost.Should().Be(
            Rate * 3 / 10, "the 300 ms the reader was away is what the card dropped");
        source.TuningGeneration.Should().Be(0, "a gap is not a retune");

        source.Read(iq);
        source.SamplesLost.Should().Be(Rate * 3 / 10, "nothing more was lost");
    }

    [Fact]
    public void An_Overrun_That_Cut_A_Read_Short_Counts_What_The_Card_Was_Holding()
    {
        var clock = new FakeTimeProvider();
        var card = new ScriptedCapture(Rate, new short[2 * 48000]) { Clock = clock, BufferFrames = 24000 };
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions { TimeProvider = clock });
        var iq = new float[2 * 4800];
        source.Read(iq);

        // The read meets the overrun 1000 frames in, and the next is the first after the restart.
        card.CutNextReadTo = 1000;
        source.Read(iq).Should().Be(2 * 1000);
        card.OverrunBeforeNextRead = true;
        source.Read(iq);

        source.SamplesLost.Should().Be(24000, "the buffer the recovery threw away is the loss");
    }

    [Fact]
    public void An_Overrun_With_No_Time_Away_To_Show_For_It_Still_Counts_One_Sample()
    {
        var clock = new FakeTimeProvider();
        var card = new ScriptedCapture(Rate, new short[2 * 9600]) { Clock = clock };
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions { TimeProvider = clock });
        var iq = new float[2 * 4800];
        source.Read(iq);

        card.OverrunBeforeNextRead = true;
        source.Read(iq);

        source.SamplesLost.Should().Be(1, "a source that knows it lost something says at least one");
    }

    [Fact]
    public void A_Moved_Centre_Is_A_Retune_On_The_Next_Block_And_An_Unmoved_One_Is_Not()
    {
        var card = new ScriptedCapture(Rate, new short[2 * 9600]);
        using var source = new SoundCardIqSource(card, new SoundCardIqOptions { CentreFrequencyHz = 7_040_000 });
        var iq = new float[2 * 1000];
        source.Read(iq);

        source.SetCentreFrequency(7_040_000);
        source.Read(iq);
        source.TuningGeneration.Should().Be(0);

        source.SetCentreFrequency(7_050_000);
        source.CentreFrequencyHz.Should().Be(7_040_000, "the block already read was at the old centre");
        source.Read(iq);
        source.TuningGeneration.Should().Be(1);
        source.CentreFrequencyHz.Should().Be(7_050_000);
    }

    [Theory]
    [InlineData(7.0, 0.0)]
    [InlineData(0.0, 31.0)]
    [InlineData(double.NaN, 0.0)]
    public void A_Card_Further_Out_Of_Balance_Than_Any_Real_One_Is_Refused(double gainDb, double phaseDegrees)
    {
        var options = new SoundCardIqOptions { GainImbalanceDb = gainDb, PhaseImbalanceDegrees = phaseDegrees };

        options.Problem().Should().NotBeNull();
        FluentActions.Invoking(() => new SoundCardIqSource(new ScriptedCapture(Rate, []), options))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("usb")]
    [InlineData("lsb")]
    public void Bpsk300_Decodes_End_To_End_From_An_Unbalanced_Card_Through_The_Front_End(string sideband)
    {
        // The G90 case: 48 kS/s, the dial 6 kHz from the centre, a card 0.5 dB and 3 degrees out,
        // and a strong signal on the mirror of the channel that only the correction keeps out.
        (float[] audio, byte[] frame, SimModem sim) = IqReceiverTests.Burst("bpsk300", seed: 9);
        bool lsb = sideband == "lsb";
        double offset = 6000;
        float[] wanted = IqSynth.Resample(audio, sim.Rate, Rate);
        var iq = new float[2 * wanted.Length];
        CardSynth.AddSsb(iq, wanted, Rate, offset, lsb, 0.01);

        // The interferer: the same kind of signal, 34 dB up, on the mirror of the dial.
        (float[] other, _, _) = IqReceiverTests.Burst("bpsk300", seed: 10);
        CardSynth.AddSsb(iq, IqSynth.Resample(other, sim.Rate, Rate), Rate, -offset, !lsb, 0.5);

        short[] pcm = CardSynth.Card(iq, 0.5, 3.0);
        var spec = new IqChannelSpec
        {
            OffsetHz = offset,
            Demodulation = lsb ? IqDemodulation.Lsb : IqDemodulation.Usb,
        };

        bool Heard(SoundCardIqOptions options)
        {
            using var source = new SoundCardIqSource(new ScriptedCapture(Rate, pcm), options);
            using var receiver = new IqReceiver(source, [spec], new IqReceiverOptions { DspRate = sim.Rate });
            return sim.Decode(IqSynth.Receive(receiver)[0], frame).Matched;
        }

        Heard(new SoundCardIqOptions { GainImbalanceDb = 0.5, PhaseImbalanceDegrees = 3.0 })
            .Should().BeTrue("corrected, the interferer's image is 60 dB down and the burst is clear");
        Heard(new SoundCardIqOptions())
            .Should().BeFalse("uncorrected, the 31 dB the mismatch leaves puts the image of a signal 34 dB up over the burst");
    }

    /// <summary>The image rejection a tone at <paramref name="toneHz"/> shows once read through a
    /// source with <paramref name="options"/>: its power over its mirror's, dB.</summary>
    private static double ImageRejection(short[] pcm, SoundCardIqOptions options, double toneHz)
    {
        using var source = new SoundCardIqSource(new ScriptedCapture(Rate, pcm), options);
        var all = new float[pcm.Length];
        int at = 0;
        int got = 1;
        while (at < all.Length && got > 0)
        {
            got = source.Read(all.AsSpan(at, Math.Min(8192, all.Length - at)));
            at += got;
        }

        return 10 * Math.Log10(Power(all, toneHz) / Power(all, -toneHz));
    }

    /// <summary>Power at one frequency: a single-bin DFT over the whole capture.</summary>
    private static double Power(float[] iq, double hz)
    {
        double re = 0;
        double im = 0;
        int n = iq.Length / 2;
        for (int k = 0; k < n; k++)
        {
            double w = -2 * Math.PI * hz * k / Rate;
            double c = Math.Cos(w);
            double s = Math.Sin(w);
            re += (iq[2 * k] * c) - (iq[(2 * k) + 1] * s);
            im += (iq[2 * k] * s) + (iq[(2 * k) + 1] * c);
        }

        return ((re * re) + (im * im)) / ((double)n * n);
    }

    /// <summary>A complex tone through a card with the given mismatch.</summary>
    private static short[] Tone(double hz, double gainDb, double phaseDegrees, double seconds)
    {
        int n = (int)(seconds * Rate);
        var iq = new float[2 * n];
        for (int k = 0; k < n; k++)
        {
            double t = 2 * Math.PI * hz * k / Rate;
            iq[2 * k] = (float)(0.5 * Math.Cos(t));
            iq[(2 * k) + 1] = (float)(0.5 * Math.Sin(t));
        }

        return CardSynth.Card(iq, gainDb, phaseDegrees);
    }

    /// <summary>Complex white noise through a card with the given mismatch.</summary>
    private static short[] Noise(double gainDb, double phaseDegrees, double seconds, int seed)
    {
        var iq = new float[2 * (int)(seconds * Rate)];
        IqSynth.AddNoise(iq, 0.05, seed);
        return CardSynth.Card(iq, gainDb, phaseDegrees);
    }

    /// <summary>A card that plays back a fixed recording of 16-bit pairs, with an overrun on cue.</summary>
    private sealed class ScriptedCapture(int sampleRate, short[] pcm) : IStereoCapture
    {
        private int _at;

        public int SampleRate { get; } = sampleRate;

        public long Overruns { get; private set; }

        public int BufferFrames { get; init; }

        /// <summary>End the next read short, as an overrun mid-read does.</summary>
        public int? CutNextReadTo { get; set; }

        /// <summary>Count an overrun on the next read, as ALSA's recovery does.</summary>
        public bool OverrunBeforeNextRead { get; set; }

        /// <summary>Advanced by each read's own length, as a real card's blocking read would.</summary>
        public FakeTimeProvider? Clock { get; init; }

        public int Read(Span<short> interleaved)
        {
            int frames = Math.Min(interleaved.Length / 2, (pcm.Length - _at) / 2);
            if (frames <= 0)
            {
                return 0;
            }

            if (OverrunBeforeNextRead)
            {
                OverrunBeforeNextRead = false;
                Overruns++;
            }

            if (CutNextReadTo is int cut)
            {
                CutNextReadTo = null;
                frames = Math.Min(frames, cut);
            }

            pcm.AsSpan(_at, 2 * frames).CopyTo(interleaved);
            _at += 2 * frames;
            Clock?.Advance(TimeSpan.FromSeconds((double)frames / SampleRate));
            return frames;
        }

        public void Dispose()
        {
        }
    }
}
