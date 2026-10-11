using M0LTE.Dsp;
using M0LTE.Ofdm;

namespace Packet.SoundModem.Iq;

/// <summary>
/// Digital down-converter: shifts one channel from a frequency offset (relative to the
/// IQ centre) down to complex baseband with an NCO mix, band-limits it with an anti-alias
/// low-pass, and decimates by an integer factor to the channel's DSP rate. For receive from an
/// IQ source use <see cref="IqReceiver"/>, which decimates before it filters and demodulates
/// SSB and FM; this class filters at the source rate.
/// </summary>
/// <remarks>
/// Textbook NCO + decimating FIR - no ported code. The FIR is designed at construction by the
/// existing windowed-sinc <see cref="FilterDesign.LowPass(double, double, int)"/>; the per-sample
/// path allocates nothing (pre-sized complex history, scalar MACs, an FIR evaluated only at output
/// instants - the polyphase-equivalent cost, mirroring <see cref="Decimator"/>). The NCO uses a
/// wrapped <see cref="double"/> phase accumulator with per-sample <see cref="MathF"/> trig rather
/// than a recursive phasor, so it neither drifts in frequency nor decays in amplitude over a long
/// stream.
/// </remarks>
public sealed class DigitalDownconverter
{
    private readonly float[] _coefficients;
    private readonly float[] _historyRe;
    private readonly float[] _historyIm;
    private readonly int _factor;
    private readonly double _phaseIncrement;
    private double _phase;
    private int _position;
    private int _sinceLastOutput;

    /// <summary>Creates a down-converter for one channel.</summary>
    /// <param name="inputRate">Complex sample rate of the IQ input, Hz.</param>
    /// <param name="offsetHz">Channel centre relative to the IQ centre, Hz (may be negative);
    /// |offset| must be below the input Nyquist (<paramref name="inputRate"/>/2).</param>
    /// <param name="decimation">Integer decimation factor; <paramref name="inputRate"/> must be
    /// an exact multiple of it (the same "rate is a multiple of the DSP rate" rule the ALSA path
    /// uses). 1 = shift only, no decimation.</param>
    /// <param name="taps">Anti-alias FIR length. More taps ⇒ sharper channel selectivity.</param>
    public DigitalDownconverter(int inputRate, double offsetHz, int decimation, int taps = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(decimation, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(taps, 3);
        if (inputRate % decimation != 0)
        {
            throw new ArgumentException(
                $"inputRate ({inputRate}) must be an exact multiple of decimation ({decimation})",
                nameof(decimation));
        }

        if (Math.Abs(offsetHz) >= inputRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offsetHz), offsetHz, "|offset| must be below the input Nyquist");
        }

        InputRate = inputRate;
        OutputRate = inputRate / decimation;
        OffsetHz = offsetHz;
        _factor = decimation;

        // Channel-select low-pass at the input rate, cutoff just under the output Nyquist so the
        // full decimated band is kept while adjacent channels alias-free.
        _coefficients = FilterDesign.LowPass(0.45 * OutputRate, inputRate, taps);
        _historyRe = new float[taps];
        _historyIm = new float[taps];

        // Mixing by e^{-jφ} brings +offset down to DC; φ advances by 2π·offset/inputRate.
        _phaseIncrement = 2.0 * Math.PI * offsetHz / inputRate;
    }

    /// <summary>Input complex sample rate, Hz.</summary>
    public int InputRate { get; }

    /// <summary>Output (baseband) complex sample rate, Hz.</summary>
    public int OutputRate { get; }

    /// <summary>Channel offset from the IQ centre, Hz.</summary>
    public double OffsetHz { get; }

    /// <summary>Upper bound on baseband samples produced for an interleaved input of
    /// <paramref name="interleavedLength"/> floats.</summary>
    public int MaxOutput(int interleavedLength) => ((interleavedLength / 2) / _factor) + 1;

    /// <summary>
    /// Down-converts a block of interleaved <c>I, Q</c> input into complex baseband
    /// <paramref name="output"/>; returns the number of baseband samples written.
    /// </summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<Cf> output)
    {
        int pairs = interleaved.Length / 2;
        int produced = 0;

        for (int k = 0; k < pairs; k++)
        {
            float re = interleaved[2 * k];
            float im = interleaved[(2 * k) + 1];

            // NCO mix: (re + j·im)·(cosφ − j·sinφ).
            float c = MathF.Cos((float)_phase);
            float s = MathF.Sin((float)_phase);
            float mixedRe = (re * c) + (im * s);
            float mixedIm = (im * c) - (re * s);

            _phase += _phaseIncrement;
            if (_phase > Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
            else if (_phase < -Math.PI)
            {
                _phase += 2.0 * Math.PI;
            }

            _historyRe[_position] = mixedRe;
            _historyIm[_position] = mixedIm;

            if (++_sinceLastOutput == _factor)
            {
                _sinceLastOutput = 0;
                float accRe = 0f;
                float accIm = 0f;
                int index = _position;
                foreach (float coefficient in _coefficients)
                {
                    accRe += coefficient * _historyRe[index];
                    accIm += coefficient * _historyIm[index];
                    if (--index < 0)
                    {
                        index = _coefficients.Length - 1;
                    }
                }

                output[produced++] = new Cf(accRe, accIm);
            }

            if (++_position == _coefficients.Length)
            {
                _position = 0;
            }
        }

        return produced;
    }
}
