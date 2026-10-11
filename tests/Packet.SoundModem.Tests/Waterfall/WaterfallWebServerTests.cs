using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class WaterfallWebServerTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public WaterfallWebServerTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _port = FreePorts.Next();
        _server = new WaterfallWebServer(_channel, _port);
    }

    public ValueTask InitializeAsync()
    {
        _server.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _cancellation.Dispose();
    }


    private static byte[] TestFrame()
    {
        var frame = new byte[24];
        WriteAddress(frame, 0, "GB7RDG", 0, last: false);
        WriteAddress(frame, 7, "M0LTE", 9, last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        Encoding.ASCII.GetBytes("hi there").CopyTo(frame, 16);
        return frame;
    }

    /// <summary>EI0RSI-1 calling GB7RDG-2: a SABM command with P set.</summary>
    private static byte[] CallFrame()
    {
        var frame = new byte[15];
        WriteAddress(frame, 0, "GB7RDG", 2, last: false, command: true);
        WriteAddress(frame, 7, "EI0RSI", 1, last: true);
        frame[14] = 0x3F;
        return frame;
    }

    private static void WriteAddress(byte[] frame, int at, string call, int ssid, bool last, bool command = false)
    {
        for (int n = 0; n < 6; n++)
        {
            frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
        }

        frame[at + 6] = (byte)((command ? 0xE0 : 0x60) | (ssid << 1) | (last ? 1 : 0));
    }

    [Fact]
    public async Task Serves_the_embedded_page_over_http()
    {
        using var http = new HttpClient();

        string page = await http.GetStringAsync($"http://127.0.0.1:{_port}/", _cancellation.Token);

        page.Should().Contain("<!doctype html>").And.Contain("waterfall");
    }

    /// <summary>
    /// The page is served with its own version written in, and the config message announces
    /// the same version: that is how a tab left open across an upgrade finds out it is stale.
    /// </summary>
    [Fact]
    public async Task The_Page_Carries_The_Version_The_Config_Announces()
    {
        using var http = new HttpClient();
        string page = await http.GetStringAsync($"http://127.0.0.1:{_port}/", _cancellation.Token);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        string version = config.RootElement.GetProperty("page").GetString()!;
        version.Should().MatchRegex("^[0-9a-f]{12}$", "a hash of the page's text, not a number anyone has to bump");
        page.Should().Contain($"const PAGE_VERSION = \"{version}\";", "the served page must know its own version")
            .And.NotContain("__PAGE_VERSION__", "the placeholder must not reach a browser");
    }

    /// <summary>
    /// The config message also carries what this build of the daemon is (#480), so the page can
    /// show it without a second round trip. This does not assert an exact version - that changes
    /// every build - only its shape, and that it is the same value <see cref="DaemonVersion"/>
    /// itself reports, which is what proves the wire is actually reading that helper and not a
    /// second, divergent copy of the reflection call.
    /// </summary>
    [Fact]
    public async Task The_Config_Message_Carries_The_Daemon_Version()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        JsonElement daemonVersion = config.RootElement.GetProperty("daemonVersion");
        daemonVersion.GetProperty("version").GetString().Should().Be(DaemonVersion.Version);
        daemonVersion.GetProperty("release").GetBoolean().Should().Be(DaemonVersion.IsRelease);

        JsonElement commit = daemonVersion.GetProperty("commit");
        if (DaemonVersion.Commit is null)
        {
            commit.ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            commit.GetString().Should().Be(DaemonVersion.Commit)
                .And.MatchRegex("^[0-9a-f]{40}$", "the full commit the .NET SDK embeds, not a shortened form");
        }
    }

    [Fact]
    public async Task A_Captures_Audio_Can_Be_Fetched_And_Nothing_Else_Can()
    {
        // The one route that reads from disk, so the one worth being careful about. A name is not
        // a path: only the exact shape the capture writer produces, only out of the survey
        // directory. There is no authentication on this page, which makes the refusals load-bearing
        // rather than tidy-minded.
        string dir = Directory.CreateTempSubdirectory("pdnsm-captures").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "20260804-151909-862hz-unclaimed.wav"), "RIFFfake");
            File.WriteAllText(Path.Combine(dir, "20260804-151909-862hz-unclaimed.json"), "{}");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(dir)!, "secrets.wav"), "no");

            _server.SetSurveyStatus(captured: 1, skipped: 0, bytes: 8, dir);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

            (await http.GetStringAsync("/survey/20260804-151909-862hz-unclaimed.wav", _cancellation.Token))
                .Should().Be("RIFFfake");
            HttpResponseMessage sidecar =
                await http.GetAsync("/survey/20260804-151909-862hz-unclaimed.json", _cancellation.Token);
            sidecar.StatusCode.Should().Be(HttpStatusCode.OK);
            sidecar.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

            foreach (string refused in new[]
                     {
                         "/survey/../secrets.wav",
                         "/survey/%2e%2e%2fsecrets.wav",
                         "/survey/20260804-151909-862hz-unclaimed.txt",
                         "/survey/frames.db",
                         "/survey/",
                     })
            {
                HttpResponseMessage response = await http.GetAsync(refused, _cancellation.Token);
                response.StatusCode.Should().Be(
                    HttpStatusCode.NotFound, "{0} must not be served", refused);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            File.Delete(Path.Combine(Path.GetDirectoryName(dir)!, "secrets.wav"));
        }
    }

    [Fact]
    public async Task The_Survey_Status_Reaches_A_Browser_And_Only_Changes_Are_Sent()
    {
        // What a budget has been refusing is the number an operator has no other way to see.
        _server.SetSurveyStatus(captured: 7, skipped: 2, bytes: 12_582_912, "/tmp/none");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        JsonDocument first = await NextTextAsync(socket);
        first.RootElement.GetProperty("type").GetString().Should()
            .Be("survey", "a browser arriving mid-session is told where the survey has got to");
        first.RootElement.GetProperty("captured").GetInt64().Should().Be(7);
        first.RootElement.GetProperty("skipped").GetInt64().Should().Be(2);
        first.Dispose();

        _server.SetSurveyStatus(captured: 7, skipped: 2, bytes: 12_582_912, "/tmp/none");
        _server.SetSurveyStatus(captured: 8, skipped: 2, bytes: 14_000_000, "/tmp/none");

        JsonDocument next = await NextTextAsync(socket);
        next.RootElement.GetProperty("captured").GetInt64().Should()
            .Be(8, "the repeat must not have been sent");
        next.Dispose();
    }

    [Fact]
    public async Task A_Capture_Is_Reported_With_Its_Age_Rather_Than_A_Line_Index()
    {
        // The survey runs its own spectrum feed so it keeps working with nobody watching, and its
        // line clock is therefore not the display's. Seconds-ago is what both agree on.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportCapture(
            "unclaimed", centreHz: 1144, lowHz: 944, highHz: 1344,
            durationSeconds: 2.1, snrDb: 11.4, secondsAgo: 0.42,
            file: "20260804-151909-1144hz-unclaimed.wav");

        JsonDocument message = await NextTextAsync(socket);
        message.RootElement.GetProperty("type").GetString().Should().Be("capture");
        message.RootElement.GetProperty("verdict").GetString().Should().Be("unclaimed");
        message.RootElement.GetProperty("centreHz").GetDouble().Should().Be(1144);
        message.RootElement.GetProperty("secondsAgo").GetDouble().Should().Be(0.42);
        message.RootElement.GetProperty("file").GetString().Should()
            .Be("20260804-151909-1144hz-unclaimed.wav");
        message.Dispose();
    }

    [Fact]
    public async Task Unknown_paths_return_404()
    {
        using var http = new HttpClient();

        HttpResponseMessage response =
            await http.GetAsync($"http://127.0.0.1:{_port}/nope", _cancellation.Token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Measures_the_modem_band_around_its_audio_centre()
    {
        _server.Bands.Should().HaveCount(1);
        ModemBand band = _server.Bands[0];
        band.Mode.Should().Be("afsk1200");
        // Bell 202 at the 1700 Hz default centre: the 99 % OBW must bracket both tones.
        band.LowHz.Should().BeLessThan(1200);
        band.HighHz.Should().BeGreaterThan(2200);
        band.HighHz.Should().BeLessThan(4000);
        band.CentreHz.Should().BeApproximately(1700, 400);
    }

    [Fact]
    public async Task Our_Own_Transmission_Is_Drawn_But_Marked_As_Ours()
    {
        // Receive processing is gated off while transmitting, so without this the display simply
        // stops for the length of every keyup and its time axis stops meaning anything. Drawn -
        // but under its own line type, because a burst of your own must not read as a strong
        // station.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        // Transmit for real, so this exercises the path the transmitter loop actually takes.
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        using var transmitCancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), transmitCancel.Token);
        await _channel.EnqueueTransmit(0, TestFrame()).WaitAsync(TimeSpan.FromSeconds(15));

        byte[]? txLine = null;
        for (int i = 0; i < 20 && txLine is null; i++)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Binary && payload[0] == 0x03)
            {
                txLine = payload;
            }
        }

        txLine.Should().NotBeNull("a transmission must still produce waterfall lines");
        txLine!.Length.Should().Be(5 + 1024, "a transmitted line is shaped like any other");

        await transmitCancel.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Our_Own_Transmitted_Frame_Is_Listed_And_Marked_As_Ours()
    {
        // The panel was a record of half the channel: everything heard, nothing sent, so an
        // operator watching their own beacon go out had only the burst to go on. Listed after
        // the audio has left, so what is shown actually went on air, and marked so it can never
        // read as a station heard.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
        using var transmitCancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = new Channel.FakeAudioOutput(SampleRate);
        Task transmitter = _channel.RunTransmitterAsync(output, new NullPtt(), transmitCancel.Token);
        await _channel.EnqueueTransmit(0, TestFrame()).WaitAsync(TimeSpan.FromSeconds(15));

        JsonDocument? frame = null;
        for (int i = 0; i < 40 && frame is null; i++)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            JsonDocument message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetString() == "frame")
            {
                frame = message;
            }
            else
            {
                message.Dispose();
            }
        }

        frame.Should().NotBeNull("what this station sends belongs in the panel too");
        frame!.RootElement.GetProperty("tx").GetBoolean().Should().BeTrue();
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE-9");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(0);
        frame.RootElement.GetProperty("mode").GetString().Should().Be("afsk1200");
        frame.RootElement.GetProperty("lenBytes").GetInt32().Should().Be(TestFrame().Length);
        // Receive measurements, and we did not receive this: reporting them would be reporting a
        // measurement of ourselves.
        frame.RootElement.GetProperty("snrDb").ValueKind.Should().Be(JsonValueKind.Null);
        frame.RootElement.GetProperty("offsetHz").ValueKind.Should().Be(JsonValueKind.Null);
        frame.RootElement.GetProperty("crc").ValueKind.Should().Be(JsonValueKind.Null);
        frame.Dispose();

        await transmitCancel.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task A_Frame_Whose_Addresses_Will_Not_Read_Says_Why_And_Carries_Its_Bytes()
    {
        // The panel is where an operator sees the word "unattributed" - it said that and stopped,
        // which is where this whole diagnosis was missing. The frame here is the live 40 m shape:
        // decoded, CRC-valid, and its first bytes are not a shifted AX.25 address field.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        byte[] frame = [0x00, 0x01, 0x02, 0x03, .. new byte[24]];
        _channel.ProcessReceive(transmitter.Modulate(frame, txDelayMilliseconds: 100));
        _channel.ProcessReceive(new float[SampleRate / 4]);

        JsonDocument? message = null;
        while (message is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                message = JsonDocument.Parse(payload);
            }
        }

        message.RootElement.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null);
        message.RootElement.GetProperty("why").GetString().Should()
            .Contain("byte 0").And.Contain("destination");
        message.RootElement.GetProperty("hex").GetString().Should()
            .StartWith("00010203", "the bytes are what an operator wants to copy out of the panel");
        message.Dispose();
    }

    [Fact]
    public async Task An_Ordinary_Frame_Carries_No_Diagnosis_And_No_Payload()
    {
        // The diagnosis is for the frames that need explaining; every other row stays as it was,
        // and a panel full of hex would be worse than useless.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        _channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        _channel.ProcessReceive(new float[SampleRate / 4]);

        JsonDocument? message = null;
        while (message is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                message = JsonDocument.Parse(payload);
            }
        }

        message.RootElement.GetProperty("from").GetString().Should().Be("M0LTE-9");
        message.RootElement.GetProperty("why").ValueKind.Should().Be(JsonValueKind.Null);
        message.RootElement.GetProperty("hex").ValueKind.Should().Be(JsonValueKind.Null);
        message.Dispose();
    }

    [Fact]
    public async Task A_Received_Frame_Is_Not_Marked_As_Ours()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportFrame(0, "afsk1200", "M0LTE", "GB7RDG", 22, 14.0, true);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("tx").ValueKind.Should()
            .Be(JsonValueKind.Null, "only our own transmission is ours");
        frame.Dispose();
    }

    [Fact]
    public async Task A_Frame_Sent_By_Something_That_Is_Not_A_Modem_Is_Listed_As_Ours()
    {
        // ARDOP transmits as a whole virtual TNC through the channel's audio path, so the
        // channel's own transmit event - which is what lists every other mode's traffic - never
        // fires for it and the panel showed the station's own ARQ sessions as nothing at all
        // (issue #471). Reported here instead, and marked as ours so the page draws it as a
        // transmission rather than as a station we heard.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportTransmittedFrame(2, "ConReq500M", "GB7RDG", "M0LTE", 0);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("tx").GetBoolean().Should().BeTrue();
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(2);
        frame.RootElement.GetProperty("mode").GetString().Should().Be("ConReq500M");
        frame.RootElement.GetProperty("from").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("to").GetString().Should().Be("M0LTE");
        frame.RootElement.GetProperty("snrDb").ValueKind.Should().Be(
            JsonValueKind.Null, "measuring our own transmission would be inventing a measurement");
        frame.RootElement.GetProperty("crc").ValueKind.Should().Be(JsonValueKind.Null);
        frame.Dispose();
    }

    [Fact]
    public async Task A_Reported_Frame_Carries_Ardops_Own_Quality()
    {
        // ARDOP measures a 0-100 constellation quality for every frame it decodes, and until
        // #479 the panel never heard about it at all.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportFrame(2, "IDFrame", "GB7NOT", null, 0, snrDb: null, decodedOk: true, quality: 78);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("quality").GetInt32().Should().Be(78);
        frame.Dispose();
    }

    [Fact]
    public async Task A_Reported_Frame_With_No_Quality_Says_Nothing_About_It()
    {
        // Every mode but ARDOP: quality is absent rather than zero, which would read as the
        // worst reading on the scale rather than as "not measured".
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportFrame(0, "afsk1200", "M0LTE", "GB7RDG", 22, 14.0, true);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("quality").ValueKind.Should().Be(JsonValueKind.Null);
        frame.Dispose();
    }

    [Fact]
    public async Task A_Browser_Opens_On_The_Stations_Logged_Frames()
    {
        // A panel that starts empty says nothing about a channel that has been busy all morning,
        // and on a quiet band it is indistinguishable from a modem that is not working.
        var logged = new[]
        {
            new LoggedFrame(
                new DateTimeOffset(2026, 8, 4, 9, 15, 0, TimeSpan.Zero),
                0, "bpsk300-il2pc", "GB7RDG-2", "EI0RSI-1", 31, 0, true, 8.6),
            new LoggedFrame(
                new DateTimeOffset(2026, 8, 4, 9, 16, 30, TimeSpan.Zero),
                1, "afsk300-il2pc", "GB7BEX-15", "GB7IOW-1", 22, 2, false, null),
        };

        int asked = 0;
        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 5),
            FreePorts.Next(),
            new WaterfallOptions
            {
                FrameHistory = count =>
                {
                    asked = count;
                    return logged;
                },
            });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);

        (_, byte[] first) = await Receive(socket);
        using JsonDocument config = JsonDocument.Parse(first);
        config.RootElement.GetProperty("type").GetString().Should()
            .Be("config", "the config still comes first - the page needs it to render anything");

        (_, byte[] second) = await Receive(socket);
        using JsonDocument history = JsonDocument.Parse(second);
        history.RootElement.GetProperty("type").GetString().Should().Be("history");
        asked.Should().BeGreaterThan(0, "the server decides how much of the log to open with");

        JsonElement frames = history.RootElement.GetProperty("frames");
        frames.GetArrayLength().Should().Be(2);
        // Oldest first: the page prepends, so this is what puts the newest on top and leaves
        // live frames landing above the lot.
        frames[0].GetProperty("from").GetString().Should().Be("GB7RDG-2");
        frames[0].GetProperty("sub").GetInt32().Should().Be(0);
        frames[0].GetProperty("mode").GetString().Should().Be("bpsk300-il2pc");
        frames[0].GetProperty("lenBytes").GetInt32().Should().Be(31);
        frames[0].GetProperty("offsetHz").GetDouble().Should().Be(8.6);
        frames[0].GetProperty("crc").GetBoolean().Should().BeTrue();
        frames[0].GetProperty("hist").GetBoolean().Should().BeTrue();
        DateTimeOffset.Parse(frames[0].GetProperty("at").GetString()!).Should()
            .Be(logged[0].HeardAt, "a logged frame carries when it was heard, not when it was shown");

        frames[1].GetProperty("from").GetString().Should().Be("GB7BEX-15");
        frames[1].GetProperty("crc").GetBoolean().Should().BeFalse();
        frames[1].GetProperty("corrected").GetInt32().Should().Be(2);
        frames[1].GetProperty("offsetHz").ValueKind.Should().Be(
            JsonValueKind.Null, "an unmeasured offset stays unmeasured");
    }

    /// <summary>
    /// The backlog says which of its frames this station sent, so a reload shows the same TX
    /// badge a live transmission gets.
    /// </summary>
    /// <remarks>
    /// The station logs what it transmits as well as what it hears, so the panel's opening list
    /// is a record of the whole channel - and a transmission listed as an ordinary decode would
    /// have an operator reading their own beacon as somebody else's signal.
    /// </remarks>
    [Fact]
    public async Task The_Backlog_Marks_The_Stations_Own_Transmissions()
    {
        var logged = new[]
        {
            new LoggedFrame(
                new DateTimeOffset(2026, 8, 4, 9, 15, 0, TimeSpan.Zero),
                0, "bpsk300-il2pc", "GB7RDG-2", "M0LTE", 31, 0, true, 8.6),
            new LoggedFrame(
                new DateTimeOffset(2026, 8, 4, 9, 16, 30, TimeSpan.Zero),
                0, "bpsk300-il2pc", "M0LTE", "GB7RDG-2", 22, null, null, null,
                Transmitted: true),
        };

        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 5),
            FreePorts.Next(),
            new WaterfallOptions { FrameHistory = _ => logged });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);

        await Receive(socket);   // config
        (_, byte[] second) = await Receive(socket);
        using JsonDocument history = JsonDocument.Parse(second);
        JsonElement frames = history.RootElement.GetProperty("frames");

        frames[0].GetProperty("tx").ValueKind.Should().Be(
            JsonValueKind.Null, "a frame the station heard is nobody's transmission but theirs");
        frames[1].GetProperty("tx").GetBoolean().Should().BeTrue();
        frames[1].GetProperty("hist").GetBoolean().Should().BeTrue(
            "and it is still a backlog row: dimmed, and never tagged onto the waterfall");
        frames[1].GetProperty("crc").ValueKind.Should().Be(
            JsonValueKind.Null, "receive measurements stay unmeasured on our own frame");
    }

    /// <summary>
    /// The backlog says which of its frames Reed-Solomon alone stood behind, so a reload shows
    /// the same RS ONLY badge a live frame gets, with the same tooltip.
    /// </summary>
    /// <remarks>
    /// The page builds the badge from <c>plain</c> and words its tooltip from <c>monitorOnly</c>,
    /// and a backlog message carrying neither lost the badge outright - a frame nothing but RS
    /// checked, listed after a restart looking like one something had. Both fields go under the
    /// names the live <c>frame</c> message already uses, so there is one row builder and it
    /// cannot drift.
    /// </remarks>
    [Fact]
    public async Task The_Backlog_Marks_The_Frames_Reed_Solomon_Alone_Stood_Behind()
    {
        var logged = new[]
        {
            new LoggedFrame(
                new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero),
                0, "bpsk300-il2pc", "GB7BPQ", "BEACON", 46, 0, null, 27.0,
                MonitorOnly: true, PlainIl2p: true),
            new LoggedFrame(
                new DateTimeOffset(2026, 9, 3, 11, 1, 0, TimeSpan.Zero),
                0, "bpsk300-il2pc", "GB7RDG-2", "M0LTE", 31, 0, true, 8.6),
        };

        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 5),
            FreePorts.Next(),
            new WaterfallOptions { FrameHistory = _ => logged });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);

        await Receive(socket);   // config
        (_, byte[] second) = await Receive(socket);
        using JsonDocument history = JsonDocument.Parse(second);
        JsonElement frames = history.RootElement.GetProperty("frames");

        frames[0].GetProperty("plain").GetBoolean().Should().BeTrue(
            "the badge is drawn from this, and a backlogged RS-only row must keep it");
        frames[0].GetProperty("monitorOnly").GetBoolean().Should().BeTrue(
            "and the tooltip says whether the host was given the frame");
        frames[0].GetProperty("crc").ValueKind.Should().Be(
            JsonValueKind.Null, "no CRC was checked - which is not the same question");

        frames[1].GetProperty("plain").ValueKind.Should().Be(
            JsonValueKind.Null, "a CRC stood behind this one, and absent means no");
        frames[1].GetProperty("monitorOnly").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_Station_With_No_Frame_Log_Sends_No_Backlog()
    {
        // This fixture's server has no FrameHistory: the next message after config must be an
        // ordinary one, not an empty history the page would clear its panel for.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.ReportFrame(0, "afsk1200", "M0LTE", "GB7RDG", 22, 14.0, true);

        JsonDocument? next = null;
        while (next is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                next = JsonDocument.Parse(payload);
            }
        }

        next.RootElement.GetProperty("type").GetString().Should().Be("frame");
        next.Dispose();
    }

    [Fact]
    public async Task A_Log_That_Cannot_Be_Read_Costs_The_Browser_Its_Backlog_And_Nothing_Else()
    {
        // The station is still decoding; a browser losing its opening list is not a reason to
        // drop its connection.
        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 5),
            FreePorts.Next(),
            new WaterfallOptions { FrameHistory = _ => throw new InvalidOperationException("disk") });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);

        (_, byte[] first) = await Receive(socket);
        using JsonDocument config = JsonDocument.Parse(first);
        config.RootElement.GetProperty("type").GetString().Should().Be("config");
        socket.State.Should().Be(WebSocketState.Open, "the connection survives an unreadable log");
    }

    [Fact]
    public async Task Receive_Audio_Is_Sent_Only_To_A_Browser_That_Asked_For_It()
    {
        // Opening the page to look at a waterfall must not quietly start pulling ~24 KB/s, and
        // several viewers must not each cost that unless they each asked.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var tone = new float[SampleRate];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = 0.3f * MathF.Sin(2 * MathF.PI * 1700 * n / SampleRate);
        }

        _channel.ProcessReceive(tone);
        var seenBefore = new List<byte>();
        for (int i = 0; i < 5; i++)
        {
            (_, byte[] payload) = await Receive(socket);
            seenBefore.Add(payload[0]);
        }

        seenBefore.Should().NotContain((byte)0x02, "audio is off until the browser asks");

        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes("""{"type":"audio","on":true}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);

        // Keep feeding and reading until audio appears: the request is applied by the server's
        // receive loop asynchronously, and the per-client queue drops oldest when it backs up,
        // so a single shot can race either way.
        byte[]? audio = null;
        for (int round = 0; round < 20 && audio is null; round++)
        {
            await Task.Delay(50);
            _channel.ProcessReceive(tone);
            for (int i = 0; i < 20 && audio is null; i++)
            {
                (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
                if (kind == WebSocketMessageType.Binary && payload[0] == 0x02)
                {
                    audio = payload;
                }
            }
        }

        audio.Should().NotBeNull("audio must flow once asked for");
        // [0x02][pad×3][s16 LE mono], 40 ms at the channel rate.
        (audio!.Length - WaterfallWebServer.AudioHeaderBytes).Should().Be(SampleRate * 40 / 1000 * 2);
        // The header must keep the samples on a 2-byte boundary: a browser views them as an
        // Int16Array, which throws outright on an unaligned offset. A 1-byte header did.
        (WaterfallWebServer.AudioHeaderBytes % 2).Should().Be(
            0, "an unaligned payload cannot be read by the client at all");
    }

    [Fact]
    public async Task Streams_config_lines_and_frame_events_over_the_websocket()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);

        // 1: the config message arrives first
        (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Text);
        using JsonDocument config = JsonDocument.Parse(payload);
        config.RootElement.GetProperty("type").GetString().Should().Be("config");
        config.RootElement.GetProperty("sampleRate").GetInt32().Should().Be(SampleRate);
        config.RootElement.GetProperty("linesPerSecond").GetInt32().Should().Be(30);
        config.RootElement.GetProperty("modems").GetArrayLength().Should().Be(1);

        // 2: audio produces binary spectrum lines [0x01][u32 index][bins]
        var tone = new float[SampleRate];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = 0.3f * MathF.Sin(2 * MathF.PI * 1700 * n / SampleRate);
        }

        _channel.ProcessReceive(tone);
        (kind, payload) = await Receive(socket);
        kind.Should().Be(WebSocketMessageType.Binary);
        payload[0].Should().Be(0x01);
        payload.Length.Should().Be(5 + 1024);

        // 3: a decoded frame produces an attributed frame event
        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        _channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        _channel.ProcessReceive(new float[SampleRate / 4]);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (kind, payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            frame = JsonDocument.Parse(payload);
        }

        frame.RootElement.GetProperty("type").GetString().Should().Be("frame");
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE-9");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(0);
        frame.RootElement.GetProperty("mode").GetString().Should().Be("afsk1200");
        frame.RootElement.GetProperty("snrDb").GetDouble().Should().BeGreaterThan(3);
        frame.RootElement.GetProperty("burstLines").GetInt32().Should().BeGreaterThan(5);
        frame.Dispose();
    }

    [Fact]
    public async Task Transmit_Metering_Reaches_The_Page_And_Is_Averaged_On_Key_Up()
    {
        // Forward power and SWR live while keyed, and the transmission's average once it is over -
        // held, not cleared. A burst is a fraction of a second and the next one may be a quarter
        // of an hour away, so a readout that existed only during the keyup was never read.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // config

        _server.SetTransmitReading(29.4, 1.2);
        JsonDocument keyed = await NextTextAsync(socket);
        keyed.RootElement.GetProperty("type").GetString().Should().Be("tx");
        keyed.RootElement.GetProperty("keyed").GetBoolean().Should().BeTrue();
        keyed.RootElement.GetProperty("watts").GetDouble().Should().Be(29.4);
        keyed.RootElement.GetProperty("swr").GetDouble().Should().Be(1.2);
        keyed.RootElement.GetProperty("at").ValueKind.Should().Be(
            JsonValueKind.Null, "a live reading is now, and needs no timestamp");
        keyed.Dispose();

        _server.SetTransmitReading(25.6, 1.6);
        (await NextTextAsync(socket)).Dispose();

        _server.SetTransmitReading(null, null);
        JsonDocument held = await NextTextAsync(socket);
        held.RootElement.GetProperty("keyed").GetBoolean().Should().BeFalse();
        held.RootElement.GetProperty("watts").GetDouble().Should().Be(
            27.5, "the held figure is the mean of the samples, not whichever arrived last");
        held.RootElement.GetProperty("swr").GetDouble().Should().Be(1.4);
        held.RootElement.GetProperty("at").GetDateTimeOffset().Should().BeCloseTo(
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            "a held reading has to say when it was taken, or it reads as one from just now");
        held.Dispose();

        // And the next transmission starts a fresh average rather than continuing the last one.
        _server.SetTransmitReading(10.0, 3.0);
        (await NextTextAsync(socket)).Dispose();
        _server.SetTransmitReading(null, null);
        JsonDocument second = await NextTextAsync(socket);
        second.RootElement.GetProperty("watts").GetDouble().Should().Be(10.0);
        second.RootElement.GetProperty("swr").GetDouble().Should().Be(3.0);
        second.Dispose();
    }

    [Fact]
    public async Task An_Idle_Transmitter_Does_Not_Wipe_The_Reading_It_Left_Behind()
    {
        // The meters go on reporting nothing for as long as the station is not transmitting. Only
        // the first of those is a key-up; the rest must leave the held average alone, or the
        // readout the operator is meant to be reading disappears the moment it appears.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);

        _server.SetTransmitReading(29.4, 1.2);
        (await NextTextAsync(socket)).Dispose();
        _server.SetTransmitReading(null, null);
        (await NextTextAsync(socket)).Dispose();

        // Two idle reports that must produce nothing, then a real one to bound the wait.
        _server.SetTransmitReading(null, null);
        _server.SetTransmitReading(null, null);
        _server.SetRadioStatus("GPSDO locked");

        JsonDocument next = await NextTextAsync(socket);
        next.RootElement.GetProperty("type").GetString().Should()
            .Be("radio", "an idle transmitter has nothing new to say");
        next.Dispose();
    }

    [Fact]
    public async Task An_Unchanged_Transmit_Reading_Is_Not_Rebroadcast()
    {
        // The meters update many times a second; only changes are worth a message.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);

        _server.SetTransmitReading(29.4, null);
        (await NextTextAsync(socket)).Dispose();

        // Same to a tenth of a watt, which is all the readout shows: not a change.
        _server.SetTransmitReading(29.44, null);
        _server.SetTransmitReading(30.1, null);

        JsonDocument next = await NextTextAsync(socket);
        next.RootElement.GetProperty("watts").GetDouble().Should()
            .Be(30.1, "the repeat must not have been sent");
        next.Dispose();
    }

    [Fact]
    public async Task A_Browser_Opening_Between_Transmissions_Is_Told_What_The_Last_One_Did()
    {
        // The whole point of holding the reading: the gaps are long, and a page opened in one of
        // them would otherwise say nothing at all about a transmitter that is working perfectly.
        _server.SetTransmitReading(29.4, 1.2);
        _server.SetTransmitReading(null, null);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // config

        JsonDocument held = await NextTextAsync(socket);
        held.RootElement.GetProperty("type").GetString().Should().Be("tx");
        held.RootElement.GetProperty("keyed").GetBoolean().Should().BeFalse();
        held.RootElement.GetProperty("watts").GetDouble().Should().Be(29.4);
        held.Dispose();
    }

    [Fact]
    public async Task Which_Kiss_Ports_Have_A_Host_Reaches_The_Page_And_Late_Joiners()
    {
        // A node that quietly drops its TCP session stops passing traffic, and from the modem's
        // side that is indistinguishable from a quiet band. The page carries it as state, so a
        // browser opened at any time - not just one that was watching when it happened - can see
        // whether anything is attached.
        _server.SetHostPorts([
            new HostPortStatus(8105, null, 1),
            new HostPortStatus(8101, 0, 0),
        ]);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // config

        JsonDocument hosts = await NextTextAsync(socket);
        hosts.RootElement.GetProperty("type").GetString().Should().Be("hosts");
        JsonElement ports = hosts.RootElement.GetProperty("ports");
        ports.GetArrayLength().Should().Be(2);
        ports[0].GetProperty("port").GetInt32().Should().Be(8101, "ports are listed in order");
        ports[0].GetProperty("sub").GetInt32().Should().Be(0);
        ports[0].GetProperty("clients").GetInt32().Should().Be(0);
        ports[1].GetProperty("port").GetInt32().Should().Be(8105);
        ports[1].GetProperty("sub").ValueKind.Should().Be(
            JsonValueKind.Null, "the multiplexed port reaches every modem");
        ports[1].GetProperty("clients").GetInt32().Should().Be(1);
        hosts.Dispose();

        // A snapshot that says nothing new is not sent; one that does, is.
        _server.SetHostPorts([
            new HostPortStatus(8105, null, 1),
            new HostPortStatus(8101, 0, 0),
        ]);
        _server.SetHostPorts([
            new HostPortStatus(8105, null, 1),
            new HostPortStatus(8101, 0, 2),
        ]);

        JsonDocument changed = await NextTextAsync(socket);
        changed.RootElement.GetProperty("ports")[0].GetProperty("clients").GetInt32().Should()
            .Be(2, "the repeat must not have been sent");
        changed.Dispose();
    }

    /// <summary>
    /// The next text message off a page's socket that says something. The server's keep-alive is
    /// skipped: it arrives every twenty seconds on any socket held that long (#411), it is a
    /// question rather than news, and no test here is about it.
    /// </summary>
    private async Task<JsonDocument> NextTextAsync(ClientWebSocket socket)
    {
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(payload);
            if (message.RootElement.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "ping")
            {
                message.Dispose();
                continue;
            }

            return message;
        }
    }

    [Fact]
    public async Task A_Frame_From_A_Demodulator_Outside_The_Channel_Is_Listed_Like_Any_Other()
    {
        // ARDOP demodulates inside its virtual TNC, so its frames never raise the channel event
        // every other modem's do. Before this the ARDOP band was drawn and its bursts painted,
        // and the decode panel stayed empty for it forever.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // the config message

        _server.ReportFrame(
            subChannel: 2, mode: "ConReq500M", from: "M0LTE", to: "GB7RDG",
            lengthBytes: 22, snrDb: 14.0, decodedOk: true);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("type").GetString().Should().Be("frame");
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(2);
        frame.RootElement.GetProperty("mode").GetString().Should()
            .Be("ConReq500M", "with ARDOP the frame type is most of what the entry says");
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("lenBytes").GetInt32().Should().Be(22);
        frame.RootElement.GetProperty("snrDb").GetDouble().Should()
            .Be(14.0, "the demodulator's own report beats anything the band tracker can infer");
        frame.RootElement.GetProperty("crc").GetBoolean().Should().BeTrue();
        frame.Dispose();
    }

    [Fact]
    public async Task An_Id_Beacon_Is_Reported_As_One_So_Both_Views_Can_Say_What_It_Is()
    {
        // A NinoTNC idents in 300 AFSK alongside its PSK data mode. The message carries the flag
        // the page uses to badge it in the panel and to write "ID" onto its burst - without it an
        // ident and a data frame on the same slot read identically.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // the config message

        _server.ReportIdBeacon(
            subChannel: 1, mode: "afsk300-multi11", from: "KK4HEJ", to: "IDENT",
            lengthBytes: 17, offsetHz: -35.0);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("type").GetString().Should().Be("frame");
        frame.RootElement.GetProperty("id").GetBoolean().Should()
            .BeTrue("this is what the page reads to badge it and mark its tag");
        frame.RootElement.GetProperty("sub").GetInt32().Should()
            .Be(1, "the ident is labelled with the modem it rode in on");
        frame.RootElement.GetProperty("mode").GetString().Should()
            .Be("afsk300-multi11", "the mode it was actually sent in, not the mode it accompanies");
        frame.RootElement.GetProperty("from").GetString().Should().Be("KK4HEJ");
        frame.RootElement.GetProperty("to").GetString().Should().Be("IDENT");
        frame.RootElement.GetProperty("lenBytes").GetInt32().Should().Be(17);
        frame.RootElement.GetProperty("snrDb").ValueKind.Should()
            .Be(JsonValueKind.Null, "a ghost has no band tracker to measure one from");
        frame.RootElement.GetProperty("offsetHz").GetDouble().Should()
            .Be(-35.0, "the ghost measures the identifying station's carrier against its own centre");
        frame.Dispose();
    }

    [Fact]
    public async Task An_Ordinary_Frame_Carries_No_Id_Flag()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        (_, _) = await Receive(socket);   // the config message

        _server.ReportFrame(2, "ConReq500M", "M0LTE", "GB7RDG", 22, 14.0, true);

        JsonDocument? frame = null;
        while (frame is null)
        {
            (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
            if (kind == WebSocketMessageType.Text)
            {
                frame = JsonDocument.Parse(payload);
            }
        }

        frame.RootElement.GetProperty("id").ValueKind.Should()
            .Be(JsonValueKind.Null, "only an ident is an ident");
        frame.Dispose();
    }

    [Fact]
    public async Task Reporting_A_Frame_Before_The_Server_Starts_Is_Harmless()
    {
        // Start order is the daemon's business, not a reason to crash a receive thread.
        await using var unstarted = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 3), FreePorts.Next());

        Action report = () => unstarted.ReportFrame(2, "IDFrame", "M0LTE", null, 0, null, true);
        Action beacon = () => unstarted.ReportIdBeacon(1, "afsk300-multi11", "KK4HEJ", "IDENT", 17);

        report.Should().NotThrow();
        beacon.Should().NotThrow();
    }

    /// <summary>
    /// A heard AX.25 frame is told to the browser twice: once as the flat row it always was, and
    /// once as what it meant for the pair of stations it passed between.
    /// </summary>
    /// <remarks>
    /// The links pane groups frames by who was talking to whom on which modem, and says in
    /// words what each frame did. The flat panel stays exactly as it was, so a browser that
    /// never opens the pane sees nothing different.
    /// </remarks>
    [Fact]
    public async Task A_Heard_Frame_Is_Narrated_On_The_Link_It_Belongs_To()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        _channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        _channel.ProcessReceive(new float[SampleRate / 4]);

        // The flat row first, as ever; then the link.
        JsonDocument? link = null;
        var seen = new List<string>();
        while (link is null)
        {
            JsonDocument message = await NextTextAsync(socket);
            string type = message.RootElement.GetProperty("type").GetString()!;
            seen.Add(type);
            if (type == "link")
            {
                link = message;
            }
            else
            {
                message.Dispose();
            }
        }

        // The row the panel already lists comes first, and nothing else came between.
        seen.Should().Equal("frame", "link");

        JsonElement card = link.RootElement.GetProperty("link");
        card.GetProperty("id").GetString().Should().Be("0|GB7RDG<>M0LTE-9", "one link per pair per modem");
        card.GetProperty("sub").GetInt32().Should().Be(0);
        card.GetProperty("a").GetString().Should().Be("M0LTE-9", "the first station heard is side A");
        card.GetProperty("b").GetString().Should().Be("GB7RDG");
        card.GetProperty("state").GetString().Should().Be("unconnected", "a UI frame makes no connection");
        card.GetProperty("ab").GetProperty("frames").GetInt32().Should().Be(1);
        card.GetProperty("ba").GetProperty("frames").GetInt32().Should().Be(0);
        card.GetProperty("concern").ValueKind.Should().Be(JsonValueKind.Null, "nothing is wrong with a beacon");
        card.GetProperty("recent").ValueKind.Should().Be(
            JsonValueKind.Null, "the live message carries the one frame, not the whole backlog again");

        JsonElement evt = link.RootElement.GetProperty("event");
        evt.GetProperty("kind").GetString().Should().Be("UI");
        evt.GetProperty("from").GetString().Should().Be("M0LTE-9");
        evt.GetProperty("to").GetString().Should().Be("GB7RDG");
        evt.GetProperty("len").GetInt32().Should().Be(8);
        evt.GetProperty("text").GetString().Should().Be("hi there", "printable text is shown as it was sent");
        evt.GetProperty("say").GetString().Should().Be("unconnected");
        evt.GetProperty("tx").ValueKind.Should().Be(JsonValueKind.Null, "somebody else sent this one");
        evt.GetProperty("flags").ValueKind.Should().Be(JsonValueKind.Null);
        link.Dispose();
    }

    /// <summary>
    /// A browser that connects after the station has been listening opens on every link it
    /// knows about, each with the frames that made it what it is.
    /// </summary>
    /// <remarks>
    /// The pane would otherwise open empty on a busy channel, and a connection that has been up
    /// for an hour would appear to begin with whatever frame happened to come next.
    /// </remarks>
    [Fact]
    public async Task A_Browser_Opens_On_The_Links_The_Station_Already_Knows()
    {
        var heardAt = new DateTimeOffset(2026, 9, 2, 10, 15, 0, TimeSpan.Zero);
        _server.Links.Observe("0", TestFrame(), heardAt);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);

        (_, byte[] first) = await Receive(socket);
        using JsonDocument config = JsonDocument.Parse(first);
        config.RootElement.GetProperty("type").GetString().Should()
            .Be("config", "the config still comes first");

        using JsonDocument links = await NextTextAsync(socket);
        links.RootElement.GetProperty("type").GetString().Should().Be("links");

        JsonElement cards = links.RootElement.GetProperty("links");
        cards.GetArrayLength().Should().Be(1);
        JsonElement card = cards[0];
        card.GetProperty("id").GetString().Should().Be("0|GB7RDG<>M0LTE-9");
        card.GetProperty("state").GetString().Should().Be("unconnected");
        DateTimeOffset.Parse(card.GetProperty("last").GetString()!).Should()
            .Be(heardAt, "a card says when its pair was last heard, not when the browser arrived");

        JsonElement backlog = card.GetProperty("recent");
        backlog.GetArrayLength().Should().Be(1, "the card opens with the frames behind it");
        JsonElement recent = backlog[0];
        recent.GetProperty("from").GetString().Should().Be("M0LTE-9");
        recent.GetProperty("say").GetString().Should().Be("unconnected");
        recent.GetProperty("text").GetString().Should().Be("hi there");
    }

    /// <summary>
    /// A call nothing answers is given up on after a while, and the browser is told the same
    /// way it is told about a frame: the card as it now stands and a line for its feed.
    /// </summary>
    /// <remarks>
    /// The observer learns the time only from the frames it is handed, and a call nobody answers
    /// is followed by no frame. On GB7RDG-2 that left a card saying "calling" thirteen minutes
    /// after the one SABM behind it. The server's clock asks the observer to give up on such
    /// calls every few seconds.
    /// </remarks>
    [Fact]
    public async Task A_Call_Nothing_Answers_Is_Given_Up_On_And_The_Browser_Told()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 10, 4, 17, TimeSpan.Zero));
        await using var server = new WaterfallWebServer(
            new SoundModemChannel(SampleRate, randomSeed: 5), FreePorts.Next(), new WaterfallOptions { TimeProvider = clock });
        server.Start();
        server.Links.Observe("0", CallFrame(), clock.GetUtcNow());

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Url.Split(':')[^1].TrimEnd('/')}/ws"), _cancellation.Token);
        await Receive(socket);   // config
        using (JsonDocument links = await NextTextAsync(socket))
        {
            links.RootElement.GetProperty("links")[0].GetProperty("state").GetString().Should().Be("calling");
        }

        // Three minutes, plus the clock's own period: nothing is said until the call is given up
        // on. The browser answers the server's keep-alive as it goes, because a page that says
        // nothing for a minute stops being counted and has its socket dropped (#411).
        await WatchingFor(socket, clock, TimeSpan.FromMinutes(3) + WaterfallWebServer.LinkExpiryPeriod);

        using JsonDocument message = await NextTextAsync(socket);
        message.RootElement.GetProperty("type").GetString().Should().Be("link");
        JsonElement card = message.RootElement.GetProperty("link");
        card.GetProperty("id").GetString().Should().Be("0|EI0RSI-1<>GB7RDG-2");
        card.GetProperty("state").GetString().Should().Be("disconnected", "the call has failed");
        card.GetProperty("concern").ValueKind.Should().Be(JsonValueKind.Null, "nothing is waiting any more");

        JsonElement evt = message.RootElement.GetProperty("event");
        evt.GetProperty("kind").ValueKind.Should().Be(JsonValueKind.Null, "no frame was behind this line");
        evt.GetProperty("from").GetString().Should().Be("EI0RSI-1", "the station that was waiting");
        evt.GetProperty("to").GetString().Should().Be("GB7RDG-2");
        evt.GetProperty("say").GetString().Should().Be("got no answer in 3 minutes; the call has failed");
        evt.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Equal("timeout");
        evt.GetProperty("state").GetString().Should().Be("disconnected");
        DateTimeOffset.Parse(evt.GetProperty("at").GetString()!).Should()
            .Be(new DateTimeOffset(2026, 9, 3, 10, 7, 17, TimeSpan.Zero), "timed at the moment the wait ran out");
    }

    [Fact]
    public async Task A_Browser_With_Nothing_Heard_Yet_Gets_No_Empty_Links_Message()
    {
        // The pane draws its own "nothing heard yet" note; an empty list would only make it
        // clear a panel that was already clear, on every reconnect.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        _server.SetRadioStatus("bounding the wait");

        using JsonDocument next = await NextTextAsync(socket);
        next.RootElement.GetProperty("type").GetString().Should().Be("radio", "there were no links to send");
    }

    [Fact]
    public async Task The_Links_Pane_Has_A_Page_Of_Its_Own_To_Detach_Into()
    {
        // The same page, served at a second path: it reads its own address and opens as the
        // links pane alone, for a browser window with no waterfall behind it.
        using var http = new HttpClient();

        string page = await http.GetStringAsync($"http://127.0.0.1:{_port}/links", _cancellation.Token);

        page.Should().Contain("<!doctype html>").And.Contain("linkCards");
    }

    /// <summary>
    /// A browser that says it does not want the spectrum stops getting it, and goes on getting
    /// everything else.
    /// </summary>
    /// <remarks>
    /// The detached links window has no waterfall to paint, and thirty binary lines a second
    /// for nothing would be the cost of leaving it open. Text messages, the links among them,
    /// must keep coming or the window is pointless.
    /// </remarks>
    [Fact]
    public async Task A_Browser_That_Declines_The_Spectrum_Stops_Getting_It_And_Nothing_Else()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(socket);   // config

        var tone = new float[SampleRate];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = 0.3f * MathF.Sin(2 * MathF.PI * 1700 * n / SampleRate);
        }

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"spectrum","on":false}"""),
            WebSocketMessageType.Text, true, _cancellation.Token);

        // The request is applied by the server's receive loop asynchronously, so lines already
        // queued, or produced before it lands, may still arrive. Each round feeds a second of
        // tone and then a text message to read up to; the opt-out has taken once a whole round
        // arrives with no spectrum line in it, and it must then hold for the round after.
        int cleanRounds = 0;
        for (int round = 0; round < 20 && cleanRounds < 2; round++)
        {
            await Task.Delay(50);
            _channel.ProcessReceive(tone);
            _server.SetRadioStatus($"round {round}");

            bool spectrum = false;
            bool marker = false;
            while (!marker)
            {
                (WebSocketMessageType kind, byte[] payload) = await Receive(socket);
                if (kind == WebSocketMessageType.Binary)
                {
                    spectrum |= payload[0] == 0x01;
                    continue;
                }

                using JsonDocument text = JsonDocument.Parse(payload);
                marker = text.RootElement.GetProperty("type").GetString() == "radio"
                    && text.RootElement.GetProperty("status").GetString() == $"round {round}";
            }

            cleanRounds = spectrum ? 0 : cleanRounds + 1;
        }

        cleanRounds.Should().Be(2, "the spectrum must stop once declined, and stay stopped");
    }

    [Fact]
    public async Task Viewers_Are_Counted_As_Browsers_Come_And_Go()
    {
        // The count is what an on-demand receiver runs on: a session while it is above zero,
        // none once it has been zero for the linger.
        var counts = new List<int>();
        _server.ViewersChanged += n => { lock (counts) counts.Add(n); };

        using var first = new ClientWebSocket();
        await first.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(first);   // config: the page is attached by the time it arrives
        _server.Viewers.Should().Be(1);

        using var second = new ClientWebSocket();
        await second.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        await Receive(second);
        _server.Viewers.Should().Be(2);

        // Waited for on the event rather than on the property. The two are set one after the
        // other and not under one lock, so a wait on the count can win the race to the assertion
        // below by the width of those few statements - which it did, once, in a full run.
        int reported() { lock (counts) return counts.Count; }

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => reported() == 3, "the closed page is no longer a viewer");
        _server.Viewers.Should().Be(1);

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cancellation.Token);
        await Until(() => reported() == 4, "the last page leaving reports nobody");
        _server.Viewers.Should().Be(0);

        lock (counts)
        {
            counts.Should().Equal([1, 2, 1, 0], "every change is reported, with the new count");
        }
    }

    [Fact]
    public async Task A_Viewer_Count_Subscriber_That_Throws_Costs_The_Page_Nothing()
    {
        _server.ViewersChanged += _ => throw new InvalidOperationException("not the page's problem");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        config.RootElement.GetProperty("type").GetString().Should().Be("config");
        socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task An_Operators_Page_Is_Not_Dressed_For_The_Public()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        config.RootElement.GetProperty("publicMonitor").GetBoolean().Should().BeFalse();
        config.RootElement.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        config.RootElement.GetProperty("receiver").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_Operators_Page_Is_Told_Which_Programs_Listen_Through_The_Audio_Stream()
    {
        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), _cancellation.Token);
        using (JsonDocument config = await NextTextAsync(page))
        {
            config.RootElement.GetProperty("listeners").GetArrayLength().Should().Be(0);
        }

        using var reader = new ClientWebSocket();
        await reader.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await reader.SendAsync(
            """{"name":"pdn-mailcast-receiver","pagePort":8130}"""u8.ToArray(),
            WebSocketMessageType.Text, true, _cancellation.Token);

        JsonElement listeners = default;
        for (int i = 0; i < 50 && listeners.ValueKind == JsonValueKind.Undefined; i++)
        {
            using JsonDocument message = await NextTextAsync(page);
            if (message.RootElement.GetProperty("type").GetString() == "listeners")
            {
                listeners = message.RootElement.GetProperty("listeners").Clone();
            }
        }

        listeners.GetArrayLength().Should().Be(1);
        listeners[0].GetProperty("name").GetString().Should().Be("pdn-mailcast-receiver");
        listeners[0].GetProperty("pagePort").GetInt32().Should().Be(8130);
    }

    [Fact]
    public async Task A_Public_Page_Is_Never_Told_Which_Programs_Listen_Through_The_Audio_Stream()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(_channel, port, new WaterfallOptions { Public = true });
        server.Start();

        using var reader = new ClientWebSocket();
        await reader.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}{ChannelAudioStream.Path}"), _cancellation.Token);
        await reader.SendAsync(
            """{"name":"pdn-mailcast-receiver","pagePort":8130}"""u8.ToArray(),
            WebSocketMessageType.Text, true, _cancellation.Token);
        for (int i = 0; i < 100 && server.ChannelAudio.Listeners.Count == 0; i++)
        {
            await Task.Delay(20, _cancellation.Token);
        }

        server.ChannelAudio.Listeners.Should().HaveCount(1, "the reader did name itself");

        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(page);

        config.RootElement.GetProperty("listeners").ValueKind.Should().Be(JsonValueKind.Null,
            "programs on the station's own machine are not a visitor's business");
    }

    [Fact]
    public async Task A_Public_Page_Is_Told_Its_Title_And_Whose_Receiver_It_Listens_Through()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(_channel, port, new WaterfallOptions
        {
            Public = true,
            Title = "40 m packet monitor",
            About = "The 7050-7052 kHz packet window, receive only.",
        });
        server.SetReceiver("M9PSY-1, Dalgety Bay, Scotland, UK", "https://m9psy-1.instance.ubersdr.org/");
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        JsonElement root = config.RootElement;
        root.GetProperty("publicMonitor").GetBoolean().Should().BeTrue();
        root.GetProperty("title").GetString().Should().Be("40 m packet monitor");
        root.GetProperty("about").GetString().Should().Be("The 7050-7052 kHz packet window, receive only.");
        root.GetProperty("receiver").GetString().Should().Be("M9PSY-1, Dalgety Bay, Scotland, UK",
            "it is somebody else's receiver, and the page credits them");
        root.GetProperty("receiverUrl").GetString().Should().Be("https://m9psy-1.instance.ubersdr.org/");
    }

    /// <summary>
    /// Moves the clock on with a browser watching, answering the server's keep-alive as the page
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>One jump would be simpler and is no longer honest: the server stops counting a page
    /// that has said nothing for <see cref="WaterfallWebServer.KeepAliveSilence"/> and drops its
    /// socket (#411), so a test that jumped a silent socket over that deadline would be watching
    /// a page the server had rightly given up on.</para>
    /// <para>An answer every <see cref="WaterfallWebServer.KeepAlivePeriod"/>, which is twelve per
    /// deadline, and a millisecond of real sleep with it: a fake clock and a real socket keep
    /// different time, and a send to loopback completes without ever yielding the thread, so
    /// without the sleep this loop can run its whole span - minutes of clock in a millisecond of
    /// wall time - before the server gets a turn at any of the answers.</para>
    /// </remarks>
    private async Task WatchingFor(ClientWebSocket socket, FakeTimeProvider clock, TimeSpan span)
    {
        for (TimeSpan gone = TimeSpan.Zero; gone < span; gone += WaterfallWebServer.KeepAlivePeriod)
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"pong\"}"),
                WebSocketMessageType.Text, true, _cancellation.Token);
            await Task.Delay(1, _cancellation.Token);
            TimeSpan step = span - gone;
            clock.Advance(step < WaterfallWebServer.KeepAlivePeriod
                ? step
                : WaterfallWebServer.KeepAlivePeriod);
        }
    }

    private async Task Until(Func<bool> condition, string because)
    {
        while (!condition())
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, _cancellation.Token);
        }

        condition().Should().BeTrue(because);
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        int filled = 0;
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, filled, buffer.Length - filled), _cancellation.Token);
            filled += result.Count;
            if (result.EndOfMessage)
            {
                return (result.MessageType, buffer[..filled]);
            }
        }
    }
}
