using AwesomeAssertions;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Tests.OpenWebRx;

/// <summary>
/// The end-to-end check against a real OpenWebRX receiver: the handshake, the receiver saying
/// where it is, the demodulator placed and started, and audio arriving in whichever encoding
/// the receiver's operator chose.
/// </summary>
/// <remarks>
/// Off by default, like <see cref="UberSdr.UberSdrLiveStreamTests"/>: it needs the internet and
/// somebody else's radio. Run it with <c>OPENWEBRX_LIVE=&lt;url&gt;</c> and
/// <c>OPENWEBRX_LIVE_DIAL=&lt;Hz&gt;</c>, a USB dial inside the band the receiver is on (it
/// does not ask for a profile, because that would move the receiver for everyone listening):
/// <code>OPENWEBRX_LIVE=http://sdr.example.org:8073/ OPENWEBRX_LIVE_DIAL=7074000 dotnet test</code>
/// What it asserts is plumbing, not decodes.
/// </remarks>
public sealed class OpenWebRxLiveStreamTests
{
    private static string? Url =>
        Environment.GetEnvironmentVariable("OPENWEBRX_LIVE") is { Length: > 0 } value ? value : null;

    private static long Dial =>
        long.TryParse(Environment.GetEnvironmentVariable("OPENWEBRX_LIVE_DIAL"), out long hz) ? hz : 7_074_000;

    [Fact]
    public async Task A_Live_Receiver_Delivers_Audio_At_The_Rate_It_Promises()
    {
        Assert.SkipUnless(Url is not null, "set OPENWEBRX_LIVE=<url> to listen through a real OpenWebRX receiver");

        string device = Url!.StartsWith("openwebrx:", StringComparison.OrdinalIgnoreCase) ? Url : "openwebrx:" + Url;
        OpenWebRxEndpoint endpoint = OpenWebRxDevice.Parse(device);
        var journal = new List<string>();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using OpenWebRxAudioInput input = await OpenWebRxAudioInput.OpenAsync(
            endpoint, OpenWebRxTuning.For(Dial, "usb", 150, 3450, 5000), line =>
            {
                lock (journal)
                {
                    journal.Add(line);
                }
            }, cancellation.Token);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{input.Server}; {input.ReceiverDescription}; {input.Band}; adpcm {input.Adpcm}");

        var audio = new List<float>();
        var buffer = new float[1200];
        DateTime start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(12))
        {
            int got = input.Read(buffer);
            audio.AddRange(buffer.AsSpan(0, got).ToArray());
        }

        lock (journal)
        {
            foreach (string line in journal)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(line);
            }
        }

        // Twelve seconds of wall clock, less the guard and the start, at 12 kHz.
        audio.Count.Should().BeInRange(12000 * 8, 12000 * 13);
        double rms = Math.Sqrt(audio.Sum(s => (double)s * s) / audio.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"{audio.Count} samples, RMS {20 * Math.Log10(rms):F1} dBFS");
        rms.Should().BeGreaterThan(0, "an open squelch always carries something");
        input.SessionLive.Should().BeTrue();
    }
}
