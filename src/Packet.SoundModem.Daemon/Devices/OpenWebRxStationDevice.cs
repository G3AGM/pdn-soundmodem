using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.OpenWebRx;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>openwebrx:</c> kind: an OpenWebRX or OpenWebRX+ receiver's demodulated
/// audio, receive only.</summary>
internal sealed class OpenWebRxDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "openwebrx";

    /// <inheritdoc/>
    public override string Spelling => "openwebrx:URL";

    /// <inheritdoc/>
    public override bool Matches(string device) => OpenWebRxDevice.IsOpenWebRx(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new OpenWebRxStationDevice(this, device, OpenWebRxDevice.Parse(device), settings);

    /// <inheritdoc/>
    public override MailcastRadioKind MailcastKindOf(string device) => MailcastRadioKind.OpenWebRx;

    /// <inheritdoc/>
    public override string? TransmitTestRefusal =>
        "tx test: refused, this station's audio comes from a web receiver, which is a receiver "
        + "and has no transmitter - there is nothing here to key";

    /// <inheritdoc/>
    public override string? RigRefusal(string device) =>
        $"\"rig\" is set and \"device\" is \"{device}\", a web receiver, which is tuned "
        + "by the band plan itself and has no rig to control - remove \"rig\".";

    /// <inheritdoc/>
    public override string? PublishRefusal(string device) =>
        // The UberSDR rule (uplink plan, decisions of 2026-09-04), for the same reason.
        $"\"publish\" on \"device\": \"{device}\", which is somebody else's public "
        + "web receiver. Relaying it through this daemon would put that receiver on the monitor "
        + "site under this station's name, and hold one of its listener slots for the site's "
        + "visitors without its operator knowing. Publish from a station with a radio of its own.";
}

/// <summary>
/// An OpenWebRX receiver: its own 12 kHz audio clock, tuned by the station to the band plan's
/// dial through the receiver's own demodulator, and no transmitter at all.
/// </summary>
internal sealed class OpenWebRxStationDevice(
    DeviceKind kind, string spec, OpenWebRxEndpoint endpoint, DeviceSettings settings)
    : StationDevice(kind, spec)
{
    /// <summary>The default SSB passband, Hz above the dial: the UberSDR device's, which clears
    /// the whole band a plan can place modems in.</summary>
    internal const int DefaultSsbLowHz = 150;

    /// <summary>See <see cref="DefaultSsbLowHz"/>.</summary>
    internal const int DefaultSsbHighHz = 3450;

    /// <summary>The default FM channel filter's half-width, Hz: a 12.5 kHz channel's signal with
    /// some room, and inside the 6 kHz the receiver's 12 kHz audio can carry.</summary>
    internal const int DefaultFmHalfWidthHz = 5000;

    private readonly OpenWebRxConfig? _config = settings.OpenWebRx;

    /// <summary>The receiver this station listens through.</summary>
    public OpenWebRxEndpoint Endpoint => endpoint;

    /// <summary>Its own audio clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"the OpenWebRX receiver at {endpoint} has to be told where to listen. Give every "
        + "modem an \"rfFrequency\" and the dial is worked out from them, or set "
        + "\"dialFrequency\" to pin it - unlike a radio there is no dial already set to read off.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio comes from the OpenWebRX receiver at "
        + $"{endpoint}, which is a receiver and has no transmitter.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        $"--device openwebrx: is a receive-only station - the receiver at {endpoint} has no "
        + "transmitter, so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <inheritdoc/>
    public override DeadFeedDevice DeadFeedKind => DeadFeedDevice.OpenWebRx;

    /// <inheritdoc/>
    public override string? SettingsProblem => Problem(_config);

    /// <summary>Why an <c>"openwebrx"</c> section cannot be used as written, or null.</summary>
    internal static string? Problem(OpenWebRxConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        int low = config.SsbLowHz ?? DefaultSsbLowHz;
        int high = config.SsbHighHz ?? DefaultSsbHighHz;
        int nyquist = OpenWebRxProtocol.AudioRate / 2;
        if (low < 0 || high <= low || high > nyquist)
        {
            return $"\"openwebrx\".\"ssbLowHz\" and \"ssbHighHz\" ({low} and {high}) have to rise from 0 "
                + $"or more to at most {nyquist} Hz, the top of the receiver's {OpenWebRxProtocol.AudioRate} Hz audio";
        }

        int halfWidth = config.FmHalfWidthHz ?? DefaultFmHalfWidthHz;
        if (halfWidth <= 0 || halfWidth > nyquist)
        {
            return $"\"openwebrx\".\"fmHalfWidthHz\" ({halfWidth}) has to be above 0 and at most {nyquist} Hz, "
                + $"half the receiver's {OpenWebRxProtocol.AudioRate} Hz audio";
        }

        if (config.StartupGuardMs is < 0)
        {
            return "\"openwebrx\".\"startupGuardMs\" cannot be negative";
        }

        if (config.Gain is double gain && (!double.IsFinite(gain) || gain <= 0))
        {
            return "\"openwebrx\".\"gain\" has to be above 0";
        }

        if (config.Profile is { } profile && string.IsNullOrWhiteSpace(profile))
        {
            return "\"openwebrx\".\"profile\" is empty; leave it out to listen on whatever band the receiver is on";
        }

        return null;
    }

    /// <inheritdoc/>
    public override async Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        StationJournal journal = context.Journal;
        string sideband = context.Sideband;
        double dialHz = context.ReceiveDialHz!.Value;

        OpenWebRxTuning tuning = OpenWebRxTuning.For(
            (long)Math.Round(dialHz), sideband,
            _config?.SsbLowHz ?? DefaultSsbLowHz, _config?.SsbHighHz ?? DefaultSsbHighHz,
            _config?.FmHalfWidthHz ?? DefaultFmHalfWidthHz) with
        {
            OutputRate = context.DspRate,
            Profile = _config?.Profile,
            StartupGuardMs = _config?.StartupGuardMs ?? 1000,
            Gain = (float)(_config?.Gain ?? 1.0),
        };

        if (context.DspRate % OpenWebRxProtocol.AudioRate != 0)
        {
            Console.Error.WriteLine(
                $"\"device\": \"{Spec}\" delivers {OpenWebRxProtocol.AudioRate} Hz audio, and this "
                + $"station's modems run the channel at {context.DspRate} Hz, which is not a whole "
                + "multiple of it. Choose modes from the 12 kHz or 48 kHz families.");
            return DeviceOpening.Refused(2);
        }

        OpenWebRxAudioInput input;
        try
        {
            input = await OpenWebRxAudioInput.OpenAsync(
                endpoint, tuning, journal.ErrorSink, context.Cancellation);
        }
        catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                    or HttpRequestException or IOException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.OpenWebRx(Spec, context.ConfigPath, e));
            return DeviceOpening.Refused(1);
        }

        journal.Write(
            $"audio: {endpoint} {tuning.Modulation.ToUpperInvariant()} at {RfPlan.Mhz(dialHz)}, "
            + $"passband {tuning.LowCutHz} to {tuning.HighCutHz} Hz, "
            + $"{(input.Adpcm ? "ADPCM" : "16-bit")} audio at {OpenWebRxProtocol.AudioRate} Hz"
            + (context.DspRate == OpenWebRxProtocol.AudioRate ? "" : $" -> {context.DspRate} Hz")
            + " (RECEIVE ONLY)");
        if (input.Server is not null)
        {
            journal.Write($"openwebrx: {input.Server}"
                + (input.ReceiverDescription is null ? "" : $", {input.ReceiverDescription}")
                + (input.Band is null ? "" : $", on {input.Band}"));
        }

        if (input.Adpcm)
        {
            journal.Write("openwebrx: the receiver compresses its audio to 4-bit ADPCM, a setting of its "
                + "own that a listener cannot change");
        }

        if (input.RefusedAtStartup is string refusal)
        {
            journal.WriteError(
                $"openwebrx: the receiver is refusing this station for now ({refusal}). The station is "
                + "up and will start hearing audio when the receiver lets it in.");
        }

        if (input.ReceiverDescription is not null)
        {
            context.Waterfall?.SetRadioStatus(input.ReceiverDescription);
        }

        // A receiver that stays unreachable is not something to sit quietly on: exit 1 so the unit
        // restarts and tries afresh, as the UberSDR device does.
        input.Lost += reason =>
        {
            journal.WriteError($"openwebrx: {reason}");
            context.RadioLost();
        };

        return new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(context.DspRate),
            Input = input,
            SessionLive = () => input.SessionLive,
            Tuner = input,
        };
    }
}
