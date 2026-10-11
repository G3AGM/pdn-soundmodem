using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The connect-on-demand state machine behind the public 40 m monitor
/// (<c>docs/dev/archive/40m-monitor-plan.md</c>): a session on somebody else's receiver exists only while
/// a browser has the page open, survives a viewer's brief absence, and is retried rather than
/// restarted into. Driven here with a fake session and a fake clock; the socket side is
/// <see cref="UberSdrAudioInput"/>'s and is covered elsewhere.
/// </summary>
public class OnDemandUberSdrInputTests
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);
    private static readonly UberSdrEndpoint Endpoint = new("rx.example.org", 443, true);

    /// <summary>The line #409 is about, composed by the receive loop's own function rather than
    /// typed here, so these tests hold which of its lines are waits as well as how they read.
    /// </summary>
    private static UberSdrLine ShortSession => UberSdrAudioInput.SessionEndedLine(
        Endpoint,
        healthy: false,
        lasted: TimeSpan.FromMilliseconds(41),
        audioSamples: 0,
        outputRate: 12000,
        reasonAlreadyLogged: true,
        pause: TimeSpan.FromSeconds(300));

    [Fact]
    public void Starts_Idle_And_Reads_Nothing_Until_Somebody_Is_Watching()
    {
        using var h = new Harness();

        h.Input.Phase.Should().Be(OnDemandPhase.Idle);
        h.Input.SessionLive.Should().BeFalse();
        h.Input.Read(new float[64]).Should().Be(0);
        h.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task The_First_Viewer_Opens_The_Receiver_And_Audio_Then_Flows()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        h.Input.Phase.Should().Be(OnDemandPhase.Connecting);
        TaskCompletionSource<IUberSdrSession> attempt = await h.NextAttemptAsync();
        var session = new FakeSession { SessionLive = true };
        attempt.SetResult(session);

        await h.Announced(OnDemandPhase.Live);
        h.Input.SessionLive.Should().BeTrue();
        h.Input.Read(new float[64]).Should().Be(64);
        session.Reads.Should().Be(1);
        h.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task The_Last_Viewer_Leaving_Holds_The_Session_For_The_Linger_Then_Closes_It()
    {
        using var h = new Harness();
        FakeSession session = await h.GoLiveAsync();

        h.Input.SetViewers(0);
        h.Input.Phase.Should().Be(OnDemandPhase.Lingering);
        session.Disposed.Should().BeFalse();

        h.Time.Advance(Linger - TimeSpan.FromSeconds(1));
        h.Input.Phase.Should().Be(OnDemandPhase.Lingering);
        session.Disposed.Should().BeFalse("a refresh should not cost the receiver a session");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Input.Phase.Should().Be(OnDemandPhase.Idle);
        session.Disposed.Should().BeTrue();
        h.Input.SessionLive.Should().BeFalse();
        h.Input.Read(new float[64]).Should().Be(0);
    }

    [Fact]
    public async Task A_Viewer_Returning_Inside_The_Linger_Keeps_The_Same_Session()
    {
        using var h = new Harness();
        FakeSession session = await h.GoLiveAsync();

        h.Input.SetViewers(0);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Input.SetViewers(1);

        h.Input.Phase.Should().Be(OnDemandPhase.Live);
        h.Time.Advance(Linger * 2);
        h.Input.Phase.Should().Be(OnDemandPhase.Live, "the cancelled linger must not fire later");
        session.Disposed.Should().BeFalse();
        h.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task More_Viewers_Share_The_One_Session()
    {
        using var h = new Harness();
        await h.GoLiveAsync();

        h.Input.SetViewers(2);
        h.Input.SetViewers(3);
        h.Input.SetViewers(1);

        h.Input.Phase.Should().Be(OnDemandPhase.Live);
        h.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_Viewer_Who_Leaves_While_Connecting_Still_Gets_The_Session_Closed()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        TaskCompletionSource<IUberSdrSession> attempt = await h.NextAttemptAsync();
        h.Input.SetViewers(0);
        var session = new FakeSession();
        attempt.SetResult(session);

        await h.Announced(OnDemandPhase.Lingering);
        session.Disposed.Should().BeFalse();
        h.Time.Advance(Linger);
        h.Input.Phase.Should().Be(OnDemandPhase.Idle);
        session.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_Failed_Open_Is_Retried_On_The_Ladder_While_Somebody_Waits()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("connection refused"));
        await h.Announced(OnDemandPhase.Retrying);
        h.Attempts.Should().Be(1);

        // First transient rung is one second.
        h.Time.Advance(TimeSpan.FromMilliseconds(999));
        h.Attempts.Should().Be(1);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        TaskCompletionSource<IUberSdrSession> second = await h.NextAttemptAsync();
        h.Input.Phase.Should().Be(OnDemandPhase.Connecting);

        second.SetResult(new FakeSession { SessionLive = true });
        await h.Announced(OnDemandPhase.Live);
        h.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_Refusal_Waits_On_The_Long_Rung()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        // What the pre-flight throws for "allowed: false" with no transport error underneath.
        (await h.NextAttemptAsync()).SetException(
            new InvalidOperationException("rx.example.org refused the connection: not on the list"));
        await h.Announced(OnDemandPhase.Retrying);

        h.Time.Advance(TimeSpan.FromSeconds(59));
        h.Attempts.Should().Be(1, "a refusal is not something to re-ask every second");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.NextAttemptAsync();
        h.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_Refusal_Is_Told_Apart_From_Everything_Else_That_Will_Not_Open()
    {
        // A host listing several receivers has to be able to say which of them is refusing it
        // rather than which is broken: "the daily allowance for this monitor is used up, back
        // tomorrow" is a sentence a visitor can act on, and "unreachable" is not the same thing.
        // The ladder already knows the difference; this is that knowledge, readable from outside.
        using var h = new Harness();
        h.Input.Refused.Should().BeFalse("nothing has been asked of the receiver yet");

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(
            new InvalidOperationException("rx.example.org refused the connection: quota spent"));
        await h.Announced(OnDemandPhase.Retrying);
        h.Input.Refused.Should().BeTrue();

        // A transport failure is not a refusal, however it is retried.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("connection refused"));
        await h.Announced(OnDemandPhase.Retrying);
        h.Input.Refused.Should().BeFalse();

        // And a session that opens clears it. Past the transient cap, so the rung the ladder has
        // climbed to by now does not matter.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        (await h.NextAttemptAsync()).SetResult(new FakeSession { SessionLive = true });
        await h.Announced(OnDemandPhase.Live);
        h.Input.Refused.Should().BeFalse();
    }

    [Fact]
    public async Task A_Failed_Open_With_Nobody_Waiting_Goes_Idle_Without_Retrying()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        TaskCompletionSource<IUberSdrSession> attempt = await h.NextAttemptAsync();
        h.Input.SetViewers(0);
        attempt.SetException(new HttpRequestException("connection refused"));

        await h.Announced(OnDemandPhase.Idle);
        h.Time.Advance(TimeSpan.FromMinutes(20));
        h.Attempts.Should().Be(1, "nobody is waiting for a retry");
    }

    [Fact]
    public async Task The_Last_Viewer_Leaving_Abandons_A_Pending_Retry()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("connection refused"));
        await h.Announced(OnDemandPhase.Retrying);

        h.Input.SetViewers(0);
        h.Input.Phase.Should().Be(OnDemandPhase.Idle);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_Session_That_Gives_Up_Is_Reopened_Rather_Than_Restarted_Into()
    {
        using var h = new Harness();
        FakeSession first = await h.GoLiveAsync();

        first.GiveUp("rx.example.org has been unreachable for 5 minutes");

        await h.Announced(OnDemandPhase.Retrying);
        await Within(first.WhenDisposed, "the session that gave up to be disposed");
        h.Input.SessionLive.Should().BeFalse();
        h.Input.Read(new float[64]).Should().Be(0);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        TaskCompletionSource<IUberSdrSession> attempt = await h.NextAttemptAsync();
        var second = new FakeSession { SessionLive = true };
        attempt.SetResult(second);
        await h.Announced(OnDemandPhase.Live);
        h.Input.Read(new float[64]).Should().Be(64);
        second.Reads.Should().Be(1);
    }

    [Fact]
    public async Task A_Healthy_Session_Resets_The_Ladder()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("refused"));
        await h.Announced(OnDemandPhase.Retrying);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("refused"));
        await h.Announced(OnDemandPhase.Retrying);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        FakeSession session = new() { SessionLive = true };
        (await h.NextAttemptAsync()).SetResult(session);
        await h.Announced(OnDemandPhase.Live);

        session.GiveUp("gone");
        await h.Announced(OnDemandPhase.Retrying);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.NextAttemptAsync();
        h.Attempts.Should().Be(4, "the healthy session put the ladder back to one second");
    }

    [Fact]
    public async Task Dispose_Closes_The_Open_Session()
    {
        var h = new Harness();
        FakeSession session = await h.GoLiveAsync();

        h.Input.Dispose();

        session.Disposed.Should().BeTrue();
        h.Input.Read(new float[64]).Should().Be(0);
    }

    [Fact]
    public async Task A_Session_Arriving_After_Dispose_Is_Closed_Not_Kept()
    {
        var h = new Harness();
        h.Input.SetViewers(1);
        TaskCompletionSource<IUberSdrSession> attempt = await h.NextAttemptAsync();

        h.Input.Dispose();
        var session = new FakeSession();
        attempt.SetResult(session);

        await Within(session.WhenDisposed, "the late session to be disposed");
    }

    [Fact]
    public async Task Phase_Changes_Are_Announced_And_Live_Names_The_Receiver()
    {
        using var h = new Harness(description: "M9PSY-1, Somewhere, reference offset 0 Hz");
        await h.GoLiveAsync();
        h.Input.SetViewers(0);
        h.Time.Advance(Linger);

        h.Phases.Select(p => p.Phase).Should().Equal(
            OnDemandPhase.Connecting, OnDemandPhase.Live, OnDemandPhase.Lingering, OnDemandPhase.Idle);
        h.Phases[1].Sentence.Should().Be("M9PSY-1, Somewhere, reference offset 0 Hz");
        h.Phases[2].Sentence.Should().Contain("60 s");
        h.Phases[3].Sentence.Should().Contain("when someone is watching");
        h.Phases.Select(p => p.Sentence).Should().OnlyContain(s => s.All(c => c < 128), "the chip and the journal are ASCII");
    }

    [Fact]
    public async Task Every_Phase_Line_Says_How_Many_Are_Watching()
    {
        using var h = new Harness(description: "M9PSY-1, Somewhere");

        h.Input.SetViewers(2);
        (await h.NextAttemptAsync()).SetResult(new FakeSession { SessionLive = true });
        await h.LinesReach(2);
        h.Input.SetViewers(0);
        h.Time.Advance(Linger);
        await h.LinesReach(4);

        h.Lines.Should().Equal(
            "ubersdr: connecting, 2 viewers: connecting to rx.example.org",
            "ubersdr: live, 2 viewers: M9PSY-1, Somewhere",
            "ubersdr: lingering, 0 viewers: no viewers; holding the session with rx.example.org "
                + "for 60 s",
            "ubersdr: idle, 0 viewers: idle; connects to rx.example.org when someone is watching");
        h.Lines.Should().OnlyContain(l => l.All(c => c < 128), "the journal is ASCII");
    }

    [Fact]
    public async Task The_Sessions_Own_Lines_Carry_The_Count_The_Page_Reports_Now()
    {
        // Issue #409: the stream ending and the backoff that followed it were written by the
        // session rather than by this wrapper, and carried no count at all, so a night of them
        // could not be read as "somebody was watching" or "nobody was".
        using var h = new Harness();
        await h.GoLiveAsync();
        await h.LinesReach(2);

        h.Session(UberSdrAudioInput.StreamEndedLine(
            Endpoint,
            "The remote party closed the WebSocket connection without completing the close "
            + "handshake."));

        h.Lines[^1].Should().Be(
            "ubersdr: live, 1 viewer: stream from rx.example.org ended (The remote party closed "
            + "the WebSocket connection without completing the close handshake.)");

        // Read as the line is written, not copied when the session opened.
        h.Input.SetViewers(3);
        h.Session(UberSdrAudioInput.ReconnectedLine(Endpoint));
        h.Lines[^1].Should().Be("ubersdr: live, 3 viewers: reconnected to rx.example.org");
    }

    [Fact]
    public async Task A_Backoff_The_Last_Viewer_Has_Left_Says_It_Is_Retrying_For_Nobody()
    {
        // The session's own reconnect loop knows nothing about viewers, and keeps going until
        // the linger expires and this wrapper disposes it. That is the window #409's journal
        // could not distinguish from somebody having the page open all night, so the line
        // names it rather than leaving it to be inferred from a zero.
        using var h = new Harness();
        await h.GoLiveAsync();
        await h.LinesReach(2);
        h.Input.SetViewers(0);
        await h.LinesReach(3);

        h.Session(ShortSession);

        h.Lines[^1].Should().Be(
            "ubersdr: lingering, 0 viewers: the session ended after 41 ms with only 0 ms of "
            + "audio; backing off 300s before reconnecting to rx.example.org, "
            + "retrying for nobody");

        // And only on a wait. The stream ending is not one, however few are watching, so the
        // clause stays off it: a grep that matches lines which are not retries says nothing.
        h.Session(UberSdrAudioInput.StreamEndedLine(Endpoint, "connection reset"));
        h.Lines[^1].Should().Be(
            "ubersdr: lingering, 0 viewers: stream from rx.example.org ended (connection reset)");
        h.Session(UberSdrAudioInput.ReconnectedLine(Endpoint));
        h.Lines[^1].Should().Be("ubersdr: lingering, 0 viewers: reconnected to rx.example.org");
    }

    [Fact]
    public async Task A_Backoff_Somebody_Is_Waiting_For_Reads_As_It_Did_Plus_The_Count()
    {
        using var h = new Harness();
        await h.GoLiveAsync();
        await h.LinesReach(2);

        h.Session(ShortSession);

        h.Lines[^1].Should().Be(
            "ubersdr: live, 1 viewer: the session ended after 41 ms with only 0 ms of audio; "
            + "backing off 300s before reconnecting to rx.example.org");
        h.Lines.Should().NotContain(l => l.Contains("nobody"));
    }

    [Fact]
    public async Task The_Line_Announcing_A_Retry_Says_Who_Is_Waiting_For_It()
    {
        using var h = new Harness();

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("connection refused"));
        await h.Announced(OnDemandPhase.Retrying);
        await h.LinesReach(2);

        h.Lines[^1].Should().Be(
            "ubersdr: retrying, 1 viewer: rx.example.org unreachable (connection refused); "
            + "trying again in 1 s");
    }

    [Fact]
    public async Task The_Last_Viewer_Leaving_During_A_Retry_Stops_It_Rather_Than_Retrying_For_Nobody()
    {
        // The wrapper's own ladder, as against the session's: this one does check, and the
        // journal now shows it doing so.
        using var h = new Harness();

        h.Input.SetViewers(1);
        (await h.NextAttemptAsync()).SetException(new HttpRequestException("connection refused"));
        await h.Announced(OnDemandPhase.Retrying);
        await h.LinesReach(2);

        h.Input.SetViewers(0);
        await h.LinesReach(3);

        h.Lines[^1].Should().Be(
            "ubersdr: idle, 0 viewers: idle; connects to rx.example.org when someone is watching");
        h.Time.Advance(TimeSpan.FromMinutes(20));
        h.Attempts.Should().Be(1);
        h.Lines.Should().NotContain(l => l.Contains("retrying for nobody"));
    }

    [Fact]
    public async Task A_Session_Giving_Up_Says_How_Many_Were_Watching_When_It_Did()
    {
        using var h = new Harness();
        FakeSession session = await h.GoLiveAsync();
        await h.LinesReach(2);

        session.GiveUp("stream from rx.example.org ended (connection reset)");
        await h.Announced(OnDemandPhase.Retrying);
        await h.LinesReach(4);

        h.Lines[2].Should().Be(
            "ubersdr: live, 1 viewer: stream from rx.example.org ended (connection reset)");
        h.Lines[3].Should().Be(
            "ubersdr: retrying, 1 viewer: rx.example.org unreachable (the session gave up after "
            + "5 minutes unreachable); trying again in 1 s");
    }

    /// <summary>
    /// How long a wait goes before it is called a hang. Every wait here is on something the input
    /// raises (a journal line, a phase announcement, an attempt to open, a session disposed), so a
    /// slow run only makes the wait longer; this only turns a genuine hang into a failure that says
    /// what it was waiting for.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Each_Session_After_The_First_Counts_As_One_Hole_And_The_Sessions_Own_Losses_Carry_Over()
    {
        using var h = new Harness();
        FakeSession first = await h.GoLiveAsync();
        h.Input.Discontinuities.Should().Be(0, "the first session follows nothing");

        first.Discontinuities = 3; // say it dropped three samples
        h.Input.Discontinuities.Should().Be(3, "the open session's own losses are this input's");

        // Everybody leaves, the linger runs out, and somebody comes back after a quiet spell.
        h.Input.SetViewers(0);
        h.Time.Advance(Linger);
        await h.Announced(OnDemandPhase.Idle);
        h.Input.Discontinuities.Should().Be(3, "closing a session does not lose what it counted");

        FakeSession second = await h.GoLiveAsync();
        h.Input.Discontinuities.Should().Be(4,
            "nobody heard the time between the two sessions, so the new one follows a hole");

        second.Discontinuities = 1;
        h.Input.Discontinuities.Should().Be(5);
    }

    [Fact]
    public async Task A_Session_That_Gave_Up_Keeps_Its_Losses_And_Its_Replacement_Follows_A_Hole()
    {
        using var h = new Harness();
        FakeSession first = await h.GoLiveAsync();
        first.Discontinuities = 2;

        first.GiveUp("rx.example.org has been unreachable for 5 minutes");
        await h.Announced(OnDemandPhase.Retrying);
        h.Input.Discontinuities.Should().Be(2, "a session that gave up does not take its losses with it");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        (await h.NextAttemptAsync()).SetResult(new FakeSession { SessionLive = true });
        await h.Announced(OnDemandPhase.Live);

        h.Input.Discontinuities.Should().Be(3, "five minutes unreachable is a stretch nobody heard");
    }

    private static TimeoutException Hung(string what) =>
        new($"nothing happened for {HangGuard.TotalSeconds:F0} s while waiting for {what}: a hang, not a slow run");

    private static async Task Within(Task task, string what)
    {
        try
        {
            await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            throw Hung(what);
        }
    }

    private sealed class FakeSession : IUberSdrSession
    {
        public ConnectionResponse Connection { get; } = new() { Allowed = true, MaxSessionTime = 3600 };
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SessionLive { get; set; }

        /// <summary>The session's own loss count, as a test sets it.</summary>
        public long Discontinuities { get; set; }

        public bool Disposed => _disposed.Task.IsCompleted;

        /// <summary>Completes when the input disposes the session, which it does off the test's
        /// thread when the session gave up or arrived too late to be wanted.</summary>
        public Task WhenDisposed => _disposed.Task;
        public int Reads { get; private set; }
        public int SampleRate => 12000;

        public event Action<string>? Lost;

        public int Read(Span<float> destination)
        {
            Reads++;
            destination.Fill(0.25f);
            return destination.Length;
        }

        public void Dispose() => _disposed.TrySetResult();

        public void GiveUp(string reason) => Lost?.Invoke(reason);
    }

    /// <summary>One input over a fake clock and an opener the test completes by hand, one
    /// attempt at a time.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SemaphoreSlim _attemptStarted = new(0);
        private readonly Queue<TaskCompletionSource<IUberSdrSession>> _pending = new();
        private readonly List<string> _lines = [];
        private Action<UberSdrLine>? _sessionJournal;
        private int _attempts;

        /// <summary>Completed, and replaced, each time the input does something a test can wait
        /// for: writes a journal line, announces a phase, or asks to open a session.</summary>
        private TaskCompletionSource _changed = NewSignal();

        public Harness(string? description = null)
        {
            Input = new OnDemandUberSdrInput(
                Endpoint,
                new ConnectionResponse { Allowed = true, MaxSessionTime = 3600 },
                description,
                sampleRate: 12000,
                Linger,
                open: (journal, _) =>
                {
                    var attempt = new TaskCompletionSource<IUberSdrSession>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_pending)
                    {
                        _attempts++;
                        _pending.Enqueue(attempt);
                        _sessionJournal = journal;
                    }

                    _attemptStarted.Release();
                    Changed();
                    return attempt.Task;
                },
                log: line =>
                {
                    lock (_lines)
                    {
                        _lines.Add(line);
                    }

                    Changed();
                },
                Time);
            Input.PhaseChanged += (phase, sentence) =>
            {
                lock (Phases)
                {
                    Phases.Add((phase, sentence));
                }

                Changed();
            };
        }

        public FakeTimeProvider Time { get; } = new();
        public OnDemandUberSdrInput Input { get; }
        public List<(OnDemandPhase Phase, string Sentence)> Phases { get; } = [];

        public int Attempts
        {
            get
            {
                lock (_pending)
                {
                    return _attempts;
                }
            }
        }

        /// <summary>Every journal line the input has written, in order.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        /// <summary>The journal the input handed the session it last asked to open: where a real
        /// <see cref="UberSdrAudioInput"/>'s reconnect loop writes its own lines.</summary>
        public Action<UberSdrLine> Session
        {
            get
            {
                lock (_pending)
                {
                    return _sessionJournal
                        ?? throw new InvalidOperationException("nothing has asked to open yet");
                }
            }
        }

        /// <summary>Waits for the announcement of a phase change to reach the journal: the phase
        /// itself is set under the lock and announced after it, so the two are not simultaneous.
        /// </summary>
        public Task LinesReach(int count) => Until(() => Lines.Count >= count, $"{count} journal lines");

        /// <summary>
        /// Waits for <paramref name="phase"/> to be the last phase announced. Not merely for
        /// <see cref="OnDemandUberSdrInput.Phase"/> to read it: the phase is set under the input's
        /// lock and announced after it is released, on whichever thread made the change, so a test
        /// that moved on as soon as the property changed could announce its own next phase first.
        /// </summary>
        public Task Announced(OnDemandPhase phase) => Until(
            () =>
            {
                lock (Phases)
                {
                    return Phases.Count > 0 && Phases[^1].Phase == phase;
                }
            },
            $"the input to announce {phase}");

        /// <summary>Waits for <paramref name="condition"/>, checking it each time the input does
        /// something, never on a timer.</summary>
        public async Task Until(Func<bool> condition, string what)
        {
            using var guard = new CancellationTokenSource(HangGuard);
            while (true)
            {
                // Taken before the check, so a change made between the check and the wait still
                // completes the signal being waited on.
                Task changed = Volatile.Read(ref _changed).Task;
                if (condition())
                {
                    return;
                }

                try
                {
                    await changed.WaitAsync(guard.Token);
                }
                catch (OperationCanceledException)
                {
                    throw Hung(what);
                }
            }
        }

        public async Task<TaskCompletionSource<IUberSdrSession>> NextAttemptAsync()
        {
            (await _attemptStarted.WaitAsync(HangGuard))
                .Should().BeTrue("the input should have asked to open a session");
            lock (_pending)
            {
                return _pending.Dequeue();
            }
        }

        public async Task<FakeSession> GoLiveAsync()
        {
            Input.SetViewers(1);
            var session = new FakeSession { SessionLive = true };
            (await NextAttemptAsync()).SetResult(session);
            await Announced(OnDemandPhase.Live);
            return session;
        }

        public void Dispose() => Input.Dispose();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Changed() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
    }
}

