using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Tests.Rig;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// <c>/rig-window</c> (issue #585) over a real HTTP listener: 404 on a station with no
/// <c>rig</c> section, loopback only, refused with any <c>Origin</c> header - the same rule as
/// <c>/channel-audio</c> - and a tune/release round trip against the fake rigctld.
/// </summary>
public sealed class ReceiveWindowRouteTests : IAsyncDisposable
{
    private readonly FakeRigctld _fake = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly SoundModemChannel _channel;
    private readonly WaterfallWebServer _server;
    private readonly RigControl _rig;
    private readonly int _port;
    private readonly HttpClient _client = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(30));

    public ReceiveWindowRouteTests()
    {
        _channel = new SoundModemChannel(12000, _time, randomSeed: 31);
        _channel.AddModem(0, sink => new Afsk1200Modem(12000, sink));
        _port = FreePorts.Next();
        _server = new WaterfallWebServer(_channel, _port);
        _rig = new RigControl(new RigControlOptions
        {
            Endpoint = _fake.Endpoint,
            Time = _time,
            ReplyTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        await _rig.DisposeAsync();
        await _fake.DisposeAsync();
        _cancellation.Dispose();
    }

    private string Url => $"http://127.0.0.1:{_port}{ReceiveWindowApi.Path}";

    private async Task StartWithRigAsync()
    {
        (await _rig.StartAsync(_cancellation.Token)).Should().BeTrue();
        _server.ReceiveWindowRig = _rig;
        _server.Start();
    }

    [Fact]
    public async Task A_Station_With_No_Rig_Section_404s_The_Path()
    {
        _server.Start();

        HttpResponseMessage answer = await _client.GetAsync(Url, _cancellation.Token);

        answer.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await answer.Content.ReadAsStringAsync(_cancellation.Token)).Should().Contain("no \"rig\" section");
    }

    [Fact]
    public async Task Refuses_A_Request_That_Declares_An_Origin()
    {
        await StartWithRigAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(
                """{"dialHz": 7052000, "seconds": 60}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "http://example.com");

        HttpResponseMessage answer = await _client.SendAsync(request, _cancellation.Token);

        answer.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a browser sets Origin itself and script cannot remove it, so this is exactly the one "
            + "request a browser can never produce");
        (await answer.Content.ReadAsStringAsync(_cancellation.Token)).Should().Contain("program on this machine only");
        _fake.Sets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("Forwarded", "for=203.0.113.7")]
    [InlineData("X-Real-IP", "203.0.113.7")]
    public async Task Refuses_A_Request_Relayed_By_A_Reverse_Proxy(string header, string value)
    {
        await StartWithRigAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(
                """{"dialHz": 7052000, "seconds": 60}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header, value);

        HttpResponseMessage answer = await _client.SendAsync(request, _cancellation.Token);

        answer.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a proxy on this machine makes anyone's request arrive from loopback with no Origin");
        _fake.Sets.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_A_Request_From_An_Address_That_Is_Not_Loopback()
    {
        // This machine's own address on a real interface: a request to it comes from it, which is
        // not loopback, the same as a request from anywhere else on that network would be.
        IPAddress? own = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        Assert.SkipWhen(own is null, "this machine has no IPv4 address other than loopback");
        (await _rig.StartAsync(_cancellation.Token)).Should().BeTrue();
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(_channel, port, bind: own!.ToString());
        server.ReceiveWindowRig = _rig;
        server.Start();

        HttpResponseMessage answer = await _client.PostAsync(
            $"http://{own}:{port}{ReceiveWindowApi.Path}",
            new StringContent(
                """{"dialHz": 7052000, "seconds": 60}""", Encoding.UTF8, "application/json"),
            _cancellation.Token);

        answer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await answer.Content.ReadAsStringAsync(_cancellation.Token)).Should().Contain("program on this machine only");
        _fake.Sets.Should().BeEmpty();
        _rig.HoldsTransmitter.Should().BeFalse();
    }

    [Fact]
    public async Task Accepts_A_Loopback_Request_With_No_Origin_And_Tunes_And_Releases()
    {
        await StartWithRigAsync();

        HttpResponseMessage posted = await _client.PostAsync(
            Url,
            new StringContent(
                """{"dialHz": 7052000, "seconds": 60}""", Encoding.UTF8, "application/json"),
            _cancellation.Token);

        posted.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement tuned = await posted.Content.ReadFromJsonAsync<JsonElement>(_cancellation.Token);
        tuned.GetProperty("window").GetProperty("dialHz").GetInt64().Should().Be(7_052_000);
        tuned.GetProperty("window").GetProperty("mode").GetString().Should().Be("USB");
        _fake.DialHz.Should().Be(7_052_000);

        HttpResponseMessage released = await _client.PostAsync(
            Url,
            new StringContent("""{"release": true}""", Encoding.UTF8, "application/json"),
            _cancellation.Token);

        released.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement releasedBody = await released.Content.ReadFromJsonAsync<JsonElement>(_cancellation.Token);
        releasedBody.GetProperty("released").GetBoolean().Should().BeTrue();
        _fake.DialHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task A_Get_Reads_The_Rig_With_No_Window_Open()
    {
        await StartWithRigAsync();

        HttpResponseMessage answer = await _client.GetAsync(Url, _cancellation.Token);

        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await answer.Content.ReadFromJsonAsync<JsonElement>(_cancellation.Token);
        body.GetProperty("window").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("transmitHeld").GetBoolean().Should().BeFalse();
    }
}
