using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Iq;

/// <summary>Settings for an <see cref="IqReceiver"/> as a whole; each channel has its own
/// <see cref="IqChannelSpec"/>.</summary>
public sealed record IqReceiverOptions
{
    /// <summary>The station's DSP rate: every channel's audio comes out at this rate, so the
    /// station has nothing to decimate.</summary>
    public required int DspRate { get; init; }

    /// <summary>The most complex samples pulled from the source per read. 8192 is 3.4 ms at
    /// 2.4 MS/s and 170 ms at 48 kS/s.</summary>
    public int ReadBlockFrames { get; init; } = 8192;

    /// <summary>Swap I and Q on the way in, for a source wired or reported the other way round
    /// (its spectrum then comes out mirrored about the centre).</summary>
    public bool SwapIq { get; init; }

    /// <summary>
    /// The half-width about the source's centre, Hz, that channels may be tuned anywhere within
    /// once running, or null for just what the channels given at construction need. The coarse
    /// decimation keeps this band and no more, so a wider figure costs CPU.
    /// </summary>
    public double? CoverageHz { get; init; }

    /// <summary>The coarse decimation's alias attenuation, dB.</summary>
    public double CoarseStopbandAttenuationDb { get; init; } = 80;

    /// <summary>The longest gap marked in the audio as silence, seconds. A longer loss is marked
    /// at this length and said in the journal; the station is not made to sit through minutes of
    /// zeros for a source that stalled.</summary>
    public double MaxGapSeconds { get; init; } = 5;

    /// <summary>Each channel's buffer, seconds of audio: slack for a GC pause or a busy box, as
    /// on the UberSDR device.</summary>
    public double RingSeconds { get; init; } = 8;

    /// <summary>Where the receiver's lines go (retunes, gaps, a channel falling behind), or null.</summary>
    public Action<string>? Journal { get; init; }

    /// <summary>The clock the journal's rate limits run on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// The IQ receive front end: one <see cref="IIqSource"/> in, one <see cref="IAudioInput"/> per
/// channel out at the station's DSP rate, so every IQ backend is a transport and nothing more,
/// and the receive filter is this program's rather than the source's.
/// </summary>
/// <remarks>
/// <para><b>Chain.</b> Source -> optional I/Q swap -> <see cref="CoarseDecimator"/> (a halfband
/// cascade, shared by every channel, down to the lowest rate that still carries them all) -> per
/// channel: NCO to put the channel's dial at 0 Hz -> M0LTE.Dsp's
/// <c>ComplexRationalResampler</c> to the channel rate, which is also the channel filter -> the
/// demodulator (M0LTE.Dsp's <c>StreamingSsbDemodulator</c> for USB and LSB, the
/// <see cref="StreamingFmDiscriminator"/> for FM, then M0LTE.Dsp's real <c>RationalResampler</c>
/// to the DSP rate when the channel rate is above it) -> a fixed gain -> the channel's buffer.
/// The resamplers take any ratio, so a source at 2.048 MS/s or 250 kS/s is as welcome as one at
/// 2.4 MS/s.</para>
/// <para><b>Why the coarse stage comes first.</b> A channel filter run at the source rate is
/// what an existing <see cref="DigitalDownconverter"/> does, and at 2.4 MS/s that is about 300 M
/// multiply-accumulates a second for each channel. Halving first, where only a narrow band about
/// DC has to survive, costs about twenty per input sample for the whole source, and the
/// per-channel work then runs at tens of kS/s.</para>
/// <para><b>Retunes and gaps</b> are the source's to report (the two counters on
/// <see cref="IIqSource"/>) and this class's to act on. A retune (the generation moving) restarts
/// every filter and demodulator, rebuilds the chain if the rate moved, and re-places each channel
/// that is anchored to an RF dial; a channel the new tuning cannot hear goes silent, and says so,
/// until one can. A gap (samples lost) restarts every filter and puts the lost time into each
/// channel's audio as silence, up to <see cref="IqReceiverOptions.MaxGapSeconds"/>, so what
/// follows is heard at the right time and nothing is spliced. Both are counted on each channel
/// (<see cref="IqReceiverChannel.Discontinuities"/>), which is what the station's input-loss
/// counter wants, and said in the journal.</para>
/// <para><b>Threaded or pumped.</b> <see cref="Start"/> runs one reader thread that pulls the
/// source and feeds every channel's buffer, so the station's blocking <c>Read</c> model is
/// unchanged; <see cref="Pump"/> does one block on the caller's thread, for tests and for offline
/// replay. Not both.</para>
/// <para><b>Cost.</b> Every buffer is sized at construction (and again on a rate change), so a
/// block allocates nothing.</para>
/// <para>Original; no ported code. The filters and demodulators it strings together are
/// M0LTE.Dsp's and this namespace's, each documented where it lives.</para>
/// </remarks>
public sealed class IqReceiver : IDisposable
{
    private static readonly TimeSpan GapLineInterval = TimeSpan.FromSeconds(10);

    private readonly IIqSource _source;
    private readonly IqReceiverOptions _options;
    private readonly IqReceiverChannel[] _channels;
    private readonly float[] _read;
    private readonly double _protectedHz;
    private CoarseDecimator _coarse;
    private int _rate;
    private double _centreHz;
    private long _generation;
    private long _lost;
    private Thread? _thread;
    private volatile bool _stopping;
    private int _started;

    // Gap lines: the first one says itself, the rest within the interval are summed into the next.
    private DateTimeOffset _nextGapLine = DateTimeOffset.MinValue;
    private long _quietGaps;
    private long _quietLost;

    /// <summary>Builds the front end for <paramref name="source"/>, with a channel for each spec.</summary>
    /// <exception cref="ArgumentException">A channel cannot run as specified, or lies outside the
    /// source's band; the message says which and why.</exception>
    public IqReceiver(IIqSource source, IReadOnlyList<IqChannelSpec> channels, IqReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DspRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReadBlockFrames, 1);
        if (source.SampleRate < 1)
        {
            throw new ArgumentException($"the source's sample rate ({source.SampleRate}) is not a rate", nameof(source));
        }

        _source = source;
        _options = options;
        _rate = source.SampleRate;
        _centreHz = source.CentreFrequencyHz;
        _generation = source.TuningGeneration;
        _lost = source.SamplesLost;
        _read = new float[2 * options.ReadBlockFrames];

        // The band the coarse stage keeps: every channel's filter, out to its stopband, and any
        // room asked for on top.
        double needed = options.CoverageHz ?? 0;
        for (int i = 0; i < channels.Count; i++)
        {
            IqChannelSpec spec = channels[i] ?? throw new ArgumentException($"channel {i} is null", nameof(channels));
            if (spec.Problem(options.DspRate) is string problem)
            {
                throw new ArgumentException($"channel {i}: {problem}", nameof(channels));
            }

            int channelRate = spec.ChannelRateFor(options.DspRate);
            double reach = Math.Abs(spec.OffsetHz) + spec.StopbandHzFor(channelRate);
            if (reach > _rate / 2.0)
            {
                throw new ArgumentException(
                    $"channel {i} at {spec.OffsetHz:F0} Hz reaches {reach:F0} Hz from the centre, "
                    + $"past the {_rate / 2.0:F0} Hz a {_rate} S/s source carries",
                    nameof(channels));
            }

            needed = Math.Max(needed, reach);
        }

        _protectedHz = needed;
        _coarse = new CoarseDecimator(_rate, _protectedHz, options.ReadBlockFrames, options.CoarseStopbandAttenuationDb);

        _channels = new IqReceiverChannel[channels.Count];
        for (int i = 0; i < channels.Count; i++)
        {
            _channels[i] = new IqReceiverChannel(i, channels[i], options, _centreHz);
            _channels[i].Place(_coarse, _centreHz, rebuild: true);
            if (_channels[i].Muted)
            {
                throw new ArgumentException($"channel {i}: {_channels[i].MutedReason}", nameof(channels));
            }
        }
    }

    /// <summary>The channels, in the order given, each an <see cref="IAudioInput"/> at the DSP rate.</summary>
    public IReadOnlyList<IqReceiverChannel> Channels => _channels;

    /// <summary>The source's rate as last seen, Hz.</summary>
    public int SourceRate => Volatile.Read(ref _rate);

    /// <summary>The rate after the coarse stage, Hz: what each channel's NCO runs at.</summary>
    public int CoarseRate => _coarse.OutputRate;

    /// <summary>The coarse stage, as planned for the source's current rate.</summary>
    public CoarseDecimator Coarse => _coarse;

    /// <summary>Retunes seen (the source's generation moving), since construction.</summary>
    public long Retunes { get; private set; }

    /// <summary>Gaps seen (the source's loss counter moving), since construction.</summary>
    public long Gaps { get; private set; }

    /// <summary>Complex samples the source said it lost, since construction.</summary>
    public long SamplesLost { get; private set; }

    /// <summary>Starts the reader thread, which pumps until the source closes or this is disposed.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("the receiver is already running");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "iq-receiver" };
        _thread.Start();
    }

    /// <summary>
    /// Pulls one block from the source and feeds it to every channel. Returns false when the
    /// source has closed, after which every channel's <c>Read</c> returns what is left and then 0.
    /// For a receiver that is not <see cref="Start"/>ed.
    /// </summary>
    public bool Pump()
    {
        if (_stopping)
        {
            EndAll();
            return false;
        }

        int got = _source.Read(_read);
        if (got <= 0)
        {
            foreach (IqReceiverChannel channel in _channels)
            {
                channel.Finish();
            }

            return false;
        }

        got &= ~1;
        // A gap reported with a retune is measured at the rate before it: the interface does not
        // say which tuning the lost samples belonged to, and the old one is where the stream was.
        int lostRate = _rate;
        long generation = _source.TuningGeneration;
        long lost = _source.SamplesLost;
        bool restarted = false;
        if (generation != _generation)
        {
            _generation = generation;
            Retune();
            restarted = true;
        }

        if (lost != _lost)
        {
            long missing = Math.Max(0, lost - _lost);
            _lost = lost;
            if (missing > 0)
            {
                MarkGap(missing, lostRate, restarted);
            }
        }

        Span<float> block = _read.AsSpan(0, got);
        if (_options.SwapIq)
        {
            for (int k = 0; k < block.Length; k += 2)
            {
                (block[k], block[k + 1]) = (block[k + 1], block[k]);
            }
        }

        ReadOnlySpan<float> coarse = _coarse.Process(block);
        foreach (IqReceiverChannel channel in _channels)
        {
            channel.Ingest(coarse, _coarse, _centreHz);
        }

        return true;
    }

    /// <summary>Pumps until the source closes (offline convenience).</summary>
    public void PumpToEnd()
    {
        while (Pump())
        {
        }
    }

    /// <summary>
    /// Stops the reader thread at the next block and ends every channel. A source blocked in
    /// <c>Read</c> is the source's to unblock: dispose it first, or this waits up to two seconds
    /// for it.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        Thread? thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        EndAll();
    }

    private void Run()
    {
        try
        {
            while (!_stopping && Pump())
            {
            }
        }
        catch (Exception ex)
        {
            // The station sees its input stop and restarts as it does for any dead feed; the
            // reason is said here because nothing else will say it.
            _options.Journal?.Invoke($"iq: the receive thread stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            EndAll();
        }
    }

    private void EndAll()
    {
        foreach (IqReceiverChannel channel in _channels)
        {
            channel.End();
        }
    }

    /// <summary>The block just read is the first at a new tuning.</summary>
    private void Retune()
    {
        Retunes++;
        int rate = _source.SampleRate;
        double centre = _source.CentreFrequencyHz;
        bool rateMoved = rate != _rate;
        if (rateMoved)
        {
            if (rate < 1)
            {
                throw new InvalidDataException($"the source reported a sample rate of {rate}");
            }

            Volatile.Write(ref _rate, rate);
            _coarse = new CoarseDecimator(rate, Math.Min(_protectedHz, rate / 2.0 * 0.999), _options.ReadBlockFrames, _options.CoarseStopbandAttenuationDb);
        }
        else
        {
            _coarse.Reset();
        }

        _centreHz = centre;
        string where = centre != 0 ? $"{centre / 1e6:F6} MHz" : "an unknown frequency";
        _options.Journal?.Invoke($"iq: the source retuned to {where} at {rate} S/s; every channel's filters restarted");
        foreach (IqReceiverChannel channel in _channels)
        {
            // Counted before the audio that marks it, so the station's loss counter moves with the
            // block the discontinuity is in, not the one after.
            channel.NoteDiscontinuity();
            channel.Place(_coarse, centre, rebuild: rateMoved);
        }
    }

    /// <summary>The source lost <paramref name="missing"/> samples, at <paramref name="rate"/>,
    /// immediately before the block just read.</summary>
    private void MarkGap(long missing, int rate, bool alreadyRestarted)
    {
        Gaps++;
        SamplesLost += missing;
        if (!alreadyRestarted)
        {
            _coarse.Reset();
        }

        long cap = (long)(_options.MaxGapSeconds * rate);
        long marked = Math.Min(missing, Math.Max(0, cap));
        foreach (IqReceiverChannel channel in _channels)
        {
            if (!alreadyRestarted)
            {
                // Counted first, as for a retune; once per block when both happen at once.
                channel.NoteDiscontinuity();
                channel.Restart();
            }

            channel.AppendSilence(marked, rate);
        }

        if (_options.Journal is not Action<string> journal)
        {
            return;
        }

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        if (now < _nextGapLine)
        {
            _quietGaps++;
            _quietLost += missing;
            return;
        }

        _nextGapLine = now + GapLineInterval;
        string more = _quietGaps > 0
            ? $" ({_quietGaps} more gap(s), {_quietLost} samples, since the last line)"
            : "";
        string capped = marked < missing ? $", marked as {_options.MaxGapSeconds:F0} s" : "";
        journal($"iq: the source lost {missing} samples ({missing * 1000.0 / rate:F1} ms){capped}; "
            + $"marked as a gap in the audio, not spliced{more}");
        _quietGaps = 0;
        _quietLost = 0;
    }
}
