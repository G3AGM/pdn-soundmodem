using M0LTE.Radio.Audio;
using M0LTE.Ofdm;

namespace Packet.SoundModem.Iq;

/// <summary>
/// Fans one wideband <see cref="IIqSource"/> out into several independent narrowband channels,
/// each surfaced as an <c>IAudioInput</c> so an existing demodulator (or a whole
/// <c>SoundModemChannel</c>) attaches unchanged - the "decode N packet channels at once from one
/// slice" capability the DAX-IQ RX path unlocks (docs/dev/archive/flex-integration.md §9.1/§9.6).
/// </summary>
/// <remarks>
/// Pull the source and push to the channels with <see cref="Pump"/> (or <see cref="PumpToEnd"/>),
/// then let each consumer drain its channel via <c>IAudioInput.Read</c>. Each channel realises
/// real audio as the real part of its baseband, optionally re-centred to
/// <see cref="ChannelSpec.AudioCentreHz"/> (0 = baseband real part, which reproduces the audio a
/// receiver tuned to the channel centre would deliver). The per-sample DDC path is
/// allocation-free; <see cref="Pump"/> uses pre-sized scratch and per-channel ring buffers.
/// </remarks>
public sealed class MultiChannelReceiver
{
    private readonly IIqSource _source;
    private readonly RxChannel[] _channels;
    private readonly float[] _readBuffer;
    private readonly Cf[] _basebandScratch;

    /// <summary>Creates a receiver over <paramref name="source"/> with one channel per spec.</summary>
    /// <param name="source">The wideband IQ source.</param>
    /// <param name="specs">Channel definitions (offset, decimation, …).</param>
    /// <param name="readBlockSamples">Complex samples pulled from the source per <see cref="Pump"/>.</param>
    public MultiChannelReceiver(
        IIqSource source, IReadOnlyList<ChannelSpec> specs, int readBlockSamples = 4096)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentOutOfRangeException.ThrowIfLessThan(specs.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(readBlockSamples, 1);

        _source = source;
        _readBuffer = new float[readBlockSamples * 2];
        _basebandScratch = new Cf[readBlockSamples + 1];

        var channels = new RxChannel[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            channels[i] = new RxChannel(source.SampleRate, specs[i]);
        }

        _channels = channels;
    }

    /// <summary>The channels, in spec order, each an <c>IAudioInput</c> at its own DSP rate.</summary>
    public IReadOnlyList<IAudioInput> Channels => _channels;

    /// <summary>Pulls one block from the source and down-converts it into every channel's buffer.
    /// Returns false at end of stream.</summary>
    public bool Pump()
    {
        int got = _source.Read(_readBuffer);
        if (got <= 0)
        {
            return false;
        }

        ReadOnlySpan<float> iq = _readBuffer.AsSpan(0, got);
        foreach (RxChannel channel in _channels)
        {
            channel.Ingest(iq, _basebandScratch);
        }

        return true;
    }

    /// <summary>Pumps the source to exhaustion (offline convenience).</summary>
    public void PumpToEnd()
    {
        while (Pump())
        {
        }
    }

    /// <summary>One channel's tuning.</summary>
    /// <param name="OffsetHz">Channel centre relative to the IQ centre, Hz (may be negative).</param>
    /// <param name="Decimation">Integer IQ-rate ÷ DSP-rate factor.</param>
    /// <param name="Taps">Channel-select FIR length.</param>
    /// <param name="AudioCentreHz">Where to place the channel centre in the output audio, Hz
    /// (0 = baseband real part).</param>
    public readonly record struct ChannelSpec(
        double OffsetHz, int Decimation, int Taps = 128, double AudioCentreHz = 0);

    private sealed class RxChannel : IAudioInput
    {
        private readonly DigitalDownconverter _ddc;
        private readonly float[] _ring;
        private readonly double _audioPhaseIncrement;
        private double _audioPhase;
        private int _head;
        private int _tail;
        private int _count;

        public RxChannel(int inputRate, ChannelSpec spec)
        {
            _ddc = new DigitalDownconverter(inputRate, spec.OffsetHz, spec.Decimation, spec.Taps);
            _audioPhaseIncrement = 2.0 * Math.PI * spec.AudioCentreHz / _ddc.OutputRate;
            // Generous ring so a keeping-up consumer never overflows in steady state.
            _ring = new float[1 << 16];
        }

        public int SampleRate => _ddc.OutputRate;

        public void Ingest(ReadOnlySpan<float> interleavedIq, Cf[] scratch)
        {
            int n = _ddc.Process(interleavedIq, scratch);
            for (int i = 0; i < n; i++)
            {
                Cf baseband = scratch[i];
                float sample;
                if (_audioPhaseIncrement == 0.0)
                {
                    sample = baseband.Re;
                }
                else
                {
                    // Re-centre: real part of baseband·e^{+jθ}.
                    float c = MathF.Cos((float)_audioPhase);
                    float s = MathF.Sin((float)_audioPhase);
                    sample = (baseband.Re * c) - (baseband.Im * s);
                    _audioPhase += _audioPhaseIncrement;
                    if (_audioPhase > Math.PI)
                    {
                        _audioPhase -= 2.0 * Math.PI;
                    }
                }

                Push(sample);
            }
        }

        public int Read(Span<float> destination)
        {
            int take = Math.Min(destination.Length, _count);
            for (int i = 0; i < take; i++)
            {
                destination[i] = _ring[_tail];
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }
            }

            _count -= take;
            return take;
        }

        private void Push(float sample)
        {
            if (_count == _ring.Length)
            {
                // Overflow: drop the oldest sample to stay bounded (a lagging consumer).
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }

                _count--;
            }

            _ring[_head] = sample;
            if (++_head == _ring.Length)
            {
                _head = 0;
            }

            _count++;
        }
    }
}
