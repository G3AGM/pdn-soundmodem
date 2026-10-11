using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Updates;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// What the station page is told about a newer pdn-soundmodem in the apt repository (issue
/// #583): carried in the config message for a page that connects after the check already ran,
/// pushed live to a page that was already open when a later check finds one, and never sent to
/// a public page at all.
/// </summary>
public class WaterfallUpdateNoticeTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private readonly SoundModemChannel _channel;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public WaterfallUpdateNoticeTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>An already-resolved check: real network code never runs in these tests.</summary>
    private static UpdateCheck Resolved(bool newer, bool fromAptRepo = true)
    {
        var check = new UpdateCheck(
            TimeProvider.System,
            _ => { },
            "0.88.0",
            "amd64",
            _ => Task.FromResult(
                $"""
                Package: pdn-soundmodem
                Version: {(newer ? "0.89.0" : "0.88.0")}
                Architecture: amd64

                """),
            () => fromAptRepo);
        check.CheckOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        return check;
    }

    [Fact]
    public async Task A_Page_That_Connects_After_The_Check_Ran_Is_Told_In_Its_Config()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            _channel, port, new WaterfallOptions { Updates = Resolved(newer: true) });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        JsonElement update = config.RootElement.GetProperty("update");
        update.GetProperty("installed").GetString().Should().Be("0.88.0");
        update.GetProperty("available").GetString().Should().Be("0.89.0");
        update.GetProperty("release").GetString().Should()
            .Be("https://github.com/packet-net/pdn-soundmodem/releases/tag/v0.89.0");
        update.GetProperty("command").GetString().Should()
            .Be("sudo apt update && sudo apt install --only-upgrade pdn-soundmodem");
    }

    [Fact]
    public async Task A_Station_Up_To_Date_Carries_No_Update_In_Its_Config()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            _channel, port, new WaterfallOptions { Updates = Resolved(newer: false) });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        config.RootElement.GetProperty("update").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_Public_Page_Is_Never_Told_About_A_Newer_Version()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            _channel, port, new WaterfallOptions { Public = true, Updates = Resolved(newer: true) });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        config.RootElement.GetProperty("update").ValueKind.Should().Be(JsonValueKind.Null,
            "the operator's own software is not a visitor's business");
    }

    [Fact]
    public async Task Not_Installed_From_The_Apt_Repo_Is_Told_The_Docs_Link_Instead_Of_A_Command()
    {
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(
            _channel, port, new WaterfallOptions { Updates = Resolved(newer: true, fromAptRepo: false) });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using JsonDocument config = await NextTextAsync(socket);

        JsonElement update = config.RootElement.GetProperty("update");
        update.GetProperty("command").ValueKind.Should().Be(JsonValueKind.Null);
        update.GetProperty("setupDocs").GetString().Should().Contain("docs/01-install.md");
    }

    [Fact]
    public async Task A_Page_Already_Open_Is_Told_Live_When_A_Later_Check_Finds_A_Newer_Version()
    {
        // Nothing found yet - the daemon's real UpdateCheck before its first look completes.
        var check = new UpdateCheck(
            TimeProvider.System,
            _ => { },
            "0.88.0",
            "amd64",
            _ => Task.FromResult(
                """
                Package: pdn-soundmodem
                Version: 0.89.0
                Architecture: amd64

                """),
            () => true);

        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(_channel, port, new WaterfallOptions { Updates = check });
        server.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        using (JsonDocument config = await NextTextAsync(socket))
        {
            config.RootElement.GetProperty("update").ValueKind.Should().Be(JsonValueKind.Null,
                "the page connected before the first look completed");
        }

        // The daily recheck finds a newer version; the check raises Checked and the server,
        // subscribed since Start(), pushes it to the page that is already open rather than
        // leaving it to find out only on its next reconnect.
        await check.CheckOnceAsync(CancellationToken.None);

        using JsonDocument update = await NextTextAsync(socket);
        update.RootElement.GetProperty("type").GetString().Should().Be("update");
        update.RootElement.GetProperty("update").GetProperty("available").GetString().Should().Be("0.89.0");
    }

    /// <summary>The next text message off a page's socket that is not the keep-alive ping.</summary>
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
