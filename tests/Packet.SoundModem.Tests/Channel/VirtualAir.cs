using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A clock that only moves when the transmitter is waiting on it, and a sound card on the end of
/// it that costs the clock what the audio costs the air.
/// </summary>
/// <remarks>
/// <para>The no-op sink the other channel tests use makes a keyup instantaneous, which is fine
/// while nothing under test cares how long a transmission takes. It stops being fine the moment
/// the question is where a frame's wait went, because most of a queued frame's wait can be this
/// station's own airtime and a free write has none.</para>
/// <para><b>Nothing here is moved by the wall clock.</b> The first version of this harness
/// cranked a <see cref="FakeTimeProvider"/> a millisecond at a time from a thread-pool loop, so
/// the fake clock ran at roughly real speed whatever the transmitter was doing, and a loaded box
/// that held the transmitter off a thread put however long that took onto the figures under
/// test. It also made a test that waits two virtual seconds take two real ones at best and thirty
/// at worst (#537). <see cref="Clock"/> instead moves only in two ways: the sound card moves it
/// by exactly the length of each burst, from inside the write, and a test moves it only while the
/// transmitter is parked on one of the clock's own timers, and then straight to that timer's due
/// time. Between those, nothing moves it, however long the thread pool takes.</para>
/// </remarks>
internal static class VirtualAir
{
    /// <summary>
    /// What a figure measured through this harness can be off by: one CSMA slot (10 ms in these
    /// tests), because a test that opens the channel does so while the transmitter is parked in a
    /// slot, and the transmitter only looks again when that slot ends. Nothing else is uncertain.
    /// </summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// How long, on the wall clock, a test waits for the transmitter to do the next thing before
    /// calling it a hang. Only a hang reaches it: no correct run, however slow the box, waits
    /// anywhere near this for one step.
    /// </summary>
    private static readonly TimeSpan HangAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far a test may drive the clock on one wait before it is called a transmitter that is
    /// never going to finish: far beyond anything these tests wait, which is seconds.
    /// </summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The one place a <see cref="FakeTimeProvider"/> is moved by tests that crank one themselves,
    /// so that two crankers cannot lose an advance to each other.
    /// </summary>
    private static readonly object Crank = new();

    internal static void Tick(FakeTimeProvider time, TimeSpan step)
    {
        lock (Crank)
        {
            time.Advance(step);
        }
    }

    /// <summary>
    /// A clock that moves only when told to, and knows when the code under test is waiting on it.
    /// </summary>
    /// <remarks>
    /// <para><b>Parked</b> means a <c>Task.Delay</c> on this clock is armed. That is the only way
    /// the transmitter waits for time to pass - a CSMA slot, a turnaround hold, a transmit-inhibit
    /// poll - and while one is armed the transmitter is doing nothing else: it is blocked on that
    /// delay, which only this clock can complete. Other timers (a frame's held-too-long expiry)
    /// fire when the clock passes them, but they do not count as parked, because the transmitter
    /// is not waiting on them and may be busy while they are armed.</para>
    /// <para>A delay is recognised by its state: <c>Task.Delay(TimeSpan, TimeProvider, ...)</c>
    /// hands <see cref="CreateTimer"/> the delay's own task as the callback state.</para>
    /// <para>Timer callbacks run on the thread that moved the clock, as a real timer's would run
    /// on a timer thread, so the transmitter's continuation after a slot can run - and key up, and
    /// write, and move the clock again from inside the sound card - nested inside a test's own
    /// advance. The crank is a re-entrant lock for that reason.</para>
    /// </remarks>
    internal sealed class Clock : TimeProvider
    {
        private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly object _gate = new();
        private readonly object _crank = new();
        private readonly List<VirtualTimer> _armed = [];
        private long _now;
        private long _armings;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(Volatile.Read(ref _now));

        public override long GetTimestamp() => Volatile.Read(ref _now);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>
        /// Moves the clock on by a stretch of airtime, firing whatever falls due on the way. For
        /// the sound card, which is on the transmitter's thread and is the transmitter, so it
        /// never needs to wait for the transmitter to park.
        /// </summary>
        internal void Elapse(TimeSpan span)
        {
            lock (_crank)
            {
                AdvanceHolding(Volatile.Read(ref _now) + span.Ticks);
            }

            // Letting go of the crank is a change too. A test whose step found it held, with a
            // delay already armed, waits for the next signal; the last one AdvanceHolding gave
            // was before the release, so without this one a step that could now go ahead would
            // wait for something else to move first.
            Signal();
        }

        /// <summary>
        /// Moves the clock to <paramref name="until"/>, one parked wait at a time: each step goes
        /// to the earlier of the transmitter's next due time and the target, and only once the
        /// transmitter is parked again. On return the clock reads exactly <paramref name="until"/>
        /// (or later, if a keyup that ran inside the last step took it past) and the transmitter
        /// is parked or idle.
        /// </summary>
        internal async Task AdvanceToAsync(DateTimeOffset until)
        {
            long target = (until - Epoch).Ticks;
            while (true)
            {
                // The signal is taken before the clock is read, never after: see RunUntilAsync.
                (Task changed, long? due) = Look();
                if (Volatile.Read(ref _now) >= target)
                {
                    return;
                }

                if (due is long next && TryStep(Math.Min(next, target)))
                {
                    continue;
                }

                await WaitAsync(changed, "to park on the clock").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs the transmitter until <paramref name="done"/> completes, moving the clock only while
        /// it is parked, to the instant its wait ends.
        /// </summary>
        internal async Task RunUntilAsync(Task done)
        {
            // Completion wakes the loop like any other change, so the loop only ever resumes
            // from the clock's own signal, on a thread of the pool's, and never inline on whatever
            // thread completed the task - which can be inside a timer callback, holding the crank.
            _ = done.ContinueWith(_ => Signal(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            long limit = Volatile.Read(ref _now) + GiveUpAfter.Ticks;
            while (true)
            {
                // The signal first and the test second, never the other way round. The task
                // usually completes on a pool thread, after the transmitter's own thread has
                // moved on (EnqueueTransmit announces from a continuation), and its Signal
                // replaces the signal it completes. Read IsCompleted first and a completion
                // landing between that read and Look was signalled on the instance before the
                // one this loop then waits on, and nothing ever completes that one: the wait ran
                // out its 60 s with the frame long since sent (#686). Taken first, a completion
                // after Look completes the signal in hand, and one before it is seen here.
                (Task changed, long? due) = Look();
                if (done.IsCompleted)
                {
                    break;
                }

                if (due is long next)
                {
                    if (next > limit)
                    {
                        throw new TimeoutException(
                            $"the transmitter was still waiting after {GiveUpAfter.TotalMinutes} virtual minutes");
                    }

                    if (TryStep(next))
                    {
                        continue;
                    }
                }

                await WaitAsync(changed, "to park on the clock or finish").ConfigureAwait(false);
            }

            await done.ConfigureAwait(false);
        }

        /// <summary>The signal for the next change, and the due time of the earliest armed delay.</summary>
        private (Task Changed, long? Due) Look()
        {
            lock (_gate)
            {
                long? due = null;
                foreach (VirtualTimer timer in _armed)
                {
                    if (timer.IsDelay && (due is null || timer.Due < due))
                    {
                        due = timer.Due;
                    }
                }

                return (_changed.Task, due);
            }
        }

        /// <summary>
        /// One step of a test's advance, unless the clock is being moved already - which with a
        /// delay armed means a keyup's write is still finishing on another thread, and the step
        /// waits for it rather than racing it.
        /// </summary>
        private bool TryStep(long target)
        {
            if (!global::System.Threading.Monitor.TryEnter(_crank))
            {
                return false;
            }

            try
            {
                AdvanceHolding(target);
                return true;
            }
            finally
            {
                global::System.Threading.Monitor.Exit(_crank);
            }
        }

        private static async Task WaitAsync(Task changed, string what)
        {
            try
            {
                await changed.WaitAsync(HangAfter).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"the transmitter did not get round {what} within {HangAfter.TotalSeconds} s of wall clock");
            }
        }

        /// <summary>Fires every timer due by <paramref name="target"/>, in order, and ends there.</summary>
        private void AdvanceHolding(long target)
        {
            while (true)
            {
                VirtualTimer? next = null;
                lock (_gate)
                {
                    foreach (VirtualTimer timer in _armed)
                    {
                        if (timer.Due <= target
                            && (next is null || timer.Due < next.Due
                                || (timer.Due == next.Due && timer.Armed < next.Armed)))
                        {
                            next = timer;
                        }
                    }

                    if (next is null)
                    {
                        // Never backwards: a keyup run inside a callback can have taken the clock
                        // past this step's own target.
                        if (_now < target)
                        {
                            Volatile.Write(ref _now, target);
                        }

                        SignalLocked();
                        return;
                    }

                    if (next.Due > _now)
                    {
                        Volatile.Write(ref _now, next.Due);
                    }

                    if (next.Period > 0)
                    {
                        next.Due += next.Period;
                        next.Armed = ++_armings;
                    }
                    else
                    {
                        _armed.Remove(next);
                    }

                    SignalLocked();
                }

                next.Fire();
            }
        }

        private void Signal()
        {
            lock (_gate)
            {
                SignalLocked();
            }
        }

        private void SignalLocked()
        {
            TaskCompletionSource changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }

        private sealed class VirtualTimer : ITimer
        {
            private readonly Clock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private bool _disposed;

            public VirtualTimer(Clock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
                IsDelay = state is Task;
            }

            public long Due { get; set; }

            public long Period { get; set; }

            public long Armed { get; set; }

            /// <summary>A <c>Task.Delay</c>, which is what parked means.</summary>
            public bool IsDelay { get; }

            public void Fire() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_clock._gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _clock._armed.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = _clock._now + dueTime.Ticks;
                        Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                        Armed = ++_clock._armings;
                        _clock._armed.Add(this);
                    }

                    _clock.SignalLocked();
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_clock._gate)
                {
                    _disposed = true;
                    _clock._armed.Remove(this);
                    _clock.SignalLocked();
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A sound card that costs the clock what the audio costs the air: the write moves the clock on
    /// by exactly the burst's own duration before it returns.
    /// </summary>
    internal sealed class PacedSink(int sampleRate, Clock clock) : IAudioOutput
    {
        private readonly List<DateTimeOffset> _written = [];

        public int SampleRate { get; } = sampleRate;

        /// <summary>
        /// When each burst finished going to the device, on the virtual clock and in order.
        /// </summary>
        /// <remarks>
        /// The instant a station stamps as a transmitted frame's <c>heard_at</c>, taken here on the
        /// transmitter's own thread rather than in a handler for FrameTransmittedWithReport, which
        /// runs once the thread pool gets to it.
        /// </remarks>
        public IReadOnlyList<DateTimeOffset> Written
        {
            get
            {
                lock (_written)
                {
                    return [.. _written];
                }
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            clock.Elapse(TimeSpan.FromTicks(samples.Length * TimeSpan.TicksPerSecond / SampleRate));
            lock (_written)
            {
                _written.Add(clock.GetUtcNow());
            }
        }

        public void Drain()
        {
        }
    }
}
