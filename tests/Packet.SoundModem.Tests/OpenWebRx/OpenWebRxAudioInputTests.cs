using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Tests.OpenWebRx;

/// <summary>
/// The OpenWebRX input against a fake receiver on the other end of a real WebSocket: the
/// conversation, the audio in both of the server's encodings, retuning, and the sessions ending.
/// </summary>
public sealed class OpenWebRxAudioInputTests : IAsyncDisposable
{
    private static readonly OpenWebRxEndpoint Endpoint = new("sdr.example.org", 8073, Ssl: false);

    private readonly System.Threading.Channels.Channel<FakeReceiver> _accepted =
        System.Threading.Channels.Channel.CreateUnbounded<FakeReceiver>();
    private readonly ConcurrentQueue<string> _journal = new();
    private readonly List<IDisposable> _owned = [];

    private static OpenWebRxTuning Tuning(int outputRate = 12000) =>
        OpenWebRxTuning.For(7_048_800, "usb", 150, 3450, 5000) with
        {
            OutputRate = outputRate,
            StartupGuardMs = 0,
        };

    /// <summary>A connect that makes a socket pair over loopback TCP and hands the far end to
    /// the test as a <see cref="FakeReceiver"/>.</summary>
    private async Task<WebSocket> ConnectAsync(CancellationToken cancellation)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellation).AsTask();
        TcpClient server = await listener.AcceptTcpClientAsync(cancellation);
        await connecting;
        lock (_owned)
        {
            _owned.Add(client);
            _owned.Add(server);
        }

        WebSocket serverSocket = WebSocket.CreateFromStream(
            server.GetStream(), new WebSocketCreationOptions { IsServer = true });
        await _accepted.Writer.WriteAsync(new FakeReceiver(serverSocket), cancellation);
        return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
    }

    private Task<OpenWebRxAudioInput> OpenAsync(OpenWebRxTuning tuning, TimeProvider? time = null) =>
        OpenWebRxAudioInput.OpenAsync(
            Endpoint, tuning, ConnectAsync, line => _journal.Enqueue(line), TestContext.Current.CancellationToken, time);

    private async Task<FakeReceiver> NextReceiverAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _accepted.Reader.ReadAsync(timeout.Token);
    }

    private static short[] Tone(int count, double hz = 1000, double amplitude = 8000)
    {
        var samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / 12000));
        }

        return samples;
    }

    private static float[] ReadAtLeast(OpenWebRxAudioInput input, int count)
    {
        var got = new List<float>();
        var buffer = new float[4096];
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (got.Count < count && DateTime.UtcNow < giveUp)
        {
            int n = input.Read(buffer);
            got.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        return [.. got];
    }

    [Fact]
    public async Task Plain_Audio_Arrives_As_The_Receiver_Sent_It()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        input.Server.Should().Be("openwebrx v1.2.2");
        input.ReceiverDescription.Should().Be("Test SDR, Here");
        input.Adpcm.Should().BeFalse();
        input.SampleRate.Should().Be(12000);

        short[] tone = Tone(6000);
        byte[] pcm = new byte[tone.Length * 2];
        Buffer.BlockCopy(tone, 0, pcm, 0, pcm.Length);
        // An odd split, so one sample straddles two messages.
        await receiver.SendAudioAsync(pcm.AsMemory(0, 1001));
        await receiver.SendAudioAsync(pcm.AsMemory(1001));

        float[] audio = ReadAtLeast(input, tone.Length);
        audio.Should().HaveCount(tone.Length);
        audio.Select(a => (short)Math.Round(a * 32768)).Should().Equal(tone);
        input.SessionLive.Should().BeTrue();
    }

    [Fact]
    public async Task Adpcm_Audio_Is_Decoded_Across_Message_Boundaries()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "adpcm");
        using OpenWebRxAudioInput input = await opening;
        input.Adpcm.Should().BeTrue();

        short[] tone = Tone(8000);
        var encoder = new ImaAdpcmSyncEncoder();
        var stream = new byte[ImaAdpcmSyncEncoder.MaxBytesFor(tone.Length)];
        stream = stream[..encoder.Encode(tone, stream)];
        for (int i = 0; i < stream.Length; i += 700)
        {
            await receiver.SendAudioAsync(stream.AsMemory(i, Math.Min(700, stream.Length - i)));
        }

        var decoder = new ImaAdpcmSyncDecoder();
        var expected = new short[ImaAdpcmSyncDecoder.MaxSamplesFor(stream.Length)];
        expected = expected[..decoder.Decode(stream, expected)];

        float[] audio = ReadAtLeast(input, expected.Length);
        audio.Select(a => (short)Math.Round(a * 32768)).Should().Equal(expected);
    }

    [Fact]
    public async Task A_48_kHz_Channel_Gets_Four_Samples_For_Each_Of_The_Receivers()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning(outputRate: 48000));
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        short[] tone = Tone(3000);
        byte[] pcm = new byte[tone.Length * 2];
        Buffer.BlockCopy(tone, 0, pcm, 0, pcm.Length);
        await receiver.SendAudioAsync(pcm);

        input.SampleRate.Should().Be(48000);
        ReadAtLeast(input, 4 * tone.Length).Should().HaveCount(4 * tone.Length);
    }

    [Fact]
    public async Task The_Demodulator_Is_Placed_On_The_Dial_And_Moved_When_The_Station_Retunes()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        IReadOnlyList<string> placed = await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        JsonNode.Parse(placed[0])!["params"]!["offset_freq"]!.GetValue<long>().Should().Be(-51_200);
        placed[1].Should().Be("""{"type":"dspcontrol","action":"start"}""");

        (await input.TuneAsync(7_050_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        string moved = await receiver.ReceiveTextAsync();
        JsonNode.Parse(moved)!["params"]!["offset_freq"]!.GetValue<long>().Should().Be(-50_000);
        input.DialHz.Should().Be(7_050_000);

        (await input.TuneAsync(14_100_000, TestContext.Current.CancellationToken)).Should().BeFalse(
            "the receiver is on 40 m");
        _journal.Should().Contain(l => l.Contains("does not reach 14.100000 MHz", StringComparison.Ordinal));
    }

    private static byte[] Pcm(int count)
    {
        short[] tone = Tone(count);
        byte[] pcm = new byte[tone.Length * 2];
        Buffer.BlockCopy(tone, 0, pcm, 0, pcm.Length);
        return pcm;
    }

    [Fact]
    public async Task A_Retune_Marks_The_First_Audio_After_It_And_Loses_Nothing()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        input.Discontinuities.Should().Be(0, "starting the demodulator is not a hole");

        (await input.TuneAsync(7_050_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        await receiver.ReceiveTextAsync();
        input.Discontinuities.Should().Be(0, "the count is as of the audio handed out, and none has been since");

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        input.Discontinuities.Should().Be(1, "the audio after the retune is another signal");

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        input.Discontinuities.Should().Be(1, "only the first audio after it follows the move");

        (await input.TuneAsync(7_050_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        input.Discontinuities.Should().Be(1, "a retune to the dial it is already on moves nothing");
        input.DroppedSamples.Should().Be(0);
    }

    [Fact]
    public async Task A_Retune_Off_The_Band_And_Back_Is_One_Mark()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);

        (await input.TuneAsync(14_100_000, TestContext.Current.CancellationToken)).Should().BeFalse();
        await receiver.SendAudioAsync(Pcm(1200));
        // Audio from off the dial is read past, and the input says it is quiet on purpose.
        await WaitForAsync(() => !input.SessionLive);
        (await input.TuneAsync(7_040_000, TestContext.Current.CancellationToken)).Should().BeTrue();
        await receiver.ReceiveTextAsync();
        await receiver.SendAudioAsync(Pcm(1200));

        ReadAtLeast(input, 1200).Should().HaveCount(1200, "the audio from off the dial was not delivered");
        input.Discontinuities.Should().Be(1);
        input.DroppedSamples.Should().Be(0);
    }

    [Fact]
    public async Task The_Receiver_Reporting_A_New_Centre_Marks_The_First_Audio_After_It()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);

        // Somebody else listening chose another 40 m profile with a different centre.
        await receiver.SendTextAsync(FakeReceiver.Config(7_000_000));
        string placed = await receiver.ReceiveTextAsync();
        JsonNode.Parse(placed)!["params"]!["offset_freq"]!.GetValue<long>().Should().Be(48_800);

        await receiver.SendAudioAsync(Pcm(1200));
        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        input.Discontinuities.Should().Be(1);
        input.DroppedSamples.Should().Be(0);
    }

    [Fact]
    public async Task A_Busy_Receiver_Lets_The_Station_Come_Up_And_Be_Asked_Again_Later()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.ExpectOpeningAsync();
        await receiver.SendTextAsync("CLIENT DE SERVER server=openwebrx version=v1.2.2");
        await receiver.SendTextAsync("""{"type":"backoff","reason":"Too many clients"}""");
        await receiver.CloseAsync();

        using OpenWebRxAudioInput input = await opening;

        input.RefusedAtStartup.Should().Be("Too many clients");
        input.SessionLive.Should().BeFalse();
        await WaitForAsync(() => _journal.Any(l => l.Contains("is refusing us for now (Too many clients); asking again in 1 min", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_Dial_Off_The_Receivers_Band_Stops_Start_Up_And_Names_The_Profiles()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.ExpectOpeningAsync();
        await receiver.SendTextAsync("CLIENT DE SERVER server=openwebrx version=v1.2.2");
        await receiver.SendTextAsync(FakeReceiver.Config(centre: 145_000_000, compression: "none"));
        await receiver.SendTextAsync(FakeReceiver.Profiles);

        Func<Task> open = () => opening;

        (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(
            "sdr.example.org:8073 is on 143.800000 to 146.200000 MHz (profile \"2m APRS\"), which does not "
            + "reach the dial 7.048800 MHz. Choose the profile that does with \"openwebrx\": "
            + "{ \"profile\": \"...\" }; it offers \"40m\" (rtl|40m), \"2m APRS\" (rtl|2m).");
    }

    [Fact]
    public async Task A_Profile_In_The_Config_Is_Asked_For_Before_The_Demodulator_Is_Placed()
    {
        var time = new FakeTimeProvider();
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning() with { Profile = "40m" }, time);
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.ExpectOpeningAsync();
        await receiver.SendTextAsync("CLIENT DE SERVER server=openwebrx version=v1.2.2");
        await receiver.SendTextAsync(FakeReceiver.Config(centre: 145_000_000, compression: "none"));
        await receiver.SendTextAsync(FakeReceiver.Profiles);

        // Not asked at once: OpenWebRX+ counts a quick change against the client. The next
        // message after ten seconds carries the ask.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        time.Advance(OpenWebRxAudioInput.ProfileAskDelay);
        await receiver.SendTextAsync("""{"type":"smeter","value":-90.0}""");

        (await receiver.ReceiveTextAsync()).Should().Be("""{"type":"selectprofile","params":{"profile":"rtl|40m"}}""");
        await receiver.SendTextAsync(FakeReceiver.Config(centre: 7_100_000, profile: "40m"));
        (await receiver.ReceiveTextAsync()).Should().Contain("\"offset_freq\":-51200");
        (await receiver.ReceiveTextAsync()).Should().Be("""{"type":"dspcontrol","action":"start"}""");

        using OpenWebRxAudioInput input = await opening;
        input.Band.Should().Be("5.900000 to 8.300000 MHz (profile \"40m\")");
    }

    [Fact]
    public async Task Something_That_Is_Not_OpenWebRx_Is_A_Start_Up_Error()
    {
        var time = new FakeTimeProvider();
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning(), time);
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.ExpectOpeningAsync();

        // The far end says nothing at all.
        await WaitForAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return opening.IsCompleted;
        });

        Func<Task> open = () => opening;
        (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(
            "did not say where it is listening within 30 s");
    }

    [Fact]
    public async Task A_Session_That_Ends_Is_Picked_Up_Again()
    {
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning());
        FakeReceiver first = await NextReceiverAsync();
        await first.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        // Ten seconds of audio makes it a healthy session, which earns a one-second breath. Sent a
        // second at a time and each second read before the next is sent, so the 8 s buffer can
        // never fill however late the reader is scheduled, and every sample is accounted for.
        short[] tone = Tone(12000 * 10);
        byte[] pcm = new byte[tone.Length * 2];
        Buffer.BlockCopy(tone, 0, pcm, 0, pcm.Length);
        int received = 0;
        for (int chunk = 0; chunk < 10; chunk++)
        {
            await first.SendAudioAsync(pcm.AsMemory(chunk * 24000, 24000));
            received += ReadAtLeast(input, 12000).Length;
        }

        received.Should().Be(tone.Length);
        input.DroppedSamples.Should().Be(0);
        await first.CloseAsync();

        FakeReceiver second = await NextReceiverAsync();
        await second.GreetAsync(compression: "none");
        await second.SendAudioAsync(pcm.AsMemory(0, 2400));

        ReadAtLeast(input, 1200).Should().HaveCount(1200);
        _journal.Should().Contain("openwebrx: the stream from sdr.example.org:8073 ended; reconnecting");
        _journal.Should().Contain("openwebrx: reconnected to sdr.example.org:8073");
    }

    [Fact]
    public async Task A_Started_Demodulator_That_Never_Delivers_Ends_Its_Session()
    {
        var time = new FakeTimeProvider();
        Task<OpenWebRxAudioInput> opening = OpenAsync(Tuning(), time);
        FakeReceiver receiver = await NextReceiverAsync();
        await receiver.GreetAsync(compression: "none");
        using OpenWebRxAudioInput input = await opening;

        await WaitForAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return _journal.Any(l => l.Contains("no audio within 30 s of starting the demodulator", StringComparison.Ordinal));
        });

        input.SessionLive.Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(giveUp, "the condition should have come true by now");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        while (_accepted.Reader.TryRead(out FakeReceiver? receiver))
        {
            receiver.Dispose();
        }

        lock (_owned)
        {
            foreach (IDisposable owned in _owned)
            {
                owned.Dispose();
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>The server end of one session.</summary>
    private sealed class FakeReceiver(WebSocket socket) : IDisposable
    {
        public const string Profiles =
            """{"type":"profiles","value":[{"id":"rtl|40m","name":"40m"},{"id":"rtl|2m","name":"2m APRS"}]}""";

        public static string Config(long centre, string? compression = null, string? profile = null)
        {
            var value = new JsonObject
            {
                ["center_freq"] = centre,
                ["samp_rate"] = 2_400_000,
                ["sdr_id"] = "rtl",
                ["profile_id"] = profile ?? (centre < 30_000_000 ? "40m" : "2m"),
            };
            if (compression is not null)
            {
                value["audio_compression"] = compression;
            }

            return new JsonObject { ["type"] = "config", ["value"] = value }.ToJsonString();
        }

        public async Task ExpectOpeningAsync()
        {
            (await ReceiveTextAsync()).Should().Be("SERVER DE CLIENT client=pdn-soundmodem type=receiver");
            (await ReceiveTextAsync()).Should().Contain("connectionproperties");
        }

        /// <summary>The whole of an ordinary start: handshake, a 40 m receiver, and the client's
        /// placing and starting of the demodulator, which is returned.</summary>
        public async Task<IReadOnlyList<string>> GreetAsync(string compression)
        {
            await ExpectOpeningAsync();
            // In the order the server sends them: its details come first, from the base client.
            await SendTextAsync("CLIENT DE SERVER server=openwebrx version=v1.2.2");
            await SendTextAsync("""{"type":"receiver_details","value":{"receiver_name":"Test SDR","receiver_location":"Here"}}""");
            await SendTextAsync(Config(7_100_000, compression));
            await SendTextAsync(Profiles);
            return [await ReceiveTextAsync(), await ReceiveTextAsync()];
        }

        public Task SendTextAsync(string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task SendAudioAsync(ReadOnlyMemory<byte> payload)
        {
            byte[] message = [OpenWebRxProtocol.AudioMessage, .. payload.Span];
            await socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public async Task<string> ReceiveTextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            return text.ToString();
        }

        public Task CloseAsync() =>
            socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        public void Dispose() => socket.Dispose();
    }
}
