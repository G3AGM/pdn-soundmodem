using System.Runtime.CompilerServices;

namespace Packet.SoundModem.Channel;

/// <summary>What happened to a <see cref="TransmitLease"/>.</summary>
public enum TransmitLeaseChange
{
    /// <summary>A sub-channel took the transmitter.</summary>
    Taken,

    /// <summary>The holder asked again and its lease was extended.</summary>
    Renewed,

    /// <summary>The holder gave the transmitter back.</summary>
    Released,

    /// <summary>The holder stopped renewing and the lease ran out.</summary>
    Expired,
}

/// <summary>
/// One change to a lease, as <see cref="TransmitLease.Changed"/> reports it.
/// </summary>
/// <param name="Change">What happened.</param>
/// <param name="SubChannel">The sub-channel that holds, or held, the lease.</param>
/// <param name="Expires">When the lease runs out (for Taken and Renewed) or ran out or was given
/// back (for Released and Expired).</param>
/// <param name="HeldFor">How long the sub-channel has held it, from the take to now.</param>
/// <param name="Renewals">How many times it has been renewed since the take.</param>
/// <param name="Refused">How many transmissions from everyone else it has refused since the take.</param>
public readonly record struct TransmitLeaseEvent(
    TransmitLeaseChange Change,
    int SubChannel,
    DateTimeOffset Expires,
    TimeSpan HeldFor,
    int Renewals,
    long Refused);

/// <summary>What <see cref="TransmitLease.Take"/> answered.</summary>
/// <param name="Granted">True when the caller holds the lease now.</param>
/// <param name="SubChannel">The holder: the caller when granted, whoever holds it when not.</param>
/// <param name="Expires">When the holder's lease runs out.</param>
/// <param name="Renewed">True when the caller already held it and this extended it.</param>
public readonly record struct TransmitLeaseGrant(bool Granted, int SubChannel, DateTimeOffset Expires, bool Renewed);

/// <summary>
/// Gives one sub-channel the channel's transmitter for a while, and refuses everyone else's
/// transmissions until it ends.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A broadcast (pdn-mailcast's daily slot) that shares a radio with
/// a node's ordinary traffic. While the broadcast runs, nothing else may key that radio, and the
/// rest of the station must otherwise carry on as normal: every modem still receives and every
/// KISS host stays connected. Restarting the station on a broadcast-only configuration would do
/// the first and break the second.</para>
/// <para><b>Refused, never queued.</b> A frame from another sub-channel is refused at once
/// through <see cref="SoundModemChannel.TransmitRejected"/> with the same message every time, so
/// the station's rate-limited drop line reports it. Queueing would release a burst of stale frames
/// when the lease ends, while AX.25 simply retries. Frames already queued when a lease is taken
/// are refused the same way. A keyup already on the air when the lease is taken finishes.</para>
/// <para><b>What belongs to the holder.</b> Its own modem's frames, and any other transmitter the
/// host has <see cref="Attribute">attributed</see> to its sub-channel (a station attributes each
/// modem's Morse identifier to that modem). Everything else is refused: other modems, their
/// idents, paging, ARDOP and anything that names no sub-channel at all.</para>
/// <para><b>It lapses by itself.</b> A lease is taken for a few tens of seconds and renewed, so a
/// holder that dies stops renewing and normal service comes back on its own within one lease.
/// The clock is the channel's <see cref="TimeProvider"/>: a timer reports the expiry as it
/// happens, and every question asked after the expiry instant reads the lease as free whether
/// or not that timer has fired yet. The length is measured on the clock's monotonic timestamp;
/// its UTC time is only ever used to tell a person when the lease ends.</para>
/// <para><b>It can close with a word of its own.</b> A host that sets <see cref="Closing"/> is
/// asked, as a lease is released or runs out, for something the holder must send before anyone
/// else may transmit: a station's closing Morse ident. The lease stays held, for that and
/// nothing else of anybody's, until it has gone or <see cref="ClosingTimeout"/> has passed.</para>
/// <para><b>Carrier sense can be bounded.</b> With <see cref="MaxCarrierWait"/> set, the holder's
/// transmissions wait at most that long for a clear channel and then go anyway, so a slot booked
/// for a time is not lost to a frequency that never quite clears. Unset, carrier sense is as it
/// always is.</para>
/// </remarks>
public sealed class TransmitLease
{
    /// <summary>The longest one take or renewal may ask for.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    /// <summary>The longest a lease stays held after its release or expiry while its
    /// <see cref="Closing"/> transmission waits to go out.</summary>
    public static readonly TimeSpan ClosingTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    // A receive window (issue #585) and a lease can never both be granted. The two decisions are
    // made under this lock, never across a rig's I/O: a window marks itself opening here, tunes
    // with nothing held, then clears the mark, by which time the rig shows the window itself.
    private readonly Lock _windowArbitration = new();
    private bool _windowOpening;
    private readonly ConditionalWeakTable<object, StrongBox<int>> _attributed = [];
    private int? _holder;

    // The lease is timed on the monotonic clock: _since when it was taken, _renewedAt when it was
    // last taken or renewed, and _duration from then. A wall clock that steps (NTP, an operator,
    // a GPS-disciplined source coming back) cannot shorten or lengthen it. _expires is the same
    // instant in UTC, for saying it to a person and nothing else.
    private long _since;
    private long _renewedAt;
    private TimeSpan _duration;
    private DateTimeOffset _expires;
    private int _renewals;
    private long _refused;
    private ITimer? _expiry;
    private TimeSpan? _maxCarrierWait;

    // Set while a released or expired lease waits for its closing transmission: still held, for
    // the holder's closing word alone, and nobody can take it until it has gone.
    private bool _closing;
    private TransmitLeaseChange _closingHow;

    internal TransmitLease(TimeProvider time) => _time = time;

    /// <summary>
    /// Raised on every change, outside any lock, on the thread that caused it: the caller of
    /// <see cref="Take"/> or <see cref="Release"/>, or the clock's timer for an expiry. A lease
    /// with a <see cref="Closing"/> transmission reports its release or expiry once that has gone.
    /// </summary>
    public event Action<TransmitLeaseEvent>? Changed;

    /// <summary>
    /// Raised when one of the holder's transmissions stops waiting for a clear channel because
    /// <see cref="MaxCarrierWait"/> ran out, with the sub-channel and how long it waited.
    /// </summary>
    public event Action<int, TimeSpan>? CarrierWaitCutShort;

    /// <summary>Raised inside the channel when a lease is taken, before <see cref="Changed"/>, so
    /// that everything already queued for anyone else is refused before the holder is told.</summary>
    internal event Action? TakenInternal;

    /// <summary>Raised inside the channel as a lease starts to end, before any closing
    /// transmission, so the channel can drop a dead holder's queued frames.</summary>
    internal event Action<int, TransmitLeaseChange>? EndingInternal;

    /// <summary>
    /// Asked, as a lease is released or runs out, for something its holder must transmit before
    /// the transmitter is handed back: the sub-channel in, the transmission's task out, or null
    /// for nothing. Until that task ends, or <see cref="ClosingTimeout"/> passes, the lease stays
    /// held. Null (the default) releases at once.
    /// </summary>
    public Func<int, Task?>? Closing { get; set; }

    /// <summary>
    /// Marks a receive window (issue #585) as being opened, unless a lease is held. Until
    /// <see cref="EndOpeningWindow"/>, <see cref="TakeUnlessWindow"/> refuses every lease, so the
    /// window can talk to the rig with nothing locked and no lease can be granted underneath it.
    /// </summary>
    /// <param name="holder">The sub-channel holding the lease, when refused.</param>
    /// <returns>False, with <paramref name="holder"/> set, while a lease is held.</returns>
    public bool TryBeginOpeningWindow(out int? holder)
    {
        lock (_windowArbitration)
        {
            holder = Holder;
            if (holder is not null)
            {
                return false;
            }

            _windowOpening = true;
            return true;
        }
    }

    /// <summary>Ends what <see cref="TryBeginOpeningWindow"/> began, whether or not the window
    /// was opened: from here an open window is seen through the rig's own state.</summary>
    public void EndOpeningWindow()
    {
        lock (_windowArbitration)
        {
            _windowOpening = false;
        }
    }

    /// <summary>
    /// <see cref="Take"/>, unless a receive window is being opened or
    /// <paramref name="windowOpen"/> says one is open, decided as one step against
    /// <see cref="TryBeginOpeningWindow"/>.
    /// </summary>
    /// <param name="windowOpen">Whether a receive window is open now. Called under a lock, so
    /// it must not block.</param>
    /// <param name="subChannel">As for <see cref="Take"/>.</param>
    /// <param name="duration">As for <see cref="Take"/>.</param>
    /// <param name="maxCarrierWait">As for <see cref="Take"/>.</param>
    /// <returns>The grant, or null when a receive window is open or being opened.</returns>
    public TransmitLeaseGrant? TakeUnlessWindow(
        Func<bool> windowOpen, int subChannel, TimeSpan duration, TimeSpan? maxCarrierWait = null)
    {
        lock (_windowArbitration)
        {
            return _windowOpening || windowOpen() ? null : Take(subChannel, duration, maxCarrierWait);
        }
    }

    /// <summary>The sub-channel holding the lease, or null when nobody does.</summary>
    public int? Holder
    {
        get
        {
            ExpireIfDue();
            return HolderNow();
        }
    }

    /// <summary>When the current lease runs out, or null when nobody holds one.</summary>
    public DateTimeOffset? Expires
    {
        get
        {
            ExpireIfDue();
            lock (_gate)
            {
                return _holder is null ? null : _expires;
            }
        }
    }

    /// <summary>
    /// How long the holder's transmissions wait for a clear channel before going anyway, or null
    /// for ordinary carrier sense; null too when nobody holds the lease.
    /// </summary>
    public TimeSpan? MaxCarrierWait
    {
        get
        {
            lock (_gate)
            {
                return _holder is null ? null : _maxCarrierWait;
            }
        }
    }

    /// <summary>Whether a released or expired lease is waiting for its closing transmission.</summary>
    public bool IsClosing
    {
        get
        {
            lock (_gate)
            {
                return _closing;
            }
        }
    }

    /// <summary>
    /// Takes the lease for <paramref name="subChannel"/>, or renews it if that sub-channel holds
    /// it already. Refused (not granted) while another sub-channel holds one, or while a lease is
    /// closing.
    /// </summary>
    /// <param name="subChannel">The sub-channel whose traffic may keep transmitting.</param>
    /// <param name="duration">How long from now; capped at <see cref="MaxDuration"/>.</param>
    /// <param name="maxCarrierWait">See <see cref="MaxCarrierWait"/>. On a renewal, null keeps
    /// what the lease already had.</param>
    /// <exception cref="ArgumentOutOfRangeException">A duration, or a carrier wait, of zero or
    /// less.</exception>
    public TransmitLeaseGrant Take(int subChannel, TimeSpan duration, TimeSpan? maxCarrierWait = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "a lease must last some time");
        }

        if (maxCarrierWait is { } wait && wait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCarrierWait), "a carrier wait must be some time");
        }

        if (duration > MaxDuration)
        {
            duration = MaxDuration;
        }

        ExpireIfDue();
        TransmitLeaseEvent change;
        ITimer? stale;
        lock (_gate)
        {
            if (_holder is int holder && (holder != subChannel || _closing))
            {
                return new TransmitLeaseGrant(false, holder, _expires, Renewed: false);
            }

            long now = _time.GetTimestamp();
            bool renewing = _holder is not null;
            if (!renewing)
            {
                _holder = subChannel;
                _since = now;
                _renewals = 0;
                _refused = 0;
                _maxCarrierWait = maxCarrierWait;
            }
            else
            {
                _renewals++;
                _maxCarrierWait = maxCarrierWait ?? _maxCarrierWait;
            }

            _renewedAt = now;
            _duration = duration;
            _expires = _time.GetUtcNow() + duration;
            stale = _expiry;
            _expiry = _time.CreateTimer(_ => OnTimer(), null, duration, Timeout.InfiniteTimeSpan);
            change = new TransmitLeaseEvent(
                renewing ? TransmitLeaseChange.Renewed : TransmitLeaseChange.Taken,
                subChannel, _expires, _time.GetElapsedTime(_since, now), _renewals, _refused);
        }

        // Never under the lock: disposing a timer can wait on its clock, and the clock's callback
        // is ExpireIfDue, which takes this lock.
        stale?.Dispose();
        if (change.Change == TransmitLeaseChange.Taken)
        {
            TakenInternal?.Invoke();
        }

        Changed?.Invoke(change);
        return new TransmitLeaseGrant(true, subChannel, change.Expires, change.Change == TransmitLeaseChange.Renewed);
    }

    /// <summary>
    /// Gives the lease back. False when nobody held one, or when <paramref name="onlyIf"/> names a
    /// sub-channel that is not the holder - checked and released in one step, so a release that
    /// arrives late cannot free a lease somebody else has taken since. With a
    /// <see cref="Closing"/> transmission to send, the lease is let go once it has gone; see
    /// <see cref="IsClosing"/>.
    /// </summary>
    /// <param name="onlyIf">Release only if this sub-channel holds the lease; null releases
    /// whoever does.</param>
    public bool Release(int? onlyIf = null)
    {
        ExpireIfDue();
        int holder;
        lock (_gate)
        {
            if (_holder is not int held || (onlyIf is int expected && expected != held))
            {
                return false;
            }

            if (_closing)
            {
                return true;
            }

            holder = held;
            _closing = true;
            _closingHow = TransmitLeaseChange.Released;
        }

        Close(holder, TransmitLeaseChange.Released);
        return true;
    }

    /// <summary>
    /// Says that transmissions queued under <paramref name="source"/> belong to
    /// <paramref name="subChannel"/>, so they go out while that sub-channel holds the lease. For
    /// the channel's delegate transmissions, which carry a source and no sub-channel: a modem's
    /// Morse identifier, say. Held weakly, so attributing a short-lived source leaks nothing.
    /// </summary>
    public void Attribute(object source, int subChannel)
    {
        ArgumentNullException.ThrowIfNull(source);
        _attributed.AddOrUpdate(source, new StrongBox<int>(subChannel));
    }

    /// <summary>
    /// Whether a frame for <paramref name="subChannel"/> may transmit now: true when nobody holds
    /// the lease or that sub-channel does.
    /// </summary>
    public bool Admits(int subChannel) => Holder is not int holder || holder == subChannel;

    /// <summary>
    /// Whether a delegate transmission queued under <paramref name="source"/> may transmit now:
    /// true when nobody holds the lease, or the source is attributed to the holder.
    /// </summary>
    public bool Admits(object? source) => Holder is not int holder || IsAttributed(source, holder);

    /// <summary>The refusal a transmission gets, the same words every time so a rate limit keyed
    /// on the message folds them together.</summary>
    internal Exception Refusal(int holder)
    {
        lock (_gate)
        {
            _refused++;
        }

        return new InvalidOperationException(
            $"sub-channel {holder} holds the transmit lease, so other transmissions are refused until it ends");
    }

    /// <summary>
    /// The holder as of this instant, an expired lease reading as free (unless it is closing),
    /// without reporting the expiry. For the channel's check under its own queue lock, where
    /// raising <see cref="Changed"/> would run a host's handler inside that lock.
    /// </summary>
    internal int? HolderNow()
    {
        lock (_gate)
        {
            return _holder is int holder && (_closing || !DueLocked()) ? holder : null;
        }
    }

    /// <summary>
    /// The holder of a lease that ran out and is sending its closing ident, or null. Its own
    /// frames are not let through any more: a holder that stopped renewing is gone, and only its
    /// closing ident may still go out. Includes an expiry that is due but not yet reported.
    /// </summary>
    internal int? ExpiringHolderNow()
    {
        lock (_gate)
        {
            if (_holder is not int holder)
            {
                return null;
            }

            return _closing ? (_closingHow == TransmitLeaseChange.Expired ? holder : null)
                : DueLocked() ? holder : null;
        }
    }

    /// <summary>Whether <paramref name="source"/> is attributed to <paramref name="subChannel"/>.</summary>
    internal bool IsAttributed(object? source, int subChannel) =>
        source is not null && _attributed.TryGetValue(source, out StrongBox<int>? box) && box.Value == subChannel;

    /// <summary>The carrier wait that applies to a transmission queued under
    /// <paramref name="source"/>: the lease's, if it belongs to the holder, else none.</summary>
    internal TimeSpan? CarrierWaitFor(object source)
    {
        lock (_gate)
        {
            return _holder is int holder && _maxCarrierWait is { } wait && IsAttributed(source, holder)
                ? wait
                : null;
        }
    }

    /// <summary>Tells the host a carrier wait was cut short.</summary>
    internal void NoteCarrierWaitCutShort(int subChannel, TimeSpan waited) =>
        CarrierWaitCutShort?.Invoke(subChannel, waited);

    /// <summary>Whether the current lease's time is up, on the monotonic clock. Call under the lock.</summary>
    private bool DueLocked() => _time.GetElapsedTime(_renewedAt) >= _duration;

    /// <summary>
    /// The expiry timer. A timer is allowed to fire a little early, and one that does would
    /// otherwise find the lease not yet due and leave nothing to report the expiry when it is,
    /// so it re-arms itself for whatever is left.
    /// </summary>
    private void OnTimer()
    {
        ExpireIfDue();
        ITimer? stale = null;
        lock (_gate)
        {
            if (_holder is not null && !_closing && !DueLocked())
            {
                TimeSpan left = _duration - _time.GetElapsedTime(_renewedAt);
                stale = _expiry;
                _expiry = _time.CreateTimer(
                    _ => OnTimer(), null, left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1),
                    Timeout.InfiniteTimeSpan);
            }
        }

        stale?.Dispose();
    }

    private void ExpireIfDue()
    {
        int holder;
        lock (_gate)
        {
            if (_holder is not int held || _closing || !DueLocked())
            {
                return;
            }

            holder = held;
            _closing = true;
            _closingHow = TransmitLeaseChange.Expired;
        }

        Close(holder, TransmitLeaseChange.Expired);
    }

    /// <summary>
    /// Ends a lease that has been marked closing: sends its closing transmission if the host has
    /// one, then lets it go and reports it.
    /// </summary>
    private void Close(int holder, TransmitLeaseChange how)
    {
        ITimer? stale;
        lock (_gate)
        {
            // The expiry timer has nothing left to do: the lease is ending one way or the other.
            stale = _expiry;
            _expiry = null;
        }

        stale?.Dispose();
        EndingInternal?.Invoke(holder, how);
        Task? closing = null;
        try
        {
            closing = Closing?.Invoke(holder);
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            // A host that cannot say what to send has nothing to send; the lease ends anyway.
        }

        if (closing is null)
        {
            Finish(holder, how);
            return;
        }

        _ = FinishAfterAsync(closing, holder, how);
    }

    private async Task FinishAfterAsync(Task closing, int holder, TransmitLeaseChange how)
    {
        using var stop = new CancellationTokenSource();
        Task bound = Task.Delay(ClosingTimeout, _time, stop.Token);
        try
        {
            await Task.WhenAny(closing, bound).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _ = closing.Exception;
            Finish(holder, how);
        }
    }

    private void Finish(int holder, TransmitLeaseChange how)
    {
        TransmitLeaseEvent change;
        lock (_gate)
        {
            if (_holder != holder || !_closing)
            {
                return;
            }

            DateTimeOffset at = how == TransmitLeaseChange.Expired ? _expires : _time.GetUtcNow();
            TimeSpan held = how == TransmitLeaseChange.Expired
                ? _time.GetElapsedTime(_since, _renewedAt) + _duration
                : _time.GetElapsedTime(_since);
            change = new TransmitLeaseEvent(how, holder, at, held, _renewals, _refused);
            _holder = null;
            _closing = false;
            _maxCarrierWait = null;
        }

        Changed?.Invoke(change);
    }
}
