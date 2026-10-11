using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Iq;

/// <summary>
/// One channel of an <see cref="IqReceiver"/>: demodulated audio at the station's DSP rate, read
/// as any sound card is, and a tuner that moves the channel within the source's band.
/// </summary>
/// <remarks>
/// <para><b>Reading.</b> <see cref="Read"/> waits up to 100 ms for audio and returns 0 if none
/// came, which is what the station's receive loop expects of a device with nothing to say. Once
/// the source has closed it returns what is left and then 0 at once.</para>
/// <para><b>Tuning.</b> <see cref="TuneAsync"/> moves the channel's dial without touching the
/// source: it is a new offset for the NCO, taken up at the next block. It answers false when the
/// band the coarse stage keeps does not reach the dial, or the source does not say where it is
/// tuned; the dial is remembered either way and heard as soon as a retune of the source brings it
/// in. Moving the source itself is the backend's business, behind its own device.</para>
/// <para><b>Behind.</b> A consumer that stops draining loses the oldest audio rather than growing
/// the buffer; the loss is counted in <see cref="DroppedSamples"/> and
/// <see cref="Discontinuities"/>, and said in the journal once a second of it has gone.</para>
/// </remarks>
public sealed class IqReceiverChannel : IAudioInput, IReceiverTuner
{
    private static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(100);

    private readonly IqChannelSpec _spec;
    private readonly int _dspRate;
    private readonly int _channelRate;
    private readonly double _passbandHz;
    private readonly double _stopbandHz;
    private readonly Action<string>? _journal;
    private readonly string _label;

    // The ring the reader thread fills and the station drains.
    private readonly object _gate = new();
    private readonly float[] _ring;
    private int _head;
    private int _tail;
    private int _count;
    private bool _ended;
    private long _dropped;
    private long _droppedReported;
    private long _discontinuities;

    // Tuning, shared between the reader thread and TuneAsync's caller.
    private readonly object _tuneGate = new();
    private double? _dialHz;          // the RF dial, when the channel is anchored to one
    private double? _requestedDialHz; // a TuneAsync not yet taken up by the reader thread
    private double _offsetHz;
    private double _centreHz;
    private double _protectedHz;

    // The chain. Built for one coarse rate; rebuilt only when that moves.
    private int _coarseRate;
    private ComplexRationalResampler? _resampler;
    private StreamingSsbDemodulator? _ssb;
    private StreamingFmDiscriminator? _fm;
    private RationalResampler? _audioResampler;
    private double _phase;
    private double _phaseIncrement;
    private float[] _mixed = [];
    private float[] _channel = [];
    private double[] _i = [];
    private double[] _q = [];
    private float[] _fmAudio = [];
    private float[] _audio = [];
    private float[] _flush = [];

    // Silence owed for a muted stretch or a gap, carried as a remainder so the count is exact
    // over any length of stream.
    private long _muteRemainder;
    private long _gapRemainder;

    internal IqReceiverChannel(int index, IqChannelSpec spec, IqReceiverOptions options, double centreHz)
    {
        Index = index;
        _spec = spec;
        _dspRate = options.DspRate;
        _channelRate = spec.ChannelRateFor(options.DspRate);
        _passbandHz = spec.PassbandHzFor(_channelRate);
        _stopbandHz = spec.StopbandHzFor(_channelRate);
        _journal = options.Journal;
        _label = spec.Name is null ? $"iq ch{index}" : $"iq ch{index} ({spec.Name})";
        _offsetHz = spec.OffsetHz;
        _centreHz = centreHz;
        if (centreHz != 0)
        {
            _dialHz = centreHz + spec.OffsetHz;
        }

        _ring = new float[Math.Max(1, (int)(options.RingSeconds * options.DspRate))];
    }

    /// <summary>The channel's position in the receiver, from 0.</summary>
    public int Index { get; }

    /// <summary>What the channel was built from.</summary>
    public IqChannelSpec Spec => _spec;

    /// <inheritdoc />
    public int SampleRate => _dspRate;

    /// <summary>The complex rate the channel is filtered to and demodulated at, Hz.</summary>
    public int ChannelRate => _channelRate;

    /// <summary>The channel filter's passband edge, Hz either side of the dial.</summary>
    public double PassbandHz => _passbandHz;

    /// <summary>The channel filter's stopband edge, Hz either side of the dial.</summary>
    public double StopbandHz => _stopbandHz;

    /// <summary>The channel's dial relative to the source's centre, Hz, as last placed.</summary>
    public double OffsetHz
    {
        get
        {
            lock (_tuneGate)
            {
                return _offsetHz;
            }
        }
    }

    /// <summary>
    /// The dial: an RF frequency when the source says where it is tuned, else the offset from its
    /// centre. A tune not yet taken up reads as the dial it asked for.
    /// </summary>
    public double DialHz
    {
        get
        {
            lock (_tuneGate)
            {
                return _requestedDialHz ?? _dialHz ?? (_centreHz + _offsetHz);
            }
        }
    }

    /// <summary>Whether the channel is silent because the source's band does not reach its dial.</summary>
    public bool Muted { get; private set; }

    /// <summary>Why the channel is muted, or null when it is not.</summary>
    public string? MutedReason { get; private set; }

    /// <summary>Samples dropped because nothing drained the buffer in time.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>
    /// A running count of the places the audio is not continuous: a retune, a gap the source
    /// reported, a tune of this channel, a stretch dropped from a full buffer. What the station's
    /// input-loss counter reads, so the channel audio stream marks each one.
    /// </summary>
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    /// <summary>Samples buffered and ready to read.</summary>
    public int Available
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Whether the source has closed and every sample has been read.</summary>
    public bool Completed
    {
        get
        {
            lock (_gate)
            {
                return _ended && _count == 0;
            }
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            while (_count == 0)
            {
                if (_ended || !Monitor.Wait(_gate, ReadWait))
                {
                    return 0;
                }
            }

            int take = Math.Min(destination.Length, _count);
            int first = Math.Min(take, _ring.Length - _tail);
            _ring.AsSpan(_tail, first).CopyTo(destination);
            _ring.AsSpan(0, take - first).CopyTo(destination[first..]);
            _tail = (_tail + take) % _ring.Length;
            _count -= take;
            return take;
        }
    }

    /// <inheritdoc />
    /// <remarks>Moves the channel within the band the receiver keeps; the source is not retuned.
    /// Taken up at the next block.</remarks>
    public Task<bool> TuneAsync(double dialHz, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!double.IsFinite(dialHz))
        {
            throw new ArgumentOutOfRangeException(nameof(dialHz), dialHz, "must be a frequency");
        }

        lock (_tuneGate)
        {
            _requestedDialHz = dialHz;
            return Task.FromResult(_centreHz != 0 && Reaches(dialHz - _centreHz, _protectedHz));
        }
    }

    /// <summary>Places the channel against the coarse stage and the source's centre, and restarts
    /// its filters. On the reader thread.</summary>
    internal void Place(CoarseDecimator coarse, double centreHz, bool rebuild)
    {
        lock (_tuneGate)
        {
            if (_requestedDialHz is double requested)
            {
                _dialHz = requested;
                _requestedDialHz = null;
            }

            _centreHz = centreHz;
            _protectedHz = coarse.ProtectedHz;
            if (_dialHz is double dial && centreHz != 0)
            {
                _offsetHz = dial - centreHz;
            }

            if (_dialHz is not null && centreHz == 0)
            {
                Mute("the source no longer says what frequency it is tuned to, so an RF dial cannot be placed");
            }
            else if (!Reaches(_offsetHz, _protectedHz))
            {
                Mute($"its dial is {_offsetHz:F0} Hz from the source's centre, outside the "
                    + $"{_protectedHz:F0} Hz either side the receiver keeps");
            }
            else
            {
                if (Muted)
                {
                    _journal?.Invoke($"{_label}: back in the source's band at {_offsetHz:F0} Hz from its centre");
                }

                Muted = false;
                MutedReason = null;
            }
        }

        if (rebuild || _resampler is null || coarse.OutputRate != _coarseRate)
        {
            Build(coarse);
        }

        _phaseIncrement = 2.0 * Math.PI * _offsetHz / _coarseRate;
        Restart();
    }

    /// <summary>Restarts every filter and demodulator, for a discontinuity in the input.</summary>
    internal void Restart()
    {
        _resampler?.Reset();
        _fm?.Reset();
        _audioResampler?.Reset();
        if (_spec.Demodulation != IqDemodulation.Fm)
        {
            // No Reset on the SSB demodulator; a fresh one is the same thing. Once per
            // discontinuity, not per block.
            FlushSsb();
            _ssb = NewSsb();
        }

        _muteRemainder = 0;
    }

    /// <summary>
    /// Takes the SSB demodulator's last outputs before it is replaced. It holds back the outputs
    /// whose window reaches past the newest input, and a fresh one holds back as many again, so
    /// dropping the old one without this would take that much time out of the audio at every
    /// restart. Flushed, they are heard against silence, which is what the gap is.
    /// </summary>
    private void FlushSsb()
    {
        if (_ssb is null)
        {
            return;
        }

        if (_flush.Length < _ssb.MaxFlushOutput)
        {
            _flush = new float[_ssb.MaxFlushOutput];
        }

        int produced = _ssb.Flush(_flush);
        Span<float> tail = _flush.AsSpan(0, produced);
        if (_spec.Gain != 1f)
        {
            for (int k = 0; k < tail.Length; k++)
            {
                tail[k] *= _spec.Gain;
            }
        }

        Publish(tail);
        _ssb = null;
    }

    /// <summary>Counts a discontinuity the receiver saw.</summary>
    internal void NoteDiscontinuity() => Interlocked.Increment(ref _discontinuities);

    /// <summary>Demodulates one coarse block into the buffer. On the reader thread.</summary>
    internal void Ingest(ReadOnlySpan<float> coarseIq, CoarseDecimator coarse, double centreHz)
    {
        bool tune;
        lock (_tuneGate)
        {
            tune = _requestedDialHz is not null;
        }

        if (tune)
        {
            Place(coarse, centreHz, rebuild: false);
            NoteDiscontinuity();
            _journal?.Invoke(Muted
                ? $"{_label}: tuned to {DialHz:F0} Hz, which the source's band does not reach; silent until it does"
                : $"{_label}: tuned to {DialHz:F0} Hz, {_offsetHz:F0} Hz from the source's centre");
        }

        int frames = coarseIq.Length / 2;
        if (Muted)
        {
            long owed = (frames * (long)_dspRate) + _muteRemainder;
            _muteRemainder = owed % _coarseRate;
            PublishSilence(owed / _coarseRate);
            return;
        }

        // NCO: e^{-j phase} brings the dial to 0 Hz. A wrapped double phase with per-sample trig,
        // as DigitalDownconverter does, so it neither drifts nor decays over a long stream; at
        // the coarse rate the trig is cheap.
        Span<float> mixed = _mixed.AsSpan(0, 2 * frames);
        double phase = _phase;
        double step = _phaseIncrement;
        for (int k = 0; k < frames; k++)
        {
            float re = coarseIq[2 * k];
            float im = coarseIq[(2 * k) + 1];
            float c = MathF.Cos((float)phase);
            float s = MathF.Sin((float)phase);
            mixed[2 * k] = (re * c) + (im * s);
            mixed[(2 * k) + 1] = (im * c) - (re * s);
            phase += step;
            if (phase > Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }
            else if (phase < -Math.PI)
            {
                phase += 2.0 * Math.PI;
            }
        }

        _phase = phase;

        int channelFrames = _resampler!.Process(mixed, _channel);
        ReadOnlySpan<float> channel = _channel.AsSpan(0, 2 * channelFrames);
        int produced;
        if (_spec.Demodulation == IqDemodulation.Fm)
        {
            int discriminated = _fm!.Process(channel, _fmAudio);
            if (_audioResampler is null)
            {
                _fmAudio.AsSpan(0, discriminated).CopyTo(_audio);
                produced = discriminated;
            }
            else
            {
                produced = _audioResampler.Process(_fmAudio.AsSpan(0, discriminated), _audio);
            }
        }
        else
        {
            for (int k = 0; k < channelFrames; k++)
            {
                _i[k] = channel[2 * k];
                _q[k] = channel[(2 * k) + 1];
            }

            produced = _ssb!.Process(_i.AsSpan(0, channelFrames), _q.AsSpan(0, channelFrames), _audio);
        }

        float gain = _spec.Gain;
        if (gain != 1f)
        {
            Span<float> audio = _audio.AsSpan(0, produced);
            for (int k = 0; k < audio.Length; k++)
            {
                audio[k] *= gain;
            }
        }

        Publish(_audio.AsSpan(0, produced));
    }

    /// <summary>Puts <paramref name="sourceFrames"/> samples' worth of silence, at the source rate
    /// <paramref name="sourceRate"/>, into the audio: the time a gap took.</summary>
    internal void AppendSilence(long sourceFrames, int sourceRate)
    {
        long owed = (sourceFrames * _dspRate) + _gapRemainder;
        _gapRemainder = owed % sourceRate;
        PublishSilence(owed / sourceRate);
    }

    /// <summary>The source has closed: hand on the audio the demodulator still holds, then end.
    /// On the reader thread.</summary>
    internal void Finish()
    {
        if (_spec.Demodulation != IqDemodulation.Fm && !Muted)
        {
            FlushSsb();
            _ssb = NewSsb();
        }

        End();
    }

    /// <summary>No more audio is coming: drain what is left, then return 0.</summary>
    internal void End()
    {
        lock (_gate)
        {
            _ended = true;
            Monitor.PulseAll(_gate);
        }
    }

    private bool Reaches(double offsetHz, double protectedHz) =>
        Math.Abs(offsetHz) + _stopbandHz <= protectedHz;

    private void Mute(string reason)
    {
        if (!Muted || MutedReason != reason)
        {
            _journal?.Invoke($"{_label}: silent: {reason}");
        }

        Muted = true;
        MutedReason = reason;
    }

    private void Build(CoarseDecimator coarse)
    {
        // The old chain's held-back SSB outputs belong to the old tuning's audio.
        FlushSsb();
        _coarseRate = coarse.OutputRate;
        _resampler = new ComplexRationalResampler(new RationalResamplerOptions
        {
            InputRate = _coarseRate,
            OutputRate = _channelRate,
            PassbandHz = _passbandHz,
            StopbandHz = _stopbandHz,
            StopbandAttenuationDb = _spec.ChannelStopbandAttenuationDb,
        });

        int coarseFrames = coarse.MaxOutput(coarse.MaxBlockFrames) + 1;
        int channelFrames = _resampler.MaxOutput(coarseFrames) + 1;
        _mixed = new float[2 * coarseFrames];
        _channel = new float[2 * channelFrames];

        if (_spec.Demodulation == IqDemodulation.Fm)
        {
            _fm = new StreamingFmDiscriminator(_channelRate, _spec.FmDeviationHz);
            _fmAudio = new float[channelFrames];
            _audioResampler = _channelRate == _dspRate
                ? null
                : new RationalResampler(new RationalResamplerOptions
                {
                    InputRate = _channelRate,
                    OutputRate = _dspRate,
                    StopbandAttenuationDb = _spec.ChannelStopbandAttenuationDb,
                });
            _audio = new float[(_audioResampler?.MaxOutput(channelFrames) ?? channelFrames) + 1];
        }
        else
        {
            _ssb = NewSsb();
            _i = new double[channelFrames];
            _q = new double[channelFrames];
            _audio = new float[_ssb.MaxOutputFor(channelFrames) + 1];
            _flush = new float[_ssb.MaxFlushOutput];
        }
    }

    private StreamingSsbDemodulator NewSsb() => new(new SsbDemodulatorOptions
    {
        InputRate = _channelRate,
        OutputRate = _dspRate,
        DialHz = 0, // the NCO has already put the dial at 0 Hz
        Sideband = _spec.Demodulation == IqDemodulation.Lsb ? Sideband.Lower : Sideband.Upper,
        SsbLowHz = _spec.SsbLowHz,
        SsbHighHz = _spec.SsbHighHz,
    });

    private void PublishSilence(long samples)
    {
        if (samples <= 0)
        {
            return;
        }

        Span<float> zeros = stackalloc float[256];
        zeros.Clear();
        while (samples > 0)
        {
            int n = (int)Math.Min(samples, zeros.Length);
            Publish(zeros[..n]);
            samples -= n;
        }
    }

    private void Publish(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        long dropped = 0;
        lock (_gate)
        {
            int length = _ring.Length;
            if (samples.Length >= length)
            {
                dropped += _count + (samples.Length - length);
                samples = samples[^length..];
                _count = 0;
                _head = 0;
                _tail = 0;
            }

            int over = _count + samples.Length - length;
            if (over > 0)
            {
                // Bounded rather than unbounded: a consumer that has stopped draining is a fault
                // to report, not a reason to grow until the process dies.
                _tail = (_tail + over) % length;
                _count -= over;
                dropped += over;
            }

            int first = Math.Min(samples.Length, length - _head);
            samples[..first].CopyTo(_ring.AsSpan(_head, first));
            samples[first..].CopyTo(_ring.AsSpan(0, samples.Length - first));
            _head = (_head + samples.Length) % length;
            _count += samples.Length;
            Monitor.PulseAll(_gate);
        }

        if (dropped == 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _dropped, dropped);
        Interlocked.Increment(ref _discontinuities);
        if (total - Interlocked.Read(ref _droppedReported) >= _dspRate)
        {
            Interlocked.Exchange(ref _droppedReported, total);
            _journal?.Invoke(
                $"{_label}: the station is not keeping up; {total} samples ({total / (double)_dspRate:F1} s) "
                + "of audio dropped so far");
        }
    }
}
