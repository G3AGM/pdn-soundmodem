using System.Globalization;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Iq;

/// <summary>Settings for a <see cref="SoundCardIqSource"/>.</summary>
public sealed record SoundCardIqOptions
{
    /// <summary>The RF frequency the card's IQ is centred on, Hz, or 0 where it is not known.
    /// Moved later with <see cref="SoundCardIqSource.SetCentreFrequency"/>.</summary>
    public double CentreFrequencyHz { get; init; }

    /// <summary>Take I from the right channel and Q from the left, for a receiver wired or built
    /// the other way round. Wrong, the spectrum comes out mirrored: a USB signal is heard as LSB,
    /// and on the other side of the centre.</summary>
    public bool SwapIq { get; init; }

    /// <summary>How much louder Q is than I on this card, dB, which the source divides out. A
    /// sound card's two inputs are not matched, and any mismatch is an image of everything in the
    /// band on the far side of the centre.</summary>
    public double GainImbalanceDb { get; init; }

    /// <summary>How far Q is from quadrature with I on this card, degrees, which the source takes
    /// out: with <c>I = cos t</c> the card gives <c>Q = g sin(t + phi)</c>, so positive is Q less
    /// than 90 degrees behind I.</summary>
    public double PhaseImbalanceDegrees { get; init; }

    /// <summary>
    /// After how many seconds of IQ the source says what balance it measures on the card, or 0
    /// never. The figure is what to put in <see cref="GainImbalanceDb"/> and
    /// <see cref="PhaseImbalanceDegrees"/>.
    /// </summary>
    public double BalanceReportSeconds { get; init; } = 60;

    /// <summary>The most frames read from the card at a time.</summary>
    public int MaxReadFrames { get; init; } = 8192;

    /// <summary>Where the source's lines go (the balance it measures), or null.</summary>
    public Action<string>? Journal { get; init; }

    /// <summary>The clock a lost stretch is measured on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Why these settings cannot be used, or null.</summary>
    public string? Problem()
    {
        if (!double.IsFinite(CentreFrequencyHz) || CentreFrequencyHz < 0)
        {
            return "the centre frequency has to be 0 (unknown) or a frequency";
        }

        if (!double.IsFinite(GainImbalanceDb) || Math.Abs(GainImbalanceDb) > 6)
        {
            return "the gain imbalance has to be within 6 dB either way; a card further out than that is miswired";
        }

        if (!double.IsFinite(PhaseImbalanceDegrees) || Math.Abs(PhaseImbalanceDegrees) > 30)
        {
            return "the phase imbalance has to be within 30 degrees either way; a card further out than that is miswired";
        }

        if (!double.IsFinite(BalanceReportSeconds) || BalanceReportSeconds < 0)
        {
            return "the balance report time cannot be negative";
        }

        return MaxReadFrames < 1 ? "the read size has to be at least one frame" : null;
    }
}

/// <summary>
/// A sound card's left and right inputs as an <see cref="IIqSource"/>: the IQ output of a Xiegu
/// G90, a SoftRock, or anything else that presents baseband IQ as stereo audio. Receive only.
/// </summary>
/// <remarks>
/// <para><b>What it does and does not do.</b> It turns the card's 16-bit pairs into I and Q at full
/// scale 1, swaps them if told to, and corrects the card's gain and phase mismatch. Filtering,
/// tuning within the band and demodulation are the front end's (<see cref="IqReceiver"/>), so
/// this is a transport and nothing more. The swap is here rather than the front end's because the
/// balance correction has to know which channel is I.</para>
/// <para><b>Balance.</b> With Q at <c>g</c> times I's gain and <c>phi</c> off quadrature, the
/// card delivers <c>I = cos t</c>, <c>Q = g sin(t + phi)</c>, and
/// <c>Q' = (Q / g - I sin phi) / cos phi</c> recovers <c>sin t</c>. Uncorrected, an imbalance of
/// <c>g</c> and <c>phi</c> leaves every signal's image on the far side of the centre at
/// <c>(1 + 2 g cos phi + g^2) / (1 - 2 g cos phi + g^2)</c> below it, about 30 dB for 0.5 dB and
/// 3 degrees, which a strong signal there can put on top of the one being received. The source
/// also measures the card's balance (the second moments of I and Q, which for a band of noise and
/// unrelated signals give <c>g</c> and <c>phi</c> directly) and says it once, after
/// <see cref="SoundCardIqOptions.BalanceReportSeconds"/>, so an operator has a number to start
/// from; an image measured on a real tone is still the test that settles it.</para>
/// <para><b>Overruns are gaps.</b> The card's read stops at an overrun
/// (<see cref="IStereoCapture.Read"/>), so a block never spans one, and the first block after it
/// raises <see cref="SamplesLost"/>. ALSA does not say how much it dropped, so the figure is the
/// time the reader was away that the block it got back does not account for, and never less than
/// one, which is what the interface asks of a source that only knows roughly.</para>
/// <para><b>Retunes.</b> The card has no tuning of its own: whatever is in front of it does. When
/// that moves (a rig read over rigctld), <see cref="SetCentreFrequency"/> says so, and the next
/// block is the first at the new centre. The boundary is as good as the news is prompt, which for
/// a rig polled every few seconds is not very; the front end restarts its filters there either
/// way.</para>
/// <para><b>Cost.</b> One buffer, sized at construction; a read allocates nothing.</para>
/// <para>Original; no ported code.</para>
/// </remarks>
public sealed class SoundCardIqSource : IIqSource, IDisposable
{
    private const float Scale = 1f / 32768f;

    private readonly IStereoCapture _capture;
    private readonly SoundCardIqOptions _options;
    private readonly short[] _pcm;
    private readonly bool _correct;
    private readonly float _qScale;
    private readonly float _iIntoQ;
    private readonly long _reportAfter;
    private IqBalanceMeter _meter;
    private bool _reported;
    private long _lastReadEnd;
    private long _pendingCentreBits;
    private bool _lastReadShort;
    private volatile bool _closing;
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    /// <summary>How long <see cref="Dispose"/> waits for a running read: past the kernel's 10 s.</summary>
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(12);

    /// <summary>A source over <paramref name="capture"/>, which it owns from here on.</summary>
    /// <exception cref="ArgumentException">The options cannot be used; the message says why.</exception>
    public SoundCardIqSource(IStereoCapture capture, SoundCardIqOptions options)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Problem() is string problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        if (capture.SampleRate < 1)
        {
            throw new ArgumentException($"the card's rate ({capture.SampleRate}) is not a rate", nameof(capture));
        }

        _capture = capture;
        _options = options;
        _pcm = new short[2 * options.MaxReadFrames];
        SampleRate = capture.SampleRate;
        CentreFrequencyHz = options.CentreFrequencyHz;
        _pendingCentreBits = BitConverter.DoubleToInt64Bits(options.CentreFrequencyHz);

        // Q' = Q / (g cos phi) - I tan phi.
        double g = Math.Pow(10, options.GainImbalanceDb / 20);
        double phi = options.PhaseImbalanceDegrees * Math.PI / 180;
        _correct = options.GainImbalanceDb != 0 || options.PhaseImbalanceDegrees != 0;
        _qScale = (float)(1 / (g * Math.Cos(phi)));
        _iIntoQ = (float)Math.Tan(phi);
        _reportAfter = options.BalanceReportSeconds > 0 && options.Journal is not null
            ? (long)(options.BalanceReportSeconds * SampleRate)
            : 0;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public double CentreFrequencyHz { get; private set; }

    /// <inheritdoc />
    public long TuningGeneration { get; private set; }

    /// <inheritdoc />
    public long SamplesLost { get; private set; }

    /// <summary>The card's overruns so far.</summary>
    public long Overruns => _capture.Overruns;

    /// <summary>The balance measured on the card so far, before any correction: null until there
    /// is a second's worth, and always null when there is no journal to report it to (or
    /// <see cref="SoundCardIqOptions.BalanceReportSeconds"/> is 0), since it is not measured then.
    /// Read on the reader's thread.</summary>
    public IqBalance? MeasuredBalance => _meter.Frames >= SampleRate ? _meter.Balance() : null;

    /// <summary>
    /// Says the card's IQ is now centred on <paramref name="centreHz"/>: the next block is the first
    /// at it, and the front end retunes there. Any thread; a centre that has not moved is no
    /// retune.
    /// </summary>
    public void SetCentreFrequency(double centreHz)
    {
        if (!double.IsFinite(centreHz) || centreHz < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(centreHz), centreHz, "not a frequency");
        }

        Interlocked.Exchange(ref _pendingCentreBits, BitConverter.DoubleToInt64Bits(centreHz));
    }

    /// <inheritdoc />
    public int Read(Span<float> interleaved)
    {
        int frames = Math.Min(interleaved.Length / 2, _options.MaxReadFrames);
        if (frames < 1)
        {
            throw new ArgumentException("room for at least one I, Q pair is needed", nameof(interleaved));
        }

        _idle.Reset();
        try
        {
            if (_closing)
            {
                return 0;
            }

            long overruns = _capture.Overruns;
            int got = _capture.Read(_pcm.AsSpan(0, 2 * frames));
            long now = _options.TimeProvider.GetTimestamp();
            if (got <= 0)
            {
                return 0;
            }

            if (_capture.Overruns != overruns)
            {
                // How long the reader was away that this block does not account for, and, when
                // the block before was cut short by the hole, what the card was holding when it
                // overran, which the recovery threw away and the time away does not see.
                double away = _lastReadEnd == 0
                    ? 0
                    : _options.TimeProvider.GetElapsedTime(_lastReadEnd, now).TotalSeconds - ((double)got / SampleRate);
                long held = _lastReadShort ? _capture.BufferFrames : 0;
                SamplesLost += Math.Max(1, (long)Math.Round(away * SampleRate) + held);
            }

            _lastReadShort = got < frames;
            _lastReadEnd = now;

            // A centre moved meanwhile is the tuning of this block: the news is no more precise.
            double pending = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _pendingCentreBits));
            if (pending != CentreFrequencyHz)
            {
                CentreFrequencyHz = pending;
                TuningGeneration++;
            }

            Convert(_pcm.AsSpan(0, 2 * got), interleaved);
            return 2 * got;
        }
        finally
        {
            _idle.Set();
        }
    }

    /// <summary>
    /// Closes the card, once no read is running on it: closing an ALSA capture under a
    /// <c>snd_pcm_readi</c> on another thread is not something alsa-lib supports. A read that is
    /// still running after <see cref="CloseWait"/> (a card that stopped clocking, which the kernel
    /// gives up on after 10 s) leaves the card open rather than risk it; the process is on its way
    /// out by then.
    /// </summary>
    public void Dispose()
    {
        _closing = true;
        if (_idle.Wait(CloseWait))
        {
            _capture.Dispose();
        }
    }

    private void Convert(ReadOnlySpan<short> pcm, Span<float> iq)
    {
        bool swap = _options.SwapIq;
        bool measure = _reportAfter > 0 && !_reported;
        for (int k = 0; k < pcm.Length; k += 2)
        {
            float left = pcm[k] * Scale;
            float right = pcm[k + 1] * Scale;
            float i = swap ? right : left;
            float q = swap ? left : right;
            if (measure)
            {
                _meter.Add(i, q);
            }

            if (_correct)
            {
                q = (q * _qScale) - (i * _iIntoQ);
            }

            iq[k] = i;
            iq[k + 1] = q;
        }

        if (measure && _meter.Frames >= _reportAfter)
        {
            _reported = true;
            _options.Journal!(BalanceLine(_meter.Balance(), _options));
        }
    }

    /// <summary>The line said once the balance has been measured.</summary>
    internal static string BalanceLine(IqBalance card, SoundCardIqOptions options)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string line = string.Format(
            c,
            "iq: the card's balance over {0:F0} s: Q is {1:+0.00;-0.00;0.00} dB against I and "
            + "{2:+0.00;-0.00;0.00} degrees off quadrature, an image {3} down uncorrected",
            options.BalanceReportSeconds, card.GainDb, card.PhaseDegrees, Rejection(card.ImageRejectionDb));
        if (options.GainImbalanceDb != 0 || options.PhaseImbalanceDegrees != 0)
        {
            IqBalance residual = card.After(options.GainImbalanceDb, options.PhaseImbalanceDegrees);
            line += string.Format(
                c,
                "; {0} with the configured {1:+0.00;-0.00;0.00} dB and {2:+0.00;-0.00;0.00} degrees",
                Rejection(residual.ImageRejectionDb), options.GainImbalanceDb, options.PhaseImbalanceDegrees);
        }

        return line + ". Measured on whatever the band holds, so a guide; a tone's image is the test.";
    }

    private static string Rejection(double db) =>
        db >= 60 ? "more than 60 dB" : string.Create(CultureInfo.InvariantCulture, $"{db:F0} dB");
}

/// <summary>A card's IQ balance: Q's gain against I's, and its departure from quadrature.</summary>
/// <param name="GainDb">How much louder Q is than I, dB.</param>
/// <param name="PhaseDegrees">How far Q is from quadrature with I, degrees: <c>phi</c> in
/// <c>Q = g sin(t + phi)</c> for <c>I = cos t</c>.</param>
public sealed record IqBalance(double GainDb, double PhaseDegrees)
{
    /// <summary>How far below a signal its image on the far side of the centre lies, dB.</summary>
    public double ImageRejectionDb
    {
        get
        {
            double g = Math.Pow(10, GainDb / 20);
            double cross = 2 * g * Math.Cos(PhaseDegrees * Math.PI / 180);
            double image = 1 - cross + (g * g);
            return image <= 1e-12 ? 120 : 10 * Math.Log10((1 + cross + (g * g)) / image);
        }
    }

    /// <summary>What is left of this balance once a correction for <paramref name="gainDb"/> and
    /// <paramref name="phaseDegrees"/> is applied.</summary>
    public IqBalance After(double gainDb, double phaseDegrees)
    {
        // Second moments of the uncorrected card, unit I: E[I^2] = 1, E[Q^2] = g^2,
        // E[IQ] = g sin phi; and of Q' = a Q - b I.
        double g = Math.Pow(10, GainDb / 20);
        double phi = PhaseDegrees * Math.PI / 180;
        double cg = Math.Pow(10, gainDb / 20);
        double cphi = phaseDegrees * Math.PI / 180;
        double a = 1 / (cg * Math.Cos(cphi));
        double b = Math.Tan(cphi);
        double iq = g * Math.Sin(phi);
        double qq = (a * a * g * g) - (2 * a * b * iq) + (b * b);
        double iqc = (a * iq) - b;
        return IqBalanceMeter.FromMoments(1, qq, iqc);
    }
}

/// <summary>Accumulates the second moments a card's balance is read from.</summary>
internal struct IqBalanceMeter
{
    private double _i;
    private double _q;
    private double _ii;
    private double _qq;
    private double _iq;

    /// <summary>Frames added so far.</summary>
    public long Frames { get; private set; }

    /// <summary>Adds one I, Q pair.</summary>
    public void Add(float i, float q)
    {
        _i += i;
        _q += q;
        _ii += (double)i * i;
        _qq += (double)q * q;
        _iq += (double)i * q;
        Frames++;
    }

    /// <summary>The balance of what has been added, with each channel's DC taken out first.</summary>
    public readonly IqBalance Balance()
    {
        double n = Math.Max(1, Frames);
        double mi = _i / n;
        double mq = _q / n;
        return FromMoments((_ii / n) - (mi * mi), (_qq / n) - (mq * mq), (_iq / n) - (mi * mq));
    }

    /// <summary>The balance from E[I^2], E[Q^2] and E[IQ]: with I = cos t and Q = g sin(t + phi),
    /// those are 1/2, g^2/2 and (g/2) sin phi.</summary>
    internal static IqBalance FromMoments(double ii, double qq, double iq)
    {
        if (ii <= 0 || qq <= 0)
        {
            return new IqBalance(0, 0);
        }

        double g = Math.Sqrt(qq / ii);
        double sin = Math.Clamp(iq / Math.Sqrt(ii * qq), -1, 1);
        return new IqBalance(20 * Math.Log10(g), Math.Asin(sin) * 180 / Math.PI);
    }
}
