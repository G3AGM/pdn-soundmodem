using System.Net.Http;
using System.Net.WebSockets;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.UberSdr;

/// <summary>What <see cref="OnDemandUberSdrInput"/> is doing, for the status line and the journal.</summary>
public enum OnDemandPhase
{
    /// <summary>Nobody is watching and no session is held.</summary>
    Idle,

    /// <summary>A viewer arrived; the session is being opened.</summary>
    Connecting,

    /// <summary>A session is open and viewers are attached.</summary>
    Live,

    /// <summary>The last viewer left; the session is held for the linger period in case
    /// they come straight back.</summary>
    Lingering,

    /// <summary>The open failed, or an open session gave up; viewers are waiting and another
    /// attempt is scheduled.</summary>
    Retrying,
}

/// <summary>
/// An UberSDR receiver as an <see cref="IAudioInput"/> that holds a session on the instance
/// <em>only while somebody is watching</em>: the daemon's waterfall page reports its viewer
/// count, and this opens the receiver when the count leaves zero and closes it a little while
/// after the count returns there.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A public packet monitor built on somebody else's receiver should not hold
/// one of that receiver's listener slots around the clock for a page nobody has open. The plain
/// <c>ubersdr:</c> device is a station and is right to stream for months; this is a display, and
/// a display with no viewer has no business on the air. See
/// <c>docs/dev/archive/40m-monitor-plan.md</c>.</para>
/// <para><b>The linger.</b> A page refresh, a tab switch or a flaky connection drops the viewer
/// count to zero for a moment; tearing the session down on each and rebuilding it a second
/// later would cost the receiver a session per hiccup and the viewer the start-up guard each
/// time. So the last viewer leaving starts a clock, and only the clock running out closes the
/// session. One session per visit, not per WebSocket.</para>
/// <para><b>Nothing restarts the daemon.</b> The plain device answers a receiver that stays
/// unreachable with exit 1, because a station with a dead feed is better restarted. A public
/// page is not: a restart loop against a receiver that is down takes the page down with it,
/// when the page could be up and saying "the receiver is unreachable". So an open that fails,
/// and a session that gives up, are retried on the same escalating ladder the session itself
/// uses, for as long as anybody is watching, and reported through <see cref="PhaseChanged"/>
/// rather than through the process exit code.</para>
/// <para><b>What "for as long as anybody is watching" covers, and what it does not.</b> This
/// wrapper's own ladder is abandoned the moment the last viewer leaves - a failed open with
/// nobody waiting goes idle, a due retry with nobody waiting goes idle, and the last viewer
/// leaving cancels a pending one. An open session's ladder is not: <see cref="UberSdrAudioInput"/>
/// reconnects on a ladder of its own that knows nothing about viewers, and the only thing that
/// stops it is this wrapper disposing the session, one linger after the count reaches zero. So a
/// receiver that accepts and drops on the spot is re-asked for the length of the linger with
/// nobody watching. Left as it stands deliberately (issue #409); what was missing was any way to
/// read it in the journal, and every line now carries the count and names a wait nobody is
/// waiting for.</para>
/// <para><b>Reading while idle</b> behaves as the real input does with an empty ring: a short
/// wait and zero samples, which the daemon's receive loop already treats as "nothing to say".
/// The starvation watch reads <see cref="SessionLive"/>, false whenever no session is
/// delivering, so idle is not mistaken for a hung stream.</para>
/// </remarks>
public sealed class OnDemandUberSdrInput : IAudioInput, IDisposable
{
    private readonly Func<Action<UberSdrLine>, CancellationToken, Task<IUberSdrSession>> _open;
    private readonly TimeSpan _linger;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly UberSdrReconnectPolicy _policy = new();
    private readonly Action<UberSdrLine> _sessionJournal;

    private IUberSdrSession? _session;

    // Read without the lock by the session's own pump thread when it journals a line, so the
    // count and the phase on that line are this instant's rather than a copy taken earlier.
    private volatile OnDemandPhase _phase;
    private string _status;
    private int _viewers;
    private ITimer? _timer;
    private int _generation;
    private bool _disposed;

    // Holes in the audio this input has handed out, for Discontinuities: the counts of the
    // sessions already closed, plus one for every session opened after the first. Under _gate.
    private long _lostBefore;
    private int _sessionsOpened;

    internal OnDemandUberSdrInput(
        UberSdrEndpoint endpoint,
        ConnectionResponse connection,
        string? receiverDescription,
        int sampleRate,
        TimeSpan linger,
        Func<Action<UberSdrLine>, CancellationToken, Task<IUberSdrSession>> open,
        Action<string>? log,
        TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(linger, TimeSpan.Zero);
        Endpoint = endpoint;
        Connection = connection;
        ReceiverDescription = receiverDescription;
        SampleRate = sampleRate;
        _linger = linger;
        _open = open;
        _log = log;
        _time = time;
        _status = SentenceFor(OnDemandPhase.Idle);
        _sessionJournal = line => Journal(line.Sentence, line.BeforeAWait);
    }

    /// <summary>Raised on every phase change, outside any lock, with the phase and a one-line
    /// ASCII sentence for the status chip and the journal.</summary>
    public event Action<OnDemandPhase, string>? PhaseChanged;

    /// <summary>The instance this connects to.</summary>
    public UberSdrEndpoint Endpoint { get; }

    /// <summary>The start-up pre-flight reply - session limits and the IQ modes on offer.</summary>
    public ConnectionResponse Connection { get; }

    /// <summary>A one-line summary of the receiver from its own description, or null when it
    /// would not say. Fetched once at start-up.</summary>
    public string? ReceiverDescription { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>What the input is doing right now.</summary>
    public OnDemandPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>The sentence the last phase change carried, for a status display that was not
    /// listening when it happened: the idle line until the first viewer arrives.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>How many viewers the page last reported.</summary>
    public int Viewers
    {
        get
        {
            lock (_gate)
            {
                return _viewers;
            }
        }
    }

    /// <summary>
    /// True while the last thing the receiver said was "not you, not now": an HTTP 429, or a
    /// daily listening allowance this address has spent. Cleared by a session that opens.
    /// </summary>
    /// <remarks>
    /// Told apart from every other reason a session will not open because it is the one an
    /// operator and a visitor can both act on - by waiting - and because a host listing several
    /// receivers has to be able to say which of them is refusing it rather than which is broken.
    /// The reconnect ladder already knows the difference (<see cref="UberSdrReconnectOutcome"/>);
    /// this only makes what it knows readable from outside.
    /// </remarks>
    public bool Refused
    {
        get
        {
            lock (_gate)
            {
                return _refused;
            }
        }
    }

    private bool _refused;

    /// <summary>True only while an open session is expected to be delivering audio. Idle,
    /// connecting, retrying and a session's own between-attempt quiet are all false, and all
    /// deliberate; the daemon's starvation watch stands down on this.</summary>
    public bool SessionLive => Volatile.Read(ref _session)?.SessionLive ?? false;

    /// <summary>
    /// A running count that moves whenever the audio this input hands out has a hole in it: the
    /// open session's own (<see cref="UberSdrAudioInput.Discontinuities"/>), those of every
    /// session before it, and one for each session opened after the first, because the time
    /// between one session closing and the next opening was not heard. Read once per block by
    /// the daemon; a <see cref="Read"/> that returns nothing is not a hole on its own.
    /// </summary>
    public long Discontinuities
    {
        get
        {
            lock (_gate)
            {
                return _lostBefore + (_session?.Discontinuities ?? 0);
            }
        }
    }

    /// <summary>
    /// Validates the configuration against the instance and returns an idle input. The
    /// pre-flight is a REST call, not a stream: a wrong host, a refused IQ mode or a password
    /// the instance does not accept is still a start-up error, and no listener slot is taken
    /// until somebody is watching.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance cannot be reached, or it refused
    /// this configuration in a way only an operator can fix.</exception>
    public static async Task<OnDemandUberSdrInput> OpenAsync(
        UberSdrEndpoint endpoint,
        UberSdrTuning tuning,
        TimeSpan linger,
        Action<string>? log,
        CancellationToken cancellation,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        // The same checks the plain device makes at start-up, in the same order, so the two
        // devices refuse the same configurations with the same sentences.
        UberSdrAudioInput.IqRateFor(tuning.Mode);
        ConnectionResponse connection = await UberSdrAudioInput.PreflightAsync(
            endpoint, Guid.NewGuid().ToString(), tuning, cancellation).ConfigureAwait(false);
        UberSdrAudioInput.RequireAcceptable(endpoint, tuning, connection);
        string? description = UberSdrAudioInput.Describe(
            await UberSdrAudioInput.FetchDescriptionAsync(endpoint, cancellation).ConfigureAwait(false));

        TimeProvider clock = time ?? TimeProvider.System;
        return new OnDemandUberSdrInput(
            endpoint, connection, description, tuning.OutputRate, linger,
            async (journal, token) => await UberSdrAudioInput
                .OpenAsync(endpoint, tuning, journal, token, clock).ConfigureAwait(false),
            log, clock);
    }

    /// <summary>
    /// The page's viewer count, as reported on every change. Leaving zero opens the receiver
    /// (or cancels a linger); returning to zero starts the linger, or abandons a retry that
    /// nobody is waiting on.
    /// </summary>
    public void SetViewers(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Action? announce = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _viewers = count;
            if (count > 0)
            {
                switch (_phase)
                {
                    case OnDemandPhase.Idle:
                        announce = BeginOpen();
                        break;
                    case OnDemandPhase.Lingering:
                        CancelTimer();
                        announce = SetPhase(OnDemandPhase.Live);
                        break;
                }
            }
            else
            {
                switch (_phase)
                {
                    case OnDemandPhase.Live:
                        announce = BeginLinger();
                        break;
                    case OnDemandPhase.Retrying:
                        // Nobody is waiting for the retry, so it would only be asking the
                        // receiver on nobody's behalf.
                        CancelTimer();
                        announce = SetPhase(OnDemandPhase.Idle);
                        break;
                }
            }
        }

        announce?.Invoke();
    }

    /// <inheritdoc />
    /// <remarks>Forwards to the open session; with none, waits briefly and returns 0, which is
    /// what the daemon's receive loop expects of a device with nothing to say.</remarks>
    public int Read(Span<float> destination)
    {
        if (Volatile.Read(ref _session) is { } session)
        {
            return session.Read(destination);
        }

        lock (_gate)
        {
            if (_session is null && !_disposed)
            {
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100));
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IUberSdrSession? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            CancelTimer();
            session = _session;
            _session = null;
            Monitor.PulseAll(_gate);
        }

        _stopping.Cancel();
        session?.Dispose();
        _stopping.Dispose();
    }

    // Everything below runs under _gate and returns the announcement to make once it is
    // released: PhaseChanged reaches the daemon, which reaches the waterfall server, which
    // reaches back here through SetViewers, and that must never happen with the lock held.

    private Action? BeginOpen()
    {
        int generation = ++_generation;
        CancellationToken stopping = _stopping.Token; // taken under the lock, before any Dispose
        Action? announce = SetPhase(OnDemandPhase.Connecting);
        _ = Task.Run(async () =>
        {
            IUberSdrSession session;
            try
            {
                session = await _open(_sessionJournal, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is InvalidOperationException or UberSdrRefusedException
                                        or HttpRequestException or WebSocketException or IOException
                                        or OperationCanceledException)
            {
                // OperationCanceledException here is a connect that timed out, not our stop.
                OnOpenFailed(generation, e);
                return;
            }

            OnOpened(generation, session);
        }, CancellationToken.None);
        return announce;
    }

    private void OnOpened(int generation, IUberSdrSession session)
    {
        Action? announce;
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                // Superseded while opening (disposed, or a retry cycle moved on). Not ours.
                session.Dispose();
                return;
            }

            _policy.Reset();
            _refused = false;
            if (_sessionsOpened++ > 0)
            {
                _lostBefore++;
            }

            session.Lost += reason => OnLost(generation, reason);
            Volatile.Write(ref _session, session);
            Monitor.PulseAll(_gate);
            announce = SetPhase(OnDemandPhase.Live);
            if (_viewers == 0)
            {
                Action? linger = BeginLinger();
                announce = Both(announce, linger);
            }
        }

        announce?.Invoke();
    }

    private void OnOpenFailed(int generation, Exception failure)
    {
        Action? announce;
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            announce = _viewers == 0
                ? SetPhase(OnDemandPhase.Idle, $"could not open {Endpoint} ({failure.Message}); nobody is waiting")
                : BeginRetry(OutcomeOf(failure), failure.Message);
        }

        announce?.Invoke();
    }

    private void OnLost(int generation, string reason)
    {
        Action? announce;
        IUberSdrSession? session;
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            session = _session;
            _lostBefore += session?.Discontinuities ?? 0;
            Volatile.Write(ref _session, null);
            CancelTimer();
            string gaveUp =
                $"the session gave up after {UberSdrAudioInput.ReconnectGiveUpAfter.TotalMinutes:F0} "
                + "minutes unreachable";
            Journal(reason, beforeAWait: false);
            announce = _viewers == 0
                ? SetPhase(OnDemandPhase.Idle, $"{gaveUp}; nobody is waiting")
                : BeginRetry(UberSdrReconnectOutcome.Transient, gaveUp);
        }

        // Off this thread: Lost is raised from the session's own pump, and its Dispose waits
        // for that pump to finish, which it cannot do while this handler is still running.
        if (session is not null)
        {
            _ = Task.Run(session.Dispose);
        }

        announce?.Invoke();
    }

    private Action? BeginLinger()
    {
        int generation = _generation;
        ArmTimer(_linger, () => OnLingerExpired(generation));
        return SetPhase(
            OnDemandPhase.Lingering,
            $"no viewers; holding the session with {Endpoint} for {_linger.TotalSeconds:F0} s");
    }

    private void OnLingerExpired(int generation)
    {
        Action? announce;
        IUberSdrSession? session;
        lock (_gate)
        {
            if (generation != _generation || _disposed || _phase != OnDemandPhase.Lingering)
            {
                return;
            }

            session = _session;
            _lostBefore += session?.Discontinuities ?? 0;
            Volatile.Write(ref _session, null);
            _timer = null;
            announce = SetPhase(OnDemandPhase.Idle);
        }

        session?.Dispose();
        announce?.Invoke();
    }

    private Action? BeginRetry(UberSdrReconnectOutcome outcome, string reason)
    {
        _refused = outcome == UberSdrReconnectOutcome.Refused;
        int generation = ++_generation;
        TimeSpan delay = _policy.After(outcome);
        ArmTimer(delay, () => OnRetryDue(generation));
        return SetPhase(
            OnDemandPhase.Retrying,
            $"{Endpoint} unreachable ({reason}); trying again in {delay.TotalSeconds:F0} s");
    }

    private void OnRetryDue(int generation)
    {
        Action? announce;
        lock (_gate)
        {
            if (generation != _generation || _disposed || _phase != OnDemandPhase.Retrying)
            {
                return;
            }

            _timer = null;
            announce = _viewers > 0 ? BeginOpen() : SetPhase(OnDemandPhase.Idle);
        }

        announce?.Invoke();
    }

    private void ArmTimer(TimeSpan delay, Action due)
    {
        CancelTimer();
        _timer = _time.CreateTimer(_ => due(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private string SentenceFor(OnDemandPhase phase) => phase switch
    {
        OnDemandPhase.Idle => $"idle; connects to {Endpoint} when someone is watching",
        OnDemandPhase.Connecting => $"connecting to {Endpoint}",
        OnDemandPhase.Live => ReceiverDescription ?? Endpoint.ToString(),
        _ => phase.ToString(),
    };

    private Action? SetPhase(OnDemandPhase phase, string? detail = null)
    {
        if (phase == OnDemandPhase.Idle)
        {
            // Nobody is waiting, so nothing is being refused: a refusal is a state of an attempt,
            // and there is no attempt.
            _refused = false;
        }

        _phase = phase;
        string sentence = detail ?? SentenceFor(phase);
        _status = sentence;
        int viewers = _viewers;

        // Retrying is the one phase that is a wait before another attempt at the receiver. Both
        // paths into it check that somebody is waiting first, so the clause should never appear
        // here; if it ever does, the check has a hole in it and the journal says so.
        bool beforeAWait = phase == OnDemandPhase.Retrying;
        return () =>
        {
            _log?.Invoke(JournalLine(phase, viewers, sentence, beforeAWait));
            PhaseChanged?.Invoke(phase, sentence);
        };
    }

    /// <summary>One line about this receiver, with the count the page reports at this instant.
    /// Called from the session's pump thread as well as this input's own, so it takes no
    /// lock.</summary>
    private void Journal(string sentence, bool beforeAWait) =>
        _log?.Invoke(JournalLine(_phase, Volatile.Read(ref _viewers), sentence, beforeAWait));

    /// <summary>
    /// The one shape every ubersdr line this input writes takes: what it is doing, how many the
    /// page says are watching, and the sentence.
    /// </summary>
    /// <remarks>
    /// The phase lines carried the count from the start; the session's own lines - the stream
    /// ending, the backoff before the next attempt, the reconnect - did not, so six hours of
    /// them in issue #409 could not be read as "somebody had the page open all night" or
    /// "the loop kept going after the last one left". Now every one of them does.
    /// </remarks>
    /// <param name="phase">What this input is doing.</param>
    /// <param name="viewers">How many the page reports.</param>
    /// <param name="sentence">The line itself, without the <c>ubersdr:</c> prefix.</param>
    /// <param name="beforeAWait">The line announces a wait before another attempt at the
    /// receiver. With nobody watching, that attempt is on nobody's behalf, and the line says
    /// so in words: a zero is easy to read past, and this is the fault worth grepping for.</param>
    internal static string JournalLine(
        OnDemandPhase phase, int viewers, string sentence, bool beforeAWait)
    {
        string nobody = beforeAWait && viewers == 0 ? ", retrying for nobody" : "";
        return $"ubersdr: {phase.ToString().ToLowerInvariant()}, {viewers} "
            + $"viewer{(viewers == 1 ? "" : "s")}: {sentence}{nobody}";
    }

    private static Action? Both(Action? first, Action? second) =>
        first is null ? second : second is null ? first : () => { first(); second(); };

    /// <summary>Which rung of the reconnect ladder an open failure belongs on: a refusal only
    /// time can lift waits long; everything else is a transport fault worth a quick retry.
    /// The pre-flight wraps "cannot reach" in an InvalidOperationException around the transport
    /// error, so the inner exception is what tells the two apart.</summary>
    private static UberSdrReconnectOutcome OutcomeOf(Exception failure) => failure switch
    {
        UberSdrRefusedException => UberSdrReconnectOutcome.Refused,
        InvalidOperationException { InnerException: HttpRequestException or OperationCanceledException }
            => UberSdrReconnectOutcome.Transient,
        InvalidOperationException => UberSdrReconnectOutcome.Refused,
        _ => UberSdrReconnectOutcome.Transient,
    };
}
