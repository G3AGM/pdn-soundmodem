using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Daemon;

/// <summary>Settings for an <see cref="IqPipeSource"/>.</summary>
internal sealed record IqPipeSourceOptions
{
    /// <summary>How the bytes are laid out.</summary>
    public required IqSampleFormat Format { get; init; }

    /// <summary>The complex sample rate the feed is stated to run at, S/s. Trusted: the feed has
    /// no way to say otherwise, and the rate watch says when it looks wrong.</summary>
    public required int Rate { get; init; }

    /// <summary>The RF centre the first run is started at (and the only one, for a feed that
    /// cannot be tuned), Hz.</summary>
    public required double CentreHz { get; init; }

    /// <summary>The most complex samples handed out per read.</summary>
    public int ReadBlockPairs { get; init; } = 16384;

    /// <summary>The wait before the first restart after a run ends; doubled on each run that
    /// ends young, up to <see cref="MaxRestartDelay"/>.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between restarts.</summary>
    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(16);

    /// <summary>A run this long is a healthy one, and resets the backoff and the failure count.</summary>
    public TimeSpan HealthyRun { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Runs in a row that end young before the feed is given up as lost.</summary>
    public int MaxYoungRuns { get; init; } = 5;

    /// <summary>A read that waits this long, and leaves the feed this far behind the wall clock,
    /// is a gap rather than a pause.</summary>
    public TimeSpan StallGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a stretch the rate watch measures over.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How far off the stated rate the measured one may be, as a fraction, before the
    /// rate watch says so.</summary>
    public double RateTolerance { get; init; } = 0.02;

    /// <summary>The clock for every wait and measurement here.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Where ordinary lines go.</summary>
    public Action<string> Journal { get; init; } = _ => { };

    /// <summary>Where problem lines go.</summary>
    public Action<string> JournalError { get; init; } = _ => { };
}

/// <summary>
/// Raw IQ from a pipe, as an <see cref="IIqSource"/>: a child process's standard output, a FIFO,
/// or standard input, in CU8, CS8, CS16 or CF32 at a rate and centre stated in configuration.
/// It reaches every device <c>rx_sdr</c> (SoapySDR) or <c>rtl_sdr</c> can open with no binding
/// to either, and keeps their licences and packaging on the far side of a process boundary.
/// </summary>
/// <remarks>
/// <para><b>Runs and restarts.</b> Each run of the feed (a child process, one writer on a FIFO)
/// is read until it ends. A feed that can restart is started again: at once after a retune, after
/// <see cref="IqPipeSourceOptions.RestartDelay"/> otherwise, doubling for each run in a row that
/// ends before <see cref="IqPipeSourceOptions.HealthyRun"/>. After
/// <see cref="IqPipeSourceOptions.MaxYoungRuns"/> of those, or when standard input ends, the feed
/// is lost: <see cref="Lost"/> is raised once and <see cref="Read"/> returns 0, so the station
/// stops and its unit starts it afresh, as an UberSDR receiver that stays away does.</para>
/// <para><b>Gaps, honestly.</b> A pipe carries no sample counter, so this cannot know what the
/// far end dropped; it says what it can see. The time between one run's last samples and the
/// next run's first is a gap of that length at the stated rate. Within a run, a read that waited
/// longer than <see cref="IqPipeSourceOptions.StallGap"/> while the feed fell that far behind the
/// wall clock is a gap of the shortfall (no more than the wait), which a feed that merely writes
/// in bursts never is. Both are added to <see cref="SamplesLost"/> before the block that follows
/// them, as the interface asks, and both are estimates from the clock rather than counts.</para>
/// <para><b>Retunes.</b> <see cref="RetuneTo"/> restarts a feed whose command takes
/// <c>{frequency}</c> at a new centre; the first block of the new run carries the new
/// <see cref="CentreFrequencyHz"/> and a new <see cref="TuningGeneration"/>, and the time the
/// restart took is a gap as above. The rate never moves: it is the configuration's.</para>
/// <para><b>The rate watch.</b> The rate is trusted, but checked: over each
/// <see cref="IqPipeSourceOptions.RateWindow"/> of a run the samples delivered are counted against
/// the wall clock, and a run that delivers more than <see cref="IqPipeSourceOptions.RateTolerance"/>
/// off the stated rate is said in the journal: too fast means the configuration has the rate
/// wrong (and every frequency in the audio is off by the same factor); too slow means samples are
/// being lost on the far side of the pipe, or this machine cannot keep up.</para>
/// <para><b>Cost.</b> One byte buffer, sized at construction; a read converts in place and
/// allocates nothing.</para>
/// <para>Original; no ported code.</para>
/// </remarks>
internal sealed class IqPipeSource : IIqSource, IDisposable
{
    private static readonly TimeSpan RateLineInterval = TimeSpan.FromMinutes(10);

    private readonly IIqFeedLauncher _launcher;
    private readonly IqPipeSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly int _perPair;
    private readonly byte[] _bytes;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();

    private IIqFeed? _feed;
    private double _feedCentreHz;
    private double? _pendingCentreHz;
    private bool _everStarted;
    private bool _lost;
    private int _carry;

    // The run being read: when it started, what it has delivered, and whether it has delivered.
    private long _runStart;
    private long _runPairs;
    private bool _runFresh;
    private long _lastData;

    // Restart policy.
    private TimeSpan _delay;
    private int _youngRuns;

    // The rate watch.
    private long _windowStart;
    private long _windowPairs;
    private DateTimeOffset _nextRateLine = DateTimeOffset.MinValue;

    private long _generation;
    private long _samplesLost;
    private double _centreHz;
    private double _measuredRate;

    /// <summary>A source reading what <paramref name="launcher"/> starts.</summary>
    public IqPipeSource(IIqFeedLauncher launcher, IqPipeSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Rate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReadBlockPairs, 1);
        _launcher = launcher;
        _options = options;
        _time = options.TimeProvider;
        _perPair = IqSampleFormats.BytesPerPair(options.Format);
        _bytes = new byte[options.ReadBlockPairs * _perPair];
        _centreHz = options.CentreHz;
        _feedCentreHz = options.CentreHz;
        _delay = options.RestartDelay;
    }

    /// <summary>Raised once, on the reader's thread, when the feed is given up: standard input
    /// ended, or the runs kept ending young. The argument says why.</summary>
    public event Action<string>? Lost;

    /// <inheritdoc/>
    public int SampleRate => _options.Rate;

    /// <inheritdoc/>
    public double CentreFrequencyHz => Volatile.Read(ref _centreHz);

    /// <inheritdoc/>
    public long TuningGeneration => Interlocked.Read(ref _generation);

    /// <inheritdoc/>
    public long SamplesLost => Interlocked.Read(ref _samplesLost);

    /// <summary>The rate the last full window of the rate watch measured, S/s, or 0 before one
    /// has finished.</summary>
    public double MeasuredRate => Volatile.Read(ref _measuredRate);

    /// <summary>Whether the feed can be moved, by restarting it at a new centre.</summary>
    public bool Tunes => _launcher.Tunes;

    /// <summary>Whether the feed has been given up.</summary>
    public bool IsLost => Volatile.Read(ref _lost);

    /// <summary>
    /// Starts the first run now, on the caller's thread, so that a command that cannot be
    /// started at all fails at start-up rather than on the reader's thread. Not for a FIFO, whose
    /// open waits for a writer.
    /// </summary>
    /// <exception cref="IOException">The run could not be started.</exception>
    public void StartNow()
    {
        lock (_gate)
        {
            if (_feed is not null || _everStarted)
            {
                return;
            }
        }

        IIqFeed feed = _launcher.Start(_feedCentreHz, _options.Rate, _stopping.Token);
        Adopt(feed, _feedCentreHz);
    }

    /// <summary>
    /// Restarts the feed at <paramref name="centreHz"/>. The current run is ended now and the
    /// next started at once; its first block carries the new centre.
    /// </summary>
    /// <exception cref="InvalidOperationException">The feed cannot be tuned.</exception>
    public void RetuneTo(double centreHz)
    {
        if (!_launcher.Tunes)
        {
            throw new InvalidOperationException($"{_launcher.Description} is not started at a frequency of this station's choosing");
        }

        IIqFeed? running;
        lock (_gate)
        {
            _pendingCentreHz = centreHz;
            running = _feed;
        }

        running?.Dispose();
    }

    /// <inheritdoc/>
    public int Read(Span<float> interleaved)
    {
        int maxPairs = Math.Min(interleaved.Length / 2, _bytes.Length / _perPair);
        if (maxPairs < 1)
        {
            throw new ArgumentException("room for at least one I,Q pair is needed", nameof(interleaved));
        }

        while (true)
        {
            if (_stopping.IsCancellationRequested || _lost)
            {
                return 0;
            }

            IIqFeed? feed;
            lock (_gate)
            {
                feed = _feed;
            }

            if (feed is null)
            {
                if (!StartNextRun())
                {
                    return 0;
                }

                continue;
            }

            int room = (maxPairs * _perPair) - _carry;
            long before = _time.GetTimestamp();
            int got;
            try
            {
                got = feed.Output.Read(_bytes.AsSpan(_carry, room));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                got = 0;
            }

            if (_stopping.IsCancellationRequested)
            {
                return 0;
            }

            if (got <= 0)
            {
                EndRun(feed);
                continue;
            }

            int total = _carry + got;
            int pairs = total / _perPair;
            if (pairs == 0)
            {
                _carry = total;
                continue;
            }

            long now = _time.GetTimestamp();
            Account(before, now, pairs);

            int used = pairs * _perPair;
            int values = IqSampleFormats.Convert(_options.Format, _bytes.AsSpan(0, used), interleaved);
            _carry = total - used;
            if (_carry > 0)
            {
                _bytes.AsSpan(used, _carry).CopyTo(_bytes);
            }

            return values;
        }
    }

    /// <summary>Ends the current run and stops for good; a blocked <see cref="Read"/> returns 0.</summary>
    public void Dispose()
    {
        IIqFeed? running;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
            running = _feed;
            _feed = null;
        }

        running?.Dispose();
    }

    /// <summary>The bookkeeping for a block of <paramref name="pairs"/> that a read waiting from
    /// <paramref name="before"/> to <paramref name="now"/> returned: the gap or retune before it,
    /// and the rate watch.</summary>
    private void Account(long before, long now, int pairs)
    {
        if (_runFresh)
        {
            // The first samples of a run. A restart is a gap of the time it took; a new centre is
            // a retune. Both land on this block, which is the first after them.
            _runFresh = false;
            _runStart = now;
            _windowStart = now;
            _windowPairs = 0;
            if (_lastData != 0)
            {
                double away = _time.GetElapsedTime(_lastData, now).TotalSeconds;
                Interlocked.Add(ref _samplesLost, Math.Max(1, (long)Math.Round(away * _options.Rate)));
            }

            if (_feedCentreHz != _centreHz)
            {
                Volatile.Write(ref _centreHz, _feedCentreHz);
                Interlocked.Increment(ref _generation);
            }
        }
        else
        {
            // A stall inside a run: a gap only if the read waited long enough AND the feed is now
            // that far short of the wall clock, so a burst writer that is on time is never one.
            TimeSpan waited = _time.GetElapsedTime(before, now);
            if (waited >= _options.StallGap)
            {
                // How far short of the wall clock the run was when the read began (negative for a
                // writer that buffers ahead), plus the wait, less what the wait brought.
                double expected = _time.GetElapsedTime(_runStart, before).TotalSeconds * _options.Rate;
                long waitedPairs = (long)Math.Round(waited.TotalSeconds * _options.Rate);
                long shortfall = (long)Math.Round(expected - _runPairs) + waitedPairs - pairs;
                long gap = Math.Min(shortfall, waitedPairs);
                if (gap >= (long)(_options.StallGap.TotalSeconds * _options.Rate / 2))
                {
                    Interlocked.Add(ref _samplesLost, gap);
                    // Count the gap as delivered, so the next stall is measured afresh.
                    _runPairs += gap;
                }
            }
        }

        _runPairs += pairs;
        _lastData = now;
        WatchRate(now, pairs);
    }

    private void WatchRate(long now, int pairs)
    {
        _windowPairs += pairs;
        TimeSpan span = _time.GetElapsedTime(_windowStart, now);
        if (span < _options.RateWindow)
        {
            return;
        }

        double measured = _windowPairs / span.TotalSeconds;
        Volatile.Write(ref _measuredRate, measured);
        _windowStart = now;
        _windowPairs = 0;
        double ratio = measured / _options.Rate;
        if (Math.Abs(ratio - 1) <= _options.RateTolerance)
        {
            return;
        }

        DateTimeOffset wall = _time.GetUtcNow();
        if (wall < _nextRateLine)
        {
            return;
        }

        _nextRateLine = wall + RateLineInterval;
        string why = ratio > 1
            ? "faster than it can be: the rate in the configuration is likely not the rate the program was told, and every frequency heard is off by the same factor"
            : "short: samples are being lost on the far side of the pipe, or this machine is not keeping up";
        _options.JournalError(
            $"iqpipe: the feed delivered {measured:F0} S/s over the last {span.TotalSeconds:F0} s, "
            + $"not the {_options.Rate} S/s configured ({(ratio - 1) * 100:+0.0;-0.0} %); {why}");
    }

    /// <summary>The current run read 0: it is over.</summary>
    private void EndRun(IIqFeed feed)
    {
        bool planned;
        lock (_gate)
        {
            if (!ReferenceEquals(_feed, feed))
            {
                return;
            }

            _feed = null;
            planned = _pendingCentreHz is not null;
        }

        if (planned)
        {
            feed.Dispose();
            return;
        }

        string reason = feed.EndReason;
        feed.Dispose();
        bool delivered = !_runFresh;
        bool healthy = delivered && _time.GetElapsedTime(_runStart) >= _options.HealthyRun;
        if (healthy)
        {
            _youngRuns = 0;
            _delay = _options.RestartDelay;
        }
        else
        {
            _youngRuns++;
        }

        if (!_launcher.CanRestart)
        {
            GiveUp($"{reason}; there is nothing to restart");
            return;
        }

        if (_youngRuns >= _options.MaxYoungRuns)
        {
            GiveUp($"{reason}; that is {_youngRuns} runs in a row that ended within "
                + $"{_options.HealthyRun.TotalSeconds:F0} s, so the feed is given up");
            return;
        }

        _options.JournalError($"iqpipe: {reason}; restarting in {_delay.TotalSeconds:F1} s");
    }

    /// <summary>Starts the next run, after the backoff unless it is a retune or the first.</summary>
    /// <returns>False when the source is stopping or the feed is lost.</returns>
    private bool StartNextRun()
    {
        double centre;
        bool wait;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested || _lost)
            {
                return false;
            }

            wait = _everStarted && _pendingCentreHz is null;
            centre = _pendingCentreHz ?? _feedCentreHz;
            _pendingCentreHz = null;
        }

        if (wait)
        {
            try
            {
                Task.Delay(_delay, _time, _stopping.Token).Wait(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            _delay = TimeSpan.FromTicks(Math.Min(_delay.Ticks * 2, _options.MaxRestartDelay.Ticks));

            // A retune asked for during the wait is where this run starts.
            lock (_gate)
            {
                centre = _pendingCentreHz ?? centre;
                _pendingCentreHz = null;
            }
        }
        else if (_everStarted)
        {
            _options.Journal($"iqpipe: restarting {_launcher.Description} at {centre / 1e6:F6} MHz");
        }

        IIqFeed feed;
        try
        {
            feed = _launcher.Start(centre, _options.Rate, _stopping.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException)
        {
            if (_stopping.IsCancellationRequested)
            {
                return false;
            }

            _everStarted = true;
            _feedCentreHz = centre;
            _youngRuns++;
            if (_youngRuns >= _options.MaxYoungRuns)
            {
                GiveUp($"{e.Message}; that is {_youngRuns} failed starts in a row, so the feed is given up");
                return false;
            }

            _options.JournalError($"iqpipe: {e.Message}; trying again in {_delay.TotalSeconds:F1} s");
            return !_stopping.IsCancellationRequested;
        }

        return Adopt(feed, centre);
    }

    /// <summary>Makes <paramref name="feed"/> the current run.</summary>
    private bool Adopt(IIqFeed feed, double centre)
    {
        bool retunedMeanwhile;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                feed.Dispose();
                return false;
            }

            _feed = feed;
            _feedCentreHz = centre;
            _everStarted = true;
            _runFresh = true;
            _runPairs = 0;
            _carry = 0;
            retunedMeanwhile = _pendingCentreHz is not null;
        }

        if (retunedMeanwhile)
        {
            // A retune came while this run was starting, and found nothing to end: end it now, so
            // the next read starts the run that was asked for.
            feed.Dispose();
        }

        return true;
    }

    private void GiveUp(string reason)
    {
        Volatile.Write(ref _lost, true);
        _options.JournalError($"iqpipe: {reason}");
        Lost?.Invoke(reason);
    }
}
