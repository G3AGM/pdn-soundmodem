using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>The <c>flex:</c> kind: a FlexRadio's DAX audio and its slice PTT.</summary>
internal sealed class FlexDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "flex";

    /// <inheritdoc/>
    public override string Spelling => "flex:RADIO[:SLICE][@STATION]";

    /// <inheritdoc/>
    public override bool Matches(string device) => FlexDevice.IsFlex(device);

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new FlexStationDevice(this, device, FlexDevice.Parse(device));

    /// <summary>Headless (no <c>@station</c>) only: in attach mode SmartSDR owns the slice.</summary>
    public override bool OwnsTheRadio(string device) => FlexDevice.Parse(device).Headless;

    /// <inheritdoc/>
    public override MailcastRadioKind MailcastKindOf(string device) =>
        OwnsTheRadio(device) ? MailcastRadioKind.FlexHeadless : MailcastRadioKind.FlexAttach;

    /// <inheritdoc/>
    public override string? RigRefusal(string device) =>
        $"\"rig\" is set and \"device\" is \"{device}\". A FlexRadio is tuned and keyed "
        + "over its own API, so it needs no rigctld - remove \"rig\".";
}

/// <summary>
/// A FlexRadio: its own DAX sample clock (24/48 kHz auto-picked from the DSP rate), its own
/// keying, and a transmit filter the daemon can read back. Headless (no <c>@station</c>) is the
/// deployment where the daemon owns the radio, and so the only one where it sets the dial and
/// the filters; in attach mode SmartSDR owns the slice and setting them would be fighting it.
/// </summary>
internal sealed class FlexStationDevice(DeviceKind kind, string spec, FlexDevice.FlexSpec flexSpec)
    : StationDevice(kind, spec)
{
    /// <summary>Below this the transmitter is not keyed and the readout is meaningless.</summary>
    private const double TransmitReadoutFloorWatts = 0.1;

    /// <summary>The parsed device string.</summary>
    public FlexDevice.FlexSpec FlexSpec => flexSpec;

    /// <summary>Its own DAX clock: <c>--capture-rate</c> is an ALSA concept.</summary>
    public override bool CaptureRateApplies => false;

    /// <inheritdoc/>
    public override bool OwnsTheRadio => flexSpec.Headless;

    /// <inheritdoc/>
    public override bool SelfTunes => flexSpec.Headless;

    /// <summary>
    /// The Flex owns keying (the slice PTT is an API command), so a conflicting <c>--ptt</c> or
    /// configured PTT is refused - matching how <c>--device flex:</c> implicitly keys the radio.
    /// </summary>
    public override string? PttRefusal =>
        "--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)";

    /// <inheritdoc/>
    public override bool ReportsTransmitFilter => true;

    /// <summary>
    /// <c>flex:mock</c> counts as a bench device, not a Flex: its DAX-RX path deliberately
    /// delivers nothing between injected frames, which a starvation watch would read as a dead
    /// radio 30 s into every idle bench session. Decided as <see cref="FlexDevice.OpenAsync"/>
    /// decides to start the mock.
    /// </summary>
    public override DeadFeedDevice DeadFeedKind =>
        flexSpec.RadioSpec.Equals("mock", StringComparison.OrdinalIgnoreCase)
            ? DeadFeedDevice.WavLoop
            : DeadFeedDevice.Flex;

    /// <summary>The <see cref="FlexRuntime"/> closes the DAX streams when it is disposed.</summary>
    public override bool ClosesItsOwnStreams => true;

    /// <summary>
    /// The local channel audio stream's band (issue #584) on this radio: a connection can ask for
    /// a band to hear, and while it is asking, a headless slice's receive filter widens to cover
    /// it - the same thing bring-up already does for the configured modems and the mailcast
    /// signal, just live rather than once, and never touching the filter until a connection asks.
    /// Put back to exactly what bring-up set the moment the last such connection goes.
    /// </summary>
    /// <remarks>
    /// Headless only: in attach mode the slice and its filter are SmartSDR's, and nothing is
    /// installed. And only with a filter bring-up read back; without one (the receive filter
    /// warning at bring-up said why) there is nothing known to widen from or put back to, and
    /// guessing is worse than leaving it.
    /// </remarks>
    /// <returns>What now answers the stream's band, or null where nothing does.</returns>
    internal static FlexStreamFilter? StreamFilterFor(
        FlexDevice.FlexSpec flexSpec,
        M0LTE.Flex.FlexStation station,
        (int LowHz, int HighHz)? baseline,
        WaterfallWebServer waterfallServer,
        CancellationToken cancellation)
    {
        if (!flexSpec.Headless || baseline is not (int baseLowHz, int baseHighHz))
        {
            return null;
        }

        // The slice and the session are read when a command goes, so a rebuilt slice is followed.
        var streamFilter = new FlexStreamFilter(
            (baseLowHz, baseHighHz),
            () => station.SliceIndex,
            command => station.Client.SendCommandExpectOkAsync(command, cancellation),
            Console.WriteLine,
            Console.Error.WriteLine);

        // A rebuilt slice comes up on bring-up's filter; a standing widening goes back on it.
        station.HealthChanged += report =>
        {
            if (report.Health == M0LTE.Flex.FlexStationHealth.Healthy)
            {
                streamFilter.SliceRebuilt();
            }
        };

        waterfallServer.ReceiveBandRequested = streamFilter.Request;
        return streamFilter;
    }

    /// <inheritdoc/>
    public override async Task<DeviceOpening> OpenAsync(DeviceOpenContext context)
    {
        int dspRate = context.DspRate;
        FlexTuning flexTuning = context.FlexTuning;
        IReadOnlyList<TransmitFilterPlan.Band> txBands = context.TransmitBands;
        SoundModemChannel channel = context.Channel;
        WaterfallWebServer? waterfallServer = context.Waterfall;
        FlexRuntime flex;
        M0LTE.Flex.FlexMeters? flexMeters = null;

        try
        {
            flex = await FlexDevice.OpenAsync(Spec, dspRate, context.FlexPacketBuffer, flexTuning, context.Cancellation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The one device path that had no catch: a radio still booting at daemon start
            // escaped as a raw stack trace with an abort exit code, instead of the
            // DeviceDiagnostics message and the exit 1 (retry) contract the ALSA and UberSDR
            // paths honour. Broad on purpose - whatever the radio library throws, the answer
            // is the same: say what to check, and let the unit retry.
            Console.Error.WriteLine(DeviceDiagnostics.Flex(Spec, context.ConfigPath, e));
            return DeviceOpening.Refused(1);
        }

        IAudioInput input = flex.Input;

        // Arbitrated keying: ordinary queued frames also defer BEFORE they are rendered while
        // another station transmits - the same polite hold ARDOP sessions get - rather than
        // discovering the busy radio inside Key(). Composed over whatever inhibit is already
        // installed (ARDOP's ARQ gate lands earlier), never instead of it. ARDOP's own bursts
        // bypass the inhibit by design and rely on the in-Key wait alone.
        if (flex.Ptt is M0LTE.Flex.FlexArbitratedPtt arbitratedPtt)
        {
            Func<bool>? priorInhibit = channel.TransmitInhibit;
            channel.TransmitInhibit = () =>
                (priorInhibit?.Invoke() ?? false) || arbitratedPtt.AnotherStationTransmitting;
            Console.WriteLine(
                "flex: arbitrated keying - every keyup waits out other stations, re-asserts the "
                + "transmit filter and the TX slice, and is confirmed against the interlock");
        }

        string flexModeDesc = flexSpec.Headless
            ? $"headless {flexTuning.Frequency} MHz {flexTuning.Antenna} {flexTuning.Mode}"
            : $"attach station '{flexSpec.Station}'";
        Console.WriteLine(
            $"audio: {Spec} DAX {input.SampleRate} Hz -> {dspRate} Hz "
            + $"(slice {flexSpec.SliceLetter}, dax {flexTuning.DaxChannel}, {flexModeDesc})");
        if (flex.Station.TuneWarning is string tuneWarning)
        {
            Console.Error.WriteLine($"flex: {tuneWarning}");
        }

        // Two headless instances that both take the default DAX channel displace each other, which
        // is exactly how this station lost its slice for six days (docs/dev/archive/flex-integration.md §12).
        // Said at bring-up, while it can still be acted on.
        if (flex.Station.DaxChannelWarning is string daxWarning)
        {
            Console.Error.WriteLine($"flex: {daxWarning}");
        }

        // The radio's global transmit filter, read back at bring-up (Flex 0.7.0) - it, not the
        // slice, limits transmitted DAX audio bandwidth, and it is whatever last touched the radio
        // (a 300 Hz CW filter would silently crush a 3 kHz mode). We deliberately never set it;
        // reporting it makes a stale value visible. Headless only - attach leaves it to SmartSDR.
        // A radio that reboots, or a network that blips, ends the session - and nothing used to
        // notice. The modem then sat with a dead socket: no audio, no waterfall, and nothing said
        // why. Stop instead, with exit 1 so the unit restarts and rediscovers the radio rather
        // than staying down (exit 2 is reserved for "your configuration is wrong", which a restart
        // could never fix).
        flex.Station.Client.Disconnected += () =>
        {
            Console.Error.WriteLine(
                "flex: the radio's session ended - rebooted, dropped off the network, or closed the "
                + "connection. Stopping so the service restarts and rediscovers it.");
            context.RadioLost();
        };

        // Losing the SLICE is not the same as losing the session, and it used to be invisible. The
        // socket stays up, the modem keeps queueing, and every keyup is accepted by the radio and
        // does nothing - a station deaf and mute with a healthy-looking connection. Say so once,
        // clearly, and rebuild.
        flex.Station.SliceLost += check =>
            Console.Error.WriteLine($"flex: lost our slice - {check.Detail}");

        // State the starting point explicitly. The station reaches Healthy inside the bring-up
        // above, so the HealthChanged subscription below is attached after that first transition
        // has already fired and can only ever report LATER ones. Without this line the journal
        // never says that ownership was checked at all, which is exactly the reassurance a
        // six-day silent outage taught us to want.
        Console.WriteLine(
            $"flex: slice {flex.Station.SliceIndex} health {flex.Station.Health} at bring-up "
            + $"({flex.Station.VerifyOwnership().Detail switch
            {
                "" => "owned by this client",
                string detail => detail,
            }})");

        flex.Station.HealthChanged += report =>
        {
            switch (report.Health)
            {
                case M0LTE.Flex.FlexStationHealth.Healthy:
                    Console.WriteLine($"flex: slice healthy - {report.Detail}");
                    break;

                case M0LTE.Flex.FlexStationHealth.Contended:
                    // Deliberately not an exit. Restarting would recreate the slice, which is the
                    // same move the other client is making, and two daemons rebuilding at each
                    // other churns the radio for both. Stay up, stay off the air, and be loud.
                    Console.Error.WriteLine(
                        $"flex: STANDING DOWN - {report.Detail} This station is now off the air and "
                        + "will not retake the slice. Check what else is connected to the radio "
                        + "(SmartSDR, a capture tool, a second modem), stop it, and restart this "
                        + "service.");
                    break;

                case M0LTE.Flex.FlexStationHealth.Disposed:
                    // Shutdown. The daemon already says it is stopping; "Disposed - disposed"
                    // adds nothing but a line to read past.
                    break;

                case M0LTE.Flex.FlexStationHealth.Recovering:
                case M0LTE.Flex.FlexStationHealth.SliceLost:
                case M0LTE.Flex.FlexStationHealth.Unbound:
                default:
                    Console.Error.WriteLine($"flex: {report.Health} - {report.Detail}");
                    break;
            }
        };

        // Rebuild off the status thread: RecoverAsync serialises itself and returns immediately
        // when the slice is already ours, so a duplicate trigger is free.
        M0LTE.Flex.FlexStation flexStation = flex.Station;
        flexStation.SliceLost += lostCheck =>
        {
            _ = lostCheck;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        M0LTE.Flex.FlexRecoveryResult result =
                            await flexStation.RecoverAsync(context.Cancellation);
                        if (!result.Recovered)
                        {
                            Console.Error.WriteLine(
                                $"flex: could not rebuild the slice after {result.Attempts} "
                                + $"attempt(s) - {result.Detail}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                    }
                },
                CancellationToken.None);
        };

        // The radio's frequency reference, into the waterfall's top bar and kept current. Only a
        // Flex reports one; a soundcard station shows nothing rather than an empty label.
        if (waterfallServer is not null)
        {
            void PublishReference(M0LTE.Flex.FlexReferenceStatus reference) =>
                waterfallServer.SetRadioStatus(reference.Describe());

            PublishReference(flex.Station.Client.Reference);
            flex.Station.Client.ReferenceChanged += PublishReference;
            Console.WriteLine($"flex: reference {flex.Station.Client.Reference.Describe()}");
        }

        // What the transmitter is actually doing, live, in the page's top bar. The meters are the
        // radio's own - forward power and SWR - so this reports the transmission rather than what we
        // asked for, which is the difference that matters when an antenna is wrong.
        if (waterfallServer is not null)
        {
            try
            {
                M0LTE.Flex.FlexMeters txMeters =
                    await M0LTE.Flex.FlexMeters.SubscribeAsync(flex.Station.Client);
                flexMeters = txMeters;
                txMeters.Updated += reading =>
                {
                    if (!reading.Descriptor.Name.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase))
                    {
                        return;   // one update per keyed sample is plenty; SWR is read alongside it
                    }

                    double watts = M0LTE.Flex.FlexMeters.DbmToWatts(reading.Value);
                    if (watts < TransmitReadoutFloorWatts)
                    {
                        // Key-up: the display averages what it was given and holds that average,
                        // because a packet burst is over before an operator can read a live figure.
                        waterfallServer.SetTransmitReading(null, null);
                        return;
                    }

                    waterfallServer.SetTransmitReading(watts, txMeters.SwrFromPowers());
                };
            }
            catch (Exception e) when (e is M0LTE.Flex.FlexProtocolException or IOException)
            {
                // A station that cannot read its meters still transmits perfectly well.
                Console.Error.WriteLine($"flex: no transmit metering - {e.Message}");
            }
        }

        // Always reported, set or not: an inherited power shapes every transmission just as much as
        // a configured one, and it is the number the operator will be asked about on the air.
        if (flex.Station.RfPowerApplied is int rfPower)
        {
            double watts = rfPower / 100.0 * FlexDevice.PaWatts;
            string ceiling = flex.Station.MaxPowerLevel is int max
                ? $", limit {max / 100.0 * FlexDevice.PaWatts:0.#} W"
                : "";
            string source = flexTuning.TxPowerWatts is null ? " (radio's own setting)" : "";
            Console.WriteLine($"flex: transmit power {watts:0.#} W{ceiling}{source}");
        }

        if (flex.Station.TransmitFilter is (int txFilterLow, int txFilterHigh))
        {
            Console.WriteLine($"flex: transmit filter {txFilterLow}..{txFilterHigh} Hz (radio global - limits TX audio bandwidth)");

            // What the filter passes is checked against where the modems actually are, rather than
            // assumed: a modem outside it transmits a truncated signal, or nothing at all, and does
            // so silently. The high cut we ask for can still come back narrower - it is a radio-wide
            // setting and the radio has the last word - and in attach mode we never set it at all.
            foreach (TransmitFilterPlan.Band band in txBands
                         .Where(b => b.LowHz < txFilterLow || b.HighHz > txFilterHigh))
            {
                // Only the high cut is settable through the station API, so a modem under the low
                // edge is something only the operator can fix. Telling someone to move a modem that
                // has nowhere to go - the freedv-*/ms110d-* centres are pinned by their specs - is
                // worse than saying nothing.
                string remedy = band.LowHz < txFilterLow
                    ? "The low cut is not settable from here; widen it on the radio."
                    : ModemCatalog.AcceptsCentreFrequency(band.Mode)
                        ? "Widen the high cut on the radio, or move the modem down the passband."
                        : "Widen the high cut on the radio - this mode's centre is fixed by its spec.";
                Console.Error.WriteLine(
                    $"flex: WARNING - modem {band.SubChannel} ({band.Mode}) occupies "
                    + $"{band.LowHz:F0}-{band.HighHz:F0} Hz, outside the radio's "
                    + $"{txFilterLow}..{txFilterHigh} Hz transmit filter - it will be clipped. "
                    + remedy);
            }
        }

        if (flex.Station.ReceiveFilter is (int rxFilterLow, int rxFilterHigh))
        {
            Console.WriteLine(
                $"flex: slice receive filter {rxFilterLow}..{rxFilterHigh} Hz (what the modems can hear)");

            // Deaf rather than clipped, and just as quiet about it: a modem outside the slice's filter
            // decodes nothing at all and looks exactly like a dead band.
            foreach (TransmitFilterPlan.Band band in txBands
                         .Where(b => b.LowHz < rxFilterLow || b.HighHz > rxFilterHigh))
            {
                Console.Error.WriteLine(
                    $"flex: WARNING - modem {band.SubChannel} ({band.Mode}) occupies "
                    + $"{band.LowHz:F0}-{band.HighHz:F0} Hz, outside the slice's "
                    + $"{rxFilterLow}..{rxFilterHigh} Hz receive filter - it will hear nothing there.");
            }
        }

        if (flex.Station.ReceiveFilterWarning is string receiveFilterWarning)
        {
            // The radio's ceiling on receive width is not measured, so this is how a radio that will
            // not go as wide as asked says so, rather than the modem quietly going deaf.
            Console.Error.WriteLine($"flex: WARNING - {receiveFilterWarning}");
        }

        if (waterfallServer is not null)
        {
            StreamFilterFor(flexSpec, flex.Station, flex.Station.ReceiveFilter, waterfallServer, context.Cancellation);
        }

        return new DeviceOpening
        {
            Ptt = flex.Ptt,
            Playback = flex.Output,
            Input = input,
            Flex = flex,
            FlexMeters = flexMeters,
        };
    }
}
