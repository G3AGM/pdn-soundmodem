namespace Packet.SoundModem.Iq;

/// <summary>What one observation of an <see cref="IqRateWatch"/> concluded.</summary>
public enum IqRateVerdict
{
    /// <summary>Nothing new.</summary>
    None,

    /// <summary>The stream has stopped arriving at the rate it should.</summary>
    WentOff,

    /// <summary>The stream is arriving at the rate it should again.</summary>
    CameBack,
}

/// <summary>
/// Checks that an IQ stream arrives at the rate it is supposed to, against the wall clock: the
/// one change a server can make under a read-only client that the client can see for certain.
/// </summary>
/// <remarks>
/// <para>An rtl_tcp-compatible port (OpenWebRX's) switches profile without a word, and the
/// protocol has nowhere to put one. When the new profile has a different sample rate, the bytes
/// start arriving faster or slower than the rate the station was told, and that is measurable:
/// count the samples over a window of wall-clock time and compare.</para>
/// <para>Two consecutive windows have to agree before the verdict changes, either way, so one
/// odd window (a network stall, a burst after one) moves nothing. A window in which the reader
/// was the slow party (the caller says so: the socket had a backlog waiting) proves nothing about
/// the server and is set aside. So is the time just after a <see cref="Restart"/>, when a server
/// may be flushing what it buffered.</para>
/// <para>Original. Allocation-free: it is called on every read.</para>
/// </remarks>
public sealed class IqRateWatch
{
    private readonly TimeSpan _window;
    private readonly TimeSpan _settle;
    private readonly double _tolerance;
    private int _expectedRate;
    private DateTimeOffset _settledAt;
    private DateTimeOffset _windowStart;
    private bool _started;
    private long _count;
    private bool _limited;
    private int _offRun;
    private int _onRun;

    /// <param name="expectedRate">The rate the stream should arrive at, complex samples a second.</param>
    /// <param name="window">How long each measurement is. Five seconds by default.</param>
    /// <param name="tolerance">How far off, as a fraction, still counts as the right rate. 3 %
    /// by default: well inside the 15 % between 2.4 and 2.048 MS/s, the closest two rates an
    /// RTL profile is usually set to, and well outside network jitter over five seconds.</param>
    /// <param name="settle">How long after a restart is not measured. Three seconds by default.</param>
    public IqRateWatch(int expectedRate, TimeSpan? window = null, double tolerance = 0.03, TimeSpan? settle = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRate, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);
        _expectedRate = expectedRate;
        _window = window ?? TimeSpan.FromSeconds(5);
        _tolerance = tolerance;
        _settle = settle ?? TimeSpan.FromSeconds(3);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_window, TimeSpan.Zero, nameof(window));
    }

    /// <summary>Whether the stream is, as last concluded, off its rate.</summary>
    public bool Off { get; private set; }

    /// <summary>The rate the last conclusive window measured, complex samples a second, or 0.</summary>
    public double LastMeasuredRate { get; private set; }

    /// <summary>The rate the stream should arrive at.</summary>
    public int ExpectedRate => _expectedRate;

    /// <summary>
    /// Starts measuring afresh from <paramref name="now"/>: after a connect, or a retune this
    /// client made. What has been concluded (<see cref="Off"/>) stands.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="expectedRate">A new expected rate, or null to keep the one there is.</param>
    public void Restart(DateTimeOffset now, int? expectedRate = null)
    {
        if (expectedRate is int rate)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(rate, 1, nameof(expectedRate));
            _expectedRate = rate;
        }

        _settledAt = now + _settle;
        _windowStart = _settledAt;
        _started = false;
        _count = 0;
        _limited = false;
        _offRun = 0;
        _onRun = 0;
    }

    /// <summary>
    /// Counts <paramref name="samples"/> complex samples that arrived by <paramref name="now"/>.
    /// </summary>
    /// <param name="samples">Samples received since the last call.</param>
    /// <param name="now">The time.</param>
    /// <param name="readerBehind">True when the reader found a backlog waiting, so it, not the
    /// server, set the pace.</param>
    public IqRateVerdict Observe(long samples, DateTimeOffset now, bool readerBehind)
    {
        if (now < _settledAt)
        {
            return IqRateVerdict.None;
        }

        if (!_started)
        {
            // The first samples after the settle start the window; they arrived before it.
            _started = true;
            _windowStart = now;
            _count = 0;
            _limited = readerBehind;
            return IqRateVerdict.None;
        }

        _count += samples;
        _limited |= readerBehind;
        TimeSpan elapsed = now - _windowStart;
        if (elapsed < _window)
        {
            return IqRateVerdict.None;
        }

        double measured = _count / elapsed.TotalSeconds;
        bool conclusive = !_limited;
        _windowStart = now;
        _count = 0;
        _limited = readerBehind;
        if (!conclusive)
        {
            return IqRateVerdict.None;
        }

        LastMeasuredRate = measured;
        bool wrong = Math.Abs((measured / _expectedRate) - 1) > _tolerance;
        if (wrong)
        {
            _onRun = 0;
            if (++_offRun >= 2 && !Off)
            {
                Off = true;
                return IqRateVerdict.WentOff;
            }
        }
        else
        {
            _offRun = 0;
            if (++_onRun >= 2 && Off)
            {
                Off = false;
                return IqRateVerdict.CameBack;
            }
        }

        return IqRateVerdict.None;
    }
}
