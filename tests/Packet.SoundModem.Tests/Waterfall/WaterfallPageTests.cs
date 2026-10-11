using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Audio;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The waterfall page's own JavaScript, run against a real <see cref="WaterfallWebServer"/>.
/// </summary>
/// <remarks>
/// <para>Everything else in this suite tests the server. That left a gap the size of the whole
/// browser: the Listen button once shipped completely non-functional while every server-side
/// assertion stayed green, because both defects lived in the page - a socket that wasn't in scope
/// where the click handler needed it, and a 16-bit view onto an odd byte offset, which is illegal
/// and throws. The bytes on the wire were correct throughout.</para>
/// <para>So the page script is executed here, as the browser executes it, by Node. Skipped when
/// Node isn't installed rather than failing: this is a real check where it can run, and not a
/// reason to block a build where it can't.</para>
/// </remarks>
public class WaterfallPageTests
{
    private const int SampleRate = 12000;

    /// <summary>Where a site that offers several receivers serves one of them.</summary>
    private const string ReceiverBase = "/r/m9psy-1/";
    private const double ToneHz = 1000;
    private const float ToneAmplitude = 0.25f;

    [Fact]
    public async Task Listen_Plays_The_Audio_The_Station_Is_Receiving()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var feeding = new CancellationTokenSource();
        // A thread of its own, not a pool work item: this loop sleeps between blocks for as long
        // as the probe runs, and a pool item that blocks is the pattern that lost the first cut
        // of v0.50.0 (the capture writer, same runner, same load). As a pool item it lost the
        // first cut of v0.51.0 too: with the rest of the suite loading the pool, the feeder had
        // not started by the time the page had clicked Listen and given up on silence, so the
        // page heard nothing and the test said audio was not reaching the speakers.
        Task tone = Task.Factory.StartNew(() => FeedTone(channel, feeding.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Probe probe = await RunProbeAsync(node, port, audio: true);
        await feeding.CancelAsync();
        await tone;

        probe.Thrown.Should().BeEmpty("the page must not throw while playing audio");
        probe.Connected.Should().BeTrue("the page must reach the server before anything else can work");
        probe.ClickError.Should().BeNull();

        probe.Listening.Should().Be(true, "one click on Listen must start listening");
        probe.Label.Should().Be("Listening");

        // 40 ms blocks: a couple of seconds of listening is tens of them, not zero and not one.
        probe.BlocksPlayed.Should().BeGreaterThan(20, "audio must actually reach the speakers");

        // The decisive assertion. A block that arrives but is read at the wrong offset, or scaled
        // wrongly, still counts as "played" - only the amplitude proves the samples came through
        // intact, and it is the amplitude that was fed in.
        probe.PeakAmplitude.Should().BeApproximately(
            ToneAmplitude, 0.02, "the tone must arrive at the level it was transmitted at");

        probe.BlocksAfterStop.Should().Be(0, "a second click must stop the audio, not just the label");
        probe.StoppedLabel.Should().Be("Listen");
    }

    /// <summary>
    /// The page answers the server's keep-alive, so a page somebody has left open is never
    /// mistaken for one whose browser has gone (#411).
    /// </summary>
    /// <remarks>
    /// The one assertion no server-side test can make. The keep-alive only works if the shipping
    /// page answers what it is sent, and the page answers it from <c>onmessage</c> - which is
    /// deliberate, because a browser throttles a background tab's timers and does not throttle its
    /// messages. Here the real page script does the answering, in real V8, over a real socket,
    /// while the server's clock is wound on far faster than the wall.
    /// </remarks>
    [Fact]
    public async Task The_Page_Answers_The_Servers_Keep_Alive_And_Is_Never_Given_Up_On()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var clock = new FakeTimeProvider();
        List<string> journal = [];
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            TimeProvider = clock,
            Log = line => { lock (journal) { journal.Add(line); } },
        });
        server.Start();

        // The page is held open for a DEFINITE number of keep-alives, not for however
        // long this process happens to take. The probe waits until the server has sent
        // it `hold` of them before it finishes, so the winding loop below can be as
        // fast or slow as the box allows without changing what is proved: it just
        // keeps winding until the probe is done.
        //
        // It used to assert on how much clock the winding loop got through, which made
        // the subject of the assertion (real elapsed time / the accuracy of a 50 ms
        // sleep) something the test does not control. Under suite load Task.Delay
        // overshoots, the iteration count drops, and a threshold with almost no margin
        // is missed: the v0.63.0 release run wound 4m40s where it wanted 5m, having
        // needed the probe to last 3.0 s of wall clock and got 2.8 s. It was measuring
        // the box, not the page.
        //
        // Enough keep-alives to cover several silence deadlines, which is the thing
        // being proved: a page that answers is never given up on, however long it is
        // left open. KeepAlivePing apart, so this is comfortably past
        // KeepAliveSilence * 5.
        int hold = (int)Math.Ceiling(WaterfallWebServer.KeepAliveSilence * 6 / WaterfallWebServer.KeepAlivePing);

        // Five seconds of the server's clock per fifty of the wall. A hundredfold rather
        // than any faster because the margin that matters is the real time the page has
        // to answer in: at this rate a deadline is 600 ms of wall clock, which is a very
        // long stall for a socket on loopback and a scripting engine with nothing else
        // to do, and this box does stall under suite load (#400).
        using var running = new CancellationTokenSource();
        var wound = TimeSpan.Zero;
        Task winding = Task.Run(async () =>
        {
            while (!running.IsCancellationRequested)
            {
                clock.Advance(WaterfallWebServer.KeepAlivePeriod);
                wound += WaterfallWebServer.KeepAlivePeriod;
                try
                {
                    await Task.Delay(50, running.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        Probe probe = await RunProbeAsync(node, port, holdKeepAlives: hold);
        await running.CancelAsync();
        await winding;

        probe.Thrown.Should().BeEmpty("the page must not throw while answering");
        probe.Connected.Should().BeTrue("the page must reach the server before anything else can work");
        probe.KeepAlives.Should().BeGreaterThanOrEqualTo(hold,
            "the page has to be held open across several silence deadlines for this to prove anything, "
            + $"and it was wound {wound} of server clock to get there");

        lock (journal)
        {
            journal.Should().BeEmpty(
                "a page that answers is never given up on, however long it is left open");
        }
    }

    /// <summary>
    /// A tab left open across an upgrade finds out on its next reconnect, not never: the version
    /// check runs on every config the page is sent, and reloads the tab once.
    /// </summary>
    /// <remarks>
    /// <para>This is what stops a page from before #412 - which cannot answer a keep-alive it has
    /// never heard of - being dropped at sixty seconds, reconnecting two seconds later and being
    /// dropped again for ever, three journal lines a minute, for as long as the tab is open. The
    /// config that says a tab is stale is the one it gets when it reconnects, and that is never
    /// its first, so a check inside the first-config guard could never fire.</para>
    /// <para>Driven through the page's own socket handler with the announced version replaced,
    /// because a probe cannot upgrade the daemon under itself; the comparison the page makes is
    /// the same either way. The page has to be the one the server stamped, since the embedded
    /// copy still carries the placeholder and the check rightly ignores that.</para>
    /// </remarks>
    [Fact]
    public async Task A_Tab_Left_Open_Across_An_Upgrade_Reloads_Itself_Once()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var http = new HttpClient();
        string served = await http.GetStringAsync($"http://127.0.0.1:{port}/");

        Probe probe = await RunProbeAsync(node, port, pageText: served);

        probe.Thrown.Should().BeEmpty();
        probe.StampedVersion.Should().MatchRegex("^[0-9a-f]{12}$",
            "the page under test has to be a stamped one, or the check would ignore it");
        probe.ConfigReloads.Should().Equal([0, 1, 1],
            "a reconnect that announces the version the tab is running changes nothing, one that "
            + "announces another reloads the tab, and saying it again does not reload it twice");
    }

    /// <summary>
    /// A program listening through the channel audio stream is named on the page, linked to its
    /// own page on the host the browser used when it gave a port, and unlinked when it did not
    /// (issue #586).
    /// </summary>
    [Fact]
    public async Task A_Program_Listening_Through_The_Audio_Stream_Is_Named_And_Linked_To_Its_Page()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var reader = new ClientWebSocket();
        using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await reader.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{ChannelAudioStream.Path}"), connecting.Token);
        await reader.SendAsync(
            """{"name":"pdn-mailcast-receiver","pagePort":8130}"""u8.ToArray(),
            WebSocketMessageType.Text, true, connecting.Token);
        for (int i = 0; i < 100 && server.ChannelAudio.Listeners.Count == 0; i++)
        {
            await Task.Delay(20, connecting.Token);
        }

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();
        probe.ListenersOnArrival.Hidden.Should().BeFalse("the config names the reader already connected");
        probe.ListenersOnArrival.Html.Should().Be(
            "listening: <a href=\"http://127.0.0.1:8130/\" target=\"_blank\" rel=\"noopener\">pdn-mailcast-receiver</a>");
        probe.ListenersDriven.Html.Should().Be(
            "listening: <a href=\"http://127.0.0.1:8130/\" target=\"_blank\" rel=\"noopener\">pdn-mailcast-receiver</a>, "
            + "a &lt;b&gt; tool",
            "a name with no page port is shown unlinked, and escaped like anything else from outside");
        probe.ListenersCleared.Hidden.Should().BeTrue("with nobody listening the chip goes");
    }

    /// <summary>
    /// A station identification is tagged onto its burst and listed in the panel, and both say it
    /// is an ident rather than ordinary traffic on the same slot.
    /// </summary>
    /// <remarks>
    /// Page behaviour no server-side assertion can see: the server sends the same <c>frame</c>
    /// message either way and only the <c>id</c> flag differs. The ordinary frame is driven
    /// through the same two functions in the same run, so what an ident does is measured against
    /// what a normal frame does rather than against an assumption.
    /// </remarks>
    [Fact]
    public async Task An_Id_Beacon_Is_Tagged_And_Listed_As_An_Ident()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing frames");
        probe.Connected.Should().BeTrue("the page needs the config before it can tag anything");

        // The waterfall: both tag, and the ident's says so. A ghost has no band of its own, so
        // without the word an ident reads as a station sitting on the slot it identified beside.
        //
        // Two draws each, not one: the page writes every tag twice - once at its place in the
        // scroll ring and once a ring-height up - so a tag straddling the wrap seam is whole on
        // both sides of it. Asserted rather than deduplicated, because one draw would mean that
        // seam handling had been lost.
        probe.OrdinaryTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("M0LTE").And.NotContain("ID"));
        probe.IdentTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("KK4HEJ").And.Contain("ID"));

        // The panel, newest first: the ident is logged after the ordinary frame, so it sits above.
        int ident = RowWith(probe.FrameRows, "KK4HEJ");
        int ordinary = RowWith(probe.FrameRows, "12.5 dB");
        ident.Should().BeLessThan(ordinary, "the panel is newest first");
        probe.FrameRows[ident].Should().Contain("IDENT")
            .And.Contain("class=\"id\"", "an ident must be badged, or it reads as ordinary traffic")
            .And.Contain(">ID<");
        probe.FrameRows[ordinary].Should().Contain("M0LTE")
            .And.NotContain("class=\"id\"", "only idents carry the badge");
    }

    /// <summary>
    /// A frame this station sent is listed in the panel and marked as ours, and - unlike
    /// everything heard - is not tagged onto the waterfall.
    /// </summary>
    /// <remarks>
    /// Both halves are page behaviour the server cannot see: it sends one <c>frame</c> message
    /// either way and only the <c>tx</c> flag differs. The received frame is driven through the
    /// same entry point in the same run, so "listed but not tagged" is measured against what a
    /// heard frame does rather than against an assumption. Not tagged because the burst is
    /// painted from a queue in real time while the event fires as soon as the audio device took
    /// the audio - the tag would land somewhere up the burst rather than on it.
    /// </remarks>
    [Fact]
    public async Task Our_Own_Transmission_Is_Listed_But_Not_Tagged()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing frames");
        probe.Connected.Should().BeTrue("the page needs the config before it can tag anything");

        // Two draws for a heard frame (the tag is written twice, either side of the scroll ring's
        // wrap seam), none at all for ours.
        probe.HeardTag.Should().HaveCount(2).And.AllSatisfy(tag => tag.Should().Contain("GB7RDG"));
        probe.TxTag.Should().BeEmpty("our own burst is drawn in its own style and needs no tag");

        // Newest first: ours is logged after the frame heard just before it, so it sits above.
        int ours = RowWith(probe.FrameRows, ">TX<");
        int heard = RowWith(probe.FrameRows, "9.5 dB");
        ours.Should().BeLessThan(heard, "the panel is newest first");
        probe.FrameRows[ours].Should().Contain("M0LTE-9").And.Contain("GB7RDG");
        probe.FrameRowClasses[ours].Should().Be(
            "fr tx", "a transmission must be styled apart, or it reads as a station heard");
        probe.FrameRows[heard].Should().NotContain(">TX<", "only our own transmissions are marked");
        probe.FrameRowClasses[heard].Should().Be("fr");
    }

    /// <summary>
    /// The panel opens on the station's logged frames: listed, dimmed apart from live traffic,
    /// never tagged onto the waterfall, and stamped with when they were heard.
    /// </summary>
    /// <remarks>
    /// A panel that starts empty says nothing about a channel that has been busy all morning, and
    /// on a quiet band it is indistinguishable from a modem that is not working. All of this is
    /// page behaviour: the server sends one <c>history</c> message and everything below is what
    /// the page decides to do with it.
    /// </remarks>
    [Fact]
    public async Task The_Panel_Opens_On_The_Stations_Logged_Frames()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a backlog");

        probe.HistoryTag.Should().BeEmpty(
            "a logged frame was heard before the scroll on screen and belongs to no burst on it");

        // The probe drives the backlog last, over a panel that already holds four live rows -
        // which is the reconnect case, where the log by then holds those frames too. Rebuilt, not
        // stacked: four rows, not eight.
        probe.HistoryRows.Should().HaveCount(
            4, "a re-sent backlog rebuilds the panel rather than duplicating it");
        probe.HistoryRowClasses.Should().AllSatisfy(
            row => row.Should().Contain("hist"),
            "a backlog must not read as a channel that is busy right now");

        // Newest on top, as for live frames - the page prepends and the server sends oldest first.
        probe.HistoryRows[0].Should().Contain("GB7RDG-2").And.Contain("EI0RSI-1");
        probe.HistoryRows[1].Should().Contain("GB7BEX-15");

        // Stamped with when it was heard, not when it was shown. Today's frame gets a bare clock
        // time; one from a past year must carry a date too, or it reads as this morning. The
        // date's shape is the viewer's locale's business, so what is asserted is that it is not
        // a bare time - that being the thing that would mislead.
        probe.HistoryRows[0].Should().MatchRegex(@"<span class=""t"">\d\d:\d\d:\d\d</span>");
        probe.HistoryRows[1].Should().NotMatchRegex(@"<span class=""t"">\d\d:\d\d:\d\d</span>");
        probe.HistoryRows[1].Should().MatchRegex(@"<span class=""t"">.*\d\d:\d\d</span>");

        // And the rest of the row still reads as a frame.
        probe.HistoryRows[0].Should().Contain("31 B").And.Contain("+9 Hz");
        probe.HistoryRows[1].Should().Contain("fec 2").And.Contain("crc");
    }

    /// <summary>
    /// A transmission read back out of the log is shown as both: ours, and from before the page
    /// opened.
    /// </summary>
    /// <remarks>
    /// The station logs what it sends as well as what it hears, so a backlog row can carry both
    /// classes - and the two rules set the same <c>border-left</c>, with the history one declared
    /// second. Left to source order the grey would win and a logged transmission would arrive
    /// dimmed and unmarked down the edge, reading as a station heard. The colour asserted here is
    /// resolved out of the shipping stylesheet the way a browser resolves it, by specificity then
    /// source order, because the class list alone cannot say which rule won.
    /// </remarks>
    [Fact]
    public async Task A_Logged_Transmission_Is_Shown_As_Both_Ours_And_History()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a backlog");

        // Newest first, so the transmitted row is third of the four the probe sends: only the
        // RS-only row, which goes in oldest of all, sits below it.
        probe.HistoryRows[2].Should().Contain("M0LTE").And.Contain(">TX<",
            "a logged transmission carries the same badge a live one does");
        probe.HistoryRowClasses[2].Should().Be("fr tx hist");
        probe.HistoryRows[0].Should().NotContain(">TX<", "only our own frames are marked");

        probe.TxBorder.Should().Contain("#31d2f2", "cyan is what says a row is ours");
        probe.HistBorder.Should().NotContain("#31d2f2", "and grey is what says it is old");
        probe.TxHistBorder.Should().Contain(
            "#31d2f2", "a backlogged transmission must not lose the colour that says it is ours");
    }

    /// <summary>
    /// A frame Reed-Solomon alone stood behind keeps its RS ONLY badge when it comes back out of
    /// the log, tooltip and all.
    /// </summary>
    /// <remarks>
    /// The badge is the whole point of listing one of these: on an IL2P+CRC modem the mode label
    /// beside it says the opposite, and the panel is the one place an operator sees both at once.
    /// The row builder draws it from <c>plain</c> and words the tooltip from <c>monitorOnly</c>,
    /// so a backlog message that carried neither did not merely word the tooltip wrongly - the
    /// badge was absent altogether, and every RS-only row heard before a restart read as a frame
    /// something had checked (issue #403).
    /// </remarks>
    [Fact]
    public async Task A_Backlogged_Rs_Only_Frame_Keeps_Its_Badge()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a backlog");

        // Oldest of the backlog, so the bottom row of a newest-first panel.
        probe.HistoryRows[3].Should().Contain("GB7BPQ").And.Contain(">RS ONLY<",
            "a replayed RS-only frame carries the same badge the live one did");
        probe.HistoryRows[3].Should().Contain("NOT passed to the KISS host",
            "and the tooltip still says which way the operator's own configuration went");
        probe.HistoryRowClasses[3].Should().Be(
            "fr hist", "it is a station's frame and a backlog row, and neither of ours");
        probe.HistoryRows[0].Should().NotContain(
            ">RS ONLY<", "a frame with a CRC behind it is badged nothing of the sort");
    }

    /// <summary>
    /// A frame's callsigns and mode reach the panel escaped, whatever characters are in them.
    /// </summary>
    /// <remarks>
    /// <para>The row is built with <c>innerHTML</c> and these three fields went into it raw. On a
    /// station that is safe by coincidence: <c>Ax25AddressParser.TryReadAddress</c> allows a
    /// callsign nothing but <c>[A-Z0-9]</c> and an SSID, and a mode name comes out of the
    /// catalogue. It is a dependency on a parser rather than on an escape, and nothing on the page
    /// said so.</para>
    /// <para>A frame relayed from somebody else's station arrives as strings over a socket and
    /// goes through no such parser, so the coincidence stops holding the moment there is an
    /// uplink. Closed here, ahead of the phase that adds one (docs/dev/archive/uplink-plan.md 4.6).</para>
    /// </remarks>
    [Fact]
    public async Task A_Frame_From_A_Callsign_With_Angle_Brackets_Is_Escaped_On_The_Page()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a hostile frame");

        // Every one of the three arrives as text rather than as markup: no tag the row did not
        // build itself, and the characters still readable as themselves.
        probe.HostileRow.Should().NotContain("<img", "a source callsign is not markup")
            .And.NotContain("<i>bpsk300</i>", "and neither is a mode name")
            .And.Contain("&lt;img src=x onerror=boom&gt;")
            .And.Contain("&lt;i&gt;bpsk300&lt;/i&gt;")
            .And.Contain("M0LTE&quot;&amp;&lt;3", "a destination is escaped the same way");

        // And the row is still the row: the panel's own markup around the escaped text.
        probe.HostileRow.Should().Contain("<span class=\"from\">").And.Contain("24 B");

        // The band chip is built from the config message rather than from a frame event, and on a
        // relayed station its mode name is off that station's own hello. Same gap, same fix, and
        // it is the chip that a public page shows.
        probe.HostileChip.Should().NotContain("<img", "a mode name is not markup either")
            .And.Contain("&lt;img src=x onerror=boom&gt;")
            .And.Contain("850 Hz", "and the chip is still the chip");
    }

    /// <summary>
    /// The survey on the page: a capture tagged where it happened and listed with its audio, and
    /// the status strip that says what a budget has been refusing.
    /// </summary>
    [Fact]
    public async Task A_Capture_Is_Tagged_Where_It_Happened_And_Listed_With_Its_Audio()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a capture");

        // Drawn twice, like every tag, so one straddling the scroll ring's wrap seam is whole on
        // both sides of it - and saying what it is and how wide, which is what a classifier wants
        // and what a list of filenames could never put on the frequency axis.
        probe.CaptureTag.Should().HaveCount(2).And.AllSatisfy(
            tag => tag.Should().Contain("unclaimed").And.Contain("400 Hz"));

        int capture = RowWith(probe.FrameRows, "unclaimed");
        probe.FrameRows[capture].Should()
            .Contain("/survey/20260804-151909-1144hz-unclaimed.wav")
            .And.Contain("/survey/20260804-151909-1144hz-unclaimed.json", "the sidecar too");
        probe.FrameRowClasses[capture].Should().Be("fr cap");

        // The refusals are the reason the strip exists - an operator has no other way to see them.
        probe.SurveyStatus.Should().Contain("7").And.Contain("2 skipped").And.Contain("12 MB");
    }

    /// <summary>
    /// A frame that decoded and would not yield callsigns explains itself in the panel - which is
    /// where an operator sees the word "unattributed" in the first place.
    /// </summary>
    [Fact]
    public async Task An_Unreadable_Frame_Shows_Its_Reason_And_Its_Bytes()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();

        string row = probe.FrameRows[RowWith(probe.FrameRows, "il2p Type1")];
        row.Should().Contain("il2p Type1").And.Contain("byte 0 of the destination callsign");
        row.Should().Contain("00010203DEADBEEF", "the bytes are what an operator copies out");

        // The reason quotes a character taken straight off the air. A payload that happens to
        // contain "<" is not markup, and a panel built with innerHTML has to say so.
        row.Should().NotContain("('<')", "the quoted character must arrive escaped");
        row.Should().Contain("&lt;");
    }

    /// <summary>
    /// A frame read as plain IL2P is badged in the panel, whether or not the modem was told to
    /// pass such frames to the host - because it is standing on Reed-Solomon alone either way.
    /// </summary>
    /// <remarks>
    /// <para>The row's mode column says <c>bpsk300-il2pc</c> and its decode had no CRC behind it at
    /// all, and the panel is the only place an operator sees both. Without the badge the two rows
    /// are indistinguishable from ordinary traffic, which is the state that let a CRC-less
    /// neighbour sit unread on the 40 m slot for weeks.</para>
    /// <para>The colours come out of the shipping stylesheet, resolved the way a browser resolves
    /// them (see <c>badgeBackground</c> in the probe), because the badge shares its box with the
    /// ident badge and the transmit rule also claims <c>.id</c> - so "does the warning colour
    /// actually win" is a real question and not one a stub could be trusted to answer.</para>
    /// </remarks>
    [Fact]
    public async Task A_Plain_Il2p_Frame_Is_Badged_As_Reed_Solomon_Only()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();

        string withheld = probe.FrameRows[RowWith(probe.FrameRows, "GB7BPQ")];
        string delivered = probe.FrameRows[RowWith(probe.FrameRows, "PD4R-11")];
        foreach (string row in (string[])[withheld, delivered])
        {
            row.Should().Contain("class=\"id rs\"", "the badge is about the decode, not the routing")
                .And.Contain(">RS ONLY<");
        }

        // The half only the tooltip can carry: what the operator's own configuration did with it.
        withheld.Should().Contain("NOT passed to the KISS host")
            .And.Contain("acceptPlainIl2p", "and how to change that");
        delivered.Should().Contain("Passed to the KISS host")
            .And.NotContain("NOT passed");

        // Ordinary traffic keeps the panel it had.
        probe.FrameRows[RowWith(probe.FrameRows, "12.5 dB")].Should().NotContain("RS ONLY");

        // And the stylesheet: the warning colour, distinct from the ident badge's, and surviving
        // on a row that also matches the transmit rule.
        probe.RsBadgeBackground.Should().Be("#f0b45a");
        probe.IdentBadgeBackground.Should().NotBe(
            probe.RsBadgeBackground, "an RS-only frame is not an ident and must not look like one");
        probe.RsBadgeOnTxRowBackground.Should().Be(
            "#f0b45a", "the warning must not be repainted by a rule about transmissions");
    }

    /// <summary>
    /// The transmit readout holds the last transmission's figures, and says - in words as well as
    /// in colour - whether what it is showing is live or held.
    /// </summary>
    /// <remarks>
    /// Page behaviour end to end: the server sends the same message shape either way and only
    /// <c>keyed</c> differs. The reason the readout exists at all is that a packet burst is a
    /// fraction of a second and the gaps between bursts are minutes, so the figures have to
    /// survive key-up - and the moment they do, the page has to say they are not live, or an
    /// operator reads a held 29 W as a transmitter that is still up.
    /// </remarks>
    [Fact]
    public async Task The_Transmit_Readout_Holds_The_Last_Burst_And_Says_That_It_Is_Held()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a transmit reading");

        // Keyed: the figures as the radio reports them, and a readout wearing the live class.
        probe.TxKeyed!.Hidden.Should().BeFalse("a reading makes the readout appear");
        probe.TxKeyed.When.Should().Be("Transmitting");
        probe.TxKeyed.Power.Should().Be("29.4");
        probe.TxKeyed.Swr.Should().Be("1.2");
        probe.TxKeyed.SwrHidden.Should().BeFalse();
        probe.TxKeyed.ClassName.Should().Contain("live");
        probe.TxKeyed.SwrClass.Should().NotContain("alarm");

        // The one reading an operator has to act on, in the state they are most likely to see it.
        probe.TxKeyedBadSwr!.SwrClass.Should().Contain(
            "alarm", "an SWR of 3.1 has to look different from an SWR of 1.2");

        // Key-up: the figures stay, and everything about the readout says they are from before.
        probe.TxHeld!.Hidden.Should().BeFalse("the readout survives key-up - that is the point");
        probe.TxHeld.Power.Should().Be("27.5", "the transmission's average is what is held");
        probe.TxHeld.Swr.Should().Be("1.4");
        probe.TxHeld.ClassName.Should().NotContain(
            "live", "a held reading must not be wearing the transmitting colour");
        probe.TxHeld.When.Should().StartWith("Last TX").And.Contain(
            ":", "a held reading says when it was taken, or it reads as one from just now");

        // A radio with no SWR to report shows no SWR, rather than a dash that reads as 1:1.
        probe.TxHeldNoSwr!.SwrHidden.Should().BeTrue();
        probe.TxHeldNoSwr.Power.Should().Be("27.5");

        // And the readout starts hidden, in a way that survives .ctl's own display rule - which
        // is more specific than the browser's rule for [hidden], so without a rule of its own the
        // header opens showing a transmit readout full of dashes.
        using var pageFile = new PageFile();
        string page = await File.ReadAllTextAsync(pageFile.FullName);
        page.Should().Contain("id=\"txReadout\" hidden")
            .And.Contain(".ctl[hidden] { display: none; }");
    }

    /// <summary>
    /// Each modem's label says whether the node software is attached to a KISS port that reaches
    /// it, and follows the client coming and going.
    /// </summary>
    /// <remarks>
    /// A host that quietly dropped its TCP session stops passing traffic, and from the modem's
    /// side that is indistinguishable from a band that went quiet - the one journal line that
    /// said so scrolled past hours ago. The badge is per modem rather than per port because a
    /// modem is reachable through both its own dedicated port and the multiplexed one - but a
    /// modem with a dedicated port shows and counts only that one, since that is the socket an
    /// operator actually plugged a TNC into; a modem with no dedicated port falls back to the
    /// shared one, since that is the only thing that reaches it. Either way the tooltip lists
    /// every port that reaches the modem.
    /// </remarks>
    [Fact]
    public async Task A_Modem_Label_Says_Whether_A_Host_Is_Attached()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        // A second modem with no dedicated port of its own, so the fallback to the shared port is
        // exercised in the same run as the modem that has one.
        channel.AddModem(1, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        // Set before the browser arrives, and never changed again: this is the state a page opened
        // at any moment other than a connect or a disconnect has to be given, and the whole reason
        // the server carries it rather than only broadcasting the events. Listed ascending, the
        // order SetHostPorts actually emits regardless of what order they are given in here. Only
        // modem 0 has a dedicated port (8101); modem 1 is reachable only through the shared one.
        server.SetHostPorts([new HostPortStatus(8101, 0, 0), new HostPortStatus(8105, null, 1)]);

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw on a host-port snapshot");

        probe.ChipsOnArrival.Should().HaveCount(2, "the station has two modems");
        probe.ChipsOnArrival[0].Should().Contain(
            "KISS 8101, no host", "modem 0's own port has no client yet, and the shared port's " +
            "existing client does not count for a modem that has a port of its own");
        probe.ChipsOnArrival[1].Should().Contain(
            "1 host", "the handshake's snapshot has to reach the labels with nothing else happening");

        probe.ChipsAttached.Should().HaveCount(2, "the station has two modems");
        probe.ChipsAttached[0].Should().Contain(
            "KISS 8101: 1 host", "modem 0 has its own port, so the chip names and counts that " +
            "port alone, never the shared one")
            .And.Contain("class=\"host on\"", "attached is the state that reads as good")
            .And.Contain("8105 (all modems): 1 connected", "the tooltip still says which port")
            .And.Contain("8101 (this modem): 1 connected");
        probe.ChipsAttached[1].Should().Contain(
            "KISS 8105: 1 host", "modem 1 has no dedicated port, so it falls back to showing the " +
            "shared one, same as before");

        // And it follows them out again, which is the state worth noticing.
        probe.ChipsDetached.Should().HaveCount(2, "the station has two modems");
        probe.ChipsDetached[0].Should().Contain("class=\"host\"", "nothing attached must not be wearing the good colour")
            .And.Contain("nothing connected")
            .And.Contain("KISS 8101, no host", "the dedicated port stays in the text when nobody is attached");
        probe.ChipsDetached[1].Should().Contain("KISS 8105, no host");

        // The rest of the label is untouched: the badge is an addition, not a replacement.
        probe.ChipsDetached[0].Should().Contain("AFSK1200").And.Contain("1723 Hz");
    }

    /// <summary>
    /// The AX.25 links pane: frames grouped by the pair of stations exchanging them, each card
    /// saying in words what the last frame did, and a frame sent for the second time marked so
    /// that it cannot be missed.
    /// </summary>
    /// <remarks>
    /// The server sends one <c>links</c> message on connect and one <c>link</c> message per frame,
    /// and everything asserted here is what the page makes of them: which card a line lands on,
    /// what the card's header and figures say, and that the resend carries its tag. None of it
    /// is visible to a server-side test, and "a retry is obvious" was the request.
    /// </remarks>
    [Fact]
    public async Task The_Links_Pane_Groups_Frames_By_Link_And_Makes_A_Resend_Obvious()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while building link cards");
        probe.Connected.Should().BeTrue("the page needs the config to label a card with its modem");

        // The pane is closed until asked for, and the button says what is going on behind it:
        // one link up, and amber because that link is waiting on an answer.
        probe.LinksHiddenBefore.Should().BeTrue("the pane is on demand, not always there");
        probe.LinksHiddenAfter.Should().BeFalse();
        probe.LinksOnArrival.N.Should().Be("1", "one of the pairs is a link that is up");
        probe.LinksOnArrival.Summary.Should().Contain("1 of 3 pairs shown").And.Contain("1 waiting on an answer");
        probe.LinksOnArrival.ButtonClass.Should().Contain("alert", "an unanswered poll is the thing worth looking up for");
        probe.LinksAfterEvent.ButtonClass.Should().NotContain("alert", "the poll has been answered");
        probe.LinksAfterEvent.ButtonClass.Should().Contain("on", "the pane is open");

        // The link finished two hours ago is not on the page at all; the live one is first
        // regardless of the order they arrived in; and the beacon and the repeated beacon are
        // there but hidden, this being a pane about links.
        LinkCard live = probe.LinkCards.Should().ContainSingle(c => c.Id == "0|GB7RDG-2<>M0LTE-9").Subject;
        LinkCard beacon = probe.LinkCards.Should().ContainSingle(c => c.Id == "1|GB7BEX<>ID").Subject;
        probe.LinkCards.Should().HaveCount(3, "the pair last heard two hours ago is forgotten on arrival");
        probe.LinkCards[0].Should().BeSameAs(live, "a link that is up sorts ahead of the beacons");
        live.Hidden.Should().BeFalse();
        beacon.Hidden.Should().BeTrue("unconnected traffic is hidden until asked for");
        live.ClassName.Should().Be("lk live", "a link that is up must be styled apart from a beacon");
        live.Head.Should().Contain("M0LTE-9").And.Contain("GB7RDG-2").And.Contain(">connected<")
            .And.Contain("AFSK1200", "the card says which modem the link is on")
            .And.Contain("<b class=\"me\" title=\"This station\">GB7RDG-2</b>",
                "the end this station answered from is marked as its own");
        live.Stats.Should().Contain("1 resend", "the figures count what went wrong")
            .And.Contain("1 unacknowledged");
        live.ConcernHidden.Should().BeTrue("the concern went away with the answer");
        beacon.ClassName.Should().Contain("idle", "nothing has been heard on it for twenty minutes");
        beacon.Head.Should().Contain("GB7BEX").And.Contain(">no link<");
        beacon.Stats.Should().Contain("GB7BEX").And.NotContain(">ID<", "a beacon's addressee never sends anything");

        // The feed reads newest first, worded, and the link coming up in green. The three lines
        // from the backlog were placed without the arrival light; the one that came live has it.
        live.Feed.Should().HaveCount(4);
        live.Feed[3].Html.Should().Contain("calls GB7RDG-2");
        live.Feed[2].ClassName.Should().Be("ln tx up", "the node's answer was ours, and brought the link up");
        live.Feed[1].Html.Should().Contain("hello &lt;node&gt;",
            "text off the air is quoted, and escaped, because a payload is not markup");

        // The decisive line: the resend, tagged as such, with what it carried under it, at the top
        // because it is the latest, and lit because it has just arrived.
        LinkFeedLine resend = live.Feed[0];
        resend.ClassName.Should().Be("ln tx new");
        resend.Html.Should().Contain("resends #0")
            .And.Contain("class=\"tag resend\"", "a retry has to be obvious, not inferred from a sequence number")
            .And.Contain(">RESEND<")
            .And.Contain("Welcome to GB7RDG-2");

        // And a beacon through a digipeater says which one.
        beacon.Feed.Should().ContainSingle().Which.Html.Should().Contain("beacon").And.Contain("via MB7UXX*");

        // A newer link between two other stations lands above ours: newest connection first.
        probe.CardsAfterSecondLink.Select(c => c.Id).Should().Equal(
            "0|G4ABC-1<>GB7IOW-1", "0|GB7RDG-2<>M0LTE-9", "0|ID<>M0XYZ-3", "1|GB7BEX<>ID");
        probe.CardsAfterSecondLink.Where(c => !c.Hidden).Select(c => c.Id).Should().Equal(
            "0|G4ABC-1<>GB7IOW-1", "0|GB7RDG-2<>M0LTE-9");

        // A call nothing answers: at the top while it is a call, then when the station gives up
        // on it, below the links that are up, no longer live, with the line saying why on top.
        probe.CardsAfterCall.Where(c => !c.Hidden).Select(c => c.Id).Should().Equal(
            "0|EI0RSI-1<>GB7RDG-2", "0|G4ABC-1<>GB7IOW-1", "0|GB7RDG-2<>M0LTE-9");
        probe.CardsAfterTimeout.Where(c => !c.Hidden).Select(c => c.Id).Should().Equal(
            "0|G4ABC-1<>GB7IOW-1", "0|GB7RDG-2<>M0LTE-9", "0|EI0RSI-1<>GB7RDG-2");
        probe.FailedCall.Should().NotBeNull();
        probe.FailedCall!.ClassName.Should().Be("lk", "a call given up on is not live");
        probe.FailedCall.Head.Should().Contain(">disconnected<");
        probe.FailedCall.ConcernHidden.Should().BeTrue("nothing is waiting any more");
        probe.FailedCall.Feed.Should().HaveCount(2);
        probe.FailedCall.Feed[0].ClassName.Should().Be("ln bad new");
        probe.FailedCall.Feed[0].Html.Should().Contain("got no answer in 3 minutes; the call has failed")
            .And.Contain("class=\"tag timeout\"").And.Contain(">NO ANSWER<");
        probe.FailedCall.Feed[1].Html.Should().Contain("calls GB7RDG-2");
        probe.FailedCall.Head.Should().Contain("class=\"save\"", "every card can be saved as a transcript");

        // The transcript: a markdown table, oldest line first, the classic decode beside the
        // words, and the gap since the line before. The failed call is two lines, three minutes
        // apart, the second of which has no frame behind it.
        var failed = probe.TranscriptFailed.Split('\n');
        failed[0].Should().Be("# EI0RSI-1 <> GB7RDG-2");
        failed[2].Should().StartWith("Heard on modem 0").And.Contain("2 lines").And.Contain("the link is disconnected");
        failed[4].Should().Be("| Time (UTC) | Delta | Classic | What happened |");
        failed[5].Should().Be("|---|---|---|---|");
        failed[6].Should().MatchRegex(@"^\| \d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \|  \| `EI0RSI-1>GB7RDG-2 <SABM C P>` \| EI0RSI-1 calls GB7RDG-2 \|$");
        failed[7].Should().MatchRegex(@"^\| \d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \| \+3m 00s \|  \| EI0RSI-1 got no answer in 3 minutes; the call has failed \[NO ANSWER\] \|$");
        failed[8].Should().BeEmpty("the document ends with a newline");
        failed.Should().HaveCount(9);

        // Our own link: the text of a data frame goes in the classic column in its own code span,
        // the pid and length in the decode, and a resend is tagged in words.
        var ours = probe.TranscriptOurs.Split('\n');
        ours[0].Should().Be("# M0LTE-9 <> GB7RDG-2");
        ours[2].Should().Contain("which transmits as GB7RDG-2").And.Contain("4 lines").And.Contain("the link is connected");
        ours.Skip(6).Take(4).Select(row => row.Split(" | ")[2]).Should().Equal(
            "`M0LTE-9>GB7RDG-2 <SABM C P>`",
            "`GB7RDG-2>M0LTE-9 <UA R F>`",
            "`M0LTE-9>GB7RDG-2 <I C S0 R0 pid=F0 len=12>` `hello <node>`",
            "`GB7RDG-2>M0LTE-9 <I C S0 R1 pid=F0 len=49>` `Welcome to GB7RDG-2`");
        ours.Skip(6).Take(4).Select(row => row.Split(" | ")[3].TrimEnd('|', ' ')).Should().Equal(
            "M0LTE-9 calls GB7RDG-2",
            "GB7RDG-2 accepts the call; link up",
            "M0LTE-9 sends #0, 12 bytes",
            "GB7RDG-2 resends #0 [RESEND]");

        // UI frames on: the beacons show, newest first after the links.
        probe.LinksAfterEvent.UiCount.Should().Be("2", "the filter says how many cards it is hiding");
        probe.LinksWithUi.UiClass.Should().Contain("on");
        probe.CardsWithUi.Should().OnlyContain(c => !c.Hidden);
        probe.LinksWithUi.Summary.Should().Contain("4 pairs heard");

        // Mine on: only the link this station is one end of. It knows GB7RDG-2 is its own from
        // having transmitted as it; M0XYZ-3's beacon went out of this transmitter too, but as
        // a repeat of somebody else's frame, and that must not make M0XYZ-3 ours.
        probe.LinksMine.MineClass.Should().Contain("on");
        probe.LinksMine.MineCount.Should().Be("1");
        probe.LinksMine.MineTitle.Should().Contain("as GB7RDG-2.").And.NotContain("M0XYZ-3");
        probe.CardsMine.Where(c => !c.Hidden).Select(c => c.Id).Should().Equal("0|GB7RDG-2<>M0LTE-9");
        probe.LinksMine.Summary.Should().Contain("1 of 4 pairs shown");
        probe.LinksMine.EmptyHidden.Should().BeTrue("something is showing");
    }

    /// <summary>
    /// Finds a panel row by something only it contains. Rows were addressed by index, which made
    /// every test depend on how many other things the probe happened to drive first - adding one
    /// step broke two unrelated tests. Order still matters and is asserted where it means
    /// something (newest first), but identity is by content.
    /// </summary>
    private static int RowWith(string[] rows, string marker)
    {
        int at = Array.FindIndex(rows, row => row.Contains(marker, StringComparison.Ordinal));
        at.Should().BeGreaterThanOrEqualTo(0, "no panel row contains \"{0}\"", marker);
        return at;
    }

    // ---------------------------------------------------------------- TX test
    /// <summary>
    /// The transmit control, driven from the page as an operator drives it.
    /// </summary>
    /// <remarks>
    /// The assertion is what the daemon received, over the page's own socket. Nothing server-side
    /// can make it: the button could be wired to the wrong element, send the wrong shape, or read
    /// the seconds box as a string, and every other test in this suite would stay green - which
    /// is exactly how the Listen button once shipped completely non-functional.
    /// </remarks>
    [Fact]
    public async Task The_Tx_Test_Button_Asks_The_Station_For_What_The_Operator_Chose()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        var asked = new List<TxTestRequest>();
        WaterfallWebServer? server = null;
        await using (server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            TxTest = new TxTestControl
            {
                DefaultSeconds = 5,
                MaxSeconds = 30,
                Presets = [.. Packet.SoundModem.Audio.TestTone.BesselNullTonesHz.Select(TxTestPreset.For)],
                Start = request =>
                {
                    lock (asked)
                    {
                        asked.Add(request);
                    }

                    // The station's own answer, which is what turns the button into Stop.
                    server!.ReportTxTest(new TxTestStatus(
                        "running", "two-tone 700+1900 Hz, 5.0 s, peak level 0.80"));
                },
                Stop = () => server!.ReportTxTest(new TxTestStatus(
                    "done", "two-tone 700+1900 Hz, 5.0 s, peak level 0.80 - stopped after 1.2 s")),
            },
        }))
        {
            server.Start();
            Probe probe = await RunProbeAsync(node, port, txTest: "stop");

            probe.Thrown.Should().BeEmpty("the page must not throw around a transmit control");
            probe.Connected.Should().BeTrue();
            probe.TxTestOffered.Should().BeTrue("the operator's page carries the control");
            probe.TxTestDisabled.Should().BeFalse("this station can transmit");

            // Two tones, one free tone, and the four FM presets with the deviation each one
            // calibrates - the whole reason 999 Hz is a preset rather than a number to remember.
            probe.TxTestOptions.Should().Equal([
                "Two tone 700+1900",
                "One tone",
                "500 Hz -> FM 1.2 kHz dev",
                "999 Hz -> FM 2.4 kHz dev",
                "1248 Hz -> FM 3.0 kHz dev",
                "2079 Hz -> FM 5.0 kHz dev",
            ]);

            // What the station was actually asked for, off the wire: the two-tone pair for the
            // page's own default length, as a number and not as the string in the box.
            lock (asked)
            {
                asked.Should().ContainSingle("a click is one test, and a stop is not a second")
                    .Which.Should().Be(new TxTestRequest(true, 700, 5));
            }

            probe.TxTestLabel.Should().Be(
                "Stop", "while it is on the air the button is the way to end it");
            probe.TxTestSaidAfter.Should().Contain(
                "stopped after 1.2 s", "and the page shows the station's own words");
        }
    }

    /// <summary>
    /// A station that cannot key says so on the control rather than hiding it.
    /// </summary>
    [Fact]
    public async Task A_Station_That_Cannot_Key_Shows_The_Reason_On_A_Dead_Button()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        var asked = new List<TxTestRequest>();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            TxTest = new TxTestControl
            {
                Refusal = "no \"ptt\" is configured, so this daemon does not key the radio",
                Start = asked.Add,
                Stop = () => { },
            },
        });
        server.Start();

        Probe probe = await RunProbeAsync(node, port, txTest: "send");

        probe.TxTestOffered.Should().BeTrue();
        probe.TxTestDisabled.Should().BeTrue();
        probe.TxTestSaid.Should().Contain("no \"ptt\" is configured");
        probe.TxTestLabel.Should().BeNull("nothing was ever on the air, so nothing became Stop");
        asked.Should().BeEmpty("and pressing it asks the station for nothing");
    }

    /// <summary>
    /// The button must never be clickable when a click could not possibly reach the daemon.
    /// </summary>
    /// <remarks>
    /// A WebSocket frame handed to a socket that is not open is not queued and not retried - it is
    /// simply not sent - and neither outcome ever shows up as a request in the browser's network
    /// tab, so "the button did nothing, and there was nothing in the network tab" is exactly what
    /// a click that reached the daemon looks like too. The only report an operator can trust is
    /// the button itself refusing the click (#425).
    /// </remarks>
    [Fact]
    public async Task The_Tx_Test_Button_Is_Disabled_While_The_Socket_Is_Not_Open()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            TxTest = new TxTestControl
            {
                DefaultSeconds = 5,
                MaxSeconds = 30,
                Start = _ => { },
                Stop = () => { },
            },
        });
        server.Start();

        Probe probe = await RunProbeAsync(node, port, txTestClose: true);

        probe.Thrown.Should().BeEmpty("the page must not throw when its own socket closes under it");
        probe.TxTestOffered.Should().BeTrue();
        probe.TxTestDisabled.Should().BeFalse("the control works before the socket closes");
        // Review round 1 of #430: the socket-open handler ran before initTxTest had captured the
        // button's real title, wiping it to "" for the life of the page. Checked here, after the
        // connect sequence has already run once in full, which is exactly when the bug fired.
        probe.TxTestGoTitleOnArrival.Should().Be(
            "Key the radio and send the test through the normal transmit path at the station's transmit level",
            "the button's own title must survive the connect sequence, not just exist");
        probe.TxTestDisabledWhileClosed.Should().BeTrue(
            "a click while the socket is not open would send nothing, silently, which must never "
            + "be what a visible Stop or Send does");
        probe.TxTestTitleWhileClosed.Should().Contain(
            "Reconnecting", "the disabled button says why, not only that it is");
    }

    /// <summary>
    /// A page that connects, or reconnects, finds out whether a test is already running from the
    /// daemon's own answer rather than assuming either way.
    /// </summary>
    /// <remarks>
    /// A test that was running when a socket dropped may still be running when it comes back - or
    /// it may have been started by another tab, or by <c>/api/txtest</c>, while this one was away.
    /// A <c>TxTestStatus</c> event only ever reaches a page that was already listening when it was
    /// sent, so a page that just connected has to be told the current state some other way: every
    /// config message now carries it (<c>txTest.running</c>), including a reconnect's (#425).
    /// </remarks>
    [Fact]
    public async Task The_Tx_Test_Button_Follows_The_Servers_Own_Running_State_On_A_Fresh_Config()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            TxTest = new TxTestControl
            {
                DefaultSeconds = 5,
                MaxSeconds = 30,
                Start = _ => { },
                Stop = () => { },
            },
        });
        server.Start();

        Probe probe = await RunProbeAsync(node, port, txTestResync: true);

        probe.Thrown.Should().BeEmpty("the page must not throw on a synthetic config");
        probe.TxTestLabelFromRunningConfig.Should().Be(
            "Stop", "the config said a test is running, and nothing on this page started it");
        probe.TxTestLabelFromStoppedConfig.Should().Be(
            "Send", "and it follows the config back down again");
    }

    private static void FeedTone(SoundModemChannel channel, CancellationToken token)
    {
        var block = new float[512];
        double phase = 0;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = ToneAmplitude * (float)Math.Sin(phase);
                phase += 2 * Math.PI * ToneHz / SampleRate;
            }

            channel.ProcessReceive(block);
            Thread.Sleep(block.Length * 1000 / SampleRate);
        }
    }

    /// <param name="audio">
    /// This run feeds the channel a tone, so the probe should wait for it rather than take a
    /// couple of seconds of silence as the answer: on a loaded runner the first block can be
    /// later than that, and the test that plays audio is the one run that must not give up early.
    /// </param>
    /// <param name="pathname">
    /// The path the page is being served at, which is what everything it reaches for is relative
    /// to. Null is the root, which is where a station that is its own site serves it.
    /// </param>
    /// <param name="stored">
    /// What this origin's one localStorage key already holds when the page opens, as the JSON the
    /// page writes there. Null is a browser that has never had the page open. A site serves every
    /// receiver, and the operator's own page, off a single origin and a single key, so a value
    /// left behind by one of them is what the next one starts from.
    /// </param>
    /// <param name="node">The node binary.</param>
    /// <param name="port">The server the page is to talk to.</param>
    /// <param name="audio">Whether the channel is being fed, which decides how long the probe
    /// waits for sound.</param>
    /// <param name="protocol">The page's own scheme, for the mixed-content question.</param>
    /// <param name="pathname">Where the page thinks it is being served from.</param>
    /// <param name="stored">What is already in this origin's localStorage.</param>
    /// <param name="pageText">The page to run, when it matters that it is the one the server
    /// stamped rather than the one in the assembly: the version check compares the stamp against
    /// what the server announces, and the embedded copy still carries the placeholder.</param>
    /// <param name="txTest">Whether to press the TX test button: null leaves it alone, "send"
    /// presses it once, "stop" presses it again while the test is on the air.</param>
    /// <summary>
    /// The operator page's Mixer group reads the sound card and sets it, through the station's
    /// own config API (#17).
    /// </summary>
    /// <remarks>
    /// <para>The page half of the feature, run as a browser runs it: the shipping script, real
    /// <c>fetch</c>, a real <see cref="ConfigApi"/> on the waterfall's own listener, and a made-up
    /// card behind it. Without this the group is only known to parse.</para>
    /// <para>The card is the CM108 revision on the bench, whose Speaker mutes at the bottom of
    /// its range - so the transmit slider's bottom has to be the lowest step that is a level, and
    /// its tooltip has to say what is under it, which is a page decision no server-side assertion
    /// sees.</para>
    /// </remarks>
    [Fact]
    public async Task The_Operator_Pages_Mixer_Group_Reads_The_Card_And_Sets_It()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        const string key = "page-test-key-not-a-secret";
        string dir = Directory.CreateTempSubdirectory("pdnsm-page-mixer").FullName;
        try
        {
            string configPath = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(configPath, """
                {"device": "plughw:1,0", "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]}
                """);

            var card = FakeMixer.Cm108();
            var api = new ConfigApi(
                key, configPath, Path.Combine(dir, "pending.json"),
                runningJson: () => File.ReadAllText(configPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"));
            api.ServeMixer(MixerRuntime.Start(
                card,
                new AlsaMixerConfig { StateFile = Path.Combine(dir, "mixer-state.json") },
                configPath, "plughw:1,0", _ => { }, out _)!);

            var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
            channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
            int port = FreePorts.Next();
            await using var server = new WaterfallWebServer(channel, port);
            server.ApiHandler = api.HandleAsync;
            server.Start();

            Probe probe = await RunProbeAsync(
                node, port, apiKey: key, mixer: true, mixerGain: 6, mixerPlayback: -8);

            probe.Thrown.Should().BeEmpty("the page must not throw while driving the mixer");
            probe.Connected.Should().BeTrue();

            MixerPanel arrival = probe.MixerOnArrival!;
            arrival.Hidden.Should().BeFalse("the station has a card and this browser has the key");
            arrival.ClassName.Should().NotContain("locked");
            arrival.KeyHidden.Should().BeTrue("there is nothing to ask for");
            arrival.Read.Should().Be(
                "8.0 dB of -12 to 23", "the group opens showing the card, not a guess");
            arrival.Gain.Should().Be("8", "the slider sits at the card's own level, in dB");
            arrival.GainMin.Should().Be("-12", "the slider's ends are the card's ends");
            arrival.GainMax.Should().Be("23");
            arrival.GainDisabled.Should().BeFalse();

            // The transmit level: the second slider, which is what Tom found missing ("I don't
            // see a TX Gain control"). The bench CM108's Speaker mutes below its bottom step, so
            // the slider stops at the lowest step that is a level and says what is under it.
            arrival.PlayRead.Should().Be("-20.0 dB of -36 to 0");
            arrival.Play.Should().Be("-20");
            arrival.PlayMin.Should().Be(
                "-36", "the mute step under the card's range is not a level to slide to");
            arrival.PlayMax.Should().Be("0");
            arrival.PlayDisabled.Should().BeFalse();
            arrival.PlayTitle.Should().Contain("the step below the bottom is mute");

            // Moving the slider and dispatching "change", as a browser does when the operator lets
            // go of it. The handler, not mixSend by hand: which event the slider listens for is
            // part of what shipped.
            MixerPanel gained = probe.MixerAfterGain!;
            gained.Read.Should().Be(
                "6.0 dB of -12 to 23", "the readout is the card's answer, read back");
            card.CaptureDb("Mic").Should().Be(6, "the request reached the card in dB");
            gained.Note.Should().BeEmpty(
                "a change that was made and kept has nothing the operator has to be told");

            // And the transmit slider, the same way.
            MixerPanel played = probe.MixerAfterPlay!;
            played.PlayRead.Should().Be("-8.0 dB of -36 to 0");
            card.PlaybackDb("Speaker").Should().Be(-8, "the transmit slider reached the card too");
            played.Read.Should().Be(
                "6.0 dB of -12 to 23", "and it left the capture gain exactly where it was");

            // There are no AGC or Boost buttons any more: both are switched off at start-up, so
            // there is nothing for a press to change (Tom, 2026-09-06).
            card.Find("Auto Gain Control")!.On.Should().BeFalse(
                "start-up switched it off and nothing on the page can put it back");

            // The same station, the same card, the same key in the browser, dressed for the
            // public. This is the assertion the whole "operator page only" claim rests on, and it
            // has to be made against a page that could otherwise have shown the group: a station
            // with nothing to show proves nothing.
            int publicPort = FreePorts.Next();
            await using var publicServer = new WaterfallWebServer(
                channel, publicPort, new WaterfallOptions { Public = true, Title = "packet monitor" });
            publicServer.ApiHandler = api.HandleAsync;
            publicServer.Start();

            Probe visitor = await RunProbeAsync(node, publicPort, apiKey: key);

            visitor.Thrown.Should().BeEmpty();
            visitor.PublicPage.Hidden["mixerCtl"].Should().BeTrue(
                "the sound card's gain is never a visitor's, whatever key their browser holds");
            visitor.MixerOnArrival!.Read.Should().NotBe(
                "6.0 dB of -12 to 23", "a public page must not even read the card");
            card.CaptureDb("Mic").Should().Be(
                6, "and nothing a visitor's page did may have reached it");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The input level meter: the daemon's own reading of what it is hearing, drawn on the page
    /// with the target band painted into it.
    /// </summary>
    /// <remarks>
    /// <para>Tom, 2026-09-06, watching the Mixer group on the bench Pi: "Some assistance in
    /// setting the capture level would be useful. No way for the user to know what good means."
    /// So the answer is in the picture - a green band to land peaks in and a red top to stay out
    /// of - and the page half of that is what is checked here: the zone drawn where the constants
    /// say, the bar where the reading says, and one sentence underneath.</para>
    /// <para>Real time and a real tone, as the Listen test does: the message is paced at five a
    /// second by the daemon's own clock, and the probe is another process with no way to advance
    /// a fake one.</para>
    /// </remarks>
    [Fact]
    public async Task The_Level_Meter_Draws_The_Daemons_Reading_And_The_Band_To_Aim_At()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        const string key = "page-test-key-not-a-secret";
        string dir = Directory.CreateTempSubdirectory("pdnsm-page-meter").FullName;
        try
        {
            string configPath = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(configPath, """
                {"device": "plughw:1,0", "modems": [ { "subChannel": 0, "mode": "afsk1200" } ]}
                """);

            var card = FakeMixer.Cm108();
            var api = new ConfigApi(
                key, configPath, Path.Combine(dir, "pending.json"),
                runningJson: () => File.ReadAllText(configPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"));
            api.ServeMixer(MixerRuntime.Start(
                card,
                new AlsaMixerConfig { StateFile = Path.Combine(dir, "mixer-state.json") },
                configPath, "plughw:1,0", _ => { }, out _)!);

            var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
            channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
            int port = FreePorts.Next();
            await using var server = new WaterfallWebServer(
                channel, port, new WaterfallOptions { InputLevelMeter = true });
            server.ApiHandler = api.HandleAsync;
            server.Start();

            // A tone at 0.25 full scale, which is -12.0 dBFS peak: the middle of the band this
            // repository has measured as good on real hardware.
            using var feeding = new CancellationTokenSource();
            Task tone = Task.Factory.StartNew(
                () => FeedTone(channel, feeding.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Probe probe = await RunProbeAsync(node, port, apiKey: key, mixer: true, meter: true);
            await feeding.CancelAsync();
            await tone;

            probe.Thrown.Should().BeEmpty("the page must not throw while drawing the meter");

            MeterPanel meter = probe.Meter!;
            meter.Hidden.Should().BeFalse("the meter appears on the first reading that arrives");
            meter.Read.Should().StartWith(
                "-12", "which is the peak of a 0.25 full-scale tone, to the tenth of a dB");

            // The band and the red, drawn from the daemon's own figures: -18 to -9 dBFS on a bar
            // that runs from -60 to 0, so the green starts at 70% and is 15% wide, and the red is
            // the top 10% - which is HotPeakDbFs at -6, the strictest mode's own headroom
            // requirement (docs/dev/receive-levels.md). If these move, InputLevelMeter's constants
            // moved with them.
            meter.ZoneLeft.Should().Be("70%");
            meter.ZoneWidth.Should().Be("15%");
            meter.HotWidth.Should().Be("10%");
            meter.BarWidth.Should().Be("80%", "-12 dBFS is 80% of the way up a -60 to 0 bar");
            meter.BarClass.Should().NotContain("hot").And.NotContain(
                "quiet", "a tone in the target band is drawn in the target colour");
            meter.ClipHidden.Should().BeFalse("the clip pill is shown with the meter");
            meter.ClipClass.Should().NotContain("lit", "nothing here came near full scale");

            meter.Advice.Should().Contain("-18 to -9 dBFS")
                .And.Contain("-30", "the sentence has to say what good means, in words");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The meter's two timers: the clip latch lets go after three seconds, and a bar with nothing
    /// arriving says so rather than sitting frozen.
    /// </summary>
    /// <remarks>
    /// <para>Both exist for the case where the <em>next</em> message does not come, which is
    /// every keyup - nothing is received while transmitting, so the level messages simply stop -
    /// and a card that has died altogether. Re-evaluating the latch on the next message, as the
    /// first cut did, leaves a lit CLIP pill lit for ever and a bar frozen at a pre-key value
    /// that reads exactly like a live one.</para>
    /// <para>So this drives <c>onLevel</c> by hand against a station sending nothing: with real
    /// audio flowing the stale timer is reset every 200 ms and could never fire.</para>
    /// </remarks>
    [Fact]
    public async Task The_Clip_Pill_Lets_Go_And_A_Bar_With_No_Readings_Says_So()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port, meterTimers: true);

        probe.Thrown.Should().BeEmpty("the page must not throw while drawing the meter");

        MeterPanel lit = probe.MeterAfterClip!;
        lit.Hidden.Should().BeFalse("a reading arrived, even if this one was hand-fed");
        lit.ClipClass.Should().Contain("lit");
        lit.Read.Should().Be("-5.0 dBFS");
        lit.BarWidth.Should().Be("91.66666666666667%", "-5 dBFS on a -60 to 0 bar");

        MeterPanel stale = probe.MeterStale!;
        stale.Read.Should().Be(
            "no reading", "a second with nothing arriving is not a measurement of anything");
        stale.BarWidth.Should().Be("0%");
        stale.BarClass.Should().NotContain("hot").And.NotContain("quiet");
        stale.ClipClass.Should().Contain(
            "lit", "the clip is three seconds old at most and is still worth showing");

        probe.MeterClipExpired!.ClipClass.Should().NotContain(
            "lit", "and after three seconds it lets go on its own");
    }

    /// <summary>
    /// With <c>"waterfall"."enableAudioControls"</c> the Mixer group opens on a browser that has
    /// no key at all, and the public page still has no group.
    /// </summary>
    /// <remarks>
    /// <para>The page half of the flag, and the reason it needed no page change: the group is
    /// shown on the strength of the answer to its own probe, and this station answers it. The
    /// browser here has nothing in <c>localStorage</c> - no APIKEY is given - so a group that
    /// still wanted a key would come back locked and reading "key needed".</para>
    /// <para>The public page is the second half, and it is built as the daemon builds one: a
    /// public station never installs the API open, so the visitor's page probes a closed endpoint
    /// and has no group to show. The other half of that claim - that the page hides the group
    /// even where the endpoint does answer - is the keyed test above.</para>
    /// </remarks>
    [Fact]
    public async Task An_Open_Mixer_Group_Needs_No_Key_And_Is_Still_Not_On_The_Public_Page()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        string dir = Directory.CreateTempSubdirectory("pdnsm-page-open-mixer").FullName;
        try
        {
            string configPath = Path.Combine(dir, "soundmodem.json");
            File.WriteAllText(configPath, """
                {"device": "plughw:1,0", "modems": [ { "subChannel": 0, "mode": "afsk1200" } ],
                 "waterfall": { "port": 8107, "enableAudioControls": true }}
                """);

            var card = FakeMixer.Cm108();
            var api = new ConfigApi(
                // No api.key at all, which is the bench station this flag is for.
                "", configPath, Path.Combine(dir, "pending.json"),
                runningJson: () => File.ReadAllText(configPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"),
                openAudioControls: true);
            api.ServeMixer(MixerRuntime.Start(
                card,
                new AlsaMixerConfig { StateFile = Path.Combine(dir, "mixer-state.json") },
                configPath, "plughw:1,0", _ => { }, out _)!);

            var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
            channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
            int port = FreePorts.Next();
            await using var server = new WaterfallWebServer(channel, port);
            server.ApiHandler = api.HandleAsync;
            server.Start();

            Probe probe = await RunProbeAsync(node, port, mixer: true, mixerGain: 6);

            probe.Thrown.Should().BeEmpty("the page must not throw while driving the mixer");
            MixerPanel arrival = probe.MixerOnArrival!;
            arrival.Hidden.Should().BeFalse("the station answers, so there is a group to show");
            arrival.ClassName.Should().NotContain(
                "locked", "nothing was asked of this browser and nothing was refused");
            arrival.KeyHidden.Should().BeTrue("there is no key to ask for");
            arrival.Read.Should().Be("8.0 dB of -12 to 23", "the group opens showing the card");
            probe.MixerAfterGain!.Read.Should().Be("6.0 dB of -12 to 23");
            card.CaptureDb("Mic").Should().Be(6, "and the slider reached the card, with no key");

            // The same station and the same card dressed for the public, built the way the daemon
            // builds one: "public": true zeroes the flag before the API is constructed
            // (WaterfallConfig.AudioControlsOpen, pinned in DaemonConfigTests), so the visitor's
            // page probes a CLOSED endpoint. Hanging the open handler on a public server would
            // have tested the opposite of what ships - a public site with its card in reach - and
            // the page hiding the group in front of an open endpoint is already covered by the
            // keyed test above.
            var closed = new ConfigApi(
                "", configPath, Path.Combine(dir, "pending.json"),
                runningJson: () => File.ReadAllText(configPath),
                ephemeralInForce: false,
                requestRestart: () => throw new InvalidOperationException(
                    "a mixer change must never restart the station"),
                openAudioControls: false);
            closed.ServeMixer(MixerRuntime.Start(
                card,
                new AlsaMixerConfig { StateFile = Path.Combine(dir, "mixer-state.json") },
                configPath, "plughw:1,0", _ => { }, out _)!);

            int publicPort = FreePorts.Next();
            await using var publicServer = new WaterfallWebServer(
                channel, publicPort, new WaterfallOptions { Public = true, Title = "packet monitor" });
            publicServer.ApiHandler = closed.HandleAsync;
            publicServer.Start();

            Probe visitor = await RunProbeAsync(node, publicPort);

            visitor.Thrown.Should().BeEmpty();
            visitor.PublicPage.Hidden["mixerCtl"].Should().BeTrue(
                "the card is the operator's, whatever this station has opened");
            visitor.MixerOnArrival!.Read.Should().NotBe(
                "6.0 dB of -12 to 23", "a public page must not even read the card");
            card.CaptureDb("Mic").Should().Be(6, "and nothing a visitor's page did reached it");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<Probe> RunProbeAsync(
        string node, int port, bool audio = false, string? protocol = null, string? pathname = null,
        string? stored = null, string? pageText = null, string? txTest = null, string? apiKey = null,
        bool mixer = false, double? mixerGain = null, double? mixerPlayback = null,
        bool meter = false, bool meterTimers = false, bool txTestClose = false,
        bool txTestResync = false, int holdKeepAlives = 0)
    {
        string here = Path.GetDirectoryName(typeof(WaterfallPageTests).Assembly.Location)!;
        var start = new ProcessStartInfo(node)
        {
            ArgumentList = { Path.Combine(here, "Waterfall", "browser", "page-probe.mjs") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var pageFile = new PageFile(pageText);
        start.Environment["PAGE"] = pageFile.FullName;
        start.Environment["PORT"] = port.ToString();
        if (audio) start.Environment["AUDIO"] = "1";
        if (protocol is not null) start.Environment["PROTOCOL"] = protocol;
        if (pathname is not null) start.Environment["PATHNAME"] = pathname;
        if (stored is not null) start.Environment["STORED"] = stored;
        if (txTest is not null) start.Environment["TXTEST"] = txTest;
        if (apiKey is not null) start.Environment["APIKEY"] = apiKey;
        if (mixer) start.Environment["MIXER"] = "1";
        if (meter) start.Environment["METER"] = "1";
        if (meterTimers) start.Environment["METERTIMERS"] = "1";
        if (txTestClose) start.Environment["TXTEST_CLOSE"] = "1";
        if (txTestResync) start.Environment["TXTEST_RESYNC"] = "1";
        if (holdKeepAlives > 0) start.Environment["HOLDKEEPALIVES"] = holdKeepAlives.ToString();
        if (mixerGain is double gain)
        {
            start.Environment["MIXGAIN"] = gain.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (mixerPlayback is double playback)
        {
            start.Environment["MIXPLAY"] =
                playback.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using Process probe = Process.Start(start)!;
        string stdout = await probe.StandardOutput.ReadToEndAsync();
        string stderr = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();

        probe.ExitCode.Should().Be(0, $"the probe must run to completion:\n{stderr}");
        return JsonSerializer.Deserialize<Probe>(stdout, JsonSerializerOptions.Web)
               ?? throw new InvalidOperationException($"probe produced no result:\n{stdout}{stderr}");
    }

    /// <summary>
    /// The page the server serves is an embedded resource; the probe needs it as a file. Writing
    /// the embedded copy out - rather than reaching into the source tree - keeps this testing what
    /// ships, not what happens to be sitting next to it.
    /// </summary>
    /// <remarks>
    /// Unique per instance, and deleted as soon as it has been used. The name used to be
    /// pdnsm-page-{pid}.html, which is two faults in one line. Nothing ever removed the files, so
    /// they piled up: 81 of them in /tmp on the dev box. And a leftover from a dead process of
    /// another Unix account, on a box that hosts more than one self-hosted runner, is a file this
    /// process cannot open, which surfaces as "Permission denied" on a path the test has every
    /// right to write. That is exactly how issue #349 blocked the v0.43.0 release from a
    /// neighbouring test. Nothing here is worth keeping after a failure either: these bytes are
    /// the shipped page, identical every run, and they are in the assembly.
    /// </remarks>
    /// <summary>
    /// The page's own copy of the meter's four figures, checked against the daemon's.
    /// </summary>
    /// <remarks>
    /// The zone has to be drawn where the daemon's documentation says it is, and the page cannot
    /// be handed the numbers per message without spending bytes five times a second on four
    /// constants that never change. So they are written down twice and pinned here: if
    /// <see cref="InputLevelMeter"/> moves, this fails and the page moves with it.
    /// </remarks>
    [Fact]
    public void The_Pages_Target_Band_Is_The_Daemons_Target_Band()
    {
        string page = EmbeddedPageText();

        page.Should().Contain($"MIX_TARGET_LOW = {InputLevelMeter.TargetPeakLowDbFs:0}")
            .And.Contain($"MIX_TARGET_HIGH = {InputLevelMeter.TargetPeakHighDbFs:0}")
            .And.Contain($"MIX_QUIET = {InputLevelMeter.QuietPeakDbFs:0}")
            .And.Contain($"MIX_HOT = {InputLevelMeter.HotPeakDbFs:0}");
    }

    /// <summary>
    /// Each frame row carries its own audio level, and a badge only where that level is worth
    /// doing something about.
    /// </summary>
    /// <remarks>
    /// <para>Tom, 2026-09-06 (issue #426): the meter reads the whole input five times a second,
    /// which cannot answer "was that frame's audio right" on a mode whose frames are over inside
    /// one of its intervals. So the figure goes on the row.</para>
    /// <para>The fourth row is the one that matters most for the monitor: a station running a
    /// version that does not measure this, a transmission, and anything whose audio could not be
    /// placed all arrive without the fields, and must draw exactly as they did before there were
    /// any. It is also what every sign-sliced mode's row looks like now, badge aside: the daemon
    /// leaves the figure out where the deciding modem says it means nothing
    /// (<see cref="FrameLevelLimits.PeakWorthShowing"/>), so the page has no rule of its own to
    /// apply and simply draws what it is sent.</para>
    /// </remarks>
    [Fact]
    public async Task Each_Frame_Row_Carries_Its_Own_Level_And_Is_Badged_Only_When_It_Matters()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while listing a level");

        // Newest first, so the four rows read backwards from the order they were sent.
        probe.LevelRows[3].Should().Contain("-14 dBFS", "the figure is on the row, in whole dB")
            .And.NotContain(">TOO LOUD<").And.NotContain(">TOO QUIET<",
                "a frame inside the band to aim at earns no badge at all");
        probe.LevelRows[2].Should().Contain("-1 dBFS").And.Contain(">TOO LOUD<")
            .And.Contain("ran out of codes", "the row says the card clipped during it");
        probe.LevelRows[1].Should().Contain("-39 dBFS").And.Contain(">TOO QUIET<");
        probe.LevelRows[0].Should().NotContain("dBFS")
            .And.NotContain(">TOO LOUD<").And.NotContain(">TOO QUIET<",
                "a row that arrived without the fields shows nothing new");
    }

    /// <summary>
    /// The page keeps no copy of the per-frame thresholds at all, because there is no longer one
    /// pair to copy.
    /// </summary>
    /// <remarks>
    /// It used to keep two numbers to word the sentence under the list and the badges' tooltips,
    /// pinned to the daemon's. Since the thresholds became the mode's own
    /// (<see cref="FrameLevelLimits"/>, measured in <c>docs/dev/receive-levels.md</c>) the catalogue
    /// splits into three groups that differ by 6 dB at the loud end and 39 at the quiet one, so
    /// any single number on the page would be wrong for two thirds of the rows it explained. The
    /// daemon sends the word and the page words the tooltips in terms of what was measured.
    /// </remarks>
    [Fact]
    public void The_Page_Names_No_Frame_Level_Threshold_Of_Its_Own()
    {
        string page = EmbeddedPageText();

        page.Should().NotContain("FRAME_LOUD").And.NotContain("FRAME_QUIET");
    }

    /// <summary>
    /// The AGC and Boost buttons are gone from the page, not merely hidden.
    /// </summary>
    /// <remarks>
    /// Tom, 2026-09-06: "AGC should just be forced off, as should mic boost. No need for buttons
    /// for these." A hidden button is still a button somebody can find with a debugger and a
    /// handler that would send a field the daemon now refuses, so the markup, the handlers and
    /// the ids all go.
    /// </remarks>
    [Fact]
    public void The_Agc_And_Boost_Buttons_Are_Not_On_The_Page_At_All()
    {
        string page = EmbeddedPageText();

        page.Should().NotContain("mixAgc").And.NotContain("mixBoost");
        page.Should().NotContain("\"agc\":").And.NotContain("micBoost");
        page.Should().Contain("mixPlay", "the transmit level slider replaces them");
    }

    /// <summary>The page exactly as it ships, without going through the server.</summary>
    private static string EmbeddedPageText()
    {
        using Stream? resource = typeof(WaterfallWebServer).Assembly
            .GetManifestResourceStream("Packet.SoundModem.Waterfall.wwwroot.waterfall.html");
        resource.Should().NotBeNull("the page ships embedded in the library");
        using var reader = new StreamReader(resource!);
        return reader.ReadToEnd();
    }

    private sealed class PageFile : IDisposable
    {
        public PageFile(string? text = null)
        {
            FullName = Path.Combine(
                Path.GetTempPath(), $"pdnsm-page-{Guid.NewGuid():N}.html");
            if (text is not null)
            {
                // A page as the server served it, stamp and all, for the one question the
                // embedded copy cannot answer.
                File.WriteAllText(FullName, text);
                return;
            }

            using Stream? resource = typeof(WaterfallWebServer).Assembly
                .GetManifestResourceStream("Packet.SoundModem.Waterfall.wwwroot.waterfall.html");
            resource.Should().NotBeNull("the page ships embedded in the library");

            using FileStream file = File.Create(FullName);
            resource!.CopyTo(file);
        }

        public string FullName { get; }

        public void Dispose() => File.Delete(FullName);
    }

    private static string ResolveNode()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            string candidate = Path.Combine(dir, "node");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }


    /// <summary>
    /// The links button, the pane's summary line and its two filters, as the page left them.
    /// </summary>
    private sealed record LinksBar(
        string N,
        string Summary,
        string ButtonClass,
        string UiClass,
        string UiCount,
        string MineClass,
        string MineCount,
        string MineTitle,
        bool EmptyHidden,
        string Empty);

    /// <summary>
    /// The links pane's Mine filter as the handshake left it, before any test clicks it:
    /// <see cref="Stored"/> is what the page found in localStorage, <see cref="On"/> what it
    /// decided to do about it, and <see cref="Hidden"/> whether the button that could put it
    /// right is on the page at all.
    /// </summary>
    private sealed record MineFilter(bool Hidden, bool On, bool Stored);

    private sealed record LinkFeedLine(string ClassName, string Html);

    /// <summary>A card's place in the pane: which link, how styled, and whether a filter hides it.</summary>
    private sealed record CardSlot(string Id, string ClassName, bool Hidden);

    /// <summary>One card of the links pane: its parts as built, and its feed line by line.</summary>
    private sealed record LinkCard(
        string Id,
        string ClassName,
        bool Hidden,
        string Head,
        string Stats,
        string Concern,
        bool ConcernHidden,
        LinkFeedLine[] Feed);

    /// <summary>The card for the call nothing answered, once the station has given up on it.</summary>
    private sealed record FailedCard(string ClassName, string Head, bool ConcernHidden, LinkFeedLine[] Feed);

    /// <summary>One state of the header's transmit readout, as the page left it.</summary>
    private sealed record TxReadout(
        string ClassName,
        bool Hidden,
        string When,
        string Power,
        string Swr,
        bool SwrHidden,
        string SwrClass);

    /// <summary>
    /// A page for the public says what it is and whose receiver it listens through, and does not
    /// show a visitor which KISS ports have a host on them.
    /// </summary>
    /// <remarks>
    /// Page behaviour the server cannot see: it sends the same config either way and only the
    /// flags differ. The host-port snapshot is set so that the operator's page would badge the
    /// chip, which is what makes "no badge" a measurement rather than an absence.
    /// </remarks>
    [Fact]
    public async Task A_Public_Page_Names_Itself_Credits_The_Receiver_And_Hides_The_Host_Badges()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            Public = true,
            Title = "40 m packet monitor",
            About = "The 7050-7052 kHz packet window, receive only.",
        });
        server.SetReceiver("M9PSY-1, Dalgety Bay, Scotland, UK", "https://m9psy-1.instance.ubersdr.org/");
        server.SetHostPorts([new HostPortStatus(8105, null, 1), new HostPortStatus(8101, 0, 1)]);
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty("the page must not throw while dressing itself for the public");
        probe.Connected.Should().BeTrue();

        probe.PublicPage.Title.Should().Be("40 m packet monitor", "the tab says what the page is");
        probe.PublicPage.BodyClass.Should().Contain("public");
        probe.PublicPage.AboutHidden.Should().BeFalse("the about strip is what a visitor reads first");
        probe.PublicPage.About.Should().Contain("The 7050-7052 kHz packet window, receive only.")
            .And.Contain("M9PSY-1, Dalgety Bay, Scotland, UK", "it is somebody else's receiver")
            .And.Contain("href=\"https://m9psy-1.instance.ubersdr.org/\"", "and the credit links to it");

        // The chip is still the chip; only the badge is gone, on arrival and on every update.
        probe.ChipsOnArrival.Should().ContainSingle();
        probe.ChipsOnArrival[0].Should().Contain("AFSK1200")
            .And.NotContain("host", "which KISS ports have a host is nothing to a visitor");
        probe.ChipsAttached.Should().ContainSingle();
        probe.ChipsAttached[0].Should().NotContain("host");
    }

    /// <summary>
    /// A public page has no sideband selector, no span control and no display levels, and the
    /// answers they would have given still arrive: the dial and the sideband from the receiver's
    /// band plan, the span from the page's own default.
    /// </summary>
    /// <remarks>
    /// <para>Tom, having watched the deployed monitor: "We can probably lose the sideband
    /// selector and span controls." They are hidden rather than disabled because a control a
    /// visitor cannot work is worse than no control, and because what they set is not theirs to
    /// set: one site serves fifty receivers, each with its own dial, and a viewer who moved one
    /// would only be reading the wrong frequencies off the ruler.</para>
    /// <para>Both flavours are probed, so what the public flag hides is a difference between two
    /// pages rather than a claim about one - the operator's page is untouched by all of this, and
    /// this is where that is measured. LSB rather than USB on purpose: the RF the ruler ends up
    /// drawing is below the dial rather than above it, so the scale can only have come from the
    /// config the visitor has no control for.</para>
    /// </remarks>
    /// <summary>
    /// The credit on a relayed station's page: whose radio it is, that it is live, and that their
    /// own transmissions are in what you hear - and every word of it escaped.
    /// </summary>
    /// <remarks>
    /// Two sentences in the page, one per kind, chosen by a word from the server rather than
    /// written by it. The sentence contains an anchor built around an escaped name, so a
    /// server-supplied sentence would be either unescapable or a second way for a third party's
    /// words to arrive as markup - which is the hole PR #388's review found in the picker, and a
    /// relayed station is exactly the same class of input.
    /// </remarks>
    [Fact]
    public async Task A_Relayed_Stations_Credit_Names_The_Operator_And_Escapes_Their_Words()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            Public = true,
            Title = "UK packet monitor",
            ReceiverKind = "station",
            DeclaredBands = [new DeclaredBand(0, "afsk300-il2pc", 850, 300)],
        });
        server.SetReceiver(
            "GB7RDG-2, <script>alert(1)</script>, Reading", "javascript:alert(2)");
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();
        probe.Connected.Should().BeTrue();
        probe.PublicPage.About.Should()
            .Contain("a private station relaying its own receiver to this site live")
            .And.Contain("its own transmissions are in what you hear")
            .And.NotContain("UberSDR web receiver", "this is not one, and the page says which")
            .And.Contain("GB7RDG-2", "the credit names the station");

        // The operator's words are theirs and are escaped; a scheme that is not http or https
        // does not get to write an href, whatever the daemon let through.
        probe.PublicPage.About.Should().NotContain("<script>").And.NotContain("javascript:");
        probe.PublicPage.About.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public async Task The_Public_Page_Hides_The_Sideband_And_Span_Controls()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var visitorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        visitorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int visitorPort = FreePorts.Next();
        await using var visitorServer = new WaterfallWebServer(visitorChannel, visitorPort, new WaterfallOptions
        {
            Public = true,
            Title = "40 m packet monitor",
            DialFrequencyHz = 7047500,
            Sideband = "lsb",
        });
        visitorServer.Start();

        var operatorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        operatorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int operatorPort = FreePorts.Next();
        await using var operatorServer = new WaterfallWebServer(operatorChannel, operatorPort, new WaterfallOptions
        {
            DialFrequencyHz = 7047500,
            Sideband = "lsb",
        });
        operatorServer.Start();

        Probe visitor = await RunProbeAsync(node, visitorPort);
        Probe op = await RunProbeAsync(node, operatorPort);

        visitor.Thrown.Should().BeEmpty();
        visitor.Connected.Should().BeTrue();
        op.Thrown.Should().BeEmpty();
        op.Connected.Should().BeTrue();

        visitor.PublicPage.Hidden["dialCtl"].Should().BeTrue(
            "the dial and the sideband buttons sit in it, and neither is a visitor's to move");
        visitor.PublicPage.Hidden["spanCtl"].Should().BeTrue("nor is how wide a slice is shown");
        visitor.PublicPage.Hidden["levelCtl"].Should().BeTrue(
            "nor the floor and top of the colour scale, which is the same kind of knob");
        visitor.PublicPage.Hidden["mixerCtl"].Should().BeTrue(
            "and the sound card's own gain is a station's, never a visitor's");
        visitor.PublicPage.Hidden["txTestCtl"].Should().BeTrue(
            "and least of all the control that keys the transmitter");

        // Every id the flag hides, except the two groups that hide themselves for a second and
        // separate reason - the mixer when there is no config API behind it, the transmit test
        // when the station sent no control - and are asserted on their own.
        op.PublicPage.Hidden.Where(entry => entry.Key is not ("mixerCtl" or "txTestCtl")).Should()
            .AllSatisfy(entry => entry.Value.Should().BeFalse(),
                "nothing is taken off the operator's page; the flag only hides");

        // Neither of these stations has a config API, so /api/mixer is a 404 on both and the
        // group takes itself off the page. That is the operator's page deciding, not the public
        // flag: what the flag does to it is the visitor assertion above.
        op.PublicPage.Hidden["mixerCtl"].Should().BeTrue(
            "an operator's page with no \"api\" section has no mixer group to show");

        // And the settings still apply, arriving from the config rather than from a control. On
        // LSB the ruler runs downwards from the dial, so 7044.50 is only reachable that way.
        visitor.DrawnOnArrival.Should().Contain("7047.50", "the dial is the one the station planned")
            .And.Contain("7044.50", "and the sideband it planned it on");
        visitor.DrawnOnArrival.Should().NotContain("7050.50", "which is where USB would have put it");
    }

    /// <summary>
    /// An FM station's page does no audio-to-RF arithmetic anywhere: no RF row on the ruler, no
    /// prompt asking for a dial, and the channel said once rather than added to every tone.
    /// </summary>
    /// <remarks>
    /// <para>Tom, of a station running over an FM set: "For FM modems, the flow of entering the
    /// frequency into the UI to show RF is not correct. AF does not map on to RF with FM radio."
    /// A 1700 Hz tone on 145.300 MHz is 1700 Hz of audio on that channel, not 145.3017 MHz of
    /// RF, so a figure of that shape must not appear anywhere on the page.</para>
    /// <para>The mirror of the LSB assertions above, which measure the arithmetic being right on
    /// a radio that has some. This measures it not being done at all, which is the only thing
    /// that can be measured on a radio that has none.</para>
    /// </remarks>
    [Fact]
    public async Task An_Fm_Stations_Page_Says_The_Channel_Once_And_Draws_No_Rf_Scale()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            DialFrequencyHz = 145_300_000,
            Sideband = "fm",
        });
        server.Start();

        Probe probe = await RunProbeAsync(node, port);

        probe.Thrown.Should().BeEmpty();
        probe.Connected.Should().BeTrue();

        // The audio scale is drawn as it always was.
        probe.DrawnOnArrival.Should().Contain("1.5k", "the ruler still measures audio");

        // And the amber RF row is not: on USB it would have written the channel in kHz beside
        // every tick, 145300.00 upwards, which is the wrong figure once per tick.
        probe.DrawnOnArrival
            .Where(drawn => drawn.Contains("145", StringComparison.Ordinal))
            .Should().AllBe("145.300 MHz",
                "the channel is the only RF figure on an FM ruler, and it belongs to the whole "
                + "ruler rather than to any tick on it");
        probe.DrawnOnArrival.Should().Contain("145.300 MHz", "the channel is worth saying once");

        // Nothing asking for a dial either. There is no RF to see whatever is entered, so the
        // prompt would be promising something that is not coming.
        probe.DrawnOnArrival.Should().NotContain("Set the dial frequency to see RF");

        // The chip carries the audio centre and the channel, and never their sum.
        probe.ChipsOnArrival.Should().ContainSingle();
        probe.ChipsOnArrival[0].Should().Contain("1723 Hz", "where the modem sits, in audio")
            .And.Contain("145.300", "and the channel it is on")
            .And.NotContain("145.301", "which is what adding the two would have produced");

        probe.DialWhat.Should().Be("Channel", "an FM radio has no dial in the SSB sense");
    }

    /// <summary>
    /// What is left between the top of the page and its panels, on a page for a visitor: the
    /// receiver's state, whose receiver it is, and the listen control. Not the frame rate, not
    /// the labels naming what a button plainly is, not the KISS sub-channel of a modem.
    /// </summary>
    /// <remarks>
    /// Tom, on the deployed monitor: "Thin out the text in the middle, go minimal." The operator's
    /// about text is his and is untouched, and the modem chips stay because they are the key to
    /// the coloured bands drawn on the waterfall - thinned to the mode and where it sits, which is
    /// the whole of what the key has to say to somebody who is not going to plug anything in.
    /// </remarks>
    [Fact]
    public async Task The_Public_Page_Keeps_Only_The_Receiver_The_Credit_And_The_Listen_Control()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var visitorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        visitorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int visitorPort = FreePorts.Next();
        await using var visitorServer = new WaterfallWebServer(visitorChannel, visitorPort, new WaterfallOptions
        {
            Public = true,
            Title = "40 m packet monitor",
            About = "The 7050-7052 kHz packet window, receive only.",
        });
        visitorServer.SetReceiver("M9PSY-1, Dalgety Bay, Scotland, UK", "https://m9psy-1.instance.ubersdr.org/");
        visitorServer.Start();

        var operatorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        operatorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int operatorPort = FreePorts.Next();
        await using var operatorServer = new WaterfallWebServer(operatorChannel, operatorPort, new WaterfallOptions());
        operatorServer.Start();

        Probe visitor = await RunProbeAsync(node, visitorPort);
        Probe op = await RunProbeAsync(node, operatorPort);

        visitor.Thrown.Should().BeEmpty();
        visitor.Connected.Should().BeTrue();
        op.Connected.Should().BeTrue();

        visitor.PublicPage.Hidden["stats"].Should().BeTrue(
            "frames per second and hertz per bin describe the machinery, not the band");
        visitor.PublicPage.Hidden["audioLabel"].Should().BeTrue("a button reading Listen is audio");
        visitor.PublicPage.Hidden["linksLabel"].Should().BeTrue("and one reading Links is AX.25");

        // The chip is the key to the coloured bands, so it stays - saying which mode and where,
        // and no longer which KISS sub-channel or, in a tooltip, which band edges.
        visitor.ChipsOnArrival.Should().ContainSingle();
        visitor.ChipsOnArrival[0].Should().Contain("<b>AFSK1200</b>")
            .And.Contain("1723 Hz", "where it sits is the one figure worth keeping")
            .And.NotContain("<b>0", "the KISS sub-channel is for whoever plugs something in");
        visitor.ChipTitlesOnArrival[0].Should().BeNull(
            "and the band edges it used to carry as a tooltip go with it");
        op.ChipsOnArrival[0].Should().Contain("<b>0 ");
        op.ChipTitlesOnArrival[0].Should().Contain("afsk1200", "the operator's chip is what it was");

        // The one paragraph a visitor reads is untouched: the operator's own words, then whose
        // receiver this is and a link to it.
        visitor.PublicPage.AboutHidden.Should().BeFalse();
        visitor.PublicPage.About.Should().Contain("The 7050-7052 kHz packet window, receive only.")
            .And.Contain("M9PSY-1, Dalgety Bay, Scotland, UK")
            .And.Contain("href=\"https://m9psy-1.instance.ubersdr.org/\"");

        // No dial is configured on either, so the ruler has no RF to show. It asks the operator
        // for one and says nothing to a visitor, who has no control to answer with.
        visitor.DrawnOnArrival.Should().NotContain("Set the dial frequency to see RF");
        op.DrawnOnArrival.Should().Contain("Set the dial frequency to see RF");
    }

    /// <summary>
    /// A public page has no Mine filter in its links pane, and does not filter by one even when a
    /// value left in this origin's storage says it is on.
    /// </summary>
    /// <remarks>
    /// <para>Tom: "Mine wants removing from the public view." The filter keeps only the links this
    /// station is one end of, and the page learns which callsigns those are from watching itself
    /// transmit. A public flavour is receive only and never transmits, so the button asks a
    /// question that has no answer on that page.</para>
    /// <para>Hiding it is not enough by itself. The page's ui state is one localStorage key per
    /// origin, and one origin serves the picker, every receiver's page and, on a station that is
    /// its own site, the operator's page too - so an operator who left Mine on has left it on for
    /// the next visitor, who would then be reading a pane filtered by a button that is no longer
    /// there to turn off. Both browsers here open with that value already stored, and both
    /// flavours are probed, so the difference between them is measured rather than asserted.</para>
    /// </remarks>
    [Fact]
    public async Task The_Public_Page_Hides_The_Mine_Filter()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        // What an operator's page leaves behind in the one key this origin has.
        const string MineLeftOn = """{"linksMine":true}""";

        var visitorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        visitorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int visitorPort = FreePorts.Next();
        await using var visitorServer = new WaterfallWebServer(visitorChannel, visitorPort, new WaterfallOptions
        {
            Public = true,
            Title = "40 m packet monitor",
        });
        visitorServer.Start();

        var operatorChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        operatorChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int operatorPort = FreePorts.Next();
        await using var operatorServer = new WaterfallWebServer(operatorChannel, operatorPort, new WaterfallOptions());
        operatorServer.Start();

        Probe visitor = await RunProbeAsync(node, visitorPort, stored: MineLeftOn);
        Probe op = await RunProbeAsync(node, operatorPort, stored: MineLeftOn);

        visitor.Thrown.Should().BeEmpty();
        visitor.Connected.Should().BeTrue();
        op.Thrown.Should().BeEmpty();
        op.Connected.Should().BeTrue();

        // Both browsers really did open with the filter left on, or neither half of this proves
        // anything at all.
        visitor.MineOnArrival.Stored.Should().BeTrue("the stale value is the whole point of the test");
        op.MineOnArrival.Stored.Should().BeTrue("and the operator's browser starts from the same one");

        // The visitor's page: no button, and the filter off in spite of what was stored.
        visitor.MineOnArrival.Hidden.Should().BeTrue(
            "a receive-only page has no station of its own for Mine to mean anything about");
        visitor.PublicPage.Hidden["linksMine"].Should().BeTrue("and the page's own list of what it hides says so");
        visitor.MineOnArrival.On.Should().BeFalse(
            "a stored value must not be left filtering a pane whose button is gone");

        // The operator's page is untouched: the button is there and it honours what was stored.
        op.MineOnArrival.Hidden.Should().BeFalse("nothing is taken off the operator's page");
        op.MineOnArrival.On.Should().BeTrue("where the button is there to turn it back off");
        // Except the mixer group and the transmit test, which take themselves off a page that has
        // no config API and no transmit control behind them whichever flavour that page is; see
        // The_Public_Page_Hides_The_Sideband_And_Span_Controls.
        op.PublicPage.Hidden.Where(entry => entry.Key is not ("mixerCtl" or "txTestCtl")).Should()
            .AllSatisfy(entry => entry.Value.Should().BeFalse(),
                "the flag only hides, and only on the visitor's page");

        // And it shows in the pane rather than only in the state. A link between two other
        // stations is exactly what the filter takes away: the visitor is shown it, the operator,
        // who asked for this, is not.
        const string BetweenOthers = "0|G4ABC-1<>GB7IOW-1";
        visitor.CardsAfterSecondLink.Should().ContainSingle(card => card.Id == BetweenOthers)
            .Which.Hidden.Should().BeFalse("a public links pane shows every pair the receiver heard");
        op.CardsAfterSecondLink.Should().ContainSingle(card => card.Id == BetweenOthers)
            .Which.Hidden.Should().BeTrue("the operator asked for their own links only, and got them");
    }

    [Fact]
    public async Task A_Page_Served_Over_Https_Opens_Its_Socket_Over_Wss()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions());
        server.Start();

        Probe plain = await RunProbeAsync(node, port);
        Probe secure = await RunProbeAsync(node, port, protocol: "https:");

        plain.SocketUrl.Should().Be($"ws://127.0.0.1:{port}/ws", "an http page keeps the plain socket");
        secure.SocketUrl.Should().Be($"wss://127.0.0.1:{port}/ws",
            "behind a tunnel the page is https, and a browser silently refuses ws:// from there as mixed content");
        secure.Connected.Should().BeTrue("with the right scheme the handshake goes through as before");
        secure.Thrown.Should().BeEmpty();
    }

    /// <summary>
    /// A page served under a receiver's prefix opens its socket under that prefix - the whole of
    /// what makes one process able to serve fifty receivers' pages on one port.
    /// </summary>
    /// <remarks>
    /// The page works its base out from its own path and nothing tells it: there is no build step
    /// and no configuration in it, and a page that had to be told where it was would be a
    /// different page per receiver. This runs the shipping script against a real router with a
    /// real routed server behind it, so what is proved is the whole path - the base the page
    /// derived, the URL it built, and the upgrade arriving at the server the prefix names.
    /// </remarks>
    [Fact]
    public async Task A_Page_Served_Under_A_Prefix_Opens_Its_Socket_Under_That_Prefix()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = WaterfallWebServer.Routed(channel);
        await using var router = new WaterfallRouter(port);
        router.Add(ReceiverBase, server);
        server.Start();
        router.Start();

        Probe probe = await RunProbeAsync(node, port, pathname: ReceiverBase);

        probe.SocketUrl.Should().Be($"ws://127.0.0.1:{port}{ReceiverBase}ws");
        probe.Connected.Should().BeTrue("the socket has to reach the server the prefix names");
        probe.Thrown.Should().BeEmpty();
    }

    /// <summary>
    /// The links window a page tears off is that page's receiver's links window, and at the root
    /// it is the same "/links" it always was.
    /// </summary>
    [Fact]
    public async Task A_Page_Served_Under_A_Prefix_Opens_Its_Links_Window_Under_That_Prefix()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int routerPort = FreePorts.Next();
        await using var routed = WaterfallWebServer.Routed(channel);
        await using var router = new WaterfallRouter(routerPort);
        router.Add(ReceiverBase, routed);
        routed.Start();
        router.Start();

        var ownChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        ownChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int ownPort = FreePorts.Next();
        await using var own = new WaterfallWebServer(ownChannel, ownPort);
        own.Start();

        Probe prefixed = await RunProbeAsync(node, routerPort, pathname: ReceiverBase);
        Probe root = await RunProbeAsync(node, ownPort);

        prefixed.LinksWindowUrl.Should().Be($"{ReceiverBase}links");
        root.LinksWindowUrl.Should().Be(
            "/links", "a station that is its own site opens exactly what it opened before");
        prefixed.Thrown.Should().BeEmpty();
        root.Thrown.Should().BeEmpty();
    }

    /// <summary>
    /// A capture's audio and its sidecar are fetched from the receiver's own survey, not from
    /// whatever happens to sit at the root of the site.
    /// </summary>
    [Fact]
    public async Task Survey_Links_Are_Relative_To_The_Pages_Base()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = WaterfallWebServer.Routed(channel);
        await using var router = new WaterfallRouter(port);
        router.Add(ReceiverBase, server);
        server.Start();
        router.Start();

        Probe probe = await RunProbeAsync(node, port, pathname: ReceiverBase);

        probe.Thrown.Should().BeEmpty();
        probe.FrameRows[RowWith(probe.FrameRows, "unclaimed")].Should()
            .Contain($"{ReceiverBase}survey/20260804-151909-1144hz-unclaimed.wav")
            .And.Contain($"{ReceiverBase}survey/20260804-151909-1144hz-unclaimed.json")
            .And.NotContain("\"/survey/", "an absolute link would fetch another receiver's capture");
    }

    /// <summary>
    /// The torn-off links window knows it is one under a prefix as well as at the root, and
    /// connects back to its own receiver.
    /// </summary>
    /// <remarks>
    /// It is the same page, and the only thing that tells it apart is its path: it hides the
    /// waterfall, asks the server not to send it spectrum lines, and shows the links pane alone.
    /// Reading the last segment rather than the whole path is what makes that work under a prefix,
    /// and "/links" has to keep meaning what it meant.
    /// </remarks>
    [Fact]
    public async Task The_Links_Window_Recognises_Itself_Under_A_Prefix()
    {
        string node = ResolveNode();
        Assert.SkipWhen(node.Length == 0, "node is not installed; the page cannot be executed");

        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int routerPort = FreePorts.Next();
        await using var routed = WaterfallWebServer.Routed(channel);
        await using var router = new WaterfallRouter(routerPort);
        router.Add(ReceiverBase, routed);
        routed.Start();
        router.Start();

        var ownChannel = new SoundModemChannel(SampleRate, randomSeed: 7);
        ownChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int ownPort = FreePorts.Next();
        await using var own = new WaterfallWebServer(ownChannel, ownPort);
        own.Start();

        Probe prefixed = await RunProbeAsync(node, routerPort, pathname: $"{ReceiverBase}links");
        Probe root = await RunProbeAsync(node, ownPort, pathname: "/links");
        Probe waterfall = await RunProbeAsync(node, ownPort);

        prefixed.Detached.Should().BeTrue("the window at <base>links is the links window");
        prefixed.SocketUrl.Should().Be($"ws://127.0.0.1:{routerPort}{ReceiverBase}ws",
            "and it belongs to the receiver whose page it was torn off");
        prefixed.Connected.Should().BeTrue();
        root.Detached.Should().BeTrue("/links is what it always was");
        root.SocketUrl.Should().Be($"ws://127.0.0.1:{ownPort}/ws");
        waterfall.Detached.Should().BeFalse("the page itself is not a links window");
        prefixed.Thrown.Should().BeEmpty();
        root.Thrown.Should().BeEmpty();
    }

    /// <summary>
    /// How a public deployment dressed the page, and what it took away. <see cref="Hidden"/> is
    /// read on every run, public or not, keyed by the page's own list of ids, so that what the
    /// operator still has is measured in the same shape as what the visitor no longer does.
    /// </summary>
    private sealed record PublicPage(
        string Title,
        string BodyClass,
        string About,
        bool AboutHidden,
        IReadOnlyDictionary<string, bool> Hidden);

    private sealed record ListenersChip(bool Hidden, string Html);

    private sealed record Probe(
        string? SocketUrl,
        string? LinksWindowUrl,
        bool Detached,
        PublicPage PublicPage,
        bool Connected,
        bool TxTestOffered,
        string[] TxTestOptions,
        bool TxTestDisabled,
        string TxTestSaid,
        string TxTestGoTitleOnArrival,
        string? TxTestLabel,
        string? TxTestSaidAfter,
        string? TxTestLabelFromRunningConfig,
        string? TxTestLabelFromStoppedConfig,
        string? ClickError,
        bool Listening,
        string Label,
        int BlocksPlayed,
        double PeakAmplitude,
        int BlocksAfterStop,
        string StoppedLabel,
        string[] OrdinaryTag,
        string[] IdentTag,
        string[] HeardTag,
        string[] TxTag,
        string[] FrameRows,
        string[] FrameRowClasses,
        string[] CaptureTag,
        string SurveyStatus,
        string[] HistoryTag,
        string[] HistoryRows,
        string[] HistoryRowClasses,
        string HostileRow,
        string HostileChip,
        string[] LevelRows,
        string? TxHistBorder,
        string? TxBorder,
        string? HistBorder,
        string? RsBadgeBackground,
        string? IdentBadgeBackground,
        string? RsBadgeOnTxRowBackground,
        TxReadout? TxKeyed,
        TxReadout? TxKeyedBadSwr,
        TxReadout? TxHeld,
        TxReadout? TxHeldNoSwr,
        string[] ChipsOnArrival,
        string?[] ChipTitlesOnArrival,
        string[] ChipsAttached,
        string[] ChipsDetached,
        string[] DrawnOnArrival,
        string DialWhat,
        MineFilter MineOnArrival,
        bool LinksHiddenBefore,
        bool LinksHiddenAfter,
        LinksBar LinksOnArrival,
        CardSlot[] CardsOnArrival,
        LinksBar LinksAfterEvent,
        LinkCard[] LinkCards,
        CardSlot[] CardsAfterSecondLink,
        CardSlot[] CardsAfterCall,
        int KeepAlives,
        string TranscriptFailed,
        string TranscriptOurs,
        CardSlot[] CardsAfterTimeout,
        FailedCard? FailedCall,
        CardSlot[] CardsWithUi,
        LinksBar LinksWithUi,
        CardSlot[] CardsMine,
        LinksBar LinksMine,
        string StampedVersion,
        int[] ConfigReloads,
        ListenersChip ListenersOnArrival,
        ListenersChip ListenersDriven,
        ListenersChip ListenersCleared,
        MixerPanel? MixerOnArrival,
        MixerPanel? MixerAfterGain,
        MixerPanel? MixerAfterPlay,
        MeterPanel? Meter,
        MeterPanel? MeterAfterClip,
        MeterPanel? MeterStale,
        MeterPanel? MeterClipExpired,
        bool? TxTestDisabledWhileClosed,
        string? TxTestTitleWhileClosed,
        string[] Thrown);

    /// <summary>
    /// The operator page's Mixer group as the page left it: whether it is on the page at all, and
    /// where each of its two sliders sits.
    /// </summary>
    /// <param name="Hidden">Whether the group is on the page. Null is a page that never touched
    /// it, which is every flavour with no config API behind it.</param>
    /// <param name="ClassName">The group's classes, which carry the locked state.</param>
    /// <param name="Read">The readout beside the capture slider.</param>
    /// <param name="Gain">The capture slider's position, in dB.</param>
    /// <param name="GainMin">The bottom of it, which is the bottom of the card.</param>
    /// <param name="GainMax">The top of it.</param>
    /// <param name="GainDisabled">Whether it can be moved.</param>
    /// <param name="PlayRead">The readout beside the transmit slider.</param>
    /// <param name="Play">The transmit slider's position, in dB.</param>
    /// <param name="PlayMin">The bottom of it: on a card whose lowest step is mute, the lowest
    /// step that is a level.</param>
    /// <param name="PlayMax">The top of it.</param>
    /// <param name="PlayDisabled">Whether it can be moved.</param>
    /// <param name="PlayTitle">Its tooltip, which is where the mute under the bottom is said.</param>
    /// <param name="Note">The sentence under the rows, when there is one the operator has to
    /// read: a control the config file will take back, or a state file that was not written.</param>
    /// <param name="KeyHidden">Whether the Key button is out of the way.</param>
    private sealed record MixerPanel(
        bool? Hidden,
        string? ClassName,
        string? Read,
        string? Gain,
        string? GainMin,
        string? GainMax,
        bool? GainDisabled,
        string? PlayRead,
        string? Play,
        string? PlayMin,
        string? PlayMax,
        bool? PlayDisabled,
        string? PlayTitle,
        string? Note,
        bool? KeyHidden);

    /// <summary>
    /// The input level meter as the page drew it from the daemon's own messages.
    /// </summary>
    /// <param name="Hidden">Whether the meter is on the page. It appears on the first reading,
    /// so false is also "a level message arrived".</param>
    /// <param name="BarWidth">The peak bar's width, as a percentage of the meter.</param>
    /// <param name="BarClass">"hot", "quiet", or neither, which is the colour it is drawn in.</param>
    /// <param name="RmsLeft">Where the RMS hairline sits.</param>
    /// <param name="ZoneLeft">The left edge of the green target band.</param>
    /// <param name="ZoneWidth">Its width.</param>
    /// <param name="HotWidth">The width of the red at the top.</param>
    /// <param name="Read">The dBFS figure beside the bar.</param>
    /// <param name="Advice">The sentence under it, which is what says what good means.</param>
    /// <param name="ClipHidden">Whether the clip pill is on the page at all.</param>
    /// <param name="ClipClass">"lit" while a clip is being shown.</param>
    private sealed record MeterPanel(
        bool? Hidden,
        string? BarWidth,
        string? BarClass,
        string? RmsLeft,
        string? ZoneLeft,
        string? ZoneWidth,
        string? HotWidth,
        string? Read,
        string? Advice,
        bool? ClipHidden,
        string? ClipClass);
}
