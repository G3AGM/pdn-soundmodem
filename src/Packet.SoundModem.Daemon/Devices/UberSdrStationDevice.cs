using System.Net.WebSockets;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>ubersdr:</c> kind: an UberSDR web receiver's IQ stream, receive only.</summary>
internal sealed class UberSdrDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "ubersdr";

    /// <inheritdoc/>
    public override string Spelling => "ubersdr:INSTANCE";

    /// <inheritdoc/>
    public override bool Matches(string device) => UberSdrDevice.IsUberSdr(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new UberSdrStationDevice(this, device, UberSdrDevice.Parse(device), settings);

    /// <inheritdoc/>
    public override MailcastRadioKind MailcastKindOf(string device) => MailcastRadioKind.UberSdr;

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
        // Tom's decision of 2026-09-04, and the sentence says why rather than just refusing.
        $"\"publish\" on \"device\": \"{device}\", which is somebody else's public "
        + "web receiver. A receiver like that is already on the monitor site in its own "
        + "right, so relaying it a second time through this daemon would show one "
        + "operator's antenna twice under two names and spend that receiver's daily "
        + "listening allowance on the site's behalf without the site knowing. Publish "
        + "from a station with a radio of its own; to have a say about which receivers "
        + "the site lists, use the site's own \"monitor\".\"allow\" and \"deny\".";
}

/// <summary>
/// An UberSDR web receiver: its own 48 kHz IQ clock, tuned by the station to the band plan's
/// dial, and no transmitter at all.
/// </summary>
internal sealed class UberSdrStationDevice(
    DeviceKind kind, string spec, UberSdrEndpoint uberSdrEndpoint, DeviceSettings settings)
    : StationDevice(kind, spec)
{
    private readonly UberSdrConfig? _uberSdrConfig = settings.UberSdr;

    /// <summary>The instance this station listens through.</summary>
    public UberSdrEndpoint Endpoint => uberSdrEndpoint;

    /// <summary>Its own IQ clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool SelfTunes => true;

    /// <inheritdoc/>
    public override string? NoReceiveDialRefusal =>
        $"the UberSDR instance at {uberSdrEndpoint} has to be told where to listen. Give every "
        + "modem an \"rfFrequency\" and the dial is worked out from them, or set "
        + "\"dialFrequency\" to pin it - unlike a radio there is no dial already set to read off.";

    /// <inheritdoc/>
    public override string? ReceiveOnlyReason =>
        $"this station receives only: its audio comes from the UberSDR instance at "
        + $"{uberSdrEndpoint}, which is a receiver and has no transmitter.";

    /// <inheritdoc/>
    public override string? PttRefusal =>
        $"--device ubersdr: is a receive-only station - the instance at {uberSdrEndpoint} has no "
        + "transmitter, so there is nothing for a PTT line to key. Remove \"ptt\".";

    /// <inheritdoc/>
    public override DeadFeedDevice DeadFeedKind => DeadFeedDevice.UberSdr;

    /// <inheritdoc/>
    public override string? SettingsProblem
    {
        get
        {
            if (_uberSdrConfig?.OnDemand == true)
            {
                if (_uberSdrConfig.LingerSeconds < 0)
                {
                    return "\"ubersdr\".\"lingerSeconds\" cannot be negative";
                }

                if (!settings.HasWaterfall)
                {
                    return "\"ubersdr\".\"onDemand\" needs a \"waterfall\" section: the page's viewers are "
                        + "what asks for the receiver, and without one the station would never hear anything";
                }
            }

            return null;
        }
    }

    /// <inheritdoc/>
    public override async Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        UberSdrConfig? uberSdrConfig = _uberSdrConfig;
        StationJournal stationJournal = context.Journal;
        int dspRate = context.DspRate;
        double? receiveDialHz = context.ReceiveDialHz;
        string planSideband = context.Sideband;

        // A web receiver hands this daemon single-sideband IQ and nothing else, so "fm" here is not
        // a radio it can be: taken as USB, as everything that is not LSB is below, it would
        // demodulate the wrong thing and say nothing about it.
        if (RfPlan.IsFmRadio(planSideband))
        {
            Console.Error.WriteLine(
                $"\"sideband\": \"fm\" cannot be served by {uberSdrEndpoint}: a web receiver is an "
                + "SSB receiver, and this station would be demodulating one sideband of an FM "
                + "signal. Point \"device\" at a sound card fed by the FM radio instead.");
            return DeviceOpening.Refused(2);
        }

        var uberSdrTuning = new UberSdrTuning
        {
            // The receiver is tuned to the dial itself, so the suppressed carrier lands at DC in the
            // IQ and the demodulator's own NCO has nothing left to do.
            FrequencyHz = (int)Math.Round(receiveDialHz!.Value),
            Sideband = planSideband.Equals("lsb", StringComparison.OrdinalIgnoreCase)
                ? Sideband.Lower
                : Sideband.Upper,
            OutputRate = dspRate,
            Mode = uberSdrConfig?.Mode ?? "iq48",
            Password = uberSdrConfig?.Password,
            SsbLowHz = uberSdrConfig?.SsbLowHz ?? 150,
            SsbHighHz = uberSdrConfig?.SsbHighHz ?? 3450,
            StartupGuardMs = uberSdrConfig?.StartupGuardMs ?? 1000,
            Gain = (float)(uberSdrConfig?.Gain ?? 1.0),
        };

        string audioBanner =
            $"audio: {uberSdrEndpoint} {uberSdrTuning.Mode} IQ at {RfPlan.Mhz(receiveDialHz.Value)} -> "
            + $"{planSideband.ToUpperInvariant()} {uberSdrTuning.SsbLowHz:F0}-{uberSdrTuning.SsbHighHz:F0} Hz "
            + $"audio at {dspRate} Hz (RECEIVE ONLY";
        ConnectionResponse uberSdrConnection;
        string? uberSdrReceiver;
        IAudioInput input;
        Func<bool> uberSdrSessionLive;
        Func<long> uberSdrLoss;

        if (uberSdrConfig?.OnDemand == true)
        {
            // A public monitor on somebody else's receiver: the session exists only while a browser
            // has the waterfall open, and is held for the linger after the last one leaves. The
            // pre-flight still runs here, so a wrong host or a refused IQ mode is still an error
            // at start-up; but a receiver that is merely down is not fatal - the page stays up and
            // says so, and the input keeps trying for as long as anyone is waiting.
            OnDemandUberSdrInput onDemand;
            try
            {
                // Its phase lines are this station's, so they go out through the station's journal
                // and pick up its tag when it has one.
                onDemand = await OnDemandUberSdrInput.OpenAsync(
                    uberSdrEndpoint, uberSdrTuning, TimeSpan.FromSeconds(uberSdrConfig.LingerSeconds),
                    stationJournal.ErrorSink, context.Cancellation);
            }
            catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                        or HttpRequestException or IOException)
            {
                Console.Error.WriteLine(DeviceDiagnostics.UberSdr(Spec, context.ConfigPath, e));
                return DeviceOpening.Refused(1);
            }

            input = onDemand;
            uberSdrSessionLive = () => onDemand.SessionLive;
            uberSdrLoss = () => onDemand.Discontinuities;
            uberSdrConnection = onDemand.Connection;
            uberSdrReceiver = onDemand.ReceiverDescription;
            stationJournal.Write($"{audioBanner}, on demand: connected while the waterfall has a viewer, "
                + $"held {uberSdrConfig.LingerSeconds} s after the last leaves)");

            // The page shows the input's own sentence for what it is doing, and credits the
            // receiver whether or not a session is up. The viewer count flows the other way.
            Packet.SoundModem.Waterfall.WaterfallWebServer waterfallServer = context.Waterfall!;
            waterfallServer.SetReceiver(uberSdrReceiver, uberSdrEndpoint.PublicUrl);
            waterfallServer.SetRadioStatus(onDemand.Status);
            onDemand.PhaseChanged += (_, sentence) => waterfallServer.SetRadioStatus(sentence);
            waterfallServer.ViewersChanged += onDemand.SetViewers;
        }
        else
        {
            UberSdrAudioInput uberSdr;
            try
            {
                uberSdr = await UberSdrAudioInput.OpenAsync(
                    uberSdrEndpoint, uberSdrTuning, stationJournal.ErrorSink, context.Cancellation);
            }
            catch (Exception e) when (e is InvalidOperationException or WebSocketException
                                        or HttpRequestException or IOException)
            {
                Console.Error.WriteLine(DeviceDiagnostics.UberSdr(Spec, context.ConfigPath, e));
                return DeviceOpening.Refused(1);
            }

            input = uberSdr;
            uberSdrSessionLive = () => uberSdr.SessionLive;
            uberSdrLoss = () => uberSdr.Discontinuities;
            uberSdrConnection = uberSdr.Connection;
            uberSdrReceiver = uberSdr.ReceiverDescription;
            stationJournal.Write($"{audioBanner})");
            if (uberSdrReceiver is not null)
            {
                context.Waterfall?.SetRadioStatus(uberSdrReceiver);
            }

            // A receiver that stays unreachable is not something to sit quietly on. Exit 1 so the
            // unit restarts and tries afresh, exactly as for a Flex whose session dies (exit 2 is
            // reserved for "your configuration is wrong", which restarting could never fix).
            uberSdr.Lost += reason =>
            {
                stationJournal.WriteError($"ubersdr: {reason}");
                context.RadioLost();
            };
        }

        if (uberSdrReceiver is not null)
        {
            stationJournal.Write($"ubersdr: {uberSdrReceiver}");
        }

        if (uberSdrConnection.RefusedForNow)
        {
            stationJournal.WriteError(
                "ubersdr: the receiver is refusing this address for now "
                + $"({uberSdrConnection.Reason ?? "daily listening allowance exhausted"}). The station "
                + "is up and will start hearing audio when the receiver lets us back in.");
        }
        else
        {
            stationJournal.Write(
                $"ubersdr: session limit {uberSdrConnection.MaxSessionTime} s - the stream is picked up "
                + "again each time the receiver ends one");
        }

        return new DeviceOpening
        {
            Ptt = new NullPtt(),
            Playback = new NullAudioOutput(dspRate),
            Input = input,
            SessionLive = uberSdrSessionLive,

            // What the channel audio stream marks as a gap: a session picked up again, or audio
            // dropped because the station fell behind. A Read that comes back empty is not one;
            // the input buffers the stream and the audio arrives late and whole.
            InputLossCount = uberSdrLoss,
        };
    }
}
