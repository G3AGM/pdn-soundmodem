namespace Packet.SoundModem.Daemon;

/// <summary>
/// A headless Flex's slice receive filter, widened while a channel audio stream connection asks
/// for a band (issue #584) and put back to exactly what bring-up set when none does.
/// </summary>
/// <remarks>
/// <para><see cref="Request"/> is called by the stream under its own lock, in order, so it only
/// records the latest target and makes sure one sender is running. The sender applies targets
/// one at a time, always the latest, each awaited before the next, so a late "put back" can
/// never land after a newer "widen" or the other way round.</para>
/// <para>Nothing is sent when the target is what was last applied: a band already inside the
/// filter writes nothing to the radio and says nothing in the journal.</para>
/// <para>The slice and the session are read when a command is sent, not when this was made, so
/// a rebuilt slice is followed. A rebuild comes up on bring-up's own filter, so
/// <see cref="SliceRebuilt"/> forgets what was applied and puts a standing widening back.</para>
/// </remarks>
internal sealed class FlexStreamFilter
{
    private readonly (int LowHz, int HighHz) _baseline;
    private readonly Func<string> _sliceIndex;
    private readonly Func<string, Task> _send;
    private readonly Action<string> _say;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private (int LowHz, int HighHz) _target;
    private (int LowHz, int HighHz)? _applied;
    private bool _sending;
    // Counts slice rebuilds, so a command in flight across one is not taken as applied to the
    // new slice: it went to the old one, and the new one came up on the baseline.
    private int _rebuilds;

    /// <param name="baseline">The filter bring-up left the slice on.</param>
    /// <param name="sliceIndex">The slice's index, read each time a command is sent.</param>
    /// <param name="send">Sends one command and completes when the radio has accepted it;
    /// throws if it did not.</param>
    /// <param name="say">The journal.</param>
    /// <param name="warn">The journal's error side.</param>
    public FlexStreamFilter(
        (int LowHz, int HighHz) baseline,
        Func<string> sliceIndex,
        Func<string, Task> send,
        Action<string> say,
        Action<string> warn)
    {
        _baseline = baseline;
        _target = baseline;
        _applied = baseline;
        _sliceIndex = sliceIndex;
        _send = send;
        _say = say;
        _warn = warn;
    }

    /// <summary>The filter last confirmed on the slice, or null when not known; for tests.</summary>
    internal (int LowHz, int HighHz)? Applied
    {
        get
        {
            lock (_gate)
            {
                return _applied;
            }
        }
    }

    /// <summary>Whether a command is being sent or waits to be; for tests.</summary>
    internal bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _sending;
            }
        }
    }

    /// <summary>
    /// The union of the bands the stream's connections ask for, or null when none asks.
    /// </summary>
    public void Request((int LowHz, int HighHz)? band)
    {
        lock (_gate)
        {
            _target = band is (int lowHz, int highHz)
                ? (Math.Min(_baseline.LowHz, BandPlanner.LowCutClearing(lowHz)),
                   Math.Max(_baseline.HighHz, BandPlanner.HighCutClearing(highHz)))
                : _baseline;
            StartSendingLocked();
        }
    }

    /// <summary>
    /// The slice was rebuilt, on bring-up's own filter: anything widened has to be widened
    /// again.
    /// </summary>
    public void SliceRebuilt()
    {
        lock (_gate)
        {
            _applied = _baseline;
            _rebuilds++;
            StartSendingLocked();
        }
    }

    private void StartSendingLocked()
    {
        if (_sending || Nullable.Equals(_applied, _target))
        {
            return;
        }

        _sending = true;
        _ = Task.Run(SendAsync);
    }

    private async Task SendAsync()
    {
        while (true)
        {
            (int LowHz, int HighHz) target;
            int rebuilds;
            lock (_gate)
            {
                if (Nullable.Equals(_applied, _target))
                {
                    _sending = false;
                    return;
                }

                target = _target;
                rebuilds = _rebuilds;
            }

            _say(target != _baseline
                ? $"flex: widening the slice receive filter to {target.LowHz}-{target.HighHz} Hz "
                  + "for the channel audio stream"
                : $"flex: putting the slice receive filter back to {target.LowHz}-{target.HighHz} "
                  + "Hz, no channel audio stream connection is asking for a band any more");
            try
            {
                await _send($"filt {_sliceIndex()} {target.LowHz} {target.HighHz}").ConfigureAwait(false);
                lock (_gate)
                {
                    if (rebuilds == _rebuilds)
                    {
                        _applied = target;
                    }
                }
            }
            catch (Exception e)
            {
                _warn(
                    "flex: WARNING - could not set the slice receive filter for the channel audio "
                    + $"stream: {e.GetBaseException().Message}");
                lock (_gate)
                {
                    if (rebuilds != _rebuilds)
                    {
                        // It failed on a slice that has since been replaced; the new one is
                        // still owed whatever is asked for now.
                        continue;
                    }

                    // Not known any more; the next request tries again rather than this one
                    // retrying at a radio that has just refused it.
                    _applied = null;
                    _sending = false;
                }

                return;
            }
        }
    }
}
