using System.Net.Sockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>rtltcp:</c> kind: an RTL-SDR dongle behind an rtl_tcp server, or OpenWebRX's
/// rtl_tcp-compatible IQ port, demodulated here; receive only.</summary>
internal sealed class RtlTcpDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "rtltcp";

    /// <inheritdoc/>
    public override string Spelling => "rtltcp:HOST[:PORT]";

    /// <inheritdoc/>
    public override bool Matches(string device) => RtlTcpDevice.IsRtlTcp(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new RtlTcpStationDevice(this, device, RtlTcpDevice.Parse(device), settings.RtlTcp);

    /// <inheritdoc/>
    public override string? TransmitTestRefusal =>
        "tx test: refused, this station's audio comes from an rtl_tcp server, an SDR receiver "
        + "with no transmitter - there is nothing here to key";

    /// <inheritdoc/>
    public override string? RigRefusal(string device) =>
        $"\"rig\" is set and \"device\" is \"{device}\", an rtl_tcp receiver, which the station "
        + "tunes itself from the band plan (or, read-only, its operator does) and has no rig to "
        + "control - remove \"rig\".";
}

/// <summary>
/// An rtl_tcp server: its own IQ clock, demodulated by the station's IQ front end, tuned by the
/// station (or, read-only, by whoever runs the server), and no transmitter.
/// </summary>
internal sealed class RtlTcpStationDevice(
    DeviceKind kind, string spec, RtlTcpEndpoint endpoint, RtlTcpConfig? config)
    : StationDevice(kind, spec)
{
    /// <summary>The rate a controlled dongle is asked for unless told otherwise: low enough for a
    /// Pi 3's USB and a busy LAN (2 MB/s), and far more than one channel needs.</summary>
    internal const int DefaultSampleRate = 1_024_000;

    /// <summary>The server this station listens through.</summary>
    public RtlTcpEndpoint Endpoint => endpoint;

    /// <summary>Its own IQ clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"the rtl_tcp receiver at {endpoint} has to be told where to listen. Give every modem an "
        + "\"rfFrequency\" and the dial is worked out from them, or set \"dialFrequency\" to pin it - "
        + "unlike a radio there is no dial already set to read off.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio comes from the rtl_tcp server at {endpoint}, "
        + "an SDR receiver with no transmitter.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        $"--device rtltcp: is a receive-only station - the server at {endpoint} has no "
        + "transmitter, so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <summary>The web receivers' family: an SDR's IQ always carries noise, and a connection
    /// that is up and delivering nothing is a hung stream. While the source is reconnecting, or
    /// dropping a stream at the wrong rate, <see cref="DeviceOpening.SessionLive"/> says so and
    /// the starvation watch waits.</summary>
    public override DeadFeedDevice DeadFeedKind => DeadFeedDevice.UberSdr;

    /// <inheritdoc/>
    public override string? SettingsProblem => Problem(config);

    /// <summary>Why an <c>"rtltcp"</c> section cannot be used as written, or null.</summary>
    internal static string? Problem(RtlTcpConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        if (config.ReadOnly)
        {
            if (config.CentreFrequency is not double centre || !double.IsFinite(centre) || centre <= 0)
            {
                return "\"rtltcp\".\"readOnly\" needs \"centreFrequency\": the Hz the server's profile is "
                    + "pinned to. A read-only server cannot say where it is tuned, so the station has to be told.";
            }

            if (config.SampleRate is not int rate || rate <= 0)
            {
                return "\"rtltcp\".\"readOnly\" needs \"sampleRate\": the samples a second the server's "
                    + "profile is pinned to. A read-only server cannot say its rate, so the station has to be told.";
            }

            if (config.GainDb is not null || config.Ppm is not null || config.RtlAgc is not null)
            {
                return "\"rtltcp\".\"gainDb\", \"ppm\" and \"rtlAgc\" are settings sent to a server, and "
                    + "\"readOnly\" sends nothing; set them on the server, or remove them.";
            }

            if (config.LfoOffsetHz is not null || config.IqSwap is not null)
            {
                return "\"rtltcp\".\"lfoOffsetHz\" and \"iqSwap\" describe a dongle this station tunes "
                    + "itself, and a \"readOnly\" server's profile is already fixed by its own operator; "
                    + "remove them.";
            }
        }
        else
        {
            if (config.CentreFrequency is not null)
            {
                return "\"rtltcp\".\"centreFrequency\" is for a \"readOnly\" server. Without it the station "
                    + "tunes the dongle itself, just below the dial the band plan gives; remove it.";
            }

            if (config.SampleRate is int rate && !RtlTcpProtocol.IsDongleRate(rate))
            {
                return $"\"rtltcp\".\"sampleRate\" ({rate}) is not a rate an RTL dongle runs at: 225001 to "
                    + "300000, or 900001 to 3200000 samples a second (1024000, the default, or 2048000 suit most).";
            }

            if (config.GainDb is double gain && (!double.IsFinite(gain) || gain < 0 || gain > 60))
            {
                return $"\"rtltcp\".\"gainDb\" ({gain}) has to be from 0 to 60 dB; leave it out for the tuner's own gain";
            }

            if (config.Ppm is int ppm && Math.Abs(ppm) > 1000)
            {
                return $"\"rtltcp\".\"ppm\" ({ppm}) is not a crystal error; it has to be within 1000 either way";
            }

            if (config.LfoOffsetHz is double lfo && !double.IsFinite(lfo))
            {
                return "\"rtltcp\".\"lfoOffsetHz\" has to be a frequency";
            }

            if (config.IqSwap == true && config.LfoOffsetHz is null or 0)
            {
                return "\"rtltcp\".\"iqSwap\" is for a converter that inverts the spectrum, and needs "
                    + "\"lfoOffsetHz\", that converter's local oscillator: the dongle is tuned to the "
                    + "oscillator less the centre.";
            }
        }

        if (config.NoDataSeconds is double noData && (!double.IsFinite(noData) || noData <= 0))
        {
            return "\"rtltcp\".\"noDataSeconds\" has to be above 0";
        }

        if (config.Gain is double audioGain && (!double.IsFinite(audioGain) || audioGain <= 0))
        {
            return "\"rtltcp\".\"gain\" has to be above 0";
        }

        double low = config.SsbLowHz ?? 150;
        double high = config.SsbHighHz ?? 3450;
        if (!(low >= 0 && high > low))
        {
            return $"\"rtltcp\".\"ssbLowHz\" and \"ssbHighHz\" ({low} and {high}) have to rise from 0 or more";
        }

        if (config.FmIfBandwidthHz is double ifBandwidth && !(ifBandwidth > 0))
        {
            return "\"rtltcp\".\"fmIfBandwidthHz\" has to be above 0";
        }

        if (config.FmDeviationHz is double deviation && !(deviation > 0))
        {
            return "\"rtltcp\".\"fmDeviationHz\" has to be above 0";
        }

        return null;
    }

    /// <inheritdoc/>
    public override async Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        StationJournal journal = context.Journal;
        double dialHz = context.ReceiveDialHz!.Value;
        string sideband = context.Sideband;
        bool readOnly = config?.ReadOnly ?? false;
        int rate = config?.SampleRate ?? DefaultSampleRate;

        IqDemodulation demodulation = RfPlan.IsFmRadio(sideband) ? IqDemodulation.Fm
            : sideband.Equals("lsb", StringComparison.OrdinalIgnoreCase) ? IqDemodulation.Lsb
            : IqDemodulation.Usb;
        var channel = new IqChannelSpec
        {
            OffsetHz = 0,
            Demodulation = demodulation,
            Name = "rtltcp",
            SsbLowHz = config?.SsbLowHz ?? 150,
            SsbHighHz = config?.SsbHighHz ?? 3450,
            FmIfBandwidthHz = config?.FmIfBandwidthHz ?? 16000,
            FmDeviationHz = config?.FmDeviationHz ?? 3000,
            Gain = (float)(config?.Gain ?? 1.0),
        };
        var source = new RtlTcpSourceOptions
        {
            ReadOnly = readOnly,
            CentreFrequencyHz = config?.CentreFrequency ?? dialHz,
            SampleRate = rate,
            TunerGainDb = config?.GainDb,
            FrequencyCorrectionPpm = config?.Ppm ?? 0,
            RtlAgc = config?.RtlAgc ?? false,
            LfoOffsetHz = config?.LfoOffsetHz ?? 0,
            IqSwap = config?.IqSwap ?? false,
            NoDataTimeout = TimeSpan.FromSeconds(config?.NoDataSeconds ?? 5),
            Journal = journal.ErrorSink,
        };
        var frontEnd = new IqReceiverOptions { DspRate = context.DspRate, Journal = journal.ErrorSink };

        RtlTcpAudioInput input;
        try
        {
            input = await RtlTcpAudioInput.OpenAsync(endpoint, source, channel, frontEnd, dialHz, context.Cancellation);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(readOnly
                ? $"\"device\": \"{Spec}\" cannot serve a dial of {RfPlan.Mhz(dialHz)}: {e.Message}. A read-only "
                    + $"server's profile is centred on {RfPlan.Mhz(source.CentreFrequencyHz)} at {rate} samples a "
                    + "second, which is what \"rtltcp\".\"centreFrequency\" and \"sampleRate\" say; the dial has to "
                    + "fall inside that band, its filter included."
                : $"\"device\": \"{Spec}\" cannot listen on {RfPlan.Mhz(dialHz)}: {e.Message}");
            return DeviceOpening.Refused(2);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            Console.Error.WriteLine(Diagnostic(Spec, context.ConfigPath, e));
            return DeviceOpening.Refused(1);
        }

        double lfoOffsetHz = config?.LfoOffsetHz ?? 0;
        bool iqSwap = config?.IqSwap ?? false;
        string tuning = readOnly
            ? $"read-only, its profile taken as {RfPlan.Mhz(input.Source.CentreFrequencyHz)} at {rate} S/s"
            : $"dongle at {RfPlan.Mhz(input.Source.TunerFrequencyHz)}, {rate} S/s, "
                + (config?.GainDb is double gain ? $"gain {gain:F1} dB" : "tuner AGC")
                + (lfoOffsetHz == 0 ? "" : $", hearing {RfPlan.Mhz(input.Source.CentreFrequencyHz)} through a converter at {RfPlan.Mhz(lfoOffsetHz)}")
                + (iqSwap ? ", spectrum inverted, I/Q swapped" : "");
        string filter = demodulation == IqDemodulation.Fm
            ? $"FM, IF {channel.FmIfBandwidthHz:F0} Hz"
            : $"{demodulation.ToString().ToUpperInvariant()} {channel.SsbLowHz:F0}-{channel.SsbHighHz:F0} Hz";
        journal.Write($"audio: rtl_tcp {endpoint} ({tuning}) -> {filter} at {RfPlan.Mhz(dialHz)}, "
            + $"audio at {context.DspRate} Hz (RECEIVE ONLY)");
        if (readOnly)
        {
            journal.Write("rtltcp: read-only, so nothing is sent to the server. It cannot say when its "
                + "profile changes; a change of rate is caught and dropped, a change of band at the same "
                + "rate only guessed at. Ask its operator to pin the profile.");
        }

        // A server that stays unreachable is not something to sit quietly on: exit 1 so the unit
        // restarts and tries afresh, as the web receivers do.
        input.Lost += reason =>
        {
            journal.WriteError($"rtltcp: {reason}");
            context.RadioLost();
        };

        return new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(context.DspRate),
            Input = input,
            SessionLive = () => input.SessionLive,
            Tuner = input,
            InputLossCount = () => input.Discontinuities,
        };
    }

    /// <summary>An rtl_tcp server that would not stream. Not exit 2: a Pi at the antenna that is
    /// still booting, or a server busy with another client, clears by itself.</summary>
    internal static string Diagnostic(string device, string? configPath, Exception error) =>
        $"""
        cannot listen through "{device}"
          {error.Message}

          {(configPath is null ? "Set by `--device` on the command line." : $"Set by \"device\" in {configPath}")}
          This names an rtl_tcp server (an RTL-SDR dongle behind rtl_tcp, or OpenWebRX's
          rtl_tcp-compatible port), which the station listens to instead of a sound card. Check
          the server is running and listening on that host and port; rtl_tcp serves one client
          at a time and listens on 127.0.0.1 unless started with -a. The rest of its settings
          live in the "rtltcp" section.
          The service will keep retrying: this clears by itself if the server is merely late.
        """;
}
