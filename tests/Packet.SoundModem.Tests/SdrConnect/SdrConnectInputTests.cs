using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

/// <summary>
/// The station's receive inputs over SDRconnect, end to end: a burst served by the fake as
/// SDRconnect would serve it, as IQ demodulated by the front end and as SDRconnect's own audio,
/// and decoded.
/// </summary>
public sealed class SdrConnectInputTests
{
    private const int Rate = 250_000;
    private const double DialHz = 144_800_000;

    private readonly ConcurrentQueue<string> _journal = new();

    [Theory]
    [InlineData("afsk1200")]
    [InlineData("fsk9600")]
    public async Task A_Burst_Served_As_SDRconnect_Iq_Is_Decoded(string mode)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(60, seed: 5);
        double centre = SdrConnectIqInput.CentreFor(DialHz, Rate);
        short[] iq = Iq(sim, frame, mode, DialHz - centre);

        using var host = new FakeSdrConnectHost();
        Task<SdrConnectIqInput> opening = SdrConnectIqInput.OpenAsync(
            FakeSdrConnectHost.Endpoint,
            new SdrConnectOptions { CentreFrequencyHz = DialHz, SampleRate = Rate, WatchRate = false, Journal = _journal.Enqueue },
            IqChannelSpec.ForFmMode(mode, 0),
            new IqReceiverOptions { DspRate = sim.Rate, Journal = _journal.Enqueue },
            DialHz,
            host.ConnectAsync,
            TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        using SdrConnectIqInput input = await opening;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fake.Value("device_center_frequency").Should().Be("144775000", "the LO sits below the dial, clear of its DC spike");
        input.DialHz.Should().Be(DialHz);

        // What the default settling throws away after the set-up (100 ms) is led with, so the
        // burst is all heard; sent in messages of the size a socket might carry.
        await fake.SendIqAsync(new short[2 * Rate / 5]);
        for (int k = 0; k < iq.Length; k += 16384)
        {
            await fake.SendIqAsync(iq.AsSpan(k, Math.Min(16384, iq.Length - k)));
        }

        float[] heard = Read(input, sim.Rate, (double)iq.Length / 2 / Rate);

        sim.Decode(heard, frame).Matched.Should().BeTrue($"{mode} through SDRconnect's IQ should come back bit-exact");
        input.SessionLive.Should().BeTrue();
        input.Discontinuities.Should().Be(0);
    }

    [Theory]
    [InlineData("afsk1200", "NFM")]
    [InlineData("bpsk300", "USB")]
    public async Task A_Burst_Served_As_SDRconnect_Audio_Is_Decoded(string mode, string demodulator)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(40, seed: 7);
        float[] burst = sim.RenderBurst(frame, txDelayMilliseconds: 150);
        var audio = new float[(sim.Rate / 2) + burst.Length + sim.Rate];
        burst.CopyTo(audio.AsSpan(sim.Rate / 2));
        float[] at48k = IqSynth.Resample(audio, sim.Rate, SdrConnectProtocol.AudioRate);
        var stereo = new short[2 * at48k.Length];
        for (int k = 0; k < at48k.Length; k++)
        {
            stereo[2 * k] = (short)Math.Clamp(Math.Round(at48k[k] * 16000), short.MinValue, short.MaxValue);
            stereo[(2 * k) + 1] = 1234; // the right channel is not used
        }

        using var host = new FakeSdrConnectHost();
        Task<SdrConnectAudioInput> opening = SdrConnectAudioInput.OpenAsync(
            FakeSdrConnectHost.Endpoint,
            new SdrConnectOptions { CentreFrequencyHz = DialHz, WatchRate = false, Journal = _journal.Enqueue },
            new SdrConnectAudioChannel { Demodulator = demodulator, OutputRate = sim.Rate },
            DialHz,
            host.ConnectAsync,
            TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        using SdrConnectAudioInput input = await opening;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fake.AudioOn.Should().BeTrue();
        fake.IqOn.Should().BeFalse();
        fake.Value("demodulator").Should().Be(demodulator);
        fake.Value("device_vfo_frequency").Should().Be("144800000");
        fake.Value("device_center_frequency").Should().Be("144775000");
        fake.Value("agc_enable").Should().Be("false");
        fake.Value("noise_reduction_enable").Should().Be("false");
        fake.Value("squelch_enable").Should().Be("false");
        fake.Value("audio_limiters").Should().Be("false");
        fake.Value("audio_filter").Should().Be("false");
        if (demodulator == "NFM")
        {
            fake.Value("nfm_deemphasis_enable").Should().Be("false");
            fake.Value("filter_bandwidth").Should().Be("12000", "12500 asked for, held to the 12000 SDRconnect says is its widest");
        }
        else
        {
            fake.Value("ssb_lowcut_frequency").Should().Be("150");
            fake.Value("filter_bandwidth").Should().Be("3450");
        }

        await fake.SendAudioAsync(new short[2 * SdrConnectProtocol.AudioRate / 5]);
        for (int k = 0; k < stereo.Length; k += 8192)
        {
            await fake.SendAudioAsync(stereo.AsSpan(k, Math.Min(8192, stereo.Length - k)));
        }

        float[] heard = Read(input, sim.Rate, (double)at48k.Length / SdrConnectProtocol.AudioRate);

        sim.Decode(heard, frame).Matched.Should().BeTrue($"{mode} through SDRconnect's audio should come back bit-exact");
        input.SampleRate.Should().Be(sim.Rate);
        input.Discontinuities.Should().Be(0);
    }

    [Fact]
    public async Task An_Audio_Retune_Inside_The_Band_Moves_Only_The_Vfo_And_One_Outside_Moves_The_LO_Too()
    {
        using var host = new FakeSdrConnectHost();
        Task<SdrConnectAudioInput> opening = SdrConnectAudioInput.OpenAsync(
            FakeSdrConnectHost.Endpoint,
            new SdrConnectOptions { CentreFrequencyHz = DialHz, WatchRate = false, Journal = _journal.Enqueue },
            new SdrConnectAudioChannel { Demodulator = "NFM", OutputRate = 12_000 },
            DialHz,
            host.ConnectAsync,
            TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        using SdrConnectAudioInput input = await opening;
        int loSets = fake.Sets("device_center_frequency").Count();

        (await input.TuneAsync(DialHz + 100_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        await fake.WaitForAsync(f => f.Value("device_vfo_frequency") == "144900000", "the VFO moved");
        fake.Sets("device_center_frequency").Count().Should().Be(loSets, "100 kHz along is inside the 2 MS/s SDRconnect has");

        (await input.TuneAsync(146_500_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        await fake.WaitForAsync(f => f.Value("device_vfo_frequency") == "146500000", "the VFO moved again");
        fake.Value("device_center_frequency").Should().Be("146475000");
        input.DialHz.Should().Be(146_500_000);
        input.Client.ForeignChanges.Should().Be(0);
    }

    [Fact]
    public async Task A_Report_Of_The_Demodulator_SDRconnect_Already_Has_Is_Not_A_Hole_In_The_Audio()
    {
        using var host = new FakeSdrConnectHost();
        Task<SdrConnectAudioInput> opening = SdrConnectAudioInput.OpenAsync(
            FakeSdrConnectHost.Endpoint,
            new SdrConnectOptions { CentreFrequencyHz = DialHz, WatchRate = false, RetuneSettle = TimeSpan.Zero, Journal = _journal.Enqueue },
            new SdrConnectAudioChannel { Demodulator = "NFM", OutputRate = SdrConnectProtocol.AudioRate },
            DialHz,
            host.ConnectAsync,
            TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        using SdrConnectAudioInput input = await opening;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendAudioAsync(new short[2 * 4800]);
        Read(input, SdrConnectProtocol.AudioRate, 0.1);

        await fake.ChangeAsync("demodulator", "NFM");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await fake.SendAudioAsync(new short[2 * 4800]);
        Read(input, SdrConnectProtocol.AudioRate, 0.1);

        input.Discontinuities.Should().Be(0, "the demodulator did not change");
        input.Client.ForeignChanges.Should().Be(0);
    }

    /// <summary>The burst as SDRconnect's IQ: FM at <paramref name="offsetHz"/> from the LO, a
    /// little noise, at a quarter of full scale, as signed 16-bit pairs.</summary>
    private static short[] Iq(SimModem sim, byte[] frame, string mode, double offsetHz)
    {
        float[] active = sim.RenderBurst(frame, txDelayMilliseconds: 150);
        var audio = new float[(sim.Rate / 2) + active.Length + sim.Rate];
        active.CopyTo(audio.AsSpan(sim.Rate / 2));
        float peak = 0;
        foreach (float v in active)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        double deviation = FmModeProfiles.For(mode)!.PeakDeviationHz;
        float[] iq = IqSynth.Fm(IqSynth.Resample(audio, sim.Rate, Rate), Rate, offsetHz, deviation / peak, amplitude: 0.25);
        IqSynth.AddNoise(iq, 0.0005, seed: 11);
        var samples = new short[iq.Length];
        for (int k = 0; k < iq.Length; k++)
        {
            samples[k] = (short)Math.Clamp(Math.Round(iq[k] * 32767), short.MinValue, short.MaxValue);
        }

        return samples;
    }

    private static float[] Read(M0LTE.Radio.Audio.IAudioInput input, int rate, double seconds)
    {
        var heard = new List<float>();
        var buffer = new float[4096];
        int want = (int)(seconds * rate * 0.95);
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);
        while (heard.Count < want && DateTime.UtcNow < giveUp)
        {
            int got = input.Read(buffer);
            heard.AddRange(buffer.AsSpan(0, got));
        }

        return [.. heard];
    }
}
