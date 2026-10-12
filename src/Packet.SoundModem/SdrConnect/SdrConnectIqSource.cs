using System.Net.WebSockets;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.SdrConnect;

/// <summary>
/// An <see cref="IIqSource"/> over SDRconnect's WebSocket API: the primary device's IQ, as
/// SDRconnect streams it at the device rate, tuned by the station.
/// </summary>
/// <remarks>
/// <para><b>Buffering.</b> The client's receive loop pushes and the front end's reader pulls, so
/// between them is a second of samples at the stream's rate, kept in runs (segments) that each
/// start with what the IQ contract needs said about them: a new tuning, or samples lost just
/// before. A block <see cref="Read"/> returns never spans the start of a run, so a gap or a retune
/// is reported with the block it precedes. A reader that falls a whole second behind loses what
/// does not fit after a short wait, counted as lost; the wait holds up the socket, which is how
/// a slow reader of any TCP stream slows its sender.</para>
/// <para><b>Rate and tuning</b> are what SDRconnect reports (see <see cref="SdrConnectClient"/>):
/// <see cref="SampleRate"/> and <see cref="CentreFrequencyHz"/> are the reported values from the
/// connect on, and move with <see cref="TuningGeneration"/> when SDRconnect reports a change.</para>
/// <para><b>Cost.</b> The buffer is sized at the connect (and again on a faster rate); a steady
/// stream converts straight into it and allocates nothing.</para>
/// <para>Original, from SDRplay's published SDRconnect WebSocket API document, version 1.0.3.</para>
/// </remarks>
public sealed class SdrConnectIqSource : IIqSource, ISdrConnectSink, IDisposable
{
    private const int MaxSegments = 256;

    /// <summary>How long the receive loop waits for the reader to make room before it throws
    /// samples away: a stall this short is passed back to SDRconnect through the socket, as any
    /// TCP reader passes it back, rather than lost.</summary>
    private static readonly TimeSpan WriterPatience = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OverflowLineInterval = TimeSpan.FromSeconds(60);

    private readonly SdrConnectClient _client;
    private readonly SdrConnectOptions _options;
    private readonly object _gate = new();
    private readonly Segment[] _segments = new Segment[MaxSegments];
    private float[] _ring = [];
    private int _head;
    private int _tail;
    private int _count;
    private int _firstSegment;
    private int _segmentCount;
    private bool _ended;
    private bool _everDelivered;

    // The writer's side, under _gate: what the next run starts with.
    private bool _nextTuning;
    private double _nextCentreHz;
    private int _nextRate;
    private long _nextLost;
    private DateTimeOffset _nextOverflowLine = DateTimeOffset.MinValue;
    private long _quietOverflows;

    // Read from other threads; 64-bit, so Interlocked to keep a 32-bit Pi from tearing them.
    private long _generation;
    private long _samplesLost;
    private long _overflows;

    /// <summary>Makes a source for SDRconnect at <paramref name="endpoint"/>; nothing is opened
    /// until <see cref="OpenAsync"/>.</summary>
    public SdrConnectIqSource(SdrConnectEndpoint endpoint, SdrConnectOptions options)
        : this(endpoint, options, connect: null)
    {
    }

    /// <summary>The same, over sockets from <paramref name="connect"/>; the tests hand it one end
    /// of a pair whose other end is a fake SDRconnect.</summary>
    internal SdrConnectIqSource(SdrConnectEndpoint endpoint, SdrConnectOptions options, Func<CancellationToken, Task<WebSocket>>? connect)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options with { Stream = SdrConnectStream.Iq };
        _client = new SdrConnectClient(endpoint, _options, this, connect);
        _client.Lost += reason => Lost?.Invoke(reason);
        CentreFrequencyHz = options.CentreFrequencyHz;
        SampleRate = options.SampleRate ?? 1;
    }

    /// <summary>SDRconnect's address.</summary>
    public SdrConnectEndpoint Endpoint => _client.Endpoint;

    /// <summary>The connection.</summary>
    internal SdrConnectClient Client => _client;

    /// <inheritdoc />
    public int SampleRate { get; private set; }

    /// <inheritdoc />
    public double CentreFrequencyHz { get; private set; }

    /// <inheritdoc />
    public long TuningGeneration => Interlocked.Read(ref _generation);

    /// <inheritdoc />
    public long SamplesLost => Interlocked.Read(ref _samplesLost);

    /// <summary>Times the reader fell so far behind that samples were thrown away.</summary>
    public long Overflows => Interlocked.Read(ref _overflows);

    /// <summary>Whether the station should expect samples; see <see cref="SdrConnectClient.SessionLive"/>.</summary>
    public bool SessionLive => _client.SessionLive;

    /// <summary>What SDRconnect said at the connect, property by property.</summary>
    public IReadOnlyDictionary<string, string> Report => _client.Report;

    /// <summary>The LO the station last asked for: where the source is, or is going.</summary>
    public double TargetCentreHz => _client.TargetCentreHz;

    /// <summary>Raised once when SDRconnect has delivered nothing for too long; the source has
    /// stopped.</summary>
    public event Action<string>? Lost;

    /// <summary>
    /// Connects, sets the radio up and starts the stream; from then <see cref="SampleRate"/> and
    /// <see cref="CentreFrequencyHz"/> are what SDRconnect reported.
    /// </summary>
    /// <exception cref="IOException">SDRconnect could not be reached.</exception>
    /// <exception cref="InvalidOperationException">SDRconnect answered but will not do.</exception>
    public async Task OpenAsync(CancellationToken cancellation)
    {
        await _client.OpenAsync(cancellation).ConfigureAwait(false);
        lock (_gate)
        {
            SampleRate = Math.Max(_client.ReportedRate, 1);
            CentreFrequencyHz = _client.ReportedCentreHz;
            Size(SampleRate);
        }
    }

    /// <summary>Retunes the LO. The stream follows when SDRconnect reports it.</summary>
    public void Tune(double centreHz) => _client.TuneCentre(centreHz);

    /// <inheritdoc />
    public int Read(Span<float> interleaved)
    {
        if (interleaved.Length < 2)
        {
            throw new ArgumentException("room for at least one I,Q pair is needed", nameof(interleaved));
        }

        lock (_gate)
        {
            while (true)
            {
                if (_segmentCount > 0)
                {
                    ref Segment front = ref _segments[_firstSegment];
                    if (front.Floats == 0 && _segmentCount > 1)
                    {
                        Dequeue();
                        continue;
                    }

                    if (front.Floats > 0)
                    {
                        if (!front.Applied)
                        {
                            Apply(ref front);
                        }

                        int take = Math.Min(interleaved.Length & ~1, front.Floats);
                        int first = Math.Min(take, _ring.Length - _tail);
                        _ring.AsSpan(_tail, first).CopyTo(interleaved);
                        _ring.AsSpan(0, take - first).CopyTo(interleaved[first..]);
                        _tail = (_tail + take) % _ring.Length;
                        _count -= take;
                        front.Floats -= take;
                        if (front.Floats == 0 && _segmentCount > 1)
                        {
                            Dequeue();
                        }

                        _everDelivered = true;
                        Monitor.PulseAll(_gate); // room for a writer that is waiting
                        return take;
                    }
                }

                if (_ended)
                {
                    return 0;
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <summary>Closes the connection; a blocked <see cref="Read"/> returns 0.</summary>
    public void Dispose()
    {
        _client.Dispose();
        End();
    }

    /// <inheritdoc />
    void ISdrConnectSink.Frames(ReadOnlySpan<byte> frames)
    {
        int floats = frames.Length / 2;
        bool overflowed = false;
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            bool fresh = _segmentCount == 0 || _nextTuning || _nextLost > 0;
            long deadline = 0;
            while (!_ended && (floats > _ring.Length - _count || (fresh && _segmentCount == MaxSegments)) && floats <= _ring.Length)
            {
                long now = _options.TimeProvider.GetTimestamp();
                if (deadline == 0)
                {
                    deadline = now + (long)(WriterPatience.TotalSeconds * _options.TimeProvider.TimestampFrequency);
                }
                else if (now >= deadline)
                {
                    break;
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(50));
                fresh = _segmentCount == 0 || _nextTuning || _nextLost > 0;
            }

            if (_ended)
            {
                return;
            }

            if (floats > _ring.Length - _count || (fresh && _segmentCount == MaxSegments))
            {
                // The reader is a second behind and has stayed there: what does not fit is lost,
                // and said so.
                _nextLost += floats / 2;
                Interlocked.Increment(ref _overflows);
                overflowed = true;
            }
            else
            {
                if (fresh)
                {
                    int index = (_firstSegment + _segmentCount) % MaxSegments;
                    _segments[index] = new Segment
                    {
                        NewTuning = _nextTuning,
                        CentreHz = _nextCentreHz,
                        Rate = _nextRate,
                        LostBefore = _nextLost,
                    };
                    _segmentCount++;
                    _nextTuning = false;
                    _nextLost = 0;
                }

                int first = Math.Min(floats, _ring.Length - _head);
                SdrConnectProtocol.ConvertIq(frames[..(first * 2)], _ring.AsSpan(_head, first), _options.IqFullScale);
                SdrConnectProtocol.ConvertIq(frames[(first * 2)..], _ring.AsSpan(0, floats - first), _options.IqFullScale);
                _head = (_head + floats) % _ring.Length;
                _count += floats;
                _segments[(_firstSegment + _segmentCount - 1) % MaxSegments].Floats += floats;
                Monitor.PulseAll(_gate);
            }
        }

        if (overflowed)
        {
            SayOverflow();
        }
    }

    /// <inheritdoc />
    void ISdrConnectSink.Gap(long frames)
    {
        lock (_gate)
        {
            _nextLost += frames;
        }
    }

    /// <inheritdoc />
    void ISdrConnectSink.Tuning(double frequencyHz, int sampleRate)
    {
        lock (_gate)
        {
            _nextTuning = true;
            _nextCentreHz = frequencyHz;
            _nextRate = sampleRate;
            Size(sampleRate);
        }
    }

    /// <inheritdoc />
    void ISdrConnectSink.End() => End();

    private void End()
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Makes the buffer hold a second at <paramref name="rate"/>. A faster rate than it
    /// was sized for throws away what is waiting, counted as lost; that is a rate change, not a
    /// steady state. Under <see cref="_gate"/>.</summary>
    private void Size(int rate)
    {
        int wanted = Math.Max(2 * rate, 8192);
        if (_ring.Length >= wanted)
        {
            return;
        }

        if (_count > 0)
        {
            _nextLost += _count / 2;
        }

        _ring = new float[wanted];
        _head = 0;
        _tail = 0;
        _count = 0;
        _firstSegment = 0;
        _segmentCount = 0;
    }

    /// <summary>What a run says before its first sample is handed out: its tuning, and what was
    /// lost before it. Nothing is said for the first run of all, which the properties already
    /// describe. Under <see cref="_gate"/>.</summary>
    private void Apply(ref Segment segment)
    {
        segment.Applied = true;
        if (segment.NewTuning && (segment.CentreHz != CentreFrequencyHz || segment.Rate != SampleRate))
        {
            CentreFrequencyHz = segment.CentreHz;
            SampleRate = segment.Rate;
            if (_everDelivered)
            {
                Interlocked.Increment(ref _generation);
            }
        }
        else if (segment.NewTuning && _everDelivered)
        {
            // SDRconnect reported a tuning, and the same one: a consumer may restart its filters
            // for nothing, which costs less than missing a real one.
            Interlocked.Increment(ref _generation);
        }

        if (segment.LostBefore > 0 && _everDelivered)
        {
            Interlocked.Add(ref _samplesLost, segment.LostBefore);
        }
    }

    private void Dequeue()
    {
        _firstSegment = (_firstSegment + 1) % MaxSegments;
        _segmentCount--;
    }

    private void SayOverflow()
    {
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        if (now < _nextOverflowLine)
        {
            _quietOverflows++;
            return;
        }

        string more = _quietOverflows > 0 ? $" ({_quietOverflows} more since the last line)" : "";
        _options.Journal?.Invoke($"sdrconnect: the receiver is more than a second behind SDRconnect's IQ; samples "
            + $"were thrown away and marked as a gap{more}. This machine may be too slow for the sample rate.");
        _nextOverflowLine = now + OverflowLineInterval;
        _quietOverflows = 0;
    }

    /// <summary>One run of samples, and what is to be said before it.</summary>
    private struct Segment
    {
        public int Floats;
        public bool Applied;
        public bool NewTuning;
        public double CentreHz;
        public int Rate;
        public long LostBefore;
    }
}
