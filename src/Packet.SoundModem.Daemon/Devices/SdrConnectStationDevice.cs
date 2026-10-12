using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Rig;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>sdrconnect:</c> kind: an SDRplay radio through SDRconnect's WebSocket API,
/// its IQ demodulated here or its own audio; receive only.</summary>
internal sealed class SdrConnectDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "sdrconnect";

    /// <inheritdoc/>
    public override string Spelling => "sdrconnect:HOST[:PORT]";

    /// <inheritdoc/>
    public override bool Matches(string device) => SdrConnectDevice.IsSdrConnect(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new SdrConnectStationDevice(this, device, SdrConnectDevice.Parse(device), settings.SdrConnect);

    /// <inheritdoc/>
    public override string? TransmitTestRefusal =>
        "tx test: refused, this station's audio comes from SDRconnect, an SDR receiver with no "
        + "transmitter - there is nothing here to key";

    /// <inheritdoc/>
    public override string? RigRefusal(string device) =>
        $"\"rig\" is set and \"device\" is \"{device}\", an SDRconnect receiver, which the station "
        + "tunes itself from the band plan and has no rig to control - remove \"rig\".";
}

/// <summary>
/// SDRconnect: its own clock, tuned by the station, its IQ demodulated by the station's front end
/// or its audio taken as it is, and no transmitter.
/// </summary>
internal sealed class SdrConnectStationDevice(
    DeviceKind kind, string spec, SdrConnectEndpoint endpoint, SdrConnectConfig? config)
    : StationDevice(kind, spec)
{
    /// <summary>SDRconnect this station listens through.</summary>
    public SdrConnectEndpoint Endpoint => endpoint;

    /// <summary>Its own clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"SDRconnect at {endpoint} has to be told where to listen. Give every modem an "
        + "\"rfFrequency\" and the dial is worked out from them, or set \"dialFrequency\" to pin it - "
        + "unlike a radio there is no dial already set to read off.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio comes from SDRconnect at {endpoint}, "
        + "an SDR receiver with no transmitter.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        $"--device sdrconnect: is a receive-only station - SDRconnect at {endpoint} has no "
        + "transmitter, so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <summary>The web receivers' family: an SDR's stream always carries noise, and a connection
    /// that is up and delivering nothing is a hung stream. While the client is reconnecting
    /// <see cref="DeviceOpening.SessionLive"/> says so and the starvation watch waits.</summary>
    public override DeadFeedDevice DeadFeedKind => DeadFeedDevice.UberSdr;

    /// <inheritdoc/>
    public override string? SettingsProblem => Problem(config);

    /// <summary>Whether the section asks for SDRconnect's audio rather than its IQ.</summary>
    internal static bool IsAudio(SdrConnectConfig? config) =>
        string.Equals(config?.Mode, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why an <c>"sdrconnect"</c> section cannot be used as written, or null.</summary>
    internal static string? Problem(SdrConnectConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        if (config.Mode is { } mode && !mode.Equals("iq", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("audio", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"sdrconnect\".\"mode\" (\"{mode}\") has to be \"iq\" (SDRconnect's IQ, demodulated here; "
                + "wants a wired link) or \"audio\" (SDRconnect's own demodulated audio; suits Wi-Fi)";
        }

        if (config.SampleRate is int rate && rate is < 48_000 or > 20_000_000)
        {
            return $"\"sdrconnect\".\"sampleRate\" ({rate}) is not a rate an RSP runs at; leave it out for "
                + "2000000, the lowest an RSP runs at without SDRconnect decimating";
        }

        if (config.LnaState is < 0)
        {
            return "\"sdrconnect\".\"lnaState\" cannot be negative; SDRconnect's start-up lines say the range its radio offers";
        }

        if (config.DeviceSerial is not null && config.DeviceName is not null)
        {
            return "\"sdrconnect\" has both \"deviceSerial\" and \"deviceName\"; name the radio one way, not both";
        }

        if (config.DeviceSerial is { } serial && string.IsNullOrWhiteSpace(serial))
        {
            return "\"sdrconnect\".\"deviceSerial\" is empty; leave it out to use the radio SDRconnect has selected";
        }

        if (config.DeviceName is { } name && string.IsNullOrWhiteSpace(name))
        {
            return "\"sdrconnect\".\"deviceName\" is empty; leave it out to use the radio SDRconnect has selected";
        }

        if (config.Antenna is { } antenna && string.IsNullOrWhiteSpace(antenna))
        {
            return "\"sdrconnect\".\"antenna\" is empty; leave it out to use the antenna SDRconnect has selected";
        }

        if (config.NoDataSeconds is double noData && (!double.IsFinite(noData) || noData <= 0))
        {
            return "\"sdrconnect\".\"noDataSeconds\" has to be above 0";
        }

        if (config.Gain is double audioGain && (!double.IsFinite(audioGain) || audioGain <= 0))
        {
            return "\"sdrconnect\".\"gain\" has to be above 0";
        }

        double low = config.SsbLowHz ?? 150;
        double high = config.SsbHighHz ?? 3450;
        if (!(low >= 0 && high > low))
        {
            return $"\"sdrconnect\".\"ssbLowHz\" and \"ssbHighHz\" ({low} and {high}) have to rise from 0 or more";
        }

        if (IsAudio(config) && high > SdrConnectProtocol.AudioRate / 2)
        {
            return $"\"sdrconnect\".\"ssbHighHz\" ({high}) is above {SdrConnectProtocol.AudioRate / 2} Hz, "
                + $"the top of SDRconnect's {SdrConnectProtocol.AudioRate} Hz audio";
        }

        if (config.FmIfBandwidthHz is double ifBandwidth && !(ifBandwidth > 0))
        {
            return "\"sdrconnect\".\"fmIfBandwidthHz\" has to be above 0";
        }

        if (config.FmDeviationHz is double deviation && !(deviation > 0))
        {
            return "\"sdrconnect\".\"fmDeviationHz\" has to be above 0";
        }

        if (IsAudio(config) && config.FmDeviationHz is not null)
        {
            return "\"sdrconnect\".\"fmDeviationHz\" is the station's own FM demodulator's, and \"mode\": \"audio\" "
                + "uses SDRconnect's; remove it";
        }

        return null;
    }

    /// <inheritdoc/>
    public override async Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        StationJournal journal = context.Journal;
        double dialHz = context.ReceiveDialHz!.Value;
        string sideband = context.Sideband;
        bool fm = RfPlan.IsFmRadio(sideband);
        bool lsb = !fm && sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase);
        var options = new SdrConnectOptions
        {
            CentreFrequencyHz = dialHz,
            SampleRate = config?.SampleRate,
            LnaState = config?.LnaState,
            Antenna = config?.Antenna,
            DeviceSerial = config?.DeviceSerial,
            DeviceName = config?.DeviceName,
            NoDataTimeout = TimeSpan.FromSeconds(config?.NoDataSeconds ?? 5),
            Journal = journal.ErrorSink,
        };

        try
        {
            return IsAudio(config)
                ? await OpenAudioAsync(context, options, dialHz, fm, lsb)
                : await OpenIqAsync(context, options, dialHz, fm, lsb);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"\"device\": \"{Spec}\" cannot listen on {RfPlan.Mhz(dialHz)}: {e.Message}");
            return DeviceOpening.Refused(2);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or WebSocketException)
        {
            Console.Error.WriteLine(Diagnostic(Spec, context.ConfigPath, e));
            return DeviceOpening.Refused(1);
        }
    }

    private async Task<DeviceOpening> OpenIqAsync(
        DeviceOpenContext context, SdrConnectOptions options, double dialHz, bool fm, bool lsb)
    {
        StationJournal journal = context.Journal;
        IqDemodulation demodulation = fm ? IqDemodulation.Fm : lsb ? IqDemodulation.Lsb : IqDemodulation.Usb;
        var channel = new IqChannelSpec
        {
            OffsetHz = 0,
            Demodulation = demodulation,
            Name = "sdrconnect",
            SsbLowHz = config?.SsbLowHz ?? 150,
            SsbHighHz = config?.SsbHighHz ?? 3450,
            FmIfBandwidthHz = config?.FmIfBandwidthHz ?? 16000,
            FmDeviationHz = config?.FmDeviationHz ?? 3000,
            Gain = (float)(config?.Gain ?? 1.0),
        };
        var frontEnd = new IqReceiverOptions { DspRate = context.DspRate, Journal = journal.ErrorSink };
        SdrConnectIqInput input = await SdrConnectIqInput.OpenAsync(endpoint, options, channel, frontEnd, dialHz, context.Cancellation);

        string filter = fm
            ? $"FM, IF {channel.FmIfBandwidthHz:F0} Hz"
            : $"{demodulation.ToString().ToUpperInvariant()} {channel.SsbLowHz:F0}-{channel.SsbHighHz:F0} Hz";
        journal.Write($"audio: SDRconnect {endpoint} IQ (LO {RfPlan.Mhz(input.Source.CentreFrequencyHz)}, "
            + $"{input.Source.SampleRate} S/s) -> {filter} at {RfPlan.Mhz(dialHz)}, audio at {context.DspRate} Hz "
            + "(RECEIVE ONLY)");
        return Opened(context, input, () => input.SessionLive, () => input.Discontinuities, input, lost => input.Lost += lost);
    }

    private async Task<DeviceOpening> OpenAudioAsync(
        DeviceOpenContext context, SdrConnectOptions options, double dialHz, bool fm, bool lsb)
    {
        StationJournal journal = context.Journal;
        var channel = new SdrConnectAudioChannel
        {
            Demodulator = fm ? "NFM" : lsb ? "LSB" : "USB",
            SsbLowHz = config?.SsbLowHz ?? 150,
            SsbHighHz = config?.SsbHighHz ?? 3450,
            FmBandwidthHz = config?.FmIfBandwidthHz ?? 12500,
            Gain = (float)(config?.Gain ?? 1.0),
            OutputRate = context.DspRate,
        };
        SdrConnectAudioInput input = await SdrConnectAudioInput.OpenAsync(endpoint, options, channel, dialHz, context.Cancellation);

        string filter = fm
            ? $"NFM, filter {channel.FmBandwidthHz:F0} Hz"
            : $"{channel.Demodulator} {channel.SsbLowHz:F0}-{channel.SsbHighHz:F0} Hz";
        journal.Write($"audio: SDRconnect {endpoint} demodulated audio, {filter} at {RfPlan.Mhz(dialHz)}, "
            + $"{SdrConnectProtocol.AudioRate} Hz"
            + (context.DspRate == SdrConnectProtocol.AudioRate ? "" : $" -> {context.DspRate} Hz")
            + " (RECEIVE ONLY)");
        journal.Write("sdrconnect: SDRconnect demodulates on this stream, with its own filter; its AGC, noise "
            + "reduction, squelch and audio filters were asked to be off");
        return Opened(context, input, () => input.SessionLive, () => input.Discontinuities, input, lost => input.Lost += lost);
    }

    private static DeviceOpening Opened(
        DeviceOpenContext context, IAudioInput input, Func<bool> live, Func<long> losses,
        IReceiverTuner tuner, Action<Action<string>> onLost)
    {
        StationJournal journal = context.Journal;

        // SDRconnect that stays unreachable is not something to sit quietly on: exit 1 so the
        // unit restarts and tries afresh, as the other network receivers do.
        onLost(reason =>
        {
            journal.WriteError($"sdrconnect: {reason}");
            context.RadioLost();
        });

        return new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(context.DspRate),
            Input = input,
            SessionLive = live,
            Tuner = tuner,
            InputLossCount = losses,
        };
    }

    /// <summary>SDRconnect that would not stream. Not exit 2: a shack PC that is still booting, or
    /// SDRconnect not yet started, clears by itself.</summary>
    internal static string Diagnostic(string device, string? configPath, Exception error) =>
        $"""
        cannot listen through "{device}"
          {SdrConnectProtocol.Printable(error.Message)}

          {(configPath is null ? "Set by `--device` on the command line." : $"Set by \"device\" in {configPath}")}
          This names SDRconnect, SDRplay's receiver program, which the station listens to
          instead of a sound card. Check that SDRconnect is running on that machine with the
          radio started, that its WebSocket API is on that port (5454 unless changed), and
          that the machine's firewall lets this one connect to it. The rest of its settings
          live in the "sdrconnect" section.
          The service will keep retrying: this clears by itself if SDRconnect is merely late.
        """;
}
