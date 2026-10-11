using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// The station's receive input over rtl_tcp, end to end: a burst frequency-modulated onto IQ,
/// cut to unsigned bytes as a dongle sends them, served by the fake server, demodulated by the
/// front end and decoded.
/// </summary>
public sealed class RtlTcpAudioInputTests
{
    private const int Rate = 250_000;
    private const double DialHz = 144_800_000;

    private readonly ConcurrentQueue<string> _journal = new();

    [Theory]
    [InlineData("afsk1200")]
    [InlineData("fsk9600")]
    public async Task A_Burst_Served_As_Dongle_Bytes_Is_Decoded(string mode)
    {
        var sim = new SimModem(mode);
        byte[] frame = SimModem.Frame(60, seed: 5);
        double centre = RtlTcpAudioInput.CentreFor(DialHz, Rate);
        byte[] dongle = DongleBytes(sim, frame, mode, DialHz - centre);

        using var server = new FakeRtlTcpServer();
        using RtlTcpAudioInput input = await RtlTcpAudioInput.OpenAsync(
            server.Endpoint,
            new RtlTcpSourceOptions { CentreFrequencyHz = 0, SampleRate = Rate, Journal = _journal.Enqueue, WatchProfile = false },
            IqChannelSpec.ForFmMode(mode, 0),
            new IqReceiverOptions { DspRate = sim.Rate, Journal = _journal.Enqueue },
            DialHz,
            CancellationToken.None);
        server.WaitForConnections(1);
        server.WaitForCommands(5);

        server.Commands.Should().Contain((1, RtlTcpCommand.SetFrequency, (uint)centre));
        centre.Should().Be(DialHz - 25_000, "the dongle sits below the dial, clear of its DC spike");
        input.DialHz.Should().Be(DialHz);

        // The default 100 ms of settling after the settings is thrown away, so lead with it.
        server.Send(new byte[2 * Rate / 10]);
        server.Send(dongle);

        float[] heard = ReadSeconds(input, sim.Rate, (double)dongle.Length / 2 / Rate);

        sim.Decode(heard, frame).Matched.Should().BeTrue($"{mode} through rtl_tcp should come back bit-exact");
        input.SessionLive.Should().BeTrue();
        input.Discontinuities.Should().Be(0);
    }

    [Fact]
    public async Task A_Dial_Out_Of_The_Band_Retunes_A_Controlled_Dongle_And_One_Inside_Moves_Only_The_Channel()
    {
        using var server = new FakeRtlTcpServer();
        using RtlTcpAudioInput input = await OpenFm(server, readOnly: false, coverageHz: 50_000);
        server.WaitForCommands(5);
        int before = server.Commands.Count;

        (await input.TuneAsync(DialHz + 3_000, CancellationToken.None)).Should().BeTrue();
        Thread.Sleep(100);
        server.Commands.Count.Should().Be(before, "3 kHz along is inside the 50 kHz the front end was asked to keep");

        (await input.TuneAsync(145_500_000, CancellationToken.None)).Should().BeTrue();
        server.WaitForCommands(before + 1);
        server.Commands[^1].Should().Be((1, RtlTcpCommand.SetFrequency, 145_475_000u));
        input.DialHz.Should().Be(145_500_000);
    }

    [Fact]
    public async Task A_Read_Only_Dongle_Is_Never_Retuned_And_Says_When_A_Dial_Is_Out_Of_Its_Band()
    {
        using var server = new FakeRtlTcpServer();
        using RtlTcpAudioInput input = await OpenFm(server, readOnly: true);
        server.WaitForConnections(1);

        (await input.TuneAsync(145_500_000, CancellationToken.None)).Should().BeFalse();
        Thread.Sleep(100);
        server.BytesFromClient.Should().Be(0);
    }

    [Fact]
    public async Task A_Dial_Outside_A_Read_Only_Profile_Is_Refused_Before_Anything_Is_Opened()
    {
        using var server = new FakeRtlTcpServer();
        Func<Task> open = () => RtlTcpAudioInput.OpenAsync(
            server.Endpoint,
            new RtlTcpSourceOptions { ReadOnly = true, CentreFrequencyHz = 145_500_000, SampleRate = Rate },
            IqChannelSpec.ForFmMode("afsk1200", 0),
            new IqReceiverOptions { DspRate = 12_000 },
            DialHz,
            CancellationToken.None);

        await open.Should().ThrowAsync<ArgumentException>().WithMessage("*from the centre*");
        server.Connections.Should().Be(0);
    }

    private async Task<RtlTcpAudioInput> OpenFm(FakeRtlTcpServer server, bool readOnly, double? coverageHz = null) =>
        await RtlTcpAudioInput.OpenAsync(
            server.Endpoint,
            new RtlTcpSourceOptions
            {
                ReadOnly = readOnly,
                CentreFrequencyHz = DialHz - 25_000,
                SampleRate = Rate,
                Journal = _journal.Enqueue,
                WatchProfile = false,
            },
            IqChannelSpec.ForFmMode("afsk1200", 0),
            new IqReceiverOptions { DspRate = 12_000, Journal = _journal.Enqueue, CoverageHz = coverageHz },
            DialHz,
            CancellationToken.None);

    /// <summary>The burst as a dongle would send it: FM at <paramref name="offsetHz"/> from the
    /// centre, a little noise, the whole stream at a quarter of full scale, then cut to bytes.</summary>
    private static byte[] DongleBytes(SimModem sim, byte[] frame, string mode, double offsetHz)
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
        var bytes = new byte[iq.Length];
        for (int k = 0; k < iq.Length; k++)
        {
            bytes[k] = (byte)Math.Clamp(Math.Round((iq[k] * 127.5) + 127.5), 0, 255);
        }

        return bytes;
    }

    private static float[] ReadSeconds(RtlTcpAudioInput input, int rate, double seconds)
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
