using System.Globalization;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>What kind of receiver the station is, for where the mailcast signal can be heard.</summary>
internal enum MailcastRadioKind
{
    /// <summary>A sound card on a radio this station does not set the filters of.</summary>
    SoundCard,

    /// <summary>A headless FlexRadio: the daemon owns the slice and sets its receive filter.</summary>
    FlexHeadless,

    /// <summary>A FlexRadio slice owned by SmartSDR: its filter is SmartSDR's business.</summary>
    FlexAttach,

    /// <summary>An UberSDR web receiver, tuned by this station to its dial.</summary>
    UberSdr,

    /// <summary>An OpenWebRX web receiver, tuned by this station to its dial.</summary>
    OpenWebRx,
}

/// <summary>What the station hears, as far as start-up knows it.</summary>
/// <param name="Kind">What kind of receiver it is.</param>
/// <param name="DialHz">Its dial, or null when nothing says where that is (an audio-placed
/// station with no <c>dialFrequency</c>).</param>
/// <param name="Sideband">"usb", "lsb" or "fm".</param>
/// <param name="WindowLowHz">The lowest audio frequency it hears.</param>
/// <param name="WindowHighHz">The highest audio frequency it hears.</param>
/// <param name="HasRig">Whether there is a <c>rig</c> section to retune.</param>
internal sealed record MailcastRadio(
    MailcastRadioKind Kind, double? DialHz, string Sideband, double WindowLowHz, double WindowHighHz, bool HasRig);

/// <summary>Where the mailcast modem listens.</summary>
/// <param name="Retunes">True when the rig is retuned around each slot; false when the station's
/// own passband hears the signal and nothing is retuned.</param>
/// <param name="AudioCentreHz">Where the signal's centre falls in the station's audio: wherever
/// <see cref="MailcastConfig.DialKHz"/> puts it when retuning (1800 Hz for the default
/// 7.052 MHz), else wherever the station's own dial puts it.</param>
/// <param name="LowHz">The signal's lower edge in that audio.</param>
/// <param name="HighHz">Its upper edge.</param>
internal sealed record MailcastPlacement(bool Retunes, double AudioCentreHz, double LowHz, double HighHz)
{
    /// <summary>The start-up line saying where the modem listens.</summary>
    internal string Describe(MailcastConfig config) => Retunes
        ? string.Create(CultureInfo.InvariantCulture,
            $"mailcast: the station's passband does not reach the signal on {MailcastOnAir.Mhz(config.CentreHz)} MHz, so the rig is "
            + $"retuned to {MailcastOnAir.Mhz(config.DialHz)} MHz USB from {MailcastOnAir.ListenBefore.TotalMinutes:F0} minute before each "
            + $"slot to {MailcastOnAir.ListenAfter.TotalMinutes:F0} minutes after, and put back; nothing is transmitted meanwhile")
        : string.Create(CultureInfo.InvariantCulture,
            $"mailcast: listening on the station's own passband, the signal's centre {MailcastOnAir.Mhz(config.CentreHz)} MHz at "
            + $"{AudioCentreHz:F0} Hz audio ({LowHz:F0}-{HighHz:F0} Hz); no retuning");

    /// <summary>
    /// Half the mailcast signal's occupied width, measured off the MS110D modem the way the band
    /// planner measures every other mode, or the nominal figure if it cannot be.
    /// </summary>
    internal static double HalfWidthHz()
    {
        try
        {
            IModem probe = ModemCatalog.Create(MailcastOnAir.Mode, 48000, static _ => { });
            if (ModemBandProbe.TryMeasure(probe, 48000, out double low, out double high) && high > low)
            {
                return (high - low) / 2;
            }
        }
        catch (ArgumentException)
        {
        }

        return MailcastOnAir.NominalHalfWidthHz;
    }

    /// <summary>
    /// Operator slop allowed around pdn-mailcast's original recommended dial, 7.052 MHz, for a
    /// Flex in attach mode or an SDR's own dial to count as tuned to the signal, whatever its
    /// nominal window says. Untouched by the 2026-10 sound-card filter study: neither has an
    /// analogue filter whose roll-off the dial needs to dodge, so there is nothing for either to
    /// gain by moving off 7.052.
    /// </summary>
    internal const double OnTheDialToleranceHz = 100;

    /// <summary>The recommended dial, in kHz, for a Flex in attach mode or an SDR.</summary>
    internal const double WideFilterDialKHz = MailcastConfig.DefaultDialKHz;

    /// <summary>The band, in Hz, a Flex-attach or SDR dial counts as "on the mailcast dial"
    /// within: <see cref="OnTheDialToleranceHz"/> either side of <see cref="WideFilterDialKHz"/>.</summary>
    internal const double OnTheDialLowHz = WideFilterDialKHz * 1000 - OnTheDialToleranceHz;

    /// <summary>See <see cref="OnTheDialLowHz"/>.</summary>
    internal const double OnTheDialHighHz = WideFilterDialKHz * 1000 + OnTheDialToleranceHz;

    /// <summary>
    /// The lowest audio centre a sound-card rig's own dial may put the signal at and still count
    /// as heard on its own passband, no retuning: the 2026-10 filter study's measure-or-type
    /// rule. See <see cref="OnTheDialHighHz"/>'s counterpart <see cref="SoundCardCentreHighHz"/>
    /// and <see cref="MailcastOnAir.SuggestedDialHz"/> for how a dial follows from it.
    /// </summary>
    internal const double SoundCardCentreLowHz = 1000;

    /// <summary>The highest audio centre a sound-card rig's own dial may put the signal at; see
    /// <see cref="SoundCardCentreLowHz"/>.</summary>
    internal const double SoundCardCentreHighHz = 2000;

    /// <summary>
    /// Decides where the modem listens. The station's own passband first: if it hears the whole
    /// signal (or the dial is the mailcast dial), nothing is retuned. Otherwise the rig is
    /// retuned around each slot, if the config allows it and there is a rig. Otherwise null, with
    /// the sentence that says why and what to change.
    /// </summary>
    internal static MailcastPlacement? Decide(
        MailcastConfig config, MailcastRadio radio, double halfWidthHz, out string? refusal)
    {
        refusal = null;
        if (RfPlan.IsFmRadio(radio.Sideband))
        {
            refusal =
                "mailcast: this station is an FM radio (\"sideband\": \"fm\"), and pdn-mailcast is a USB "
                + "signal on HF. Run the receiver on an HF station, or remove \"mailcast\".";
            return null;
        }

        bool upper = !radio.Sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase);
        string heard = "";
        if (radio.DialHz is double dial && upper)
        {
            double centre = config.CentreHz - dial;
            double low = centre - halfWidthHz;
            double high = centre + halfWidthHz;

            // A sound-card rig: any dial that puts the signal's audio centre in the band a
            // typical SSB filter's roll-off clears, 1000 to 2000 Hz, is heard where it is, placing
            // the modem at that exact centre - the 2026-10 filter study's measure-or-type rule.
            // A Flex in attach mode or an SDR is untouched: both keep the original fixed-dial
            // tolerance band around 7.052 MHz, having no analogue roll-off to dodge.
            bool onDial = radio.Kind == MailcastRadioKind.SoundCard
                ? centre >= SoundCardCentreLowHz && centre <= SoundCardCentreHighHz
                : dial >= OnTheDialLowHz && dial <= OnTheDialHighHz;
            if (onDial || (low >= radio.WindowLowHz && high <= radio.WindowHighHz))
            {
                return new MailcastPlacement(false, centre, low, high);
            }

            heard = string.Create(CultureInfo.InvariantCulture,
                $"This station's dial is {MailcastOnAir.Mhz(dial)} MHz USB and it hears {radio.WindowLowHz:F0}-{radio.WindowHighHz:F0} Hz of audio "
                + $"({MailcastOnAir.Mhz(dial + radio.WindowLowHz)} to {MailcastOnAir.Mhz(dial + radio.WindowHighHz)} MHz); the signal would be at "
                + $"{low:F0}-{high:F0} Hz. ");
        }
        else if (!upper)
        {
            heard = "This station is on LSB, and the signal is USB. ";
        }
        else
        {
            heard = "Nothing in this configuration says where the station's dial is (no \"rfFrequency\" on the modems and no \"dialFrequency\"). ";
        }

        if (config.Retune && radio.HasRig)
        {
            double centre = MailcastOnAir.CentreAudioHz(config.DialHz);
            return new MailcastPlacement(true, centre, centre - halfWidthHz, centre + halfWidthHz);
        }

        string signal =
            $"mailcast: the signal on {MailcastOnAir.Mhz(config.CentreHz - halfWidthHz)} to {MailcastOnAir.Mhz(config.CentreHz + halfWidthHz)} MHz "
            + "is outside what this station hears. ";
        refusal = signal + heard + radio.Kind switch
        {
            MailcastRadioKind.FlexHeadless or MailcastRadioKind.FlexAttach =>
                "A FlexRadio is not retuned for it: put the slice within reach, with the band plan or "
                + "\"flex\".\"frequency\", so the signal falls inside the slice.",
            MailcastRadioKind.UberSdr =>
                "A web receiver is not retuned for it: set \"dialFrequency\" or the band plan so the signal "
                + "falls inside \"ubersdr\".\"ssbLowHz\" to \"ssbHighHz\".",
            MailcastRadioKind.OpenWebRx =>
                "A web receiver is not retuned for it: set \"dialFrequency\" or the band plan so the signal "
                + "falls inside \"openwebrx\".\"ssbLowHz\" to \"ssbHighHz\".",
            _ when !radio.HasRig =>
                "Add a \"rig\" section (rigctld) and \"mailcast\".\"retune\": true, and the rig is retuned "
                + "to it around each slot and put back; or tune so the signal's audio centre falls "
                + "between 1000 and 2000 Hz: the best dial is 7.0538 MHz minus the middle of your "
                + "receive passband (an FT-450D at about 367-2190 Hz, centred on 1278 Hz, wants "
                + "7.05252 MHz) - POST /api/mailcast/measure measures your passband and works the dial "
                + "out for you, or read the edges off your filter's manual and do the sum by hand.",
            _ =>
                "Set \"mailcast\".\"retune\": true, and the rig is retuned to it around each slot and put "
                + "back; or tune so the signal's audio centre falls between 1000 and 2000 Hz: the best "
                + "dial is 7.0538 MHz minus the middle of your receive passband (an FT-450D at about "
                + "367-2190 Hz, centred on 1278 Hz, wants 7.05252 MHz) - POST /api/mailcast/measure "
                + "measures your passband and works the dial out for you, or read the edges off your "
                + "filter's manual and do the sum by hand.",
        };
        return null;
    }
}
