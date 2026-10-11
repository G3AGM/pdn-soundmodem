using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Iq;

/// <summary>How a channel of an <see cref="IqReceiver"/> turns its baseband into audio.</summary>
public enum IqDemodulation
{
    /// <summary>Upper sideband: the audio is what lies above the channel's dial.</summary>
    Usb,

    /// <summary>Lower sideband: the audio is what lies below the channel's dial, mirrored.</summary>
    Lsb,

    /// <summary>Frequency modulation: a flat discriminator, the carrier at the channel's dial.</summary>
    Fm,
}

/// <summary>
/// One channel of an <see cref="IqReceiver"/>: where it sits in the source's band, how it is
/// demodulated, and the filter it is heard through.
/// </summary>
/// <remarks>
/// <para><b>Where.</b> <see cref="OffsetHz"/> is the channel's dial relative to the source's
/// centre: the suppressed carrier for SSB, the carrier for FM. When the source says what RF
/// frequency its centre is (<c>IIqSource.CentreFrequencyHz</c> is not 0) the channel is anchored
/// to that RF dial, so a retune of the source moves the offset rather than the dial; when it does
/// not, the offset is all there is.</para>
/// <para><b>Off the DC spike.</b> Most direct-conversion SDRs and IQ sound cards have a spur at
/// 0 Hz. A channel is kept off it by tuning the source a little away from the dial and giving the
/// channel that offset: see <see cref="IqTuning.DcClearOffsetHz"/>.</para>
/// <para><b>The filter is ours.</b> The channel filter is the front end's rational resampler: flat
/// to its passband, down by <see cref="ChannelStopbandAttenuationDb"/> from its stopband. For SSB
/// it is wide (80 % of the channel's Nyquist) and the sideband filter does the selecting; for FM
/// it is the IF filter, centred on the carrier, with its -6 dB points near
/// <see cref="FmIfBandwidthHz"/>.</para>
/// </remarks>
public sealed record IqChannelSpec
{
    /// <summary>The rate an FM channel is discriminated at unless told otherwise: room for a
    /// wide (25 kHz) channel's 16 kHz IF and the discriminator's noise beyond it.</summary>
    public const int DefaultFmChannelRate = 48000;

    /// <summary>The channel's dial relative to the source's centre, Hz (may be negative).</summary>
    public required double OffsetHz { get; init; }

    /// <summary>How the channel is demodulated.</summary>
    public required IqDemodulation Demodulation { get; init; }

    /// <summary>A short name for the journal, or null for the channel's number.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The complex rate the channel is filtered to and demodulated at, Hz, or null for the
    /// default: the DSP rate for SSB, and for FM the smallest multiple of the DSP rate at or above
    /// <see cref="DefaultFmChannelRate"/>. Must be a multiple of the DSP rate.
    /// </summary>
    public int? ChannelRate { get; init; }

    /// <summary>SSB: the low edge of the sideband filter, Hz from the dial. The default is the
    /// UberSDR device's.</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>SSB: the high edge of the sideband filter, Hz from the dial.</summary>
    public double SsbHighHz { get; init; } = 3450;

    /// <summary>FM: the IF bandwidth, Hz, whole width about the carrier. 8 kHz suits a 12.5 kHz
    /// channel and 16 kHz a 25 kHz one, the figures the FM link model uses.</summary>
    public double FmIfBandwidthHz { get; init; } = 16000;

    /// <summary>FM: the peak deviation that comes out as plus and minus 0.5, Hz. Fixed, so the
    /// level does not move with the signal.</summary>
    public double FmDeviationHz { get; init; } = 3000;

    /// <summary>A fixed gain on the audio. There is no AGC: FM needs none, and SSB is heard at the
    /// level it arrives at, as on the UberSDR device.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>The channel filter's stopband attenuation, dB.</summary>
    public double ChannelStopbandAttenuationDb { get; init; } = 70;

    /// <summary>
    /// The FM channel for a catalogue mode: its recommended deviation (or its target where there
    /// is no recommendation), and an IF as wide as the mode's published occupied bandwidth (10 kHz
    /// for afsk1200, 20 kHz for fsk9600), or the IF of its channel spacing where it has none.
    /// </summary>
    /// <param name="mode">The mode, e.g. <c>fsk9600-il2p</c>.</param>
    /// <param name="offsetHz">The channel's offset from the source's centre, Hz.</param>
    /// <exception cref="ArgumentException">The mode has no FM deviation target.</exception>
    public static IqChannelSpec ForFmMode(string mode, double offsetHz)
    {
        FmModeProfile profile = FmModeProfiles.For(mode)
            ?? throw new ArgumentException($"'{mode}' has no FM deviation target", nameof(mode));
        return new IqChannelSpec
        {
            OffsetHz = offsetHz,
            Demodulation = IqDemodulation.Fm,
            Name = mode,
            FmDeviationHz = profile.PeakDeviationHz,
            // As wide as the mode says it occupies, so the IF does not trim the signal's own
            // sidebands; the spacing's IF where the mode publishes no occupancy.
            FmIfBandwidthHz = profile.OccupiedBandwidthHz ?? IfBandwidthForSpacing(profile.ChannelSpacingHz),
        };
    }

    /// <summary>The IF bandwidth for a channel spacing: 8 kHz on 12.5 kHz, 16 kHz on 25 kHz, and
    /// the same proportion in between.</summary>
    public static double IfBandwidthForSpacing(double spacingHz) => spacingHz * 0.64;

    /// <summary>The complex rate this channel runs at on a station whose DSP rate is
    /// <paramref name="dspRate"/>.</summary>
    internal int ChannelRateFor(int dspRate)
    {
        if (ChannelRate is int rate)
        {
            return rate;
        }

        if (Demodulation != IqDemodulation.Fm)
        {
            return dspRate;
        }

        int multiple = (DefaultFmChannelRate + dspRate - 1) / dspRate;
        return Math.Max(1, multiple) * dspRate;
    }

    /// <summary>The channel filter's passband edge, Hz either side of the dial.</summary>
    internal double PassbandHzFor(int channelRate) =>
        Demodulation == IqDemodulation.Fm ? 0.4 * FmIfBandwidthHz : 0.4 * channelRate;

    /// <summary>The channel filter's stopband edge, Hz either side of the dial.</summary>
    internal double StopbandHzFor(int channelRate) =>
        Demodulation == IqDemodulation.Fm ? 0.6 * FmIfBandwidthHz : 0.5 * channelRate;

    /// <summary>Why this channel cannot run on a station at <paramref name="dspRate"/>, or null.</summary>
    internal string? Problem(int dspRate)
    {
        int rate = ChannelRateFor(dspRate);
        if (rate < dspRate || rate % dspRate != 0)
        {
            return $"channel rate {rate} Hz is not a multiple of the {dspRate} Hz DSP rate";
        }

        if (Demodulation == IqDemodulation.Fm)
        {
            if (!(FmIfBandwidthHz > 0))
            {
                return "FM IF bandwidth must be above 0 Hz";
            }

            if (!(FmDeviationHz > 0))
            {
                return "FM deviation must be above 0 Hz";
            }

            double pass = PassbandHzFor(rate);
            if (StopbandHzFor(rate) > rate - pass)
            {
                return $"an FM IF of {FmIfBandwidthHz:F0} Hz does not fit a {rate} Hz channel rate; raise the channel rate";
            }
        }
        else
        {
            if (!(SsbLowHz >= 0 && SsbHighHz > SsbLowHz))
            {
                return $"SSB filter {SsbLowHz:F0} to {SsbHighHz:F0} Hz is not a band";
            }

            if (SsbHighHz > PassbandHzFor(rate))
            {
                return $"SSB filter top {SsbHighHz:F0} Hz is past the {PassbandHzFor(rate):F0} Hz a {rate} Hz channel passes; raise the channel rate";
            }
        }

        if (!(ChannelStopbandAttenuationDb >= 20 && ChannelStopbandAttenuationDb <= 110))
        {
            return "channel stopband attenuation must be from 20 to 110 dB";
        }

        return null;
    }
}

/// <summary>Where to point an IQ source so that a channel stays off its DC spike.</summary>
public static class IqTuning
{
    /// <summary>The most a source is put off the dial by default, Hz.</summary>
    public const double MaxDcClearOffsetHz = 25000;

    /// <summary>
    /// How far from the dial to tune a source at <paramref name="sampleRate"/>, Hz: an eighth of
    /// the rate, at most <see cref="MaxDcClearOffsetHz"/>. Tune the source to
    /// <c>dial - offset</c> and give the channel <c>OffsetHz = offset</c>. 6 kHz on a 48 kS/s
    /// sound card, 25 kHz on a 2.4 MS/s dongle.
    /// </summary>
    public static double DcClearOffsetHz(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        return Math.Min(sampleRate / 8.0, MaxDcClearOffsetHz);
    }
}
