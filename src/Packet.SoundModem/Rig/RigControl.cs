using System.Globalization;
using Packet.SoundModem.Channel;

namespace Packet.SoundModem.Rig;

/// <summary>How a <see cref="RigControl"/> is set up.</summary>
public sealed class RigControlOptions
{
    /// <summary>Where rigctld listens.</summary>
    public required RigctldEndpoint Endpoint { get; init; }

    /// <summary>The clock every wait and window is timed on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>True when the radio is keyed with rigctld's <c>T</c> command
    /// (<c>"ptt": {"type": "rigctld"}</c>) rather than by a line of its own.</summary>
    public bool KeysThroughRig { get; init; }

    /// <summary>Where the band plan puts the dial, or null for a station with no plan, whose
    /// dial is the operator's and is never touched.</summary>
    public RigTuning? Plan { get; init; }

    /// <summary>Where the restore target of an open window is written down (see
    /// <see cref="RigRestoreFile"/>), or null to keep it in memory only.</summary>
    public string? RestoreFile { get; init; }

    /// <summary>True while the station has a transmission queued; a poll then waits rather than
    /// hold the connection just as the transmitter wants it for a keyup.</summary>
    public Func<bool>? TransmitPending { get; init; }

    /// <summary>How often a connected rig is read, which is also how a dead rigctld that left
    /// its socket open is found.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often an unkey that did not go through is tried again.</summary>
    public TimeSpan UnkeyRetry { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The first wait before reconnecting after a failed attempt; it doubles each time,
    /// and only goes back to this once a connection has been read from successfully.</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one reply from rigctld may take before the connection is given up on.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long one connect attempt may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The least time between two of the same repeating journal line.</summary>
    public TimeSpan QuietInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>What became of a request to tune the rig for a window.</summary>
public enum RigTuneOutcome
{
    /// <summary>The rig is tuned and a new window is open.</summary>
    Tuned,

    /// <summary>The caller's own window was extended, and retuned if it asked for somewhere new.</summary>
    Renewed,

    /// <summary>Nothing was wrong with the request; the station was in no state to do it.</summary>
    Refused,

    /// <summary>The request itself was wrong.</summary>
    Invalid,

    /// <summary>The rig or rigctld failed part way. The rig has been put back where it can be,
    /// and is owed its restore where it cannot.</summary>
    Failed,
}

/// <summary>The answer to <see cref="RigControl.Tune"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Why">The reason, for anything but a tune or a renewal.</param>
/// <param name="Window">The window, when there is one.</param>
/// <param name="Capped">True when the length asked for was more than <see cref="RigControl.MaxWindow"/>.</param>
public sealed record RigTuneResult(RigTuneOutcome Outcome, string? Why, RigWindow? Window, bool Capped = false)
{
    /// <summary>Whether the caller now holds a window.</summary>
    public bool Granted => Outcome is RigTuneOutcome.Tuned or RigTuneOutcome.Renewed;
}

/// <summary>
/// One stretch of time the rig is tuned somewhere other than where the station keeps it, and the
/// promise to put it back. Disposing it ends the window now.
/// </summary>
public sealed class RigWindow : IDisposable
{
    private readonly RigControl _rig;

    internal RigWindow(RigControl rig, string owner, RigTuning tuning, RigTuning restoreTo)
    {
        _rig = rig;
        Owner = owner;
        Tuning = tuning;
        RestoreTo = restoreTo;
    }

    /// <summary>Who asked for it, in a journal line: <c>the API</c>, a feature's name.</summary>
    public string Owner { get; }

    /// <summary>Where the rig is tuned for the window.</summary>
    public RigTuning Tuning { get; internal set; }

    /// <summary>Where the rig was before, and goes back to.</summary>
    public RigTuning RestoreTo { get; }

    /// <summary>When the window ends unless it is renewed.</summary>
    public DateTimeOffset Expires { get; internal set; }

    internal ITimer? Timer { get; set; }

    internal CancellationTokenRegistration Lifetime { get; set; }

    /// <summary>Ends the window and puts the rig back.</summary>
    public void Dispose() => _rig.EndWindow(this, $"{Owner} released it");
}

/// <summary>What <see cref="RigControl.Snapshot"/> reads.</summary>
/// <param name="Endpoint">Where rigctld is.</param>
/// <param name="Connected">Whether it is connected now.</param>
/// <param name="Tuning">What the rig was last read or set to, or null before the first read.</param>
/// <param name="Keyed">Whether a keyup is under way.</param>
/// <param name="KeysThroughRig">Whether the radio is keyed with rigctld's <c>T</c> command.</param>
/// <param name="Window">The open tuning window, or null.</param>
/// <param name="RestoreOwed">Where the rig is still to be put back to after a window that has
/// ended, or null.</param>
/// <param name="UnkeyOwed">Whether an unkey has not yet been confirmed by rigctld.</param>
/// <param name="LastProblem">The last thing that went wrong, or null since the last good connection.</param>
public sealed record RigState(
    RigctldEndpoint Endpoint,
    bool Connected,
    RigTuning? Tuning,
    bool Keyed,
    bool KeysThroughRig,
    RigWindow? Window,
    RigTuning? RestoreOwed,
    bool UnkeyOwed,
    string? LastProblem);

/// <summary>
/// A radio controlled through Hamlib's rigctld: the dial set from the band plan, PTT through
/// <c>T 1</c>/<c>T 0</c> when the station keys that way, and short tuning windows that are always
/// put back.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> An ordinary SSB rig hears 2.4 to 3 kHz. A station whose packet
/// frequencies and pdn-mailcast's bulletin frequency are further apart than that cannot hear both,
/// so something has to retune the rig for the bulletin's window and put it back afterwards. This is
/// that something, and the band plan's dial and the rigctld PTT come with it because they need the
/// same connection.</para>
/// <para><b>A missing rigctld is not fatal.</b> The station runs without rig control and this keeps
/// trying, with a backoff from <see cref="RigControlOptions.FirstRetry"/> to
/// <see cref="RigControlOptions.MaxRetry"/>. Whatever was owed while it was away (an unkey, a
/// restore, the band plan's dial) is done the moment it answers again. Nothing that goes wrong on
/// the connection, an error code from a rig that is switched off included, stops the watch.</para>
/// <para><b>Never retuned while keyed.</b> Tuning and keying are mutually exclusive: a tune asked
/// for while the transmitter is keyed, or while an unkey is still owed, is refused, and a keyup
/// asked for while a window is open, while the rig is owed its restore or while it is being
/// retuned is refused with <see cref="TransmitterHeldException"/>. Ordinary frames are held rather
/// than refused, through the channel's transmit inhibit, which <see cref="HoldsTransmitter"/>
/// feeds; the refusal is the backstop for anything that gets past it.</para>
/// <para><b>Always put back.</b> A window ends when its owner releases it, when its time runs out,
/// when the owner's lifetime token is cancelled, and when the station shuts down. The restore is
/// owed from the moment the first command that moves the rig is sent, and written to
/// <see cref="RigControlOptions.RestoreFile"/> before it is, so neither a rigctld that goes away
/// part way nor a station killed mid-window can leave the rig where the window put it: the restore
/// is done as soon as rigctld answers, at the next start-up if need be, and the station does not
/// transmit until it has been.</para>
/// <para><b>Fail-safe PTT.</b> An unkey is owed from the moment it is asked for until rigctld
/// confirms it, and while one is owed the station neither keys nor retunes. A <c>T 1</c> that fails
/// is followed by a <c>T 0</c> at once. An owed unkey is retried every
/// <see cref="RigControlOptions.UnkeyRetry"/>, every new connection starts with <c>T 0</c>, and so
/// does shutdown.</para>
/// <para><b>Two locks.</b> <c>_io</c> owns the socket, one command at a time; <c>_gate</c> owns the
/// state and is only ever held briefly, inside <c>_io</c> or on its own, never the other way round.
/// <see cref="HoldsTransmitter"/> takes neither.</para>
/// </remarks>
public sealed class RigControl : IAsyncDisposable
{
    /// <summary>The longest one tune or renewal may ask for.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(5);

    private readonly RigControlOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Lock _io = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly QuietLine _changedLine;
    private readonly QuietLine _restoreLine;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;

    // When the watch's current wait on the clock ends, or null while it is not waiting on one.
    // Set and cleared under _gate.
    private DateTimeOffset? _napUntil;

    // Changed only while holding both _io and _gate, so either is enough to read it steadily.
    private volatile RigctldConnection? _connection;
    private RigTuning? _known;
    private bool _everConnected;
    private bool _planApplied;
    private bool _unreachableSaid;
    private bool _disposed;
    private int _disposing;
    private int _unkeyAttempts;
    private bool _skipVfoCheck;

    // The flags a keyup and a retune decide on. Set under _gate; read anywhere.
    private volatile bool _keyed;
    private volatile bool _unkeyOwed;
    private volatile bool _retuning;
    private volatile RigWindow? _window;
    private volatile RigTuning? _restoreTo;
    private string? _lastProblem;

    /// <summary>Sets one up. Nothing connects until <see cref="StartAsync"/>; subscribe to the
    /// events first.</summary>
    /// <param name="options">Where rigctld is, and how to behave.</param>
    public RigControl(RigControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = options.Time;
        _changedLine = new QuietLine(_time, options.QuietInterval);
        _restoreLine = new QuietLine(_time, options.QuietInterval);
    }

    /// <summary>
    /// An ordinary journal line, plain ASCII, starting <c>rig: </c>: what the rig is on, a window
    /// opened or ended, the rig put back.
    /// </summary>
    public event Action<string>? Journal;

    /// <summary>
    /// Something worth an operator's attention, plain ASCII, starting <c>rig: WARNING - </c>:
    /// rigctld unreachable or lost, a refusal from the rig, an unkey or restore that is owed.
    /// Repeating problems are said once, or at most once per
    /// <see cref="RigControlOptions.QuietInterval"/>.
    /// </summary>
    public event Action<string>? Problem;

    /// <summary>
    /// Raised with a fresh <see cref="Snapshot"/> when the connection comes or goes, a window opens,
    /// renews or ends, the rig is put back, or an unkey becomes owed or is confirmed.
    /// </summary>
    /// <remarks>Raised on the thread that caused it, which may be holding the rig's connection: a
    /// handler should note the state and return, not call <see cref="Tune"/> or <see cref="Key"/>.</remarks>
    public event Action<RigState>? Changed;

    /// <summary>Where rigctld is.</summary>
    public RigctldEndpoint Endpoint => _options.Endpoint;

    /// <summary>Whether the radio is keyed through rigctld.</summary>
    public bool KeysThroughRig => _options.KeysThroughRig;

    /// <summary>
    /// Where the band plan puts this station's own modems, or null for a station with no plan,
    /// whose dial is the operator's and is never touched. A feature that wants "the rig's own
    /// data mode, or USB" (the built-in mailcast receiver's retuner, and the receive window's
    /// API) reads <see cref="RigTuning.Mode"/> here and asks <see cref="RigModes.SidebandOf"/>
    /// whether it is a USB-family one.
    /// </summary>
    public RigTuning? Plan => _options.Plan;

    /// <summary>Whether rigctld is connected right now.</summary>
    public bool Connected => _connection is not null;

    /// <summary>
    /// True while the station must not transmit: a window is open, the rig is owed its restore,
    /// it is being retuned, or an unkey has not been confirmed. The channel's transmit inhibit
    /// reads this, so it takes no lock.
    /// </summary>
    public bool HoldsTransmitter =>
        _window is not null || _restoreTo is not null || _retuning || _unkeyOwed;

    /// <summary>
    /// True while the rig is anywhere but where it is to be put back to: a tuning window is open,
    /// anyone's, or a restore is still owed after one. Unlike <see cref="Snapshot"/>'s
    /// <see cref="RigState.RestoreOwed"/>, an open window does not hide it. Takes no lock.
    /// </summary>
    public bool RestorePending => _restoreTo is not null || _window is not null;

    /// <summary>
    /// For tests on a fake clock: whether the background watch is running. Not started, or
    /// stopped, it never waits on the clock and never will.
    /// </summary>
    internal bool Watching => !_loop.IsCompleted;

    /// <summary>
    /// For tests on a fake clock: when the watch's wait between looks at rigctld ends, or null
    /// while it is not waiting on the clock - connecting, polling, retrying an unkey or a restore,
    /// or woken early and on its way to do so. A test that moves the clock only while this is set,
    /// and never past it without letting the watch act on it first, keeps the clock in step with
    /// what the rig has actually done, however long rigctld takes to answer.
    /// </summary>
    internal DateTimeOffset? NapUntil
    {
        get
        {
            lock (_gate)
            {
                return _napUntil;
            }
        }
    }

    /// <summary>The state as it stands, for the API and the tests.</summary>
    public RigState Snapshot()
    {
        lock (_gate)
        {
            return new RigState(
                Endpoint, _connection is not null, _known, _keyed, KeysThroughRig, _window,
                _window is null ? _restoreTo : null, _unkeyOwed, _lastProblem);
        }
    }

    /// <summary>
    /// Picks up a restore left owed by a station that stopped mid-window, connects once, journals
    /// what the rig is on, puts it back or sets the band plan's dial, and starts the background
    /// watch that keeps the connection up.
    /// </summary>
    /// <param name="cancellation">Ends the first connect attempt early.</param>
    /// <returns>Whether rigctld answered. False is a <see cref="Problem"/>, already raised, and the
    /// watch keeps trying.</returns>
    public async Task<bool> StartAsync(CancellationToken cancellation)
    {
        if (_options.RestoreFile is string file)
        {
            RigTuning? owed = RigRestoreFile.Read(file, Endpoint, out string? why, out bool foreign);
            if (owed is not null)
            {
                _restoreTo = owed;
                Say(
                    $"rig: the station stopped during a tuning window last time; the rig is put back "
                    + $"to {owed} before anything is sent");
            }
            else if (why is not null)
            {
                // Another rigctld's file is another station's promise, and is left alone; only one
                // that cannot be read at all is removed.
                Warn($"rig: WARNING - {why}; ignoring it");
                if (!foreign)
                {
                    TryDeleteRestoreFile();
                }
            }
        }

        bool connected = await TryConnectAsync(cancellation).ConfigureAwait(false);
        _loop = Task.Run(() => RunAsync(connected, _stop.Token), CancellationToken.None);
        return connected;
    }

    // ------------------------------------------------------------------ keying

    /// <summary>
    /// Starts a keyup: refused while the rig is retuned or an unkey is owed. With
    /// <see cref="KeysThroughRig"/> it keys the radio with <c>T 1</c>; without, the caller keys
    /// the radio by its own line and this marks the keyup, so no window opens during it.
    /// </summary>
    /// <exception cref="TransmitterHeldException">Not a moment to transmit.</exception>
    /// <exception cref="IOException">rigctld is not there to key the radio.</exception>
    /// <exception cref="RigctldException">rigctld refused the keyup.</exception>
    public void Key() => KeyCore(sendT: KeysThroughRig);

    /// <summary>
    /// Ends a keyup: with <see cref="KeysThroughRig"/> it sends <c>T 0</c>, owed until rigctld
    /// confirms it. The keyup is over as far as this station is concerned whatever happens to
    /// the command.
    /// </summary>
    public void Unkey() => UnkeyCore(sendT: KeysThroughRig);

    private void KeyCore(bool sendT)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new TransmitterHeldException("rig control has shut down, so the radio is not keyed");
            }

            if (HoldReasonLocked() is string why)
            {
                throw new TransmitterHeldException(why);
            }

            _keyed = true;
        }

        if (!sendT)
        {
            return;
        }

        lock (_io)
        {
            if (_connection is not { } connection)
            {
                _keyed = false;
                throw new IOException($"rigctld at {Endpoint} is not connected, so the radio cannot be keyed");
            }

            try
            {
                connection.SetPtt(true);
            }
            catch (Exception failed) when (failed is IOException or RigctldException)
            {
                // It may have keyed the rig whatever the answer said, so an unkey is owed, set
                // before the keyup is let go so there is never a moment with neither.
                _unkeyOwed = true;
                _keyed = false;
                if (failed is IOException lost)
                {
                    DropIo(lost.Message);
                }
                else
                {
                    UnkeyOwedIo(connection);
                }

                throw;
            }
        }
    }

    private void UnkeyCore(bool sendT)
    {
        if (!sendT)
        {
            _keyed = false;
            return;
        }

        _unkeyOwed = true;
        _keyed = false;
        lock (_io)
        {
            if (_connection is not { } connection)
            {
                Wake();
                throw new IOException(
                    $"rigctld at {Endpoint} is not connected, so the unkey could not be sent; the "
                    + "radio may still be keyed, and is unkeyed the moment rigctld answers");
            }

            try
            {
                connection.SetPtt(false);
                _unkeyOwed = false;
                _unkeyAttempts = 0;
            }
            catch (IOException lost)
            {
                DropIo(lost.Message);
                throw;
            }
            catch (RigctldException refused)
            {
                NoteUnkeyRefused(refused);
                throw;
            }
        }
    }

    // ------------------------------------------------------------------ tuning windows

    /// <summary>
    /// Tunes the rig to <paramref name="tuning"/> for <paramref name="duration"/> on behalf of
    /// <paramref name="owner"/>, and puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// The same owner asking again while its window is open renews it, retuning first if it asks
    /// for somewhere new; the rig still goes back to where it was before the first request.
    /// Another owner is refused while a window is open.
    /// </remarks>
    /// <param name="tuning">Where to tune.</param>
    /// <param name="duration">How long for; capped at <see cref="MaxWindow"/>.</param>
    /// <param name="owner">Who is asking, as a journal line should name them.</param>
    /// <param name="ownerLifetime">Cancelled when the owner goes away; the window then ends at
    /// once. A token that cannot be cancelled leaves it to the window's own time.</param>
    public RigTuneResult Tune(
        RigTuning tuning, TimeSpan duration, string owner, CancellationToken ownerLifetime = default)
    {
        if (tuning.DialHz is <= 0 or > 100_000_000_000)
        {
            return new(RigTuneOutcome.Invalid, $"{tuning.DialHz} Hz is not a dial frequency", null);
        }

        if (!RigModes.IsKnown(tuning.Mode))
        {
            return new(RigTuneOutcome.Invalid,
                $"\"{tuning.Mode}\" is not a mode this station asks a rig for. Use one of {RigModes.List}", null);
        }

        if (tuning.PassbandHz < 0 || tuning.PassbandHz > RigModes.MaxPassbandHz)
        {
            return new(RigTuneOutcome.Invalid,
                $"a passband of {tuning.PassbandHz} Hz is not one this station asks for; use 0 for the "
                + $"rig's normal width, or up to {RigModes.MaxPassbandHz} Hz", null);
        }

        if (duration <= TimeSpan.Zero)
        {
            return new(RigTuneOutcome.Invalid, "a window has to last more than 0 seconds", null);
        }

        if (ownerLifetime.IsCancellationRequested)
        {
            return new(RigTuneOutcome.Refused, "the owner had already gone away", null);
        }

        tuning = tuning with { Mode = tuning.Mode.ToUpperInvariant() };
        bool capped = duration > MaxWindow;
        TimeSpan length = capped ? MaxWindow : duration;

        RigWindow? existing;
        lock (_gate)
        {
            if (_disposed)
            {
                return new(RigTuneOutcome.Refused, "the station is shutting down", null);
            }

            if (_keyed)
            {
                return new(RigTuneOutcome.Refused,
                    "the transmitter is keyed, and the rig is never retuned in the middle of a "
                    + "transmission; ask again once it has unkeyed", null);
            }

            if (_unkeyOwed)
            {
                return new(RigTuneOutcome.Refused,
                    "the last unkey has not been confirmed by rigctld, so the radio may still be keyed "
                    + "and is not retuned until it is", null);
            }

            if (_retuning)
            {
                return new(RigTuneOutcome.Refused, "the rig is being retuned already; ask again in a moment", null);
            }

            if (_window is { } open && open.Owner != owner)
            {
                return new(RigTuneOutcome.Refused,
                    $"the rig is already tuned to {open.Tuning} for {open.Owner} until "
                    + $"{Utc(open.Expires)}", open);
            }

            if (_window is null && _restoreTo is not null)
            {
                return new(RigTuneOutcome.Refused,
                    $"the rig is still owed its restore to {_restoreTo}; ask again once it has been put back", null);
            }

            existing = _window;
            _retuning = true;
        }

        RigTuneResult result;
        try
        {
            result = TuneIo(tuning, length, owner, existing, capped);
        }
        finally
        {
            _retuning = false;
        }

        if (!result.Granted)
        {
            return result;
        }

        RigWindow window = result.Window!;

        // Registered outside both locks: a token cancelled between the check above and here runs
        // its callback inline, and that callback takes them.
        if (ownerLifetime.CanBeCanceled && existing is null)
        {
            window.Lifetime = ownerLifetime.Register(() => EndWindow(window, $"{owner} went away"));
        }

        Say(
            existing is not null
                ? $"rig: {owner} renewed its window on {window.Tuning} until {Utc(window.Expires)}"
                : $"rig: tuned to {window.Tuning} for {owner} until {Utc(window.Expires)}; "
                  + $"transmissions are held until it is put back to {window.RestoreTo}");
        RaiseChanged();
        return result;
    }

    private RigTuneResult TuneIo(RigTuning tuning, TimeSpan length, string owner, RigWindow? existing, bool capped)
    {
        lock (_io)
        {
            if (_connection is not { } connection)
            {
                return new(RigTuneOutcome.Refused,
                    $"rigctld at {Endpoint} is not connected"
                    + (_lastProblem is null ? "" : $" ({_lastProblem})"), existing);
            }

            RigTuning restoreTo;
            try
            {
                restoreTo = existing?.RestoreTo ?? ReadIo(connection);
            }
            catch (IOException lost)
            {
                DropIo(lost.Message);
                return new(RigTuneOutcome.Failed, $"rigctld at {Endpoint} went away: {lost.Message}", existing);
            }
            catch (RigctldException refused)
            {
                return new(RigTuneOutcome.Failed, $"the rig could not be read: {refused.Message}", existing);
            }

            // Owed from here, before the first command that moves the rig, and on the disk before
            // that, so neither a connection lost part way nor the process killed part way can leave
            // the rig where the window put it.
            if (existing is null)
            {
                PersistRestore(restoreTo);
                _restoreTo = restoreTo;
            }

            if (existing is null || existing.Tuning != tuning)
            {
                try
                {
                    ApplyIo(connection, tuning, $"for {owner}");
                }
                catch (RigctldException refused)
                {
                    // Put back whatever half of it took, and keep any window that was open.
                    try
                    {
                        ApplyIo(connection, existing?.Tuning ?? restoreTo, "");
                        if (existing is null)
                        {
                            ClearRestoreIo(restoreTo);
                        }
                    }
                    catch (IOException lost)
                    {
                        DropIo(lost.Message);
                    }
                    catch (RigctldException)
                    {
                        // Left owed; the next poll tries again.
                    }

                    return new(RigTuneOutcome.Failed, refused.Message, existing);
                }
                catch (IOException lost)
                {
                    DropIo(lost.Message);
                    return new(RigTuneOutcome.Failed,
                        $"rigctld at {Endpoint} went away part way: {lost.Message}. The rig is put back "
                        + $"to {restoreTo} as soon as it answers", existing);
                }
            }

            lock (_gate)
            {
                if (existing is not null && !ReferenceEquals(_window, existing))
                {
                    // It ran out while this was retuning it; its restore follows once this lets go.
                    return new(RigTuneOutcome.Refused,
                        "the window ended while it was being renewed; ask again for a new one", null);
                }

                RigWindow window = existing ?? new RigWindow(this, owner, tuning, restoreTo);
                window.Tuning = tuning;
                window.Expires = _time.GetUtcNow() + length;
                if (window.Timer is null)
                {
                    window.Timer = _time.CreateTimer(
                        _ => EndWindow(window, "its time ran out"), null, length, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    window.Timer.Change(length, Timeout.InfiniteTimeSpan);
                }

                _window = window;
                return new(existing is null ? RigTuneOutcome.Tuned : RigTuneOutcome.Renewed, null, window, capped);
            }
        }
    }

    /// <summary>Ends <paramref name="owner"/>'s window now, if it has one.</summary>
    /// <param name="owner">As it was given to <see cref="Tune"/>.</param>
    /// <returns>Whether there was one to end.</returns>
    public bool Release(string owner)
    {
        RigWindow? window = _window;
        if (window is null || window.Owner != owner)
        {
            return false;
        }

        EndWindow(window, $"{owner} released it");
        return true;
    }

    /// <summary>Ends a window and puts the rig back, or leaves the restore owed if it cannot.</summary>
    internal void EndWindow(RigWindow window, string why)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_window, window))
            {
                return;
            }

            _window = null;
            window.Timer?.Dispose();
            window.Lifetime.Unregister();
        }

        Say($"rig: the window on {window.Tuning} for {window.Owner} has ended ({why})");
        lock (_io)
        {
            RestoreIo();
        }

        RaiseChanged();
    }

    // ------------------------------------------------------------------ the connection

    private async Task RunAsync(bool connectedAtStart, CancellationToken cancellation)
    {
        TimeSpan retry = _options.FirstRetry;
        bool failed = !connectedAtStart;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (Connected)
                {
                    await NapAsync(_unkeyOwed ? _options.UnkeyRetry : _options.PollInterval,
                        whileConnected: true, cancellation).ConfigureAwait(false);

                    // The backoff starts again only once a connection has proved itself, so a
                    // rigctld that accepts and then fails at once is not hammered.
                    if (Poll())
                    {
                        retry = _options.FirstRetry;
                    }

                    failed = !Connected;
                    continue;
                }

                if (failed)
                {
                    await NapAsync(retry, whileConnected: false, cancellation).ConfigureAwait(false);
                    retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, _options.MaxRetry.Ticks));
                }

                failed = !await TryConnectAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception unexpected)
            {
                // Nothing may end this loop but shutdown: everything owed (an unkey, a restore)
                // is done from here.
                Warn(
                    $"rig: WARNING - the rigctld watch hit an error it did not expect "
                    + $"({unexpected.GetType().Name}: {unexpected.Message}); reconnecting");
                lock (_io)
                {
                    DropIo(unexpected.Message, quietly: true);
                }

                failed = true;
            }
        }
    }

    /// <summary>Waits for <paramref name="span"/> on the station's clock, or until something
    /// asks for the connection to be looked at sooner (a drop, an unkey that did not go).</summary>
    /// <param name="span">How long.</param>
    /// <param name="cancellation">Shutdown.</param>
    /// <param name="whileConnected">True for the wait between polls, which a connection that has
    /// already gone ends at once: the drop's wake may have come before this wait began.</param>
    private async Task NapAsync(TimeSpan span, bool whileConnected, CancellationToken cancellation)
    {
        Task wake;
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (whileConnected && _connection is null)
            {
                return;
            }

            wake = _wake.Task;
        }

        using var nap = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        DateTimeOffset until = _time.GetUtcNow() + span;
        Task delay = Task.Delay(span, _time, nap.Token);
        lock (_gate)
        {
            if (!wake.IsCompleted)
            {
                _napUntil = until;
            }
        }

        try
        {
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _napUntil = null;
            }
        }

        await nap.CancelAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
    }

    private void Wake()
    {
        lock (_gate)
        {
            _wake.TrySetResult();

            // Woken is no longer waiting on the clock, even before the continuation has run.
            _napUntil = null;
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellation)
    {
        RigctldConnection opened;
        try
        {
            opened = await RigctldConnection.OpenAsync(
                    Endpoint, _options.ConnectTimeout, _options.ReplyTimeout, cancellation)
                .ConfigureAwait(false);
        }
        catch (IOException unreachable)
        {
            lock (_gate)
            {
                _lastProblem = unreachable.Message;
                if (_unreachableSaid)
                {
                    return false;
                }

                _unreachableSaid = true;
            }

            Warn(
                $"rig: WARNING - cannot reach rigctld at {Endpoint} ({unreachable.Message}). "
                + "The station carries on without rig control and keeps trying. Is rigctld "
                + "running? For example: rigctld -m <model> -r /dev/ttyUSB0");
            return false;
        }

        lock (_io)
        {
            lock (_gate)
            {
                _connection = opened;
            }

            try
            {
                OnConnectedIo(opened);
                lock (_gate)
                {
                    _unreachableSaid = false;
                    _lastProblem = null;
                }

                RaiseChanged();
                return _connection is not null;
            }
            catch (Exception unusable) when (unusable is IOException or RigctldException)
            {
                // A rig that is switched off answers every read with an error; a rigctld that is
                // starting, or wedged, hangs up. Either is said once and retried with the backoff.
                string why = unusable is RigctldException
                    ? $"rigctld answered, but the rig did not: {unusable.Message}. Is the rig switched on and connected?"
                    : unusable.Message;
                bool said;
                lock (_gate)
                {
                    said = _unreachableSaid;
                }

                DropIo(why, quietly: true);
                if (!said)
                {
                    Warn($"rig: WARNING - rigctld at {Endpoint} cannot be used yet ({why}); retrying");
                }

                return false;
            }
        }
    }

    /// <summary>
    /// A new connection: check it can be spoken to, settle an owed unkey, read the rig, then put
    /// it where the station wants it: back from a window first, then the window, then the band
    /// plan. Throws <see cref="IOException"/> or <see cref="RigctldException"/> if the connection
    /// is not one to keep.
    /// </summary>
    private void OnConnectedIo(RigctldConnection connection)
    {
        if (!_skipVfoCheck)
        {
            switch (connection.WantsVfoArguments())
            {
                case true:
                    throw new IOException(
                        "rigctld was started with --vfo, which this station does not speak; start it without --vfo");
                case null:
                    // An old rigctld that ignores the command, so one too old to have --vfo. The
                    // connection may yet get the late answer, so it goes, and the next one does
                    // not ask.
                    _skipVfoCheck = true;
                    throw new IOException(
                        "rigctld did not answer \\chk_vfo, so it is taken to be too old to have --vfo; "
                        + "reconnecting without asking");
                default:
                    break;
            }
        }

        // Before anything else: a radio left keyed by a connection that died is the one thing here
        // that is on the air.
        if (KeysThroughRig && !_keyed)
        {
            bool owed = _unkeyOwed;
            try
            {
                connection.SetPtt(false);
                _unkeyOwed = false;
                _unkeyAttempts = 0;
                if (owed)
                {
                    Say("rig: unkeyed the radio, which had been keyed when rigctld went away");
                }
            }
            catch (RigctldException refused)
            {
                if (owed)
                {
                    NoteUnkeyRefused(refused);
                }
                else
                {
                    Warn($"rig: WARNING - the unkey sent on connecting was refused ({refused.Message})");
                }
            }
        }

        RigTuning state = ReadIo(connection);
        Say(_everConnected
            ? $"rig: rigctld at {Endpoint} is back: {state}"
            : $"rig: rigctld at {Endpoint}: {state}");
        _everConnected = true;

        if (_keyed || _unkeyOwed)
        {
            return;
        }

        if (_window is null && _restoreTo is not null)
        {
            RestoreIo();
            if (_restoreTo is not null || _connection is null)
            {
                return;
            }

            // Put back to where it was before the window; the band plan, which the config may
            // have changed since, is checked against that below.
            lock (_gate)
            {
                state = _known ?? state;
            }
        }

        if (_window is { } open)
        {
            Retuning(() => TryApplyIo(connection, open.Tuning));
        }
        else if (_options.Plan is { } plan && (!_planApplied || !IsOn(state, plan)))
        {
            // At start-up, and again after a reconnect that finds the rig somewhere else (a rig
            // that was power-cycled, a rigctld restarted against a rig that forgot): the band plan
            // is where this station's modems are, so it is where the dial has to be.
            Say(_planApplied
                ? $"rig: the rig is not where the band plan puts it; setting it back to {plan}"
                : $"rig: setting the rig to {plan} from the band plan");
            Retuning(() =>
            {
                try
                {
                    ApplyIo(connection, plan, "from the band plan");
                }
                catch (RigctldException refused)
                {
                    Warn(
                        $"rig: WARNING - {refused.Message}. The band plan's dial is not set; set the rig "
                        + $"to {plan} by hand");
                }
            });
            _planApplied = true;
        }
    }

    /// <summary>
    /// One look at a connected rig: an owed unkey, an owed restore, then its dial and mode.
    /// </summary>
    /// <returns>True when the rig answered all of it.</returns>
    private bool Poll()
    {
        // Skipped while a frame waits to key, so the keyup does not queue behind a slow reply -
        // unless something is owed, which is what that frame is waiting for.
        if (_keyed
            || (_options.TransmitPending?.Invoke() == true && !_unkeyOwed && _restoreTo is null))
        {
            return false;
        }

        lock (_io)
        {
            if (_connection is not { } connection || _keyed)
            {
                return false;
            }

            try
            {
                if (_unkeyOwed && KeysThroughRig)
                {
                    UnkeyOwedIo(connection);
                    if (_unkeyOwed)
                    {
                        return false;
                    }
                }

                if (_window is null && _restoreTo is not null)
                {
                    RestoreIo();
                    return _connection is not null && _restoreTo is null;
                }

                RigTuning? before;
                lock (_gate)
                {
                    before = _known;
                }

                RigTuning now = ReadIo(connection);
                if (before is not null && now != before && _changedLine.Due(out int more))
                {
                    Say(
                        $"rig: the rig is now on {now}, changed outside this station"
                        + (more > 0 ? $" ({more} more changes since the last line)" : ""));
                }

                return true;
            }
            catch (IOException lost)
            {
                DropIo(lost.Message);
                return false;
            }
            catch (RigctldException refused)
            {
                lock (_gate)
                {
                    _lastProblem = refused.Message;
                }

                return false;
            }
        }
    }

    /// <summary>Sends an owed <c>T 0</c>, saying how it went. Call holding <c>_io</c>.</summary>
    private void UnkeyOwedIo(RigctldConnection connection)
    {
        try
        {
            connection.SetPtt(false);
            if (_unkeyOwed)
            {
                _unkeyOwed = false;
                Say(_unkeyAttempts > 0
                    ? $"rig: unkeyed the radio after {_unkeyAttempts} refused attempts"
                    : "rig: unkeyed the radio after a keyup that went wrong");
                _unkeyAttempts = 0;
                RaiseChanged();
            }
        }
        catch (IOException lost)
        {
            DropIo(lost.Message);
        }
        catch (RigctldException refused)
        {
            NoteUnkeyRefused(refused);
        }
    }

    private void NoteUnkeyRefused(RigctldException refused)
    {
        _unkeyOwed = true;
        _unkeyAttempts++;
        Warn(
            $"rig: WARNING - the unkey was refused ({refused.Message}), attempt {_unkeyAttempts}. The "
            + $"radio may still be keyed; nothing is sent until it is unkeyed, and the unkey is tried "
            + $"again every {_options.UnkeyRetry.TotalSeconds:0.#} s");
        Wake();
        RaiseChanged();
    }

    /// <summary>Puts the rig back after a window, or leaves the restore owed. Call holding <c>_io</c>.</summary>
    private void RestoreIo()
    {
        if (_window is not null || _restoreTo is not { } restore || _keyed)
        {
            return;
        }

        if (_connection is not { } connection)
        {
            if (_restoreLine.Due(out _))
            {
                Warn(
                    $"rig: WARNING - rigctld at {Endpoint} is not connected, so the rig cannot be put "
                    + $"back to {restore} yet. The station does not transmit until it has been");
            }

            return;
        }

        try
        {
            if (!Retuning(() => ApplyIo(connection, restore, "after the window")))
            {
                // Keyed, or an unkey owed: the restore waits for it.
                return;
            }

            if (ClearRestoreIo(restore))
            {
                Say($"rig: put back to {restore}; transmissions resume");
                RaiseChanged();
            }
        }
        catch (IOException lost)
        {
            DropIo(lost.Message);
            if (_restoreLine.Due(out _))
            {
                Warn(
                    $"rig: WARNING - the rig could not be put back to {restore} yet. The station does "
                    + "not transmit until it has been");
            }
        }
        catch (RigctldException refused)
        {
            lock (_gate)
            {
                _lastProblem = refused.Message;
            }

            if (_restoreLine.Due(out int more))
            {
                Warn(
                    $"rig: WARNING - the rig could not be put back to {restore} ({refused.Message}"
                    + (more > 0 ? $", {more} more refusals since the last line" : "")
                    + "). The station does not transmit until it has been; trying again at every poll");
            }
        }
    }

    /// <summary>The restore to <paramref name="target"/> is confirmed: nothing is owed any more.
    /// Call holding <c>_io</c>.</summary>
    private bool ClearRestoreIo(RigTuning target)
    {
        lock (_gate)
        {
            if (_window is not null || _restoreTo != target)
            {
                return false;
            }

            _restoreTo = null;
        }

        TryDeleteRestoreFile();
        return true;
    }

    private void PersistRestore(RigTuning restoreTo)
    {
        if (_options.RestoreFile is not string file)
        {
            return;
        }

        try
        {
            RigRestoreFile.Write(file, Endpoint, restoreTo);
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            Warn(
                $"rig: WARNING - could not write {file} ({unwritable.Message}). The window goes ahead, "
                + "but a station killed during it would not know to put the rig back");
        }
    }

    private void TryDeleteRestoreFile()
    {
        if (_options.RestoreFile is not string file)
        {
            return;
        }

        try
        {
            RigRestoreFile.Delete(file);
        }
        catch (Exception undeletable) when (undeletable is IOException or UnauthorizedAccessException)
        {
            Warn(
                $"rig: WARNING - could not remove {file} ({undeletable.Message}); the next start-up "
                + "puts the rig back to it again");
        }
    }

    /// <summary>Runs a retune with keyups refused for its length. A keyed station, or one owed
    /// an unkey, is not retuned, and false says so.</summary>
    private bool Retuning(Action retune)
    {
        lock (_gate)
        {
            if (_keyed || _unkeyOwed)
            {
                return false;
            }

            _retuning = true;
        }

        try
        {
            retune();
            return true;
        }
        finally
        {
            _retuning = false;
        }
    }

    /// <summary>
    /// Sets the mode and passband, then the dial, and reads them back. The mode goes first because
    /// some rigs move the dial when the mode changes. Call holding <c>_io</c>.
    /// </summary>
    /// <exception cref="IOException">The connection failed; drop it.</exception>
    /// <exception cref="RigctldException">The rig refused the mode or the dial.</exception>
    private void ApplyIo(RigctldConnection connection, RigTuning tuning, string purpose)
    {
        try
        {
            connection.SetMode(tuning.Mode, tuning.PassbandHz);
        }
        catch (RigctldException narrow) when (tuning.PassbandHz > 0)
        {
            Warn(
                $"rig: WARNING - the rig would not take a {tuning.PassbandHz} Hz passband in "
                + $"{tuning.Mode} ({RigctldException.Describe(narrow.Code)}); using its normal width "
                + "for the mode instead");
            connection.SetMode(tuning.Mode, 0);
        }

        connection.SetFrequency(tuning.DialHz);
        RigTuning now = ReadIo(connection);
        if (now.DialHz != tuning.DialHz || !string.Equals(now.Mode, tuning.Mode, StringComparison.OrdinalIgnoreCase))
        {
            Warn($"rig: WARNING - asked the rig for {tuning} {purpose}, and it reports {now}");
        }
        else if (tuning.PassbandHz > 0 && now.PassbandHz != tuning.PassbandHz)
        {
            Warn(
                $"rig: WARNING - asked the rig for a {tuning.PassbandHz} Hz passband {purpose}, and "
                + $"it reports {now.PassbandHz} Hz. A modem outside the rig's filter is clipped or "
                + "deaf; \"rig\".\"passbandHz\" sets the width asked for, 0 the rig's normal one");
        }
    }

    /// <summary><see cref="ApplyIo"/> where a refusal is only worth a warning.</summary>
    private void TryApplyIo(RigctldConnection connection, RigTuning tuning)
    {
        try
        {
            ApplyIo(connection, tuning, "");
        }
        catch (RigctldException refused)
        {
            Warn($"rig: WARNING - {refused.Message}");
        }
    }

    /// <summary>Whether the rig is on <paramref name="wanted"/>: the dial and mode, and the
    /// passband when one was asked for.</summary>
    private static bool IsOn(RigTuning state, RigTuning wanted) =>
        state.DialHz == wanted.DialHz
        && string.Equals(state.Mode, wanted.Mode, StringComparison.OrdinalIgnoreCase)
        && (wanted.PassbandHz == 0 || state.PassbandHz == wanted.PassbandHz);

    private RigTuning ReadIo(RigctldConnection connection)
    {
        long dial = connection.GetFrequency();
        (string mode, int passband) = connection.GetMode();
        var read = new RigTuning(dial, mode.ToUpperInvariant(), passband);
        lock (_gate)
        {
            _known = read;
        }

        return read;
    }

    /// <summary>Throws the connection away. Call holding <c>_io</c>.</summary>
    private void DropIo(string why, bool quietly = false)
    {
        lock (_gate)
        {
            if (_connection is not { } connection)
            {
                return;
            }

            connection.Dispose();
            _connection = null;
            _lastProblem = why;
            _unreachableSaid = true;
            _wake.TrySetResult();
        }

        RaiseChanged();
        if (!quietly)
        {
            Warn(
                $"rig: WARNING - lost rigctld at {Endpoint} ({why}); reconnecting"
                + (KeysThroughRig && (_keyed || _unkeyOwed)
                    ? ". The radio may still be keyed: it is unkeyed the moment rigctld answers"
                    : ""));
        }
    }

    private string? HoldReasonLocked() =>
        _window is { } open
            ? $"the rig is tuned to {open.Tuning} for {open.Owner} until {Utc(open.Expires)}, "
              + "so this station does not transmit until it is put back"
        : _restoreTo is { } owed
            ? $"the rig has not yet been put back to {owed} after a tuning window, so this station "
              + "does not transmit until it has been"
        : _unkeyOwed
            ? "the last unkey has not been confirmed by rigctld, so the radio may still be keyed; "
              + "nothing is sent until it is"
        : _retuning
            ? "the rig is being retuned, so this station does not transmit until it has been"
        : null;

    private void Say(string line) => Journal?.Invoke(line);

    private void Warn(string line) => Problem?.Invoke(line);

    private void RaiseChanged() => Changed?.Invoke(Snapshot());

    /// <summary>An instant as ISO 8601 UTC to the second.</summary>
    private static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ shutdown

    /// <summary>Stops the watch, ends any window (putting the rig back), unkeys a rigctld PTT and
    /// closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        RigWindow? open;
        lock (_gate)
        {
            _disposed = true;
            open = _window;
            _window = null;
            open?.Timer?.Dispose();
            open?.Lifetime.Unregister();
        }

        if (open is not null)
        {
            Say($"rig: the window on {open.Tuning} for {open.Owner} has ended (the station is stopping)");
        }

        // One last try at a connection when something is owed, so a rigctld that was away is not
        // left with a radio on the wrong frequency, or keyed.
        bool owed = _restoreTo is not null || _unkeyOwed || _keyed;
        if (owed && !Connected)
        {
            try
            {
                await TryConnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception last) when (last is IOException or RigctldException or OperationCanceledException)
            {
                // Said below.
            }
        }

        lock (_io)
        {
            if (KeysThroughRig && _connection is { } connection)
            {
                try
                {
                    connection.SetPtt(false);
                    _unkeyOwed = false;
                }
                catch (Exception failed) when (failed is IOException or RigctldException)
                {
                    Warn($"rig: WARNING - the unkey at shutdown failed ({failed.Message})");
                }
            }

            _keyed = false;
            RestoreIo();
            if (_restoreTo is { } stillOwed)
            {
                Warn(
                    $"rig: WARNING - stopping with the rig not put back to {stillOwed}"
                    + (_options.RestoreFile is null ? "" : "; it is put back at the next start-up"));
            }

            lock (_gate)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }

        _stop.Dispose();
    }

    /// <summary>A line said at most once per interval, with a count of the ones held back.
    /// Guarded by its own lock, so callable from anywhere.</summary>
    private sealed class QuietLine(TimeProvider time, TimeSpan interval)
    {
        private readonly Lock _gate = new();
        private bool _said;
        private long _at;
        private int _held;

        internal bool Due(out int heldBack)
        {
            lock (_gate)
            {
                if (_said && time.GetElapsedTime(_at) < interval)
                {
                    _held++;
                    heldBack = 0;
                    return false;
                }

                _said = true;
                _at = time.GetTimestamp();
                heldBack = _held;
                _held = 0;
                return true;
            }
        }
    }
}
