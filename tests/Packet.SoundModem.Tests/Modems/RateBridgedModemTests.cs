using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The rate bridge that runs a 12 kHz-native modem at 12 kHz on a 48 kHz channel (issue #648).
/// The claims: a channel builds it where it fits and not where it does not, a bridged modem talks
/// to itself and to a modem built at the channel rate in both directions, its frame spans land on
/// the channel's grid, and the bridge itself costs nothing per block once running.
/// </summary>
public class RateBridgedModemTests
{
    private const int Channel = 48000;

    private static byte[] TestFrame(int length, int seed)
    {
        var random = new Random(seed);
        var frame = new byte[length];
        random.NextBytes(frame);
        return frame;
    }

    private static IModem ForChannel(string mode, Action<byte[]> sink, double? centre = null) =>
        ModemCatalog.CreateForChannel(mode, Channel, sink, new ModemOptions(CentreFrequencyHz: centre));

    private static void Feed(IModem modem, float[] burst)
    {
        modem.Process(new float[Channel / 10]);
        for (int i = 0; i < burst.Length; i += Channel / 10)
        {
            modem.Process(burst.AsSpan(i, Math.Min(Channel / 10, burst.Length - i)));
        }

        modem.Process(new float[Channel]);
    }

    [Theory]
    [InlineData("afsk1200")]
    [InlineData("afsk300-il2pc")]
    [InlineData("bpsk300")]
    [InlineData("qpsk2400")]
    public void A_12_kHz_Mode_On_A_48_kHz_Channel_Runs_At_12_kHz(string mode)
    {
        IModem modem = ForChannel(mode, _ => { });

        modem.Should().BeOfType<RateBridgedModem>()
            .Which.Factor.Should().Be(4, "the mode's own rate is a quarter of the channel's");
        ((RateBridgedModem)modem).Inner.Mode.Should().Be(modem.Mode);
    }

    [Fact]
    public void A_12_kHz_Channel_And_A_48_kHz_Mode_Are_Built_As_Before()
    {
        ModemCatalog.CreateForChannel("bpsk300", 12000, _ => { })
            .Should().NotBeOfType<RateBridgedModem>("nothing to bridge at the mode's own rate");
        ForChannel("fsk9600-il2p", _ => { })
            .Should().NotBeOfType<RateBridgedModem>("a 48 kHz mode already runs at the channel rate");
        ForChannel("ms110d-wn4", _ => { })
            .Should().NotBeOfType<RateBridgedModem>();
    }

    [Fact]
    public void A_Modem_Placed_Above_What_12_kHz_Can_Carry_Stays_At_The_Channel_Rate()
    {
        // 4800 Hz puts afsk1200's upper tone past the decimator's flat passband, which a 48 kHz
        // channel with a wide radio filter can carry and a 12 kHz modem cannot.
        IModem modem = ForChannel("afsk1200", _ => { }, centre: 4800);

        modem.Should().NotBeOfType<RateBridgedModem>();
    }

    [Theory]
    [InlineData("afsk1200")]
    [InlineData("afsk300-il2pc")]
    [InlineData("bpsk300")]
    [InlineData("qpsk2400")]
    public void A_Frame_Round_Trips_Between_Two_Bridged_Modems(string mode)
    {
        byte[] frame = TestFrame(60, 3);
        var received = new List<byte[]>();

        float[] burst = ForChannel(mode, _ => { }).Modulate(frame, txDelayMilliseconds: 100);
        Feed(ForChannel(mode, received.Add), burst);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData("afsk300-il2pc")]
    [InlineData("bpsk300")]
    public void A_Bridged_Receiver_Decodes_A_Modem_Built_At_The_Channel_Rate(string mode)
    {
        // What every other station on the air was built as until now, and what a 48 kHz station
        // whose modem does not fit the bridge still is.
        byte[] frame = TestFrame(60, 5);
        var received = new List<byte[]>();

        float[] burst = ModemCatalog.Create(mode, Channel, _ => { }).Modulate(frame, txDelayMilliseconds: 100);
        Feed(ForChannel(mode, received.Add), burst);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData("afsk300-il2pc")]
    [InlineData("bpsk300")]
    public void A_Bridged_Burst_Decodes_On_A_Modem_At_Its_Own_Rate(string mode)
    {
        // The other direction, checked on a 12 kHz station - which is what most of the stations
        // hearing a bridged transmission are - by decimating the burst the way its card would.
        byte[] frame = TestFrame(60, 7);
        var received = new List<byte[]>();
        float[] burst = ForChannel(mode, _ => { }).Modulate(frame, txDelayMilliseconds: 100);

        var decimator = new M0LTE.Dsp.Decimator(Channel, 4);
        var native = new float[decimator.MaxOutput(burst.Length)];
        int produced = decimator.Process(burst, native);
        IModem receiver = ModemCatalog.Create(mode, 12000, received.Add);
        receiver.Process(new float[1200]);
        receiver.Process(native.AsSpan(0, produced));
        receiver.Process(new float[12000]);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void A_Frame_Span_Lands_On_The_Channel_Grid()
    {
        // Lead-in silence of a known length, then the burst: the span the channel is handed must
        // sit inside the burst as the channel counted it, not at a quarter of the way there.
        byte[] frame = TestFrame(60, 9);
        IModem modem = ForChannel("afsk300-il2pc", _ => { });
        var source = (IFrameSpanSource)modem;
        long from = -1, to = -1;
        modem.FrameDecoded += (_, _) => source.TryTakeFrameSpan(out from, out to);

        float[] burst = ForChannel("afsk300-il2pc", _ => { }).Modulate(frame, txDelayMilliseconds: 100);
        const int lead = 3 * Channel;
        modem.Process(new float[lead]);
        modem.Process(burst);
        modem.Process(new float[Channel]);

        from.Should().BeGreaterThan(lead, "the sync is inside the burst, which starts after the lead-in");
        to.Should().BeGreaterThan(from);
        to.Should().BeLessThanOrEqualTo(lead + burst.Length + source.FrameSpanMarginSamples);
    }

    [Fact]
    public void The_Bridge_Feeds_A_Quarter_Of_The_Samples_And_Allocates_Nothing_Per_Block()
    {
        var inner = new ToneModem();
        RateBridgedModem.TryWrap(inner, 12000, Channel, out IModem? bridged).Should().BeTrue();
        var block = new float[Channel / 10];

        for (int i = 0; i < 10; i++)
        {
            bridged!.Process(block);
        }

        // A collection that pauses this thread mid-stretch, started by another test's
        // allocations, can move this thread's count without this thread allocating anything,
        // most likely by charging it the unused end of its allocation buffer: 2.9 to 5.7 kB in
        // 16 stretches out of about 13,000 run beside the rest of the Modems tests on a loaded
        // box, every one of them with a GC pause inside it, and not one byte in any stretch
        // without a pause (#668). Such a stretch says nothing about the bridge, so it is run
        // again; a bridge that allocated per block would allocate in every stretch, and the
        // clean one fails it.
        int stretches = 0;
        long allocated = -1;
        for (int attempt = 0; attempt < 10 && allocated < 0; attempt++)
        {
            TimeSpan paused = GC.GetTotalPauseDuration();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                bridged!.Process(block);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            stretches++;
            if (GC.GetTotalPauseDuration() == paused)
            {
                allocated = after - before;
            }
        }

        allocated.Should().BeGreaterThanOrEqualTo(0, "a stretch with no collection in it is needed to measure anything");
        allocated.Should().Be(0, "the receive path is the DSP hot path");
        inner.Processed.Should().Be((10 + (100 * stretches)) * block.Length / 4);
    }

    [Fact]
    public void A_Burst_Comes_Out_At_The_Channel_Rate_At_The_Same_Level_With_Its_Tail()
    {
        var inner = new ToneModem();
        RateBridgedModem.TryWrap(inner, 12000, Channel, out IModem? bridged).Should().BeTrue();

        float[] native = inner.Modulate([], 0);
        float[] burst = bridged!.Modulate([], 0);

        burst.Length.Should().BeGreaterThan(native.Length * 4, "the upsampler's tail is flushed, not cut");
        burst.Length.Should().BeLessThan((native.Length * 4) + 200);
        double Rms(ReadOnlySpan<float> s)
        {
            double sum = 0;
            foreach (float x in s)
            {
                sum += x * x;
            }

            return Math.Sqrt(sum / s.Length);
        }

        double nativeRms = Rms(native.AsSpan(native.Length / 4, native.Length / 2));
        double bridgedRms = Rms(burst.AsSpan(burst.Length / 4, burst.Length / 2));
        (20 * Math.Log10(bridgedRms / nativeRms)).Should().BeApproximately(0, 0.2);
    }

    /// <summary>A 1 kHz tone at 12 kHz that counts what it is fed.</summary>
    private sealed class ToneModem : IModem
    {
        public long Processed { get; private set; }

        public string Mode => "tone";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public void Process(ReadOnlySpan<float> samples) => Processed += samples.Length;

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
        {
            var tone = new float[12000];
            for (int i = 0; i < tone.Length; i++)
            {
                tone[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / 12000.0);
            }

            return tone;
        }

        public void ResetCarrierState()
        {
        }
    }
}
