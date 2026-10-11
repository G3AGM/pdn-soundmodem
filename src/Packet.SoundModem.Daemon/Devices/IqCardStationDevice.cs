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

    /// <summary>Whether the card's centre, and so its DC spike, falls inside the channel's audio.</summary>
    internal static bool CentreInPassband(IqChannelSpec channel)
    {
        // The centre is at -OffsetHz from the dial.
        double centreFromDial = -channel.OffsetHz;
        return channel.Demodulation switch
        {
            IqDemodulation.Usb => centreFromDial >= channel.SsbLowHz && centreFromDial <= channel.SsbHighHz,
            IqDemodulation.Lsb => -centreFromDial >= channel.SsbLowHz && -centreFromDial <= channel.SsbHighHz,
            _ => Math.Abs(centreFromDial) <= channel.FmIfBandwidthHz / 2,
        };
    }

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
        if (CentreInPassband(spec))
        {
            journal.WriteError(
                $"iqcard: the card's centre ({RfPlan.Mhz(centreHz)}) is inside the channel's audio, so "
                + "the card's DC offset and hum will be heard there. Centre the IQ about "
                + string.Create(CultureInfo.InvariantCulture, $"{IqTuning.DcClearOffsetHz(SampleRate) / 1000:F0} kHz")
                + " below the dial instead.");
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
            follow = state =>
            {
                if (state.Tuning is RigTuning tuning)
                {
                    source.SetCentreFrequency(tuning.DialHz + centreOffset);
                }
            };
            rig!.Changed += follow;
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
