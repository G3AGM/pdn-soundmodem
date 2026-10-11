using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// These run one at a time, though the class still runs beside every other class.
/// </summary>
/// <remarks>
/// Each test here stands up a whole site - an <c>HttpListener</c> on a real port, a directory, a
/// relayed station with its own receive thread and frame-log writer - and drives it over real
/// sockets. Forty-odd of those at once starves the runner's own scheduler: the process goes
/// completely idle with every test parked on a continuation that has nowhere to run, about one
/// run in three, and no test times out because none of them is doing anything. Nothing in the
/// daemon is at fault - it has no synchronization context to be starved of - so the answer is to
/// stop asking the runner to hold forty sites open at the same moment rather than to make the
/// daemon quieter.
/// </remarks>
[CollectionDefinition(nameof(UplinkTests), DisableParallelization = true)]
public sealed class UplinkTestsCollection;

/// <summary>
/// The monitor's side of a private station's uplink: who is let in, what a relayed station is
/// once it is, and what neither a mistake nor an attack can make this site do.
/// </summary>
/// <remarks>
/// <para>Driven against a real <see cref="MonitorHost"/> on a real port, with a real
/// <see cref="ClientWebSocket"/> standing in for the station and real browsers watching the page,
/// because every promise here is about a socket. The station is <see cref="StubStation"/>, which
/// speaks the wire format of <c>docs/dev/uplink-wire-format.md</c> and can also send things a
/// well-behaved station never would.</para>
/// <para>The clock is fake, so a linger is sixty seconds of nothing rather than sixty seconds of
/// waiting, and the one test that is genuinely about elapsed time says so.</para>
/// </remarks>
[Collection(nameof(UplinkTests))]
public class UplinkTests
{
    /// <summary>
    /// How long any one of these gets before it is failed by name.
    /// </summary>
    /// <remarks>
    /// Every test here is a whole site on a real port driven over real sockets, and the slowest
    /// of them is under a second, so thirty is a safety net rather than a budget. It is here
    /// because the alternative is not a slow test but a silent one: a wedged runner goes
    /// completely idle with nothing to report, and a test that fails with its own name on it is
    /// worth a great deal more than a build that stops.
    /// </remarks>
    private const int TestTimeoutMs = 30_000;

    private const string Callsign = "GB7RDG-2";
    private const string Slug = "gb7rdg-2";

    /// <summary>What a site behind a tunnel writes in "monitor"."publicUrl".</summary>
    private const string PublicUrl = "https://monitor.ukpacketradio.network";

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Uplink_With_No_Token_Is_Refused()
    {
        await using var h = await Harness.StartAsync();

        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(
            () => StubStation.ConnectAsync(h.Port, token: null));

        Status(refused).Should().Be(
            401, "the endpoint is on the public port and the token is the whole credential");
        (await h.StationsAsync()).Should().BeEmpty("nothing is built for a connection that is not let in");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Uplink_With_A_Wrong_Token_Is_Refused_And_Delayed()
    {
        await using var h = await Harness.StartAsync();

        // On the real clock, because the delay is what makes a guessing run cost something and a
        // fake one would let it be skipped. It is not the defence - 256 bits is - but it is the
        // reason an attempt is not free.
        var elapsed = Stopwatch.StartNew();
        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(
            () => StubStation.ConnectAsync(h.Port, "pdnsm_not-a-token-this-site-ever-issued"));
        elapsed.Stop();

        Status(refused).Should().Be(401);
        elapsed.Elapsed.Should().BeGreaterThan(
            UplinkServer.BadTokenDelay - TimeSpan.FromMilliseconds(150),
            "a wrong token is held before it is told so");

        h.Errors.Should().ContainSingle(
            line => line.Contains("token this site has not issued", StringComparison.Ordinal),
            "a run of guesses is visible in the journal, counted, and at most one line a minute");
        (await h.StationsAsync()).Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Uplink_Whose_Callsign_Does_Not_Match_Its_Token_Is_Refused()
    {
        await using var h = await Harness.StartAsync();

        // The token is real. What it is not is a licence to be somebody else: the callsign is
        // bound to the token, which is what stops one station claiming another's page.
        await using var station = await StubStation.OpenAsync(h.Port, h.Token, "M0LTE-7");
        await station.ClosedAsync();

        station.Welcome.Should().BeNull();
        station.ClosedBecause.Should().Contain("M0LTE-7").And.Contain(Callsign);
        (await h.StationsAsync()).Should().BeEmpty("no page is built for a station that could not say who it is");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_Cannot_Choose_Its_Own_Slug()
    {
        await using var h = await Harness.StartAsync();

        // On the first and only hello, which is the one that is read. Sent on a second one it
        // would never be looked at, because a second hello ends the session - so the test would
        // have passed off the first welcome and proved nothing about the field.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, extra: new { slug = "somewhere-else", path = "/r/nope/" });
        await station.WelcomedAsync();
        station.Connected.Should().BeTrue("the field is ignored, not refused");

        station.Welcome!.Value.GetProperty("slug").GetString().Should().Be(Slug);
        station.Welcome!.Value.GetProperty("path").GetString().Should().Be($"/r/{Slug}/");
        (await h.GetAsync($"/r/{Slug}/")).Should().Contain("<!doctype html>");
        (await h.StatusAsync("/r/somewhere-else/")).Should().Be(
            System.Net.HttpStatusCode.NotFound, "a station cannot ask for a page");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_Is_Told_The_Address_Of_Its_Own_Page()
    {
        await using var h = await Harness.StartAsync(publicUrl: PublicUrl);

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // Which is what the station's own journal reads back as "publish: live at <url>". The
        // upgrade arrives on 127.0.0.1 here, exactly as it does behind a tunnel that rewrites the
        // Host header, so this URL can only have come from the site's own configuration.
        station.Welcome!.Value.GetProperty("url").GetString().Should().Be($"{PublicUrl}/r/{Slug}/");
        h.Lines.Should().Contain(
            line => line.Contains($"published as {PublicUrl}/", StringComparison.Ordinal),
            "start-up says the address this site is reached at, once");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Site_That_Has_Not_Said_Its_Address_Tells_A_Station_No_Url()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // 127.0.0.1 is a bare address and not a name worth repeating back, and a guessed URL in
        // somebody else's journal is worse than none: the station is told its slug and nothing
        // more, and names that instead. This is the behaviour every site had before there was
        // anywhere to write the address down.
        station.Welcome!.Value.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null);
        station.Welcome!.Value.GetProperty("slug").GetString().Should().Be(Slug);
        h.Lines.Should().NotContain(line => line.Contains("published as", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Second_Connection_On_One_Token_Closes_The_First()
    {
        await using var h = await Harness.StartAsync();

        await using var first = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await first.WelcomedAsync();

        // A station whose socket has half-closed - a NAT entry dropped, a router rebooted - must
        // not be locked out by its own ghost, so the newcomer wins.
        await using var second = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await second.WelcomedAsync();
        await first.ClosedAsync();

        first.ClosedBecause.Should().Contain("another connection authenticated");
        second.Connected.Should().BeTrue();
        (await h.StationsAsync()).Should().ContainSingle("one station, however many sockets have claimed it");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Socket_That_Never_Says_Hello_Is_Closed()
    {
        await using var h = await Harness.StartAsync();

        // The token is checked on the upgrade, so until a hello arrives there is no station, no
        // slug and nothing for the one-connection-per-token rule to apply to. A socket that said
        // nothing was held for the life of the process, costing a WebSocket, a semaphore, a
        // linked cancellation source and a read buffer, with nothing in the journal about it.
        int before = h.Time.Created(UplinkServer.HelloDeadline);
        using ClientWebSocket silent = await StubStation.ConnectAsync(h.Port, h.Token);
        silent.State.Should().Be(WebSocketState.Open);
        await StubStation.UntilAsync(
            () => h.Time.Created(UplinkServer.HelloDeadline) > before, "the hello deadline on the clock");

        h.Time.Advance(UplinkServer.HelloDeadline + TimeSpan.FromSeconds(1));

        var buffer = new byte[256];
        WebSocketReceiveResult closed = await silent.ReceiveAsync(
            buffer, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        closed.MessageType.Should().Be(WebSocketMessageType.Close);
        silent.CloseStatusDescription.Should().Contain("says hello first");
        (await h.StationsAsync()).Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task One_Token_Holds_One_Socket_That_Has_Not_Said_Hello()
    {
        await using var h = await Harness.StartAsync();

        using ClientWebSocket first = await StubStation.ConnectAsync(h.Port, h.Token);

        // Before the hello there was no cap of any kind: one token could open sockets until this
        // process ran out of handles. The class's claim that the token table caps how many
        // stations this site can hold was true of stations and not of sockets.
        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(
            () => StubStation.ConnectAsync(h.Port, h.Token));
        Status(refused).Should().Be(429);
        h.Errors.Should().Contain(line =>
            line.Contains(Callsign, StringComparison.Ordinal)
            && line.Contains("already has a connection waiting", StringComparison.Ordinal));

        // And the slot comes back, so a station that reconnects is not locked out by its own
        // last attempt.
        await first.CloseAsync(
            WebSocketCloseStatus.NormalClosure, "",
            new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Connected_Station_Is_Offered_And_A_Disconnected_One_Is_Not()
    {
        await using var h = await Harness.StartAsync();

        var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await h.UntilAsync(async () => (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        JsonElement live = await h.RowAsync(Slug);
        live.GetProperty("kind").GetString().Should().Be("station");
        live.GetProperty("callsign").GetString().Should().Be(Callsign);
        live.GetProperty("why").ValueKind.Should().Be(JsonValueKind.Null);

        await station.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        JsonElement gone = await h.RowAsync(Slug);
        gone.GetProperty("why").GetString().Should().Be("not connected just now");
        gone.GetProperty("state").GetString().Should().Be("offline");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Disconnected_Station_Keeps_Its_Page_Its_History_And_Its_Links()
    {
        await using var h = await Harness.StartAsync();

        var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await using (Browser watching = await h.WatchAsync(Slug))
        {
            // The browser first, then the frame: a frame is broadcast to whoever is watching when
            // it arrives, and one sent into an empty room is only ever seen again as history.
            await watching.UntilTextAsync("config");
            await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "hello from the shed"));
            await watching.UntilTextAsync("frame");
        }

        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));

        await station.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        // The page is still there, and so is everything the station sent while it was up. This is
        // the whole reason a station is never torn down: a quiet band an hour later still looks
        // like a band somebody has been listening to.
        (await h.GetAsync($"/r/{Slug}/")).Should().Contain("<!doctype html>");
        await using Browser after = await h.WatchAsync(Slug);
        JsonElement history = await after.UntilTextAsync("history");
        history.GetProperty("frames").EnumerateArray().Should().ContainSingle(
            f => f.GetProperty("from").GetString() == "M0LTE");
        JsonElement links = await after.UntilTextAsync("links");
        links.GetProperty("links").GetArrayLength().Should().BeGreaterThan(
            0, "the links panel is folded from the frames' own bytes and outlives the station");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Reconnecting_Station_Comes_Back_Under_The_Same_Slug_And_The_Same_Log()
    {
        await using var h = await Harness.StartAsync();

        var first = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await first.WelcomedAsync();
        await first.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "before the gap"));
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));
        await first.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        await using var again = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await again.WelcomedAsync();
        again.Welcome!.Value.GetProperty("slug").GetString().Should().Be(Slug);

        await again.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "after the gap"));
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 2));
        (await h.StationsAsync()).Should().ContainSingle("the same station came back, not a second one");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Reconnect_Marks_Exactly_The_First_Block_After_It_In_The_Channel_Audio_Stream()
    {
        await using var h = await Harness.StartAsync();
        // Disposed by hand, half way through: that is the disconnect.
        var first = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await first.WelcomedAsync();
        await using Browser browser = await h.WatchAsync(Slug);
        await StubStation.UntilAsync(() => first.Demands.Contains(1), "a demand for one viewer");

        // A local program reading the relayed station's channel audio, as a decoder would.
        var blocks = new List<(byte Flags, int Samples)>();
        var greeted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = new ClientWebSocket();
        using var stop = new CancellationTokenSource();
        await reader.ConnectAsync(new Uri($"ws://127.0.0.1:{h.Port}/r/{Slug}/channel-audio"), CancellationToken.None);
        Task reading = Task.Run(async () =>
        {
            byte[] buffer = new byte[1 << 16];
            try
            {
                while (true)
                {
                    WebSocketReceiveResult result = await reader.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        greeted.TrySetResult(); // the hello: this reader is registered
                    }
                    else if (result.Count > 1 && buffer[0] == 0x01)
                    {
                        lock (blocks)
                        {
                            blocks.Add((buffer[1], BitConverter.ToInt32(buffer, 12)));
                        }
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
            }
        });
        int Blocks()
        {
            lock (blocks)
            {
                return blocks.Count;
            }
        }

        int Heard()
        {
            lock (blocks)
            {
                return blocks.Sum(b => b.Samples);
            }
        }

        await greeted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // Half a second, as SendSecondsAsync counts it.
        int sentBlocks = (int)Math.Round(0.5 * first.AudioRate / first.BlockSamples);
        for (int i = 0; i < sentBlocks; i++)
        {
            await first.SendAudioAsync(transmitted: false);
        }

        int beforeTheGap = sentBlocks * first.BlockSamples;
        await StubStation.UntilAsync(() => Blocks() >= 3, "the first connection's audio in the stream");

        await first.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());
        await using var again = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await again.WelcomedAsync();
        await StubStation.UntilAsync(() => again.Demands.Contains(1), "the viewer's demand, again");
        await again.SendSecondsAsync(0.5);
        await StubStation.UntilAsync(
            () => Heard() >= beforeTheGap + (beforeTheGap / 2), // the last partial block waits for more
            "the second connection's audio in the stream");

        await stop.CancelAsync();
        await reading;
        List<int> gaps;
        int heardBeforeTheMark;
        lock (blocks)
        {
            gaps = [.. Enumerable.Range(0, blocks.Count).Where(i => (blocks[i].Flags & 0x02) != 0)];
            heardBeforeTheMark = gaps.Count == 0 ? -1 : blocks.Take(gaps[0]).Sum(b => b.Samples);
        }

        gaps.Should().ContainSingle("the reconnect is one hole and nothing else is");
        heardBeforeTheMark.Should().Be(beforeTheGap,
            "the mark goes on the first block after the hole: all of the first connection's audio "
            + "before it, none of the second's");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Reconnecting_Station_Is_Listed_As_It_Now_Describes_Itself()
    {
        await using var h = await Harness.StartAsync();

        var first = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, op: "Tom M0LTE", radio: "IC-7300 into a doublet at 10 m");
        await first.WelcomedAsync();
        await first.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        // An operator changes their publish block and restarts, which is the whole way any of
        // this is configured. Their words used to stay whatever the first hello said for the
        // life of the site, with nothing in either journal saying why.
        await using var again = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, op: "Someone Else", location: "Newbury, England",
            radio: "FT-991A into a vertical", site: "https://new.example/");
        await again.WelcomedAsync();
        await h.UntilAsync(async () => (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        JsonElement row = await h.RowAsync(Slug);
        row.GetProperty("operator").GetString().Should().Be("Someone Else");
        row.GetProperty("location").GetString().Should().Be("Newbury, England");
        row.GetProperty("radio").GetString().Should().Be("FT-991A into a vertical");
        row.GetProperty("publicUrl").GetString().Should().Be("https://new.example/");

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");
        config.GetProperty("receiver").GetString().Should()
            .Contain("Someone Else").And.Contain("Newbury");
        config.GetProperty("receiverUrl").GetString().Should().Be("https://new.example/");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_That_Comes_Back_At_A_New_Rate_Is_Drawn_At_The_New_Rate()
    {
        await using var h = await Harness.StartAsync();

        var first = await StubStation.OpenAsync(h.Port, h.Token, Callsign, audioRate: 12000);
        await first.WelcomedAsync();
        await first.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "before the change"));
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));
        await first.DisposeAsync();
        await h.UntilAsync(async () => !(await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        // docs/reference/config.md tells an operator on ADSL that audioRate is one of their two levers. The
        // channel was built at the old rate and the audio checked against the new block length,
        // so the audio was accepted and painted at the wrong rate and the page was silently
        // wrong until somebody restarted the public site.
        await using var again = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, audioRate: 6000, blockSamples: 240);
        await again.WelcomedAsync();

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");
        config.GetProperty("sampleRate").GetInt32().Should().Be(6000);

        await again.SendSecondsAsync(0.5);
        (await browser.UntilBinaryAsync(0x01))[0].Should().Be(
            0x01, "and the new rate's audio draws lines on the rebuilt page");

        // The page was rebuilt and the log was not: the history is still there.
        h.LoggedFrames().Should().Be(1, "the frame log is the same file and the same history");
        h.Lines.Should().Contain(line =>
            line.Contains("came back with a different audio rate", StringComparison.Ordinal)
            && line.Contains("12000 -> 6000", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Station_Builds_No_Modems()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");

        // The bands on the page are the ones off the wire, edge for edge. That is only possible
        // when nothing enumerable carries them: the server draws a declared band exactly when the
        // channel has no modem on that sub-channel, so a band drawn at the declared edges is the
        // proof there is no modem behind it. This site runs none for a relayed station, which is
        // what keeps the decodes the operator's own - and what makes a station cost 20 demodulators
        // less than a receiver.
        JsonElement band = config.GetProperty("modems").EnumerateArray().Single();
        band.GetProperty("sub").GetInt32().Should().Be(0);
        band.GetProperty("mode").GetString().Should().Be("afsk300-il2pc");
        band.GetProperty("lowHz").GetDouble().Should().Be(700);
        band.GetProperty("highHz").GetDouble().Should().Be(1000);
        band.GetProperty("centreHz").GetDouble().Should().Be(850);
        config.GetProperty("sampleRate").GetInt32().Should().Be(
            12000, "the channel runs at the rate the station said it is relaying at");
        config.GetProperty("receiverKind").GetString().Should().Be("station");

        // The same two levels a receiver's page climbs (#399): this page is at /r/<slug>/ too,
        // and "../" would only reach /r/, which the router answers with a 404.
        string picker = new Uri(new Uri($"http://127.0.0.1:{h.Port}/r/{Slug}/"),
            config.GetProperty("pickerUrl").GetString()!).AbsolutePath;
        picker.Should().Be("/");
        (await h.StatusAsync(picker)).Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Station_On_Fm_Is_Relayed_As_An_Fm_Station()
    {
        await using var h = await Harness.StartAsync();

        // The kind of radio travels with the dial, because the page a visitor opens here draws
        // the same scale the station's own page draws. Read as "usb" - which is what anything
        // this end did not recognise used to become - an FM station's channel would be added to
        // every audio frequency on it (issue #413).
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, dialHz: 145_300_000, extra: new { sideband = "fm" });
        await station.WelcomedAsync();

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");
        config.GetProperty("sideband").GetString().Should().Be("fm");
        config.GetProperty("dialHz").GetDouble().Should().Be(145_300_000);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(6000, 30)]
    [InlineData(8000, 25)]
    [InlineData(9600, 30)]
    [InlineData(11025, 25)]
    [InlineData(12000, 30)]
    [InlineData(16000, 25)]
    [InlineData(24000, 30)]
    [InlineData(48000, 30)]
    public async Task Every_Audio_Rate_The_Station_Side_Offers_Is_One_This_Site_Can_Draw(
        int audioRate, int expectedLines)
    {
        await using var h = await Harness.StartAsync();

        // publish validates audioRate as an integer divisor of the station's channel rate, so a
        // 48 kHz station is offered 8000 and 16000 by its own start-up - and thirty lines a
        // second divides neither. A station following its own daemon's advice used to be refused
        // here with a sentence naming nothing it could change, and a stack trace in this site's
        // journal. The line rate comes down to the next one that fits instead.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, audioRate: audioRate, blockSamples: audioRate / 25);
        await station.WelcomedAsync();

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");
        config.GetProperty("sampleRate").GetInt32().Should().Be(audioRate);
        config.GetProperty("linesPerSecond").GetInt32().Should().Be(expectedLines);

        h.Errors.Should().NotContain(
            line => line.Contains("could not build", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Audio_Rate_This_Site_Cannot_Draw_Is_Refused_Without_A_Stack_Trace()
    {
        await using var h = await Harness.StartAsync();

        // A prime rate: no line rate divides it, so there is no waterfall to be had. Only a
        // hand-written client sends one, and the answer is still a sentence about rates rather
        // than an exception from inside a constructor.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, audioRate: 15013, blockSamples: 600);
        await station.ClosedAsync();

        station.ClosedBecause.Should().Contain("a whole number of lines a second divides")
            .And.Contain("12000", "and names a rate that works")
            .And.HaveLength(
                station.ClosedBecause!.Length,
                "the whole sentence has to fit a close frame, which is where its operator reads it");
        station.ClosedBecause!.Length.Should().BeLessThanOrEqualTo(120);
        (await h.StationsAsync()).Should().BeEmpty();
        string[] everything = [.. h.Lines, .. h.Errors];
        everything.Should().NotContain(line => line.Contains("   at ", StringComparison.Ordinal))
            .And.NotContain(line => line.Contains("System.ArgumentException", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Block_Longer_Than_A_Message_Is_Named_At_The_Door()
    {
        await using var h = await Harness.StartAsync();

        // The hello's cap and the reader's used to disagree: a station declaring a second of
        // audio was welcomed and then had its first audio message refused for being over the
        // message cap, which named the wrong thing and cost it a minute of backoff.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, blockSamples: 12000);
        await station.ClosedAsync();

        station.Welcome.Should().BeNull("it is refused at the door, not after being welcomed");
        station.ClosedBecause.Should().Contain("one message carries 8190")
            .And.Contain("4.2's 40 ms is 480 here", "and says what its own block should have been");
        station.ClosedBecause!.Length.Should().BeLessThanOrEqualTo(120);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Relayed_Audio_Becomes_A_Waterfall_Line()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await using Browser browser = await h.WatchAsync(Slug);
        await browser.UntilTextAsync("config");
        await StubStation.UntilAsync(() => station.Demands.Contains(1), "a demand for one viewer");

        await station.SendSecondsAsync(0.5);

        byte[] line = await browser.UntilBinaryAsync(0x01);
        line.Length.Should().BeGreaterThan(
            5, "a spectrum line is a type byte, a line index and one byte per bin");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Relayed_Transmit_Audio_Becomes_A_Line_Marked_As_Ours()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await using Browser browser = await h.WatchAsync(Slug);
        await browser.UntilTextAsync("config");

        // The station's own transmissions are part of what a visitor hears and sees, flagged so
        // the monitor paints them as ours: the take is what the station was working, not only
        // what it heard.
        await station.SendSecondsAsync(0.5, transmitted: true);

        byte[] ours = await browser.UntilBinaryAsync(0x03);
        ours[0].Should().Be(0x03);
        browser.Binaries(0x01).Should().BeEmpty(
            "a block is never half transmitted and half received, and nor is the stream");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Frame_Is_Written_To_The_Stations_Own_Frame_Log()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // Inside the day either side of this site's clock that a relayed timestamp is held to,
        // so what is being tested here is that the station's own time is kept rather than the
        // moment its bytes crossed the wire.
        DateTimeOffset heardAt = h.Time.GetUtcNow().AddMinutes(-7);
        await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "into the log"), at: heardAt);
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));

        (string from, string mode, string at) = h.LastLoggedFrame();
        from.Should().Be("M0LTE");
        mode.Should().Be("afsk300-il2pc");
        at.Should().Contain(
            heardAt.UtcDateTime.ToString("HH:mm:ss"),
            "the log carries the station's own clock, not the wire's");
        File.Exists(Path.Combine(h.FrameLogDirectory, $"frames-{Slug}.db")).Should().BeTrue(
            "one log per station, named as every other station's is");
    }

    /// <summary>
    /// A relayed frame Reed-Solomon alone stood behind is written down as one, and comes back out
    /// of the station's own backlog still saying so.
    /// </summary>
    /// <remarks>
    /// <para>The monitor is where this matters most. A visitor reading a public page has no other
    /// way to tell an RS-only row from a verified one than the badge on it, and the page they get
    /// after a restart is built entirely out of the station's log - so if the flag is lost on the
    /// way in or on the way out, somebody else's unverified callsign pair is presented to the
    /// public as a station that was definitely there.</para>
    /// <para>Nothing on this path is special-cased for it: the wire carries <c>plain</c> both
    /// ways, <c>RelayStation.Log</c> hands it to <c>FrameLog.Record</c> inside an ordinary
    /// <c>FrameQuality</c>, and the backlog is the same <c>Recent</c> a station's own page uses.
    /// That is exactly why it is worth a test - there is no code here that would notice if it
    /// stopped happening (issue #403).</para>
    /// </remarks>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Rs_Only_Frame_Is_Logged_And_Replayed_As_One()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // The GB7BPQ case as it arrives over an uplink: read on Reed-Solomon alone, and withheld
        // by the station that heard it from its own host.
        await station.SendFrameAsync(
            Ax25.Ui("GB7BPQ", "BEACON", "rs only, over the wire"),
            plainIl2p: true, monitorOnly: true);
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));

        h.LastLoggedFlags().Should().Be(
            (1L, 1L), "the relayed row records what stood behind the frame and what became of it");

        // And a browser arriving afterwards - which is every browser, after a restart - is sent
        // the same two fields a live relayed frame carried.
        await using Browser watching = await h.WatchAsync(Slug);
        JsonElement history = await watching.UntilTextAsync("history");
        JsonElement row = history.GetProperty("frames").EnumerateArray().Single();
        row.GetProperty("from").GetString().Should().Be("GB7BPQ");
        row.GetProperty("plain").GetBoolean().Should().BeTrue(
            "the RS ONLY badge on a relayed row must survive the log as well");
        row.GetProperty("monitorOnly").GetBoolean().Should().BeTrue(
            "and the tooltip still says the station kept it from its host");
        row.GetProperty("crc").ValueKind.Should().Be(
            JsonValueKind.Null, "nothing checked a CRC on it, here or at the station");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Frame_Dated_Outside_This_Sites_Clock_Is_Logged_At_This_Sites_Clock()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // A station is a semi-trusted publisher, and its own timestamp is the one field it could
        // use against itself: a frame dated in the year 9999 is written into this site's copy of
        // its log and sorts above everything else on its page for ever. Self-harm rather than an
        // attack, and treated like every other untrusted field here anyway.
        await station.SendFrameAsync(
            Ax25.Ui("M0LTE", "GB7RDG-2", "from the future"),
            at: new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));
        h.LastLoggedFrame().At.Should().NotContain(
            "9999", "a day either side of this site's clock, and nothing beyond it");

        // And a plausible one is kept exactly, because it is the station's own and better than
        // the moment its bytes happened to cross the wire.
        DateTimeOffset heard = h.Time.GetUtcNow().AddMinutes(-5);
        await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "five minutes ago"), at: heard);
        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 2));
        h.LastLoggedFrame().At.Should().Contain(heard.UtcDateTime.ToString("HH:mm:ss"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Unusable_Bearer_Value_Is_Delayed_And_Counted_Like_Any_Other_Guess()
    {
        await using var h = await Harness.StartAsync();

        // A bearer value this site could never have issued - too long to be one of its tokens -
        // is still somebody presenting something. It used to be refused instantly and silently,
        // which made a run of them invisible in the journal.
        var elapsed = Stopwatch.StartNew();
        WebSocketException refused = await Assert.ThrowsAsync<WebSocketException>(
            () => StubStation.ConnectAsync(h.Port, new string('x', 600)));
        elapsed.Stop();

        Status(refused).Should().Be(401);
        elapsed.Elapsed.Should().BeGreaterThan(
            UplinkServer.BadTokenDelay - TimeSpan.FromMilliseconds(150));
        h.Errors.Should().ContainSingle(
            line => line.Contains("token this site has not issued", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_Tail_Of_A_Run_Of_Bad_Tokens_Is_Said_When_The_Window_Closes()
    {
        await using var h = await Harness.StartAsync();

        // The line carries the count up to it, so a run that stops before the next line is due
        // used to end with the journal reporting the first attempt and never mentioning the rest.
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<WebSocketException>(
                () => StubStation.ConnectAsync(h.Port, $"pdnsm_guess-{i}"));
        }

        h.Errors.Should().ContainSingle(
            line => line.Contains("refused 1 connection", StringComparison.Ordinal),
            "the first is said at once, and the rest are inside the quiet minute");

        h.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await h.UntilAsync(() => Task.FromResult(h.Errors.Count == 2));
        h.Errors[1].Should().Contain("refused 4 connections").And.Contain("5 in all");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Frame_Reaches_The_Monitors_Own_Links_Panel()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await using Browser browser = await h.WatchAsync(Slug);
        await browser.UntilTextAsync("config");

        await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "and into the links panel"));

        // Folded here from the frame's own bytes rather than sent as a summary of them: one
        // implementation of a link card, so the site's and the station's cannot disagree.
        JsonElement link = await browser.UntilTextAsync("link");
        string card = link.GetProperty("link").GetRawText();
        card.Should().Contain("M0LTE").And.Contain("GB7RDG-2");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Relayed_Frame_Is_Tagged_Onto_The_Burst_That_Carried_It()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await using Browser browser = await h.WatchAsync(Slug);
        await browser.UntilTextAsync("config");

        // A frame crosses the wire as soon as the station decodes it, but the audio that carried
        // the burst is still in the jitter buffer here. Both go to a browser down one ordered
        // queue, so the test is an ordering one: the frame must arrive after the lines its own
        // audio produced, not among them.
        browser.Forget();
        await station.SendSecondsAsync(1.5);
        await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "on the burst"));

        JsonElement frame = await browser.UntilTextAsync("frame");
        int linesBefore = browser.BinariesBefore(0x01, "frame");
        linesBefore.Should().BeGreaterThanOrEqualTo(
            30,
            "the frame waits for the audio it belongs to; listed on arrival it would be tagged "
            + "five to twelve lines above its own burst");
        frame.GetProperty("line").GetInt64().Should().BeGreaterThanOrEqualTo(30);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Viewer_Arriving_Sends_Demand_And_Leaving_Sends_It_Again_After_The_Linger()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await StubStation.UntilAsync(
            () => station.Demands.Count >= 1, "the demand a station gets on connecting");
        station.Demands[0].Should().Be(0, "nothing flows until somebody is watching");

        Browser browser = await h.WatchAsync(Slug);
        await StubStation.UntilAsync(() => station.Demands.Contains(1), "a demand for one viewer");

        await browser.DisposeAsync();
        await h.UntilAsync(async () => (await h.RowAsync(Slug)).GetProperty("viewers").GetInt32() == 0);

        // The same linger a receiver's session gets, and for the same reason: a page refresh or a
        // tab switch must not stop and restart a home station's stream. The count is repeated on
        // the heartbeat while the linger runs, which is why this asks whether a zero ever went out
        // rather than counting messages.
        h.Time.Advance(Harness.Linger - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        List<int> sinceWatched = [.. station.Demands.SkipWhile(v => v == 0)];
        sinceWatched.Should().NotContain(
            0, "nothing is stopped inside the linger, however many heartbeats pass");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await StubStation.UntilAsync(
            () => station.Demands[^1] == 0, "the demand after the linger");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Hello_With_Only_Its_Required_Fields_Is_Enough()
    {
        await using var h = await Harness.StartAsync();

        // The station's client omits a null rather than writing one, so every optional field can
        // be absent from the wire. A parser that read them positionally, or that assumed a null
        // would be there to find, would refuse a perfectly good station.
        ClientWebSocket socket = await StubStation.ConnectAsync(h.Port, h.Token);
        using (socket)
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(
                    $$"""
                    {"type":"hello","protocol":1,"callsign":"{{Callsign}}",
                     "audioRate":12000,"blockSamples":480}
                    """),
                WebSocketMessageType.Text, true, CancellationToken.None);

            await h.UntilAsync(async () =>
                (await h.StationsAsync()).Any(r =>
                    r.GetProperty("offered").GetBoolean()));
        }

        JsonElement row = await h.RowAsync(Slug);
        row.GetProperty("callsign").GetString().Should().Be(Callsign);
        foreach (string absent in (string[])["operator", "location", "radio", "publicUrl"])
        {
            row.GetProperty(absent).ValueKind.Should().Be(
                JsonValueKind.Null, "{0} was never sent", absent);
        }

        row.GetProperty("modes").GetArrayLength().Should().Be(
            0, "a station with nothing to draw is a station with an empty waterfall");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_Viewer_Count_Is_Repeated_On_A_Heartbeat()
    {
        await using var h = await Harness.StartAsync();
        int before = h.Time.Created(UplinkServer.DemandHeartbeat);

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        await StubStation.UntilAsync(() => station.Demands.Count >= 1, "the demand on connecting");
        await StubStation.UntilAsync(
            () => h.Time.Created(UplinkServer.DemandHeartbeat) > before, "the station's heartbeat timer on the clock");

        // The station's client reconnects after 45 s of silence, so something has to come down
        // this socket well inside that. The count repeated every twenty seconds is that
        // something, which is why the heartbeat is the demand rather than a message of its own.
        h.Time.Advance(UplinkServer.DemandHeartbeat + TimeSpan.FromSeconds(1));
        await StubStation.UntilAsync(
            () => station.Demands.Count >= 2, "the viewer count repeated on the heartbeat");

        h.Time.Advance(UplinkServer.DemandHeartbeat);
        await StubStation.UntilAsync(() => station.Demands.Count >= 3, "and again");
        station.Demands.Should().AllSatisfy(v => v.Should().Be(0));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Stations_Own_Status_Sentence_Becomes_The_Pages_Status_Line()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // A message type the plan's 4.2 does not list and the station's client sends anyway: its
        // radio status sentence, which is the third thing IWaterfallRelay offers. It belongs in
        // the chip a visitor reads, alongside whose station it is.
        //
        // A page already open when the sentence arrives is sent it as a "radio" message, and the
        // site rebuilds the config every later page opens with before it sends that. So the
        // message reaching the first page is the point after which the second page's config has
        // to carry the sentence. The picker row is no such point: it is updated first, and a page
        // opened on the strength of it could be told the config from before the rebuild (and then
        // the "radio" message, which is why a real visitor never sees the difference).
        await using Browser watching = await h.WatchAsync(Slug);
        await watching.UntilTextAsync("config");
        await station.SendAsync(new { type = "radio", status = "IC-7300, 7.049450 MHz USB" });
        JsonElement told = await watching.UntilTextAsync(
            "radio",
            m => m.GetProperty("status").GetString()!.Contains("IC-7300", StringComparison.Ordinal));
        told.GetProperty("status").GetString().Should()
            .Contain(Callsign, "the chip always names the station")
            .And.Contain("IC-7300, 7.049450 MHz USB", "and says what its own radio is doing");

        await using Browser arriving = await h.WatchAsync(Slug);
        JsonElement config = await arriving.UntilTextAsync("config");
        config.GetProperty("radioStatus").GetString().Should()
            .Contain(Callsign, "the chip always names the station")
            .And.Contain("IC-7300, 7.049450 MHz USB", "and a page opened afterwards starts with it");
        (await h.RowAsync(Slug)).GetProperty("status").GetString().Should()
            .Contain("IC-7300", "and the picker row says the same");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Message_Type_This_Site_Does_Not_Know_Is_Dropped_And_Not_Fatal()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // The uplink protocol spans two machines running whatever versions their operators have
        // installed, so it is additive: a message this version has never heard of is what an
        // older monitor sees of a newer station, and hanging up over one would take a station off
        // the site for having been upgraded. Dropped, and the socket carries on.
        await station.SendAsync(new { type = "survey", captures = 3 });
        await station.SendAsync(new { type = "config", sampleRate = 48000 });
        await station.SendAsync(new { type = "tx", enable = true });
        await station.SendFrameAsync(Ax25.Ui("M0LTE", "GB7RDG-2", "still listening"));

        await h.UntilAsync(() => Task.FromResult(h.LoggedFrames() == 1));
        station.Connected.Should().BeTrue("nothing there could have closed this socket");
        (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean().Should().BeTrue();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Two_Viewers_On_One_Station_Ask_For_One_Stream()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // The demand a station is sent on connecting carries the count at that moment, so the
        // first browser waits for it: arriving first, it would make that demand a 1.
        await StubStation.UntilAsync(
            () => station.Demands.Count >= 1, "the demand a station gets on connecting");

        await using Browser first = await h.WatchAsync(Slug);
        await StubStation.UntilAsync(() => station.Demands.Contains(1), "a demand for one viewer");
        await using Browser second = await h.WatchAsync(Slug);
        await StubStation.UntilAsync(() => station.Demands.Contains(2), "a demand for two viewers");

        // Ten people watching cost the station one stream, exactly as ten people watching a
        // receiver cost its operator one session. The monitor fans it out.
        station.Demands.Should().Equal([0, 1, 2]);
        (await h.StationsAsync()).Should().ContainSingle();
        (await h.RowAsync(Slug)).GetProperty("viewers").GetInt32().Should().Be(2);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_Nobody_Is_Watching_Stands_Its_Dead_Feed_Watch_Down()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // Quiet with nobody watching is the whole design, so the starvation watch is stood down
        // by SessionLive rather than firing every threshold. Wound well past it, several times
        // over, because the watch is polled from a timer and would fire on any one of them.
        for (int i = 0; i < 5; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(20);
        }

        h.Errors.Should().NotContain(
            line => line.Contains("starved", StringComparison.Ordinal),
            "a station nobody has picked is not a broken one");
        (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean().Should().BeTrue();
        station.Connected.Should().BeTrue();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_Slug_Pushes_A_Colliding_Receiver_Onto_Its_Full_Slug()
    {
        // A receiver whose host sanitises to exactly the slug this site has promised a station.
        const string colliding = """
            {"count": 1, "instances": [
              {"host": "gb7rdg-2.instance.ubersdr.org", "port": 443, "tls": true,
               "callsign": "GB7RDG-2", "name": "somebody else's receiver",
               "location": "nowhere", "public_url": "https://gb7rdg-2.instance.ubersdr.org/",
               "is_online": true, "available_clients": 5, "max_clients": 20,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "ok",
               "snr_0_30_mhz": 10,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}}
            ]}
            """;
        await using var h = await Harness.StartAsync(directoryJson: colliding);

        // The station wins, by the mechanism that already existed for a receiver's own slug: its
        // slug is a callsign somebody was issued, and a receiver's is derived from a hostname.
        JsonDocument snapshot = JsonDocument.Parse(await h.GetAsync("/api/instances"));
        string[] slugs = [.. snapshot.RootElement.GetProperty("receivers").EnumerateArray()
            .Select(r => r.GetProperty("slug").GetString()!)];
        slugs.Should().Equal(["gb7rdg-2-instance-ubersdr-org"]);
        snapshot.Dispose();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();
        station.Welcome!.Value.GetProperty("slug").GetString().Should().Be(Slug);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Oversized_Hello_Closes_The_Connection()
    {
        await using var h = await Harness.StartAsync();

        ClientWebSocket socket = await StubStation.ConnectAsync(h.Port, h.Token);
        using (socket)
        {
            // Capped before a byte of it is parsed, and closed rather than truncated: nothing
            // should ever arrive on somebody's screen half-said.
            string huge = "{\"type\":\"hello\",\"protocol\":1,\"callsign\":\""
                + new string('A', UplinkServer.MaxHelloBytes) + "\"}";
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(huge), WebSocketMessageType.Text, true,
                CancellationToken.None);

            var buffer = new byte[1024];
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            result.MessageType.Should().Be(WebSocketMessageType.Close);
            socket.CloseStatusDescription.Should().Contain("over");
        }

        (await h.StationsAsync()).Should().BeEmpty();
        h.Errors.Should().Contain(line => line.Contains("over 8192 bytes", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Audio_Message_Of_The_Wrong_Length_Closes_The_Connection()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // The one binary message there is has one length, and it is the one the station's own
        // hello declared. Nothing in the payload is decoded, so there is nothing here for a
        // malformed one to reach - which is why the length is the whole check.
        await station.SendBinaryAsync(station.AudioMessage(false, new short[station.BlockSamples - 1]));
        await station.ClosedAsync();

        station.ClosedBecause.Should().Contain("audio message");
        h.Errors.Should().Contain(line =>
            line.Contains(Callsign, StringComparison.Ordinal)
            && line.Contains("audio message", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Flooding_Station_Is_Dropped_And_Named()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(h.Port, h.Token, Callsign);
        await station.WelcomedAsync();

        // Well past twice the bitrate its own hello declared, in no time at all, which is what a
        // flood is. The fan-out queue to browsers is already bounded and drops oldest, so what
        // has to be bounded is this reader and the jitter buffer behind it.
        try
        {
            for (int i = 0; i < 2000 && station.Connected; i++)
            {
                await station.SendAudioAsync(transmitted: false);
            }
        }
        catch (WebSocketException)
        {
            // Expected: the monitor hung up in the middle of the flood.
        }

        await station.ClosedAsync();
        h.Errors.Should().Contain(line =>
            line.Contains(Callsign, StringComparison.Ordinal)
            && line.Contains("twice the rate", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task An_Overrunning_Jitter_Buffer_Drops_The_Oldest_Audio()
    {
        bool transmit = false;
        var input = new UplinkAudioInput(1000, t => transmit = t, bufferSeconds: 1.0);

        // Three seconds into a one-second buffer. A late block is worth less than a live one to
        // somebody watching a waterfall, and a buffer that grew instead would turn a slow reader
        // into a memory leak with a display minutes behind the band.
        for (int i = 0; i < 30; i++)
        {
            input.Push(Pcm(100, (short)(i + 1)), transmitted: false);
        }

        input.Buffered.Should().BeLessThanOrEqualTo(1000, "the buffer is bounded");
        input.Dropped.Should().BeGreaterThan(0);
        input.Accepted.Should().Be(3000);

        // Dropped audio counts as consumed, which is what makes the frame hold self-correcting:
        // a frame waiting on audio that was thrown away would otherwise wait for ever.
        input.Consumed.Should().Be(input.Dropped);

        // And what is left is the newest, not the oldest.
        float[] read = new float[100];
        input.Read(read).Should().Be(100);
        read[0].Should().BeApproximately(
            21 / 32768f, 1e-6f, "the ten blocks that fit are the last ten, not the first");
        transmit.Should().BeFalse();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Station_Site_Url_That_Is_Not_Http_Is_Refused()
    {
        await using var h = await Harness.StartAsync();

        // The scheme is the part HTML escaping does not touch: a javascript: URL carries none of
        // the four characters an escaper looks for and would run on this site's origin in every
        // visitor's session. The same check the directory's public_url goes through, at the same
        // point - the boundary - because there is no bottom to the list of places it is rendered.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, site: "javascript:alert(document.cookie)");
        await station.WelcomedAsync();
        await h.UntilAsync(async () => (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        (await h.RowAsync(Slug)).GetProperty("publicUrl").ValueKind.Should().Be(JsonValueKind.Null);

        await using Browser browser = await h.WatchAsync(Slug);
        JsonElement config = await browser.UntilTextAsync("config");
        config.GetProperty("receiverUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task A_Stations_Name_In_The_Journal_Is_Ascii()
    {
        await using var h = await Harness.StartAsync();

        // journalctl's pager under a C locale renders a byte above 0x7F as <E2><80><94>, and
        // SourceTextTests cannot catch runtime data. So everything a station sends that reaches
        // the journal is flattened, exactly as the directory's strings are.
        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, op: "Tom \u2014 M0LTE \u00b5", location: "R\u00e9ading");
        await station.WelcomedAsync();

        h.Lines.Should().Contain(line => line.Contains("connected", StringComparison.Ordinal));
        string[] everything = [.. h.Lines, .. h.Errors];
        everything.Should().OnlyContain(
            line => IsAscii(line),
            "a station's words in the journal are ASCII whatever it sent");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task The_Instances_Api_Says_Which_Rows_Are_Stations()
    {
        await using var h = await Harness.StartAsync();

        await using var station = await StubStation.OpenAsync(
            h.Port, h.Token, Callsign, site: "https://gb7rdg.example/");
        await station.WelcomedAsync();
        await h.UntilAsync(async () => (await h.RowAsync(Slug)).GetProperty("offered").GetBoolean());

        using JsonDocument snapshot = JsonDocument.Parse(await h.GetAsync("/api/instances"));
        JsonElement[] rows = [.. snapshot.RootElement.GetProperty("receivers").EnumerateArray()];

        rows.Where(r => r.GetProperty("kind").GetString() == "receiver").Should().HaveCount(
            2, "every receiver row now says which kind it is, and is otherwise what it was");

        JsonElement row = rows.Single(r => r.GetProperty("kind").GetString() == "station");
        row.GetProperty("slug").GetString().Should().Be(Slug);
        row.GetProperty("callsign").GetString().Should().Be(Callsign);
        row.GetProperty("operator").GetString().Should().Be("Tom M0LTE");
        row.GetProperty("location").GetString().Should().Be("Reading, England");
        row.GetProperty("radio").GetString().Should().Be("IC-7300 into a doublet at 10 m");
        row.GetProperty("publicUrl").GetString().Should().Be("https://gb7rdg.example/");
        row.GetProperty("modes").EnumerateArray().Should().ContainSingle();

        // The five figures a station has no honest answer for. Present and null rather than
        // absent, so a reader of this API sees one row shape.
        foreach (string borrowed in (string[])
            ["host", "snrDb", "loadStatus", "availableClients", "maxClients"])
        {
            row.GetProperty(borrowed).ValueKind.Should().Be(
                JsonValueKind.Null,
                "{0} is a fact about a web receiver, and inventing it would be inventing it",
                borrowed);
        }
    }

    /// <summary>A fake clock that also counts the timers put on it, by their first due time, so
    /// a test can wait for the server's timer to exist before moving the clock on. The server
    /// acts a moment before it creates some of its timers (it announces the demand, then sets
    /// the heartbeat; it accepts the socket, then sets the hello deadline), and a clock advanced
    /// in between never fires them: the test then waits out its 30 s timeout.</summary>
    private sealed class TimerCountingTimeProvider : FakeTimeProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<TimeSpan, int> _created = new();

        internal int Created(TimeSpan dueTime) => _created.GetValueOrDefault(dueTime);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            _created.AddOrUpdate(dueTime, 1, (_, n) => n + 1);
            return timer;
        }
    }

    /// <summary>
    /// A monitor with two fake receivers, one configured uplink, a real frame-log directory and a
    /// fake clock.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);

        private readonly MonitorHost _host;
        private readonly HttpClient _http;
        private readonly ScratchDirectory _scratch = new("pdnsm-uplink-tests");
        private readonly List<Browser> _browsers = [];

        private Harness(int port, string directoryJson, string publicUrl)
        {
            Port = port;
            (Token, string hash) = UplinkToken.Mint();
            _host = new MonitorHost(new MonitorHostOptions
            {
                Directory = new UberSdrDirectoryOptions
                {
                    Url = "https://instances.example.org/api/instances",
                    IqMode = "iq48",
                    WindowLowHz = 7050136,
                    WindowHighHz = 7051776,
                },
                Port = port,
                Bind = "127.0.0.1",
                PublicUrl = publicUrl,
                Modems = [new ModemConfig { SubChannel = 0, Mode = "afsk1200", Frequency = 1700 }],
                Uplinks =
                [
                    new UplinkConfig { Callsign = Callsign, Slug = Slug, TokenSha256 = hash },
                ],
                Linger = Linger,
                DspRate = 12000,
                DialHz = 7049450,
                FrameLogDirectory = _scratch.FullName,
                Title = "UK packet monitor",
                About = "The 7050-7052 kHz packet window on 40 m. Receive only.",
                IdBeacons = false,
                TimeProvider = Time,
                Journal = _ => new StationJournal("", Lines.Add, Errors.Add),
                FetchDirectory = _ => Task.FromResult(directoryJson),
                OpenInput = (_, _, _) => throw new InvalidOperationException(
                    "no receiver is picked in these tests"),
            });

            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        internal int Port { get; }

        /// <summary>The token this site has issued, in plain, as its operator would be given it.</summary>
        internal string Token { get; }

        internal string FrameLogDirectory => _scratch.FullName;

        internal TimerCountingTimeProvider Time { get; } = new();

        internal List<string> Lines { get; } = [];

        internal List<string> Errors { get; } = [];

        internal static async Task<Harness> StartAsync(
            string? directoryJson = null, string publicUrl = "")
        {
            var harness = new Harness(FreePorts.Next(), directoryJson ?? DirectoryJson, publicUrl);
            (await harness._host.StartAsync()).Should().Be(0, "the site has to come up");
            return harness;
        }

        internal Task<string> GetAsync(string path) => _http.GetStringAsync(path);

        internal async Task<System.Net.HttpStatusCode> StatusAsync(string path) =>
            (await _http.GetAsync(path)).StatusCode;

        /// <summary>One row of /api/instances, by slug.</summary>
        internal async Task<JsonElement> RowAsync(string slug)
        {
            using JsonDocument snapshot = JsonDocument.Parse(await GetAsync("/api/instances"));
            return snapshot.RootElement.GetProperty("receivers").EnumerateArray()
                .Single(r => r.GetProperty("slug").GetString() == slug).Clone();
        }

        /// <summary>Every relayed station this site holds, which is what "built" means here.</summary>
        internal async Task<JsonElement[]> StationsAsync()
        {
            using JsonDocument snapshot = JsonDocument.Parse(await GetAsync("/api/instances"));
            return [.. snapshot.RootElement.GetProperty("receivers").EnumerateArray()
                .Where(r => r.GetProperty("kind").GetString() == "station")
                .Select(r => r.Clone())];
        }

        /// <summary>A browser watching the station's page, on its own prefix.</summary>
        internal async Task<Browser> WatchAsync(string slug)
        {
            var browser = new Browser();
            await browser.ConnectAsync(Port, slug);
            lock (_browsers)
            {
                _browsers.Add(browser);
            }

            return browser;
        }

        /// <summary>How many frames this station's own log holds, read while it is still open.</summary>
        internal long LoggedFrames()
        {
            using SqliteConnection connection = OpenLog();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM frames";
            return Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal (string From, string Mode, string At) LastLoggedFrame()
        {
            using SqliteConnection connection = OpenLog();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText =
                "SELECT source, mode, heard_at FROM frames ORDER BY id DESC LIMIT 1";
            using SqliteDataReader row = read.ExecuteReader();
            row.Read().Should().BeTrue("the log has a frame in it");
            return (row.GetString(0), row.GetString(1), row.GetString(2));
        }

        /// <summary>
        /// The two decode flags on the newest logged row: what stood behind the frame, and
        /// whether the station that heard it passed it on. Null where the column says nothing.
        /// </summary>
        internal (long? PlainIl2p, long? MonitorOnly) LastLoggedFlags()
        {
            using SqliteConnection connection = OpenLog();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText =
                "SELECT plain_il2p, monitor_only FROM frames ORDER BY id DESC LIMIT 1";
            using SqliteDataReader row = read.ExecuteReader();
            row.Read().Should().BeTrue("the log has a frame in it");
            return (
                row.IsDBNull(0) ? null : row.GetInt64(0),
                row.IsDBNull(1) ? null : row.GetInt64(1));
        }

        private SqliteConnection OpenLog()
        {
            var connection = new SqliteConnection(
                $"Data Source={Path.Combine(FrameLogDirectory, $"frames-{Slug}.db")};Mode=ReadOnly");
            connection.Open();
            return connection;
        }

        /// <summary>Waits on the condition rather than sleeping through a guess.</summary>
        internal async Task UntilAsync(Func<Task<bool>> condition)
        {
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!await condition())
            {
                giveUp.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, giveUp.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Browser[] browsers;
            lock (_browsers)
            {
                browsers = [.. _browsers];
                _browsers.Clear();
            }

            foreach (Browser browser in browsers)
            {
                await browser.DisposeAsync();
            }

            _http.Dispose();
            await _host.DisposeAsync();
            SqliteConnection.ClearAllPools();
            _scratch.Dispose();
        }

        /// <summary>Two receivers, so that a station is listed alongside them and not alone.</summary>
        internal const string DirectoryJson = """
            {"count": 2, "instances": [
              {"host": "m9psy-1.instance.ubersdr.org", "port": 443, "tls": true,
               "callsign": "M9PSY-1", "name": "RX888 with 40m Full Wave Loop (GPSDO)",
               "location": "Dalgety Bay, Scotland, UK",
               "public_url": "https://m9psy-1.instance.ubersdr.org/",
               "is_online": true, "available_clients": 19, "max_clients": 20,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "ok",
               "snr_0_30_mhz": 31,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}},
              {"host": "reading-ubersdr.m0lte.uk", "port": 443, "tls": true,
               "callsign": "M0LTE", "name": "SDR with Active Loop",
               "location": "Reading, England, UK",
               "public_url": "https://reading-ubersdr.m0lte.uk/",
               "is_online": true, "available_clients": 20, "max_clients": 20,
               "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "ok",
               "snr_0_30_mhz": 21,
               "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}}
            ]}
            """;
    }

    /// <summary>
    /// A browser watching a page, keeping every message it was sent in the order it arrived.
    /// </summary>
    /// <remarks>
    /// The order is the point for one of these tests: the spectrum lines and the frame rows go
    /// down one bounded per-client queue, so "the frame arrived after the lines its own audio
    /// produced" is a question this can actually answer.
    /// </remarks>
    private sealed class Browser : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly List<(bool Binary, byte[] Payload)> _messages = [];
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _stopping = new();
        private Task? _reading;
        private bool _disposed;

        internal async Task ConnectAsync(int port, string slug)
        {
            // Bounded, so that a page which never answers fails here with its own name on it
            // rather than as a test that simply stopped.
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{port}/r/{slug}/ws"), giveUp.Token);
            _reading = Task.Run(ReadAsync);
        }

        /// <summary>Throws away what has arrived so far, so a count starts from here.</summary>
        internal void Forget()
        {
            lock (_gate)
            {
                _messages.Clear();
            }
        }

        /// <summary>Every binary message of this type seen so far.</summary>
        internal byte[][] Binaries(byte type)
        {
            lock (_gate)
            {
                return [.. _messages
                    .Where(m => m.Binary && m.Payload.Length > 0 && m.Payload[0] == type)
                    .Select(m => m.Payload)];
            }
        }

        /// <summary>How many binary messages of this type arrived before the first text message
        /// of this kind.</summary>
        internal int BinariesBefore(byte type, string textType)
        {
            lock (_gate)
            {
                int count = 0;
                foreach ((bool binary, byte[] payload) in _messages)
                {
                    if (binary)
                    {
                        if (payload.Length > 0 && payload[0] == type)
                        {
                            count++;
                        }

                        continue;
                    }

                    if (TypeOf(payload) == textType)
                    {
                        return count;
                    }
                }

                return -1;
            }
        }

        /// <summary>Waits for the first text message of this kind and returns it.</summary>
        internal Task<JsonElement> UntilTextAsync(string type) => UntilTextAsync(type, _ => true);

        /// <summary>Waits for the first text message of this kind that matches, and returns it.</summary>
        internal async Task<JsonElement> UntilTextAsync(string type, Func<JsonElement, bool> matches)
        {
            JsonElement? found = null;
            await StubStation.UntilAsync(
                () =>
                {
                    lock (_gate)
                    {
                        foreach ((bool binary, byte[] payload) in _messages)
                        {
                            if (!binary && TypeOf(payload) == type)
                            {
                                JsonElement message = JsonDocument.Parse(payload).RootElement.Clone();
                                if (matches(message))
                                {
                                    found = message;
                                    return true;
                                }
                            }
                        }
                    }

                    return false;
                },
                $"a \"{type}\" message");
            return found!.Value;
        }

        /// <summary>Waits for the first binary message of this type and returns it.</summary>
        internal async Task<byte[]> UntilBinaryAsync(byte type)
        {
            await StubStation.UntilAsync(
                () => Binaries(type).Length > 0, $"a binary message of type {type}");
            return Binaries(type)[0];
        }

        private static string? TypeOf(byte[] payload)
        {
            try
            {
                using JsonDocument message = JsonDocument.Parse(payload);
                return message.RootElement.TryGetProperty("type", out JsonElement type)
                    ? type.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    int at = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer, at, buffer.Length - at),
                            _stopping.Token);
                        at += result.Count;
                    }
                    while (!result.EndOfMessage && at < buffer.Length);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        _messages.Add((
                            result.MessageType == WebSocketMessageType.Binary,
                            buffer[..at]));
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException
                                          or ObjectDisposedException)
            {
                // The page was closed, which is what every one of these tests does eventually.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;   // a test disposed it, and so does the harness on the way out
            }

            _disposed = true;
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    // Bounded: a browser closing is not what any of these tests is about, and a
                    // close handshake that never completes must not hang the suite.
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "",
                        new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException
                                          or OperationCanceledException)
            {
                // A socket already gone is a browser already closed.
            }

            await _stopping.CancelAsync();
            if (_reading is not null)
            {
                try
                {
                    await _reading.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // Not what any of these tests is about.
                }
            }

            _socket.Dispose();
            _stopping.Dispose();
        }
    }

    /// <summary>A block of one repeated sample, in the little-endian bytes the wire carries.</summary>
    private static byte[] Pcm(int samples, short value)
    {
        byte[] bytes = new byte[2 * samples];
        for (int i = 0; i < samples; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(2 * i, 2), value);
        }

        return bytes;
    }

    /// <summary>Whether every character is printable ASCII, which journalctl's pager needs.</summary>
    private static bool IsAscii(string line) => line.All(c => c >= ' ' && c <= '~');

    /// <summary>The HTTP status the upgrade was refused with, off the socket that carried it.</summary>
    private static int Status(WebSocketException refused)
    {
        refused.Data.Contains("HttpStatusCode").Should().BeTrue(
            "the stub records the status a refused upgrade came back with");
        return (int)refused.Data["HttpStatusCode"]!;
    }

    /// <summary>A minimal AX.25 UI frame, so a relayed frame has real bytes behind it.</summary>
    private static class Ax25
    {
        internal static byte[] Ui(string from, string to, string text)
        {
            var frame = new List<byte>();
            frame.AddRange(Address(to));
            frame.AddRange(Address(from, last: true));
            frame.Add(0x03);   // UI
            frame.Add(0xF0);   // no layer 3
            frame.AddRange(Encoding.ASCII.GetBytes(text));
            return [.. frame];
        }

        private static byte[] Address(string callsign, bool last = false)
        {
            string call = callsign;
            int ssid = 0;
            int hyphen = callsign.IndexOf('-', StringComparison.Ordinal);
            if (hyphen >= 0)
            {
                call = callsign[..hyphen];
                ssid = int.Parse(callsign[(hyphen + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }

            byte[] field = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                field[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            field[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
            return field;
        }
    }
}
