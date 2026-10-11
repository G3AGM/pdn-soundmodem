using System.Globalization;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>iqcard:</c> kind: IQ from a sound card's left and right inputs, receive only
/// (issue #596).</summary>
internal sealed class IqCardDeviceKind : DeviceKind
{
    /// <summary>The prefix, matched case-blind.</summary>
    internal const string Prefix = "iqcard:";

    /// <inheritdoc/>
    public override string Name => "iqcard";

    /// <inheritdoc/>
    public override string Spelling => "iqcard:DEVICE";

    /// <inheritdoc/>
    public override bool Matches(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings)
    {
        string card = device[Prefix.Length..].Trim();
        if (card.Length == 0)
        {
            throw new InvalidDataException(
                $"\"device\": \"{device}\" names no sound card. Put the ALSA name of the card the IQ "
                + "comes in on after the prefix, e.g. iqcard:plughw:CARD=Device,DEV=0 (arecord -L "
                + "lists them).");
        }

        return new IqCardStationDevice(this, device, card, settings.IqCard);
    }

    /// <inheritdoc/>
    public override string? TransmitTestRefusal =>
        "tx test: refused, this station takes IQ from a sound card and receives only - there is "
        + "nothing here to key";
}

/// <summary>
/// A sound card carrying IQ on its left and right inputs, through the IQ front end
/// (<see cref="IqReceiver"/>) to one channel at the band plan's dial. Receive only.
/// </summary>
/// <remarks>
/// <para><b>Where the IQ is centred</b> is <c>"iqCard"."centreFrequency"</c>, or with a
/// <c>"rig"</c> section and no centre, the rig's dial plus <c>"centreOffsetHz"</c>, followed as the
/// rig moves. <b>Where the channel listens</b> is the band plan's dial (or
/// <c>dialFrequency</c>), anywhere within the card's band, and it stays there when the centre moves;
/// the channel is the device's <see cref="DeviceOpening.Tuner"/>, so whatever later wants to move it
/// (#585) asks the device.</para>
/// <para><b>No mixer work.</b> The card's mixer is left as it is. Were any added, it would go above
/// the <see cref="AlsaStereoCapture"/> construction for the reason CLAUDE.md gives and
/// <c>StartUpOrderTests</c> pins.</para>
/// </remarks>
internal sealed class IqCardStationDevice(
    DeviceKind kind, string spec, string card, IqCardConfig? config) : StationDevice(kind, spec)
{
    /// <summary>The card's rate unless told otherwise: the G90's IQ output's.</summary>
    internal const int DefaultSampleRate = 48000;

    /// <summary>Frames per read: 43 ms at 48 kHz, short enough that the station hears a burst
    /// promptly and that stopping the reader thread at shutdown is quick.</summary>
    internal const int ReadBlockFrames = 2048;

    /// <summary>The ALSA name of the card.</summary>
    public string Card => card;

    /// <summary>The card's capture rate.</summary>
    public int SampleRate => config?.SampleRate ?? DefaultSampleRate;

    /// <summary>The card's own clock, and any rate the front end can resample: <c>--capture-rate</c>
    /// is the audio card's.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"the IQ from {card} has to be told where to listen within the card's band. Give every "
        + "modem an \"rfFrequency\" and the dial is worked out from them, or set \"dialFrequency\" "
        + "to pin it.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio is IQ from the sound card {card}, and there is no "
        + "transmit path on an iqcard: device.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        $"--device iqcard: is a receive-only station - IQ from {card} has no transmitter behind it "
        + "here, so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <inheritdoc/>
    public override string? SettingsProblem => Problem(config);

    /// <summary>Why an <c>"iqCard"</c> section cannot be used as written, or null.</summary>
    internal static string? Problem(IqCardConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        if (config.SampleRate is int rate && (rate < 8000 || rate > 768000))
        {
            return $"\"iqCard\".\"sampleRate\" ({rate}) has to be from 8000 to 768000 Hz";
        }

        if (config.CentreFrequency is double centre && (!double.IsFinite(centre) || centre <= 0))
        {
            return "\"iqCard\".\"centreFrequency\" has to be a frequency in Hz, above 0";
        }

        if (config.CentreOffsetHz is double offset && !double.IsFinite(offset))
        {
            return "\"iqCard\".\"centreOffsetHz\" has to be a number of Hz";
        }

        int cardRate = config.SampleRate ?? DefaultSampleRate;
        if (config.ChannelOffsetHz is double channel && !(Math.Abs(channel) < cardRate / 2.0))
        {
            return $"\"iqCard\".\"channelOffsetHz\" ({channel}) has to be inside the {cardRate / 2} Hz either side of the centre the card carries";
        }

        if (Options(config, 0).Problem() is string balance)
        {
            return $"\"iqCard\": {balance}";
        }

        double low = config.SsbLowHz ?? 150;
        double high = config.SsbHighHz ?? 3450;
        if (!(low >= 0 && high > low))
        {
            return $"\"iqCard\".\"ssbLowHz\" and \"ssbHighHz\" ({low} and {high}) have to rise from 0 or more";
        }

        if (config.Gain is double gain && (!double.IsFinite(gain) || gain <= 0))
        {
            return "\"iqCard\".\"gain\" has to be above 0";
        }

        return null;
    }

    /// <summary>The source's settings from the section, centred on <paramref name="centreHz"/>.</summary>
    internal static SoundCardIqOptions Options(IqCardConfig? config, double centreHz, Action<string>? journal = null) => new()
    {
        CentreFrequencyHz = centreHz,
        SwapIq = config?.SwapIq ?? false,
        GainImbalanceDb = config?.GainImbalanceDb ?? 0,
        PhaseImbalanceDegrees = config?.PhaseImbalanceDegrees ?? 0,
        MaxReadFrames = ReadBlockFrames,
        Journal = journal,
    };

    /// <summary>The front end's channel for the station's dial, <paramref name="offsetHz"/> from
    /// the card's centre, demodulated as <paramref name="sideband"/> says.</summary>
    internal static IqChannelSpec Channel(IqCardConfig? config, string sideband, double offsetHz)
    {
        IqDemodulation demodulation = RfPlan.IsFmRadio(sideband) ? IqDemodulation.Fm
            : sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase) ? IqDemodulation.Lsb
            : IqDemodulation.Usb;
        return new IqChannelSpec
        {
            OffsetHz = offsetHz,
            Demodulation = demodulation,
            Name = "iqcard",
            SsbLowHz = config?.SsbLowHz ?? 150,
            SsbHighHz = config?.SsbHighHz ?? 3450,
            Gain = (float)(config?.Gain ?? 1.0),
        };
    }

    /// <summary>How close to the channel's audio, Hz, the card's centre may come before the
    /// station warns: the DC offset itself, mains hum at 50 to 150 Hz either side of it, and room
    /// for the hum's harmonics and the card's low-frequency noise.</summary>
    internal const double CentreGuardHz = 1000;

    /// <summary>
    /// Whether the card's centre, and so its DC offset and hum, is inside the channel's audio or
    /// within <see cref="CentreGuardHz"/> of it. A channel on the centre itself is: its SSB audio
    /// starts 150 Hz above the DC, with the hum between.
    /// </summary>
    internal static bool CentreNearChannel(IqChannelSpec channel)
    {
        // Where the centre is, in the channel's audio terms: Hz above the dial for USB, below it
        // for LSB, from the carrier for FM.
        double centre = channel.Demodulation == IqDemodulation.Lsb ? channel.OffsetHz : -channel.OffsetHz;
        (double low, double high) = channel.Demodulation == IqDemodulation.Fm
            ? (-channel.FmIfBandwidthHz / 2, channel.FmIfBandwidthHz / 2)
            : (channel.SsbLowHz, channel.SsbHighHz);
        double distance = centre < low ? low - centre : centre > high ? centre - high : 0;
        return distance < CentreGuardHz;
    }

    /// <summary>
    /// Where the channel's dial sits from the card's centre, Hz, when the station puts the rig
    /// there itself: <c>"channelOffsetHz"</c>, or by default an eighth of the card's rate
    /// (<see cref="IqTuning.DcClearOffsetHz"/>, 6 kHz at 48 kHz), and for FM at least far enough
    /// that the IF clears the centre by the guard.
    /// </summary>
    /// <remarks>
    /// Why an eighth: at 48 kHz, 6 kHz puts USB audio at 6.15 to 9.45 kHz from the centre (LSB at
    /// 2.55 to 5.85 kHz), clear of the DC offset, the 50/100/150 Hz hum and their low harmonics by
    /// thousands of Hz, while the channel's filter out to its stopband (6 kHz either side of the
    /// dial on a 12 kHz station) still ends at 12 kHz, half way to the 24 kHz the card carries,
    /// so the card's own anti-alias roll-off near its band edge is not in the channel either.
    /// </remarks>
    internal static double ChannelOffsetHz(IqCardConfig? config, string sideband, int sampleRate)
    {
        if (config?.ChannelOffsetHz is double stated)
        {
            return stated;
        }

        double offset = IqTuning.DcClearOffsetHz(sampleRate);
        if (RfPlan.IsFmRadio(sideband))
        {
            offset = Math.Max(offset, (new IqChannelSpec { OffsetHz = 0, Demodulation = IqDemodulation.Fm }.FmIfBandwidthHz / 2) + CentreGuardHz);
        }

        return offset;
    }

    /// <summary>
    /// With the centre read off a rig (no <c>"centreFrequency"</c>), the rig goes
    /// <see cref="ChannelOffsetHz"/> below the band plan's dial (less <c>"centreOffsetHz"</c>), so
    /// the channel lands that far from the card's centre rather than on it.
    /// </summary>
    public override double RigDialOffsetHz(string sideband) =>
        config?.CentreFrequency is not null
            ? 0
            : -(ChannelOffsetHz(config, sideband, SampleRate) + (config?.CentreOffsetHz ?? 0));

    /// <inheritdoc/>
    public override Task<DeviceOpening> OpenAsync(DeviceOpenContext context) => Task.FromResult(Open(context));

    private DeviceOpening Open(DeviceOpenContext context)
    {
        StationJournal journal = context.Journal;
        double dialHz = context.ReceiveDialHz!.Value;
        RigControl? rig = context.Rig;
        double centreOffset = config?.CentreOffsetHz ?? 0;

        // Where the IQ is centred: said outright, or read off the rig.
        double centreHz;
        bool followRig = false;
        if (config?.CentreFrequency is double stated)
        {
            centreHz = stated;
        }
        else if (rig is not null)
        {
            long? rigDial = rig.Snapshot().Tuning?.DialHz ?? rig.Plan?.DialHz;
            if (rigDial is not long known)
            {
                // Exit 1: rigctld may only be slow, and the restart asks again.
                Console.Error.WriteLine(
                    $"\"device\": \"{Spec}\" takes its IQ centre from the rig, and rigctld at "
                    + $"{rig.Endpoint} has not said where the rig is. Start rigctld, or set "
                    + "\"iqCard\".\"centreFrequency\".");
                return DeviceOpening.Refused(1);
            }

            centreHz = known + centreOffset;
            followRig = true;
            if (!(centreHz > 0))
            {
                Console.Error.WriteLine(
                    $"\"device\": \"{Spec}\" takes its IQ centre from the rig's dial, {RfPlan.Mhz(known)}, plus "
                    + "\"iqCard\".\"centreOffsetHz\", which comes to "
                    + string.Create(CultureInfo.InvariantCulture, $"{centreHz:F0} Hz")
                    + ", not a frequency. Check the offset's sign and size.");
                return DeviceOpening.Refused(2);
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"\"device\": \"{Spec}\" needs to know what RF frequency the card's IQ is centred on. "
                + "Set \"iqCard\".\"centreFrequency\" (Hz), or add a \"rig\" section so it can be read "
                + "off the radio's dial.");
            return DeviceOpening.Refused(2);
        }

        IqChannelSpec spec = Channel(config, context.Sideband, dialHz - centreHz);
        if (CentreNearChannel(spec))
        {
            journal.WriteError(
                $"iqcard: the card's centre ({RfPlan.Mhz(centreHz)}) is "
                + string.Create(CultureInfo.InvariantCulture, $"{spec.OffsetHz:+0;-0} Hz")
                + " from the dial, in or beside the channel's audio, so the card's DC offset and hum "
                + "will be heard there. Centre the IQ about "
                + string.Create(CultureInfo.InvariantCulture, $"{ChannelOffsetHz(config, context.Sideband, SampleRate) / 1000:F1} kHz")
                + " below the dial instead"
                + (rig is null ? "." : ", or leave \"centreFrequency\" out and let the band plan put the rig there."));
        }

        // No mixer work: see the remarks. The capture opens here and nothing touches the card's
        // controls before or after.
        AlsaStereoCapture capture;
        try
        {
            capture = new AlsaStereoCapture(card, SampleRate);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or InvalidOperationException or ArgumentException
                                    or DllNotFoundException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.Audio(
                card, context.ConfigPath, e, "device", split: true, capture: true));
            return DeviceOpening.Refused(1);
        }

        var source = new SoundCardIqSource(capture, Options(config, centreHz, journal.Write));
        IqReceiver receiver;
        try
        {
            receiver = new IqReceiver(source, [spec], new IqReceiverOptions
            {
                DspRate = context.DspRate,
                ReadBlockFrames = ReadBlockFrames,
                // Room for the channel to be moved anywhere in the card's band later.
                CoverageHz = 0.45 * SampleRate,
                Journal = journal.Write,
            });
        }
        catch (ArgumentException e)
        {
            source.Dispose();
            Console.Error.WriteLine(
                $"\"device\": \"{Spec}\" cannot hear the dial {RfPlan.Mhz(dialHz)}: the card's IQ is "
                + $"centred on {RfPlan.Mhz(centreHz)} at {SampleRate} Hz, which carries "
                + string.Create(CultureInfo.InvariantCulture, $"{SampleRate / 2000.0:F1} kHz")
                + $" either side. {e.Message}");
            return DeviceOpening.Refused(2);
        }

        IqReceiverChannel channel = receiver.Channels[0];
        Action<RigState>? follow = null;
        if (followRig)
        {
            // On RigControl's thread, which does not expect a handler to throw: a centre that is
            // not a frequency is said and skipped rather than thrown.
            follow = state =>
            {
                if (state.Tuning is RigTuning tuning)
                {
                    double moved = tuning.DialHz + centreOffset;
                    if (moved > 0)
                    {
                        source.SetCentreFrequency(moved);
                    }
                    else
                    {
                        journal.WriteError(
                            $"iqcard: the rig is at {RfPlan.Mhz(tuning.DialHz)}, and with \"centreOffsetHz\" "
                            + "that is no frequency to centre the IQ on; the centre is left where it was");
                    }
                }
            };
            rig!.Changed += follow;

            // The rig may have reported, or moved, between the read above and the subscription.
            follow(rig.Snapshot());
        }

        journal.Write(
            $"audio: {card} IQ at {SampleRate} Hz centred on {RfPlan.Mhz(centreHz)}"
            + (followRig ? " (the rig's dial" + (centreOffset != 0 ? string.Create(CultureInfo.InvariantCulture, $" {centreOffset:+0;-0} Hz") : "") + ", followed)" : "")
            + $" -> {spec.Demodulation.ToString().ToUpperInvariant()} at {RfPlan.Mhz(dialHz)}"
            + string.Create(CultureInfo.InvariantCulture, $" ({spec.OffsetHz:+0;-0} Hz from centre)")
            + $", audio at {context.DspRate} Hz"
            + (config?.SwapIq == true ? ", I and Q swapped" : "")
            + (config?.GainImbalanceDb is double g && g != 0 || config?.PhaseImbalanceDegrees is double p && p != 0
                ? string.Create(CultureInfo.InvariantCulture, $", balance corrected by {config?.GainImbalanceDb ?? 0:+0.00;-0.00;0.00} dB and {config?.PhaseImbalanceDegrees ?? 0:+0.00;-0.00;0.00} degrees")
                : "")
            + " (RECEIVE ONLY)");
        journal.Write(capture.BufferMilliseconds > 0
            ? $"audio: capture buffer {capture.BufferMilliseconds} ms, period {capture.PeriodMilliseconds} ms"
            : "audio: capture buffer: the card would not say");
        if (capture.ConfigurationWarning is string refusal)
        {
            journal.WriteError($"audio: {refusal}");
        }

        receiver.Start();
        return new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(context.DspRate),
            Input = new IqCardInput(receiver, source, channel, rig, follow),
            Tuner = channel,
            InputLossCount = () => channel.Discontinuities,
        };
    }

    /// <summary>The station's input: the front end's one channel, owning the receiver and the
    /// card so the station's shutdown closes both, in the order that is safe.</summary>
    private sealed class IqCardInput(
        IqReceiver receiver, SoundCardIqSource source, IqReceiverChannel channel,
        RigControl? rig, Action<RigState>? follow) : IAudioInput, IDisposable
    {
        public int SampleRate => channel.SampleRate;

        public int Read(Span<float> destination) => channel.Read(destination);

        public void Dispose()
        {
            if (rig is not null && follow is not null)
            {
                rig.Changed -= follow;
            }

            // The reader thread first: its read is one short block, and closing the PCM under a
            // snd_pcm_readi on another thread is not something alsa-lib supports.
            receiver.Dispose();
            source.Dispose();
        }
    }
}
