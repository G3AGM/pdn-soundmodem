using Microsoft.Data.Sqlite;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A private station listed on this site: its page, its frame log, its links, and the receive
/// loop that turns its relayed audio into a waterfall.
/// </summary>
/// <remarks>
/// <para><b>An ordinary station over a socket.</b> A <see cref="SoundModemChannel"/> at the rate
/// the station said it is relaying at, <b>with no modems added at all</b>, a
/// <see cref="Station"/> over an <see cref="UplinkAudioInput"/>, and a
/// <see cref="WaterfallWebServer"/> routed under <c>/r/&lt;slug&gt;/</c> exactly as a receiver's
/// is. Everything a visitor sees therefore comes out of code that already runs in production; the
/// only new thing is where the samples came from. See <c>docs/dev/archive/uplink-plan.md</c> 4.1.</para>
/// <para><b>The decodes stay the station's own.</b> This process runs no modem for a relayed
/// station and never will: what the page lists is what that operator's daemon decoded, with their
/// modes, their diversity settings and their dial, pushed in through
/// <see cref="WaterfallWebServer.PushFrame"/>. The links panel is folded here from the frames'
/// own bytes rather than sent as a summary, so there is one implementation of a link card and it
/// survives the station going off the air.</para>
/// <para><b>Built on the first hello and kept for the life of the process</b>, exactly as a
/// receiver's station is. That is what makes a page, a frame log and a links panel outlive a
/// station going off air, and it is why there is no teardown anywhere in this class except
/// <see cref="DisposeAsync"/>.</para>
/// </remarks>
internal sealed class RelayStation : IMonitorStation
{
    /// <summary>
    /// How many frames may be held waiting for their audio before the oldest is listed anyway.
    /// </summary>
    /// <remarks>
    /// The hold exists to tag a frame onto the burst that carried it, and it is bounded because
    /// everything on this path is: a station sending frames while its audio has stalled must cost
    /// a slightly wrong burst tag rather than a queue that grows. Twenty is about a second of a
    /// channel doing one frame every 50 ms, which nothing on HF does.
    /// </remarks>
    internal const int MaxHeldFrames = 20;

    /// <summary>
    /// The display line rate to draw a relayed station at: the site's own, where the station's
    /// audio rate allows it, and the next one down that does where it does not. 0 means this
    /// site cannot draw that rate at all.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Dsp.WaterfallSource"/> hops <c>sampleRate / linesPerSecond</c> samples
    /// per line, so the line rate has to divide the sample rate exactly, and the hop has to fit
    /// inside the transform. Thirty lines a second does not divide 8000 or 16000 - and those are
    /// two of the rates a 48 kHz station is offered by its own start-up, because <c>publish</c>
    /// validates <c>audioRate</c> as an integer divisor of its channel. So a station following
    /// its own daemon's advice was refused here, with a sentence naming nothing it could change
    /// and a .NET stack trace in this site's journal.</para>
    /// <para>Deriving the rate rather than refusing it is the answer that needs no agreement
    /// between the two ends: 8000 and 16000 are drawn at 25 lines a second instead of 30, which
    /// nobody can see, and every rate <c>publish</c> will accept is one this site can draw. A
    /// rate no line rate divides - a prime one, which only a hand-written client would send - is
    /// still refused, and now at the boundary with a sentence about rates rather than a stack
    /// trace.</para>
    /// </remarks>
    internal static int LineRateFor(int sampleRate, int wanted, int fftSize)
    {
        if (sampleRate <= 0 || wanted <= 0)
        {
            return 0;
        }

        // The transform WaterfallSource would pick for this rate, if the site has not pinned one.
        int transform = fftSize > 0 ? fftSize : sampleRate >= 24000 ? 8192 : 2048;
        for (int lines = Math.Min(wanted, sampleRate); lines >= 1; lines--)
        {
            if (sampleRate % lines == 0 && sampleRate / lines <= transform)
            {
                return lines;
            }
        }

        return 0;
    }

    private readonly MonitorHostOptions _options;
    private readonly WaterfallRouter _router;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping;
    private readonly FrameLog? _frameLog;

    // Rebuilt when a station reconnects having changed something structural, so neither is
    // readonly. The frame log is, and stays: it is the same file and the same history.
    private SoundModemChannel _channel;
    private UplinkAudioInput _input;

    private readonly Lock _gate = new();
    private readonly Lock _holdGate = new();
    private readonly Queue<(long Target, RelayedFrame Frame)> _held = new();

    private IUplinkLink? _link;
    private Station? _station;
    private Task? _running;

    // Cancelled to stop one receive loop without stopping the station. Disposing the input is not
    // enough on its own: a loop whose Read returns nothing simply goes round again, which is
    // exactly what it should do for a station that is off the air, so a rebuild has to say so.
    private CancellationTokenSource? _loopStopping;
    private ITimer? _linger;
    private int _demand;
    private string? _stationStatus;
    private string? _fault;
    private bool _disposed;
    private bool _routed;

    /// <param name="options">The host's own settings: where the logs go, the page's dressing.</param>
    /// <param name="router">The front door this station's prefix is registered on.</param>
    /// <param name="entry">The configured station, which is where the slug comes from.</param>
    /// <param name="hello">What the station said about itself when it connected.</param>
    /// <param name="stopping">Cancelled when the site is shutting down.</param>
    internal RelayStation(
        MonitorHostOptions options, WaterfallRouter router, UplinkEntry entry, UplinkHello hello,
        CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(hello);

        _options = options;
        _router = router;
        _time = options.TimeProvider;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Slug = entry.Slug;
        Callsign = entry.Callsign;
        Hello = hello;
        Journal = options.Journal(Slug);

        _channel = NewChannel();

        if (options.FrameLogDirectory is { Length: > 0 } directory)
        {
            string path = Path.Combine(directory, $"frames-{Slug}.db");
            try
            {
                _frameLog = FrameLog.Open(path, _time);
                Journal.Write($"frame log: {_frameLog.Path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or SqliteException)
            {
                // The same answer a receiver's station gives: say so and carry on without a log.
                // A page with no history is worth a great deal more than no page.
                Journal.WriteError(
                    $"cannot open the frame log at {path}\n"
                    + $"  {UberSdrDirectory.Ascii(e.Message)}\n"
                    + "  Set by \"frameLog\".\"path\", which is a directory in a monitor.");
            }
        }

        try
        {
            Build();
        }
        catch
        {
            // Half a station is worse than none, and for the same reasons a receiver's is: an
            // open SQLite handle with a writer thread, a route registered, and no reference to
            // any of it anywhere.
            Unbuild();
            throw;
        }

        _input = NewInput();
        StartLoop();
    }

    /// <inheritdoc />
    public string Slug { get; }

    /// <inheritdoc />
    public WaterfallWebServer Server { get; private set; } = null!;

    /// <summary>The callsign this station's token was issued to. Never comes off the wire.</summary>
    internal string Callsign { get; }

    /// <summary>What it said about itself when it last connected.</summary>
    /// <remarks>
    /// Replaced on every reconnect by <see cref="ApplyAsync"/>, not frozen at the first one: an
    /// operator who changes their <c>publish</c> block and restarts has to see the change.
    /// </remarks>
    internal UplinkHello Hello { get; private set; }

    /// <summary>This station's own journal, tagged with its slug.</summary>
    internal StationJournal Journal { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Null, always. A receiver's station has one sentence the receiver itself supplied once a
    /// session was open; a relayed station says who and what it is in its row's own fields, off
    /// its hello, and a second summary of the same words would only be another thing to keep in
    /// step.
    /// </remarks>
    public string? Description => null;

    /// <summary>Whether its uplink is up right now.</summary>
    internal bool Connected => _input.Connected;

    /// <summary>How this station is credited on its page and named in its status chip.</summary>
    internal string Credit => string.Join(
        ", ",
        new[] { Callsign, Hello.Operator, Hello.Location }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// The station's own rate, and no modems: this process decodes nothing for it.
    /// </summary>
    /// <remarks>
    /// The receive-only reason is said here for the same reason <c>MonitorStation</c> says it -
    /// the channel is the same class a transmitting station uses, so it is told once, in one
    /// place, and anything that could ever put something on the air gets the same answer.
    /// </remarks>
    private SoundModemChannel NewChannel() => new(Hello.AudioRate)
    {
        ReceiveOnlyReason =
            "this station receives only: its audio is relayed from "
            + $"{UberSdrDirectory.Ascii(Callsign)}'s own receiver over a one-way uplink, and "
            + "nothing on this site can reach its transmitter.",
    };

    private void Build()
    {
        Server = WaterfallWebServer.Routed(_channel, new WaterfallOptions
        {
            DialFrequencyHz = Hello.DialHz,
            Sideband = Hello.Sideband,
            LinesPerSecond = LineRateFor(Hello.AudioRate, _options.LinesPerSecond, _options.FftSize),
            FftSize = _options.FftSize,
            Public = true,
            Title = _options.Title,
            About = _options.About,
            // Two levels, not one, for the reason #399 gives for a receiver's page: a relayed
            // station's page is served at /r/<slug>/ too, so "../" only climbs to /r/, which the
            // router answers with a 404.
            PickerUrl = "../../",
            // What its modems occupy, off the wire. Nothing enumerable carries them because this
            // process runs none of them, which is exactly what DeclaredBands is for.
            DeclaredBands = Hello.Bands,
            ReceiverKind = "station",
            FrameHistory = _frameLog is null ? null : _frameLog.Recent,
            TimeProvider = _time,
            // This station's own tagged sink, so that a page dropped for not answering names the
            // relayed station whose page it was on.
            Log = Journal.Write,
        });

        Server.SetReceiver(Credit, Hello.Site);
        Server.SetRadioStatus(Chip("connected"));

        if (_frameLog is not null)
        {
            StationFactory.BackfillLinks(Server, _frameLog);
        }

        Server.ViewersChanged += OnViewersChanged;
        Server.Start();
        _router.Add($"/r/{Slug}/", Server);
        _routed = true;
        Journal.Write($"station: {Server.Url} for {UberSdrDirectory.Ascii(Callsign)}");
    }

    private UplinkAudioInput NewInput() =>
        new(Hello.AudioRate, transmit => Server.IncomingIsTransmit = transmit)
        {
            BeforeRead = ReleaseDueFrames,
        };

    /// <summary>
    /// Starts the receive loop over the uplink's audio.
    /// </summary>
    /// <remarks>
    /// Its own thread, as every station's is: <c>Read</c> is synchronous and blocking, so a host
    /// running fifty of these cannot share a pool with them. The loop turns whether or not a
    /// station is connected - <c>Read</c> waits and returns 0, which is the shape every network
    /// input here has - so there is nothing to start and stop as stations come and go.
    /// </remarks>
    private void StartLoop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Built outside the lock: a Station arms its starvation watch on this same clock, and a
        // TimeProvider holds its own lock while running a callback that takes this one.
        var loopStopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        UplinkAudioInput input = _input;
        Station station = new Station(
                new StationOptions
                {
                    Channel = _channel,
                    Input = input,
                    DspRate = Hello.AudioRate,
                    Journal = Journal,
                    DeviceKind = DeadFeedDevice.Uplink,
                    DeadFeed = _options.DeadFeed,
                    // Quiet with nobody watching is the whole design, and quiet with nobody
                    // connected is a station that is off the air; neither is a starved feed. What
                    // is left, and what this watch is for, is a socket that is up and demanded
                    // and delivering nothing, which is a half-open connection.
                    SessionLive = () => _input.Connected && Server.Viewers > 0,
                    TimeProvider = _time,
                    HealthChecks = [FrameLogDropCheck(), JitterDropCheck()],

                    // Audio the jitter buffer had no room for, a reconnect, and a stream resumed
                    // after nobody was watching are real holes; a Read that comes back empty is
                    // not, because the socket delivers late audio whole.
                    InputLossCount = () => input.Discontinuities,
                },
                loopStopping.Token);

        bool wanted;
        lock (_gate)
        {
            wanted = !_disposed;
            if (wanted)
            {
                _loopStopping = loopStopping;
                _station = station;
            }
        }

        if (!wanted)
        {
            // Disposed while this was being built. Take it back down rather than starting a loop
            // nothing will ever stop.
            station.Dispose();
            loopStopping.Dispose();
            return;
        }

        station.Faulted += OnFault;
        _running = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    station.Run();
                }
                finally
                {
                    Stopped(station);
                    loopStopping.Dispose();
                }
            },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>The status chip, which always names the station: the sentence says what its
    /// uplink is doing and not whose radio it is.</summary>
    private string Chip(string sentence) => $"{Credit} - {sentence}";

    // ------------------------------------------------------------------ reconnecting

    /// <summary>
    /// Applies what a station said about itself this time, which may not be what it said last
    /// time.
    /// </summary>
    /// <remarks>
    /// <para>An operator changes their <c>publish</c> block and restarts their daemon; that is
    /// the whole way any of this is configured, and docs/reference/config.md tells somebody on ADSL that
    /// <c>audioRate</c> is one of their two levers. A station whose new hello was welcomed and
    /// then discarded went on being drawn at the old rate while its audio was checked against the
    /// new block length, so the page was silently wrong until somebody restarted the public site,
    /// and their new operator, location, radio and site never appeared at all.</para>
    /// <para>Most of a hello is words, and words are applied in place: the credit, the picker row
    /// and the status chip all read <see cref="Hello"/>. The rest - the rate, the dial, the
    /// sideband, the bands - is what the channel and the waterfall were built from, and changing
    /// any of it rebuilds them. That costs whoever is watching a reconnect of their page, which
    /// is exactly the moment a page needs rebuilding anyway. <b>The frame log is kept</b>, so the
    /// history and the links survive it.</para>
    /// </remarks>
    internal async Task ApplyAsync(UplinkHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        UplinkHello was = Hello;
        Hello = hello;

        if (!Structural(was, hello))
        {
            // Words only. SetReceiver rebuilds the config message, so a browser that arrives
            // after this gets the new credit and the new link.
            Server.SetReceiver(Credit, hello.Site);
            Server.SetRadioStatus(Chip(_stationStatus ?? "connected"));
            return;
        }

        Journal.Write(
            $"uplink: {UberSdrDirectory.Ascii(Callsign)} came back with a different "
            + $"{string.Join(" and ", Changes(was, hello))}, so its page is being rebuilt; its "
            + "frame log and its links are kept");
        await RebuildAsync().ConfigureAwait(false);
    }

    /// <summary>Whether the difference between two hellos is one the built objects hold.</summary>
    private static bool Structural(UplinkHello was, UplinkHello now) =>
        was.AudioRate != now.AudioRate
        || was.DialHz != now.DialHz
        || !string.Equals(was.Sideband, now.Sideband, StringComparison.Ordinal)
        || !was.Bands.SequenceEqual(now.Bands);

    /// <summary>What changed, for the one journal line that says a page is being rebuilt.</summary>
    private static IEnumerable<string> Changes(UplinkHello was, UplinkHello now)
    {
        if (was.AudioRate != now.AudioRate)
        {
            yield return $"audio rate ({was.AudioRate} -> {now.AudioRate} Hz)";
        }

        if (was.DialHz != now.DialHz)
        {
            yield return "dial";
        }

        if (!string.Equals(was.Sideband, now.Sideband, StringComparison.Ordinal))
        {
            yield return "sideband";
        }

        if (!was.Bands.SequenceEqual(now.Bands))
        {
            yield return "band plan";
        }
    }

    /// <summary>
    /// Takes this station's page and channel down and puts them back up from the current hello.
    /// </summary>
    /// <remarks>
    /// Everything but the frame log: that is the same file and the same history, and reopening it
    /// would be the one part of this with anything to lose. The route goes and comes back under
    /// the same prefix, so a bookmark still works and a browser that was watching reconnects to a
    /// page built from what the station is actually sending now.
    /// </remarks>
    private async Task RebuildAsync()
    {
        UplinkAudioInput oldInput;
        Station? oldStation;
        Task? running;
        WaterfallWebServer oldServer;
        CancellationTokenSource? oldLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            oldInput = _input;
            oldStation = _station;
            running = _running;
            oldServer = Server;
            oldLoop = _loopStopping;
            _station = null;
            _loopStopping = null;
        }

        // The loop is stopped by its own token rather than by taking its input away: a Read that
        // returns nothing is a station that is off the air, which is a thing the loop is meant to
        // sit through rather than end on.
        if (oldLoop is not null)
        {
            await oldLoop.CancelAsync().ConfigureAwait(false);
        }

        if (_routed)
        {
            _router.Remove($"/r/{Slug}/");
            _routed = false;
        }

        oldServer.ViewersChanged -= OnViewersChanged;

        // The input first: its Read is what the loop is sitting in, and disposing it is what lets
        // the loop notice. The loop's own Stopped will find _station null and not restart it.
        oldInput.Dispose();
        if (running is not null)
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Journal.WriteError(
                    "uplink: the old receive loop did not stop inside five seconds; rebuilding "
                    + "the page anyway");
            }
        }

        if (oldStation is not null)
        {
            oldStation.Faulted -= OnFault;
            oldStation.Dispose();
        }

        try
        {
            await oldServer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Journal.WriteError(
                "uplink: the old page did not close inside five seconds; rebuilding anyway");
        }


        // Anything still held against the old input's sample counts belongs to audio that no
        // longer exists.
        lock (_holdGate)
        {
            _held.Clear();
        }

        _channel = NewChannel();
        Build();
        _input = NewInput();
        _input.Connected = true;
        StartLoop();
    }

    // ------------------------------------------------------------------ the uplink's own side

    /// <summary>A station has connected: this is now its live socket.</summary>
    internal void Attach(IUplinkLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_gate)
        {
            _link = link;
            _fault = null;
        }

        // Anything still queued is from before the gap, and painting it now would put a burst on
        // the display long after it happened.
        _input.Flush();
        _input.MarkBreak();
        _input.Connected = true;
        Server.SetRadioStatus(Chip(_stationStatus ?? "connected"));
    }

    /// <summary>That socket has gone. The page, the log and the links stay exactly as they are.</summary>
    internal void Detach(IUplinkLink link)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_link, link))
            {
                return;   // a ghost that was superseded; the live one is somebody else's
            }

            _link = null;
            _stationStatus = null;
        }

        _input.Connected = false;
        _input.Flush();
        Server.SetRadioStatus(Chip("not connected just now"));
    }

    /// <summary>
    /// Tells the station how many people are watching it, on whichever socket is live.
    /// </summary>
    /// <remarks>
    /// Called on every change, and again on the heartbeat so a station that missed one still
    /// finds out. This is the entire downward protocol.
    /// </remarks>
    internal void Announce(IUplinkLink? link = null)
    {
        int viewers;
        IUplinkLink? live;
        lock (_gate)
        {
            viewers = _demand;
            live = link ?? _link;
        }

        live?.Demand(viewers);
    }

    /// <summary>Audio off the wire, as the little-endian bytes it arrived as.</summary>
    internal void PushAudio(ReadOnlySpan<byte> pcm, bool transmitted) =>
        _input.Push(pcm, transmitted);

    /// <summary>The station's own sentence about its radio, for the page's status chip.</summary>
    internal void PushRadio(string? status)
    {
        lock (_gate)
        {
            _stationStatus = status;
        }

        Server.SetRadioStatus(Chip(status is { Length: > 0 } ? status : "connected"));
    }

    /// <summary>
    /// A frame the station decoded, held until the audio that carried it has been painted.
    /// </summary>
    /// <remarks>
    /// A frame crosses the wire as soon as the station decodes it, but the audio that carried the
    /// burst is still in the jitter buffer here. <c>BroadcastFrame</c> tags a frame onto the line
    /// the display has reached, so listing it on arrival would tag it five to twelve lines above
    /// its own burst - visibly wrong on a display doing thirty lines a second. So it waits until
    /// as much audio has left the buffer as was in it when the frame arrived. Self-correcting
    /// across an overrun and across a reconnect, because dropped audio counts as consumed.
    /// </remarks>
    internal void PushFrame(RelayedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_holdGate)
        {
            _held.Enqueue((_input.Accepted, frame));
        }

        // A frame that arrived with the buffer already empty - which is every frame on a station
        // nobody is watching - has nothing to wait for and goes out now.
        ReleaseDueFrames();
    }

    /// <summary>Lists every held frame whose audio has now been read.</summary>
    private void ReleaseDueFrames()
    {
        // The whole drain under one lock, so that the audio thread and the socket thread cannot
        // interleave two releases and list them in the wrong order.
        lock (_holdGate)
        {
            long consumed = _input.Consumed;
            while (_held.Count > 0
                   && (_held.Peek().Target <= consumed || _held.Count > MaxHeldFrames))
            {
                RelayedFrame frame = _held.Dequeue().Frame;
                Log(frame);
                Server.PushFrame(frame);
            }
        }
    }

    /// <summary>
    /// Writes a relayed frame into this station's own log, with the station's own timestamp.
    /// </summary>
    /// <remarks>
    /// Only where the frame carried bytes. An ident ghost, and a decoder that hands none over,
    /// are listed on the page and make no row, exactly as they make no link: the log's columns
    /// are derived from the frame, and there is nothing here to derive them from.
    /// </remarks>
    private void Log(RelayedFrame frame)
    {
        if (_frameLog is null || frame.Raw is not { Length: > 0 } raw)
        {
            return;
        }

        if (frame.Transmitted)
        {
            // No held time: the wait belongs to the relaying station's own channel access, and
            // this station has no way to know what it was. Null rather than zero, which would
            // read as a relayed frame that never waited.
            _frameLog.RecordTransmitted(
                frame.SubChannel, raw, frame.Mode, audioHz: null, rfHz: null,
                frame.TransmitTrimHz, at: frame.At);
            return;
        }

        _frameLog.Record(
            frame.SubChannel, raw,
            new FrameQuality(
                frame.Mode, frame.LengthBytes, frame.CorrectedBytes, frame.CrcValid,
                FrequencyOffsetHz: frame.OffsetHz, PlainIl2p: frame.PlainIl2p,
                MonitorOnly: frame.MonitorOnly, SnrDb: frame.SnrDb,
                // What stood behind the far station's reading, written into this site's copy of
                // its log so that a row replayed out of the backlog withholds the callsign the
                // live row withheld. Null from a station too old to send them, and a row with
                // nothing said keeps its callsign.
                TrailerNearBits: frame.TrailerNearBits, ChasedBits: frame.ChasedBits,
                // The far station's own verdict, carried into this site's copy of its log
                // rather than recomputed: the thresholds belong to the modem that decoded the
                // frame, and that modem is at the other end of the uplink. Null from a station
                // too old to send one, and a row with no verdict earns no badge.
                PeakDbFs: frame.PeakDbFs, Clipped: frame.Clipped, Level: frame.Level,
                PeakWorthShowing: frame.PeakWorthShowing),
            audioHz: null, rfHz: null, modeName: null, at: frame.At);
    }

    // ------------------------------------------------------------------ viewers, and the linger

    /// <summary>
    /// The page's viewer count, which is the whole mechanism: leaving zero asks the station for
    /// audio and returning there stops it a linger later, and it is the same count however many
    /// browsers are behind it.
    /// </summary>
    private void OnViewersChanged(int viewers)
    {
        bool announce = false;
        bool resumed = false;
        bool armLinger = false;
        ITimer? cancelled = null;
        lock (_gate)
        {
            if (viewers > 0)
            {
                cancelled = _linger;
                _linger = null;
                if (_demand != viewers)
                {
                    resumed = _demand == 0;
                    _demand = viewers;
                    announce = true;
                }
            }
            else if (_demand != 0 && _linger is null && !_disposed)
            {
                armLinger = true;
            }
        }

        // Outside the lock, both of them, and this is not tidiness. A TimeProvider holds its own
        // lock while it runs a callback, and the callbacks here take this station's lock; taking
        // them in the other order - this lock, then the clock's, to make or unmake a timer - is a
        // deadlock waiting for a viewer to arrive at the moment a linger expires. It hung the
        // test class about one run in three before it was found.
        cancelled?.Dispose();

        if (armLinger)
        {
            // The same linger a receiver's session gets, and for the same reason: a page refresh
            // or a tab switch must not stop and restart a home station's stream.
            ITimer timer = _time.CreateTimer(
                _ => LingerExpired(), null, _options.Linger, Timeout.InfiniteTimeSpan);
            bool kept;
            lock (_gate)
            {
                // Somebody may have arrived while this was being made, which is the whole reason
                // to check rather than assume.
                kept = _linger is null && !_disposed && _demand != 0 && Server.Viewers == 0;
                if (kept)
                {
                    _linger = timer;
                }
            }

            if (!kept)
            {
                timer.Dispose();
            }
        }

        // The station has sent nothing since demand went to 0, so what it sends now follows a
        // stretch nobody heard. Marked before the demand goes out, so before any of that audio.
        if (resumed)
        {
            // Null only for the moment between building the page and making the input.
            _input?.MarkBreak();
        }

        if (announce)
        {
            Announce();
        }
    }

    private void LingerExpired()
    {
        ITimer? spent;
        bool announce = false;
        lock (_gate)
        {
            spent = _linger;
            _linger = null;
            if (Server.Viewers == 0 && _demand != 0)
            {
                _demand = 0;
                announce = true;
            }
        }

        // This is the timer's own callback, so the clock's lock is already this thread's; the
        // rule is about not taking it while holding the station's.
        spent?.Dispose();

        if (announce)
        {
            Announce();
        }
    }

    // ------------------------------------------------------------------ what the picker is told

    /// <inheritdoc />
    public (string State, string? Status) State()
    {
        lock (_gate)
        {
            if (_fault is { } fault)
            {
                return ("faulted", fault);
            }

            if (!_input.Connected)
            {
                return ("offline", "not connected just now");
            }

            string sentence = _stationStatus ?? "connected";
            return (Server.Viewers > 0 ? "live" : _demand > 0 ? "lingering" : "idle", sentence);
        }
    }

    /// <inheritdoc />
    public object Row(bool listed)
    {
        (string state, string? status) = State();
        bool connected = _input.Connected;
        return new
        {
            slug = Slug,
            kind = "station",
            callsign = Callsign,
            @operator = Hello.Operator,
            Hello.Location,
            Hello.Radio,
            // What it runs, off its own hello. The names are the modes' own, capped and escaped
            // like everything else it sends.
            modes = Hello.Bands.Select(b => ModeNames.Display(b.Mode)).Distinct().ToArray(),
            publicUrl = Hello.Site,
            offered = connected,
            why = connected ? null : "not connected just now",
            state,
            status,
            viewers = Server.Viewers,
            // The five figures a station has no honest answer for. Present and null rather than
            // absent, so a reader of this API sees one row shape: they are facts about a web
            // receiver, and inventing them for somebody's transceiver would be inventing them.
            host = (string?)null,
            name = (string?)null,
            snrDb = (double?)null,
            loadStatus = (string?)null,
            availableClients = (int?)null,
            maxClients = (int?)null,
            description = (string?)null,
        };
    }

    // ------------------------------------------------------------------ faults

    /// <summary>
    /// The receive loop has faulted. On a relayed station that means one thing - a socket that is
    /// up, demanded, and delivering nothing - so the answer is to drop it and let the station
    /// reconnect on its own ladder.
    /// </summary>
    private void OnFault(StationFault fault)
    {
        IUplinkLink? link;
        lock (_gate)
        {
            _fault = fault.Reason;
            link = _link;
        }

        Journal.WriteError(fault.Reason);
        Server.SetRadioStatus(Chip(fault.Reason));

        // Deliberately not Environment.Exit on fault.Stalled, which is what a receiver's station
        // does: this input's Read waits on an event with a timeout and cannot block for ever, so
        // there is nothing here that a process restart could unwedge that closing the socket
        // cannot.
        link?.Close("this site asked for audio and got none; reconnect when you can");
    }

    /// <summary>The loop has returned. Start another, because this station has no other way to
    /// hear anything, and building one costs no network at all.</summary>
    private void Stopped(Station station)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_station, station))
            {
                return;
            }

            _station = null;
        }

        station.Faulted -= OnFault;
        station.Dispose();

        if (!_stopping.IsCancellationRequested && !_disposed)
        {
            StartLoop();
        }
    }

    /// <summary>
    /// A full disk left a station keeping an empty frame log for weeks with nothing anywhere
    /// saying so. Polled from the receive loop, which turns whether or not audio is flowing.
    /// </summary>
    private Func<string?> FrameLogDropCheck()
    {
        long seen = 0;
        return () =>
        {
            if (_frameLog is null)
            {
                return null;
            }

            long drops = _frameLog.Dropped;
            if (drops <= seen)
            {
                return null;
            }

            string line =
                $"frame log: {drops - seen} frames dropped unwritten ({drops} total) - the disk "
                + "cannot keep up, is full, or is unwritable";
            seen = drops;
            return line;
        };
    }

    /// <summary>
    /// Audio thrown away because the jitter buffer overran. Worth one line rather than none: it
    /// means this site cannot keep up with the station or the station is sending faster than real
    /// time, and either way somebody watching is seeing a display with holes in it.
    /// </summary>
    private Func<string?> JitterDropCheck()
    {
        long seen = 0;
        return () =>
        {
            long drops = _input.Dropped;
            if (drops <= seen)
            {
                return null;
            }

            string line =
                $"uplink: dropped {(drops - seen) * 1000 / Hello.AudioRate} ms of relayed audio "
                + "the jitter buffer had no room for";
            seen = drops;
            return line;
        };
    }

    /// <summary>Takes down whatever <see cref="Build"/> managed to put up, on the way out of a
    /// constructor that is about to throw.</summary>
    private void Unbuild()
    {
        try
        {
            if (_routed)
            {
                _router.Remove($"/r/{Slug}/");
                _routed = false;
            }

            if (Server is { } server)
            {
                server.ViewersChanged -= OnViewersChanged;
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _frameLog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Journal.WriteError(
                "could not fully take down a station that would not build - "
                + UberSdrDirectory.Ascii(e.Message));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Station? station;
        Task? running;
        ITimer? linger;
        lock (_gate)
        {
            _disposed = true;
            linger = _linger;
            _linger = null;
            station = _station;
            _station = null;
            running = _running;
        }

        linger?.Dispose();   // outside the lock, for the reason OnViewersChanged gives
        await _stopping.CancelAsync().ConfigureAwait(false);   // and any live loop's own with it
        Server.ViewersChanged -= OnViewersChanged;

        // The input first: its Read is what the loop is sitting in, and disposing it is what lets
        // the loop notice it has been asked to stop.
        _input.Dispose();
        if (running is not null)
        {
            await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        station?.Dispose();
        try
        {
            await Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A browser that will not let go must not stop this station's frame log being closed
            // tidily, which is where somebody's afternoon of frames is.
        }

        if (_frameLog is not null)
        {
            await _frameLog.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }
}
