using System.Diagnostics;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// What the front end costs at 2.4 MS/s in, with one channel out and with four: a measurement,
/// not a check, so it runs only when asked (<c>IQ_BENCH=1</c>). Run on the box in question with
/// <c>Packet.SoundModem.Tests -class Packet.SoundModem.Tests.Iq.IqReceiverCpuTests</c>; it
/// prints how many times faster than real time one core gets through the stream.
/// </summary>
public sealed class IqReceiverCpuTests(ITestOutputHelper output)
{
    private const int Rate = 2_400_000;
    private const int Seconds = 20;

    public static TheoryData<string, int, IqChannelSpec[]> Configurations => new()
    {
        { "1 x FM fsk9600, 48 kHz DSP", 48_000, [IqChannelSpec.ForFmMode("fsk9600", 25_000)] },
        { "1 x FM afsk1200, 12 kHz DSP", 12_000, [IqChannelSpec.ForFmMode("afsk1200", 25_000)] },
        { "1 x USB, 12 kHz DSP", 12_000, [new IqChannelSpec { OffsetHz = 25_000, Demodulation = IqDemodulation.Usb }] },
        {
            "4 x FM fsk9600, 48 kHz DSP, within +-50 kHz", 48_000,
            [
                IqChannelSpec.ForFmMode("fsk9600", 25_000),
                IqChannelSpec.ForFmMode("fsk9600", -25_000),
                IqChannelSpec.ForFmMode("fsk9600", 40_000),
                IqChannelSpec.ForFmMode("fsk9600", -40_000),
            ]
        },
        {
            "4 x FM afsk1200, 12 kHz DSP, within +-50 kHz", 12_000,
            [
                IqChannelSpec.ForFmMode("afsk1200", 25_000),
                IqChannelSpec.ForFmMode("afsk1200", -25_000),
                IqChannelSpec.ForFmMode("afsk1200", 45_000),
                IqChannelSpec.ForFmMode("afsk1200", -45_000),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Front_End_Cost_At_2_4_Msps(string name, int dspRate, IqChannelSpec[] channels)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("IQ_BENCH") != "1",
            "set IQ_BENCH=1 to measure the front end's CPU cost");

        // One second of noise with a carrier in it, played round and round: the arithmetic does
        // not care what the samples are, and a fresh 20 s capture would be 384 MB.
        var second = new float[2 * Rate];
        IqSynth.AddNoise(second, 0.01, seed: 1);
        var carrier = new float[Rate];
        Array.Fill(carrier, 0.1f);
        IqSynth.AddDsb(second, carrier, Rate, 25_000);
        var source = new LoopingIqSource(second, Rate, (long)Seconds * Rate);
        using var receiver = new IqReceiver(source, channels, new IqReceiverOptions { DspRate = dspRate });
        var buffer = new float[16384];

        // Warm the JIT and the tiered compiler before the clock starts.
        for (int b = 0; b < 200; b++)
        {
            receiver.Pump();
            DrainAvailable(receiver, buffer);
        }

        source.Rewind();
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        while (receiver.Pump())
        {
            DrainAvailable(receiver, buffer);
        }

        clock.Stop();
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        double realTime = Seconds / clock.Elapsed.TotalSeconds;
        output.WriteLine(
            $"{name}: coarse {receiver.CoarseRate} S/s ({receiver.Coarse.Stages.Count} stages, "
            + $"{receiver.Coarse.MacsPerInputSample:F1} MAC/sample), {Seconds} s of IQ in "
            + $"{clock.Elapsed.TotalSeconds:F2} s wall, {cpu.TotalSeconds:F2} s CPU: "
            + $"{realTime:F1}x real time, {100.0 * clock.Elapsed.TotalSeconds / Seconds:F1} % of one core");
        realTime.Should().BeGreaterThan(1, "a front end slower than real time is no front end");
    }

    private static void DrainAvailable(IqReceiver receiver, float[] buffer)
    {
        for (int c = 0; c < receiver.Channels.Count; c++)
        {
            while (receiver.Channels[c].Available > 0)
            {
                receiver.Channels[c].Read(buffer);
            }
        }
    }

    /// <summary>A buffer played round and round until a total is reached.</summary>
    private sealed class LoopingIqSource(float[] loop, int rate, long totalFrames) : IIqSource
    {
        private long _served;
        private int _offset;

        public int SampleRate => rate;

        public double CentreFrequencyHz => 0;

        public long TuningGeneration => 0;

        public long SamplesLost => 0;

        public void Rewind()
        {
            _served = 0;
            _offset = 0;
        }

        public int Read(Span<float> interleaved)
        {
            long left = (totalFrames - _served) * 2;
            if (left <= 0)
            {
                return 0;
            }

            int take = (int)Math.Min(Math.Min(interleaved.Length & ~1, loop.Length - _offset), left);
            loop.AsSpan(_offset, take).CopyTo(interleaved);
            _offset = (_offset + take) % loop.Length;
            _served += take / 2;
            return take;
        }
    }
}
