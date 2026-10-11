using M0LTE.Flex;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.FlexRadio;

/// <summary>The Flex triplet a channel runs through: the DAX-RX input, the DAX-TX output
/// (already wrapped to the DSP rate), the slice PTT, plus the shared station (and the
/// in-process mock, when <c>flex:mock</c>).</summary>
public sealed class FlexRuntime : IAsyncDisposable
{
    internal FlexRuntime(
        MockFlexRadio? mock, FlexStation station,
        IAudioInput input, IAudioOutput output, IPttControl ptt)
    {
        Mock = mock;
        Station = station;
        Input = input;
        Output = output;
        Ptt = ptt;
    }

    /// <summary>The in-process mock radio, when the device is <c>flex:mock</c>.</summary>
    public MockFlexRadio? Mock { get; }

    /// <summary>The station (shared session + DAX stream ids).</summary>
    public FlexStation Station { get; }

    /// <summary>The DAX-RX audio source (at the DAX rate).</summary>
    public IAudioInput Input { get; }

    /// <summary>The DAX-TX audio sink (at the DSP rate - upsampled internally when needed).</summary>
    public IAudioOutput Output { get; }

    /// <summary>The slice PTT.</summary>
    public IPttControl Ptt { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        (Input as IDisposable)?.Dispose();
        (Output as IDisposable)?.Dispose();
        // The arbitrated PTT holds a status-stream subscription; the plain one holds nothing.
        (Ptt as IDisposable)?.Dispose();
        await Station.DisposeAsync().ConfigureAwait(false);
        if (Mock is not null)
        {
            await Mock.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Flex slice/DAX parameters the daemon passes to the bring-up. The
/// frequency/antenna/mode configure the <b>headless</b> slice the daemon creates (ignored in
/// attach mode - SmartSDR owns the slice there); <see cref="DaxChannel"/> applies to
/// <b>both</b> paths (the DAX channel the client claims). Defaults match
/// docs/dev/archive/flex-integration.md §8 (14.100000 MHz / ANT1 / DIGU / DAX 1).</summary>
public sealed record FlexTuning
{
    /// <summary>Slice frequency (MHz, six-decimal Flex form). Default "14.100000".
    /// Headless only.</summary>
    public string Frequency { get; init; } = "14.100000";

    /// <summary>RX/TX antenna. Default "ANT1". Headless only.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>Slice demod mode. Default "DIGU" (a data mode). Headless only.</summary>
    public string Mode { get; init; } = "DIGU";

    /// <summary>
    /// Transmit-filter high cut in Hz; null leaves whatever the radio already had. Headless only.
    /// </summary>
    /// <remarks>
    /// The transmit filter is a <b>global, persistent</b> radio setting, not a slice one - it is
    /// whatever last touched the radio, so a previous session's narrow filter will quietly
    /// truncate the top of a wide band plan. Stating it at bring-up is the only way to know what
    /// it is. Only the high cut is settable through the station API; the low cut and the receive
    /// filter are not, so a plan needing those changed has to be set on the radio.
    /// </remarks>
    public int? TransmitFilterHighHz { get; init; }

    /// <summary>
    /// The slice's receive-filter edges in Hz; null leaves the slice's own. Headless only.
    /// </summary>
    /// <remarks>
    /// The receive half of <see cref="TransmitFilterHighHz"/>, and unlike it a <b>slice</b> setting
    /// rather than a global one, so it goes away with the slice instead of persisting on the radio.
    /// A slice on an ordinary data filter delivers nothing above ~3 kHz to the modems however wide
    /// the transmit filter is opened - which is the half of the problem that cannot be seen from
    /// the transmit side. The radio's limit on receive width is unmeasured, so the station reads it
    /// back and warns rather than assuming the request took.
    /// </remarks>
    public int? ReceiveFilterLowHz { get; init; }

    /// <inheritdoc cref="ReceiveFilterLowHz" />
    public int? ReceiveFilterHighHz { get; init; }

    /// <summary>The DAX channel the client claims (both headless and attach). Default "1". A
    /// headless client sharing a box with a running SmartSDR must pick a channel SmartSDR is not
    /// using (SmartSDR grabs DAX 1) - see docs/dev/archive/flex-integration.md §8.</summary>
    public string DaxChannel { get; init; } = "1";

    /// <summary>Transmit power in watts. Null leaves the radio's own setting alone.</summary>
    public double? TxPowerWatts { get; init; }

    /// <summary>Station name to register with the radio (headless only, best-effort): what
    /// per-station state and another operator's diagnostics call this client. Null sends
    /// nothing. The daemon defaults it to "pdn-soundmodem" so two transmitting clients on
    /// one radio stop both being an anonymous "Flex".</summary>
    public string? StationName { get; init; }

    /// <summary>
    /// Key through <see cref="FlexArbitratedPtt"/> instead of <see cref="FlexPtt"/>: every
    /// keyup waits for the radio to be quiet, re-asserts the transmit filter and the TX
    /// slice, and only believes a keyup the radio confirms - for a radio shared with another
    /// transmitting client (a test instance, the sm-ota harness). Default false until the
    /// multi-client hardware probes pass (docs/dev/archive/flex-integration.md § Shared-PA probes);
    /// the sole-owner path is bit-for-bit what it always was.
    /// </summary>
    public bool Arbitration { get; init; }

    /// <summary>
    /// The timings <see cref="Arbitration"/> keys with, in place of M0LTE.Flex's defaults. Its
    /// transmit filter is ignored: the keyup always re-asserts <see cref="TransmitFilterHighHz"/>.
    /// Not configurable, and internal on purpose: the tests set a confirm window that a starved
    /// thread pool cannot outlast, because against <c>flex:mock</c> the radio's confirmation
    /// always comes and only its arrival time varies (#668).
    /// </summary>
    internal FlexPttArbitrationOptions? ArbitrationTimings { get; init; }

    /// <summary>
    /// Bring the station up for receive only: never write the radio's global transmit state
    /// (transmit audio source, transmit filter, RF power), and never contend for a slice.
    /// </summary>
    /// <remarks>
    /// For a capture or monitoring instance sharing a radio with a transmitting station. Those
    /// three settings are global and persistent rather than per-slice, so a receive-only client
    /// running the normal bring-up silently rewrites what every other client transmits with, and
    /// the change outlives its process. It also pairs with
    /// <see cref="M0LTE.Flex.FlexContentionPolicy.Never"/>: a monitor that rebuilds a slice it
    /// has lost is competing with the station that took it, which is precisely the fight worth
    /// not having.
    /// </remarks>
    public bool ReceiveOnly { get; init; }
}

/// <summary>
/// Parses <c>--device flex:&lt;radio&gt;[:slice][@station]</c> and opens the Flex triplet: a
/// shared <see cref="FlexClient"/> feeding a <see cref="FlexAudioInput"/>, a
/// <see cref="FlexAudioOutput"/> (wrapped in an <see cref="Channel.UpsamplingAudioOutput"/>
/// for the 12 kHz modes) and a <see cref="FlexPtt"/>. <c>radio</c> is <c>discover</c>, an IP
/// (<c>host[:port]</c>), a discovery spec (<c>serial=…</c>/<c>name=…</c>), or <c>mock</c>
/// (an in-process fake). <b>Selection policy:</b> with no <c>@station</c> the daemon owns the
/// radio and brings it up <b>headless</b> (register as a GUI client, create its own slice -
/// the "pdn at the radio, no SmartSDR" deployment, the default). A trailing <c>@station</c>
/// selects <b>attach</b> mode: coexist with a running SmartSDR by binding that station's
/// existing slice. See docs/dev/archive/flex-integration.md §4/§8.
/// </summary>
public static class FlexDevice
{
    private const string Prefix = "flex:";

    /// <summary>True when <paramref name="device"/> selects a FlexRadio.</summary>
    public static bool IsFlex(string device) =>
        device.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The parsed radio spec, slice letter and (attach-only) station.</summary>
    /// <param name="RadioSpec">The radio portion (<c>discover</c>/IP/<c>serial=…</c>/<c>mock</c>).</param>
    /// <param name="SliceLetter">The slice letter (default "A").</param>
    /// <param name="Station">The SmartSDR station to attach to (from a <c>@station</c> suffix),
    /// or null for the headless default.</param>
    public readonly record struct FlexSpec(string RadioSpec, string SliceLetter, string? Station)
    {
        /// <summary>True when no <c>@station</c> was given - the daemon owns the radio and
        /// brings it up headless.</summary>
        public bool Headless => Station is null;
    }

    /// <summary>Splits a <c>flex:</c> device string into its radio, slice and station parts. A
    /// trailing <c>@station</c> (anywhere after the radio) selects attach mode and names the
    /// SmartSDR station; a trailing single letter A-H is the slice; everything else is the
    /// radio.</summary>
    public static FlexSpec Parse(string device)
    {
        string rest = device[Prefix.Length..];

        string? station = null;
        int at = rest.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            station = rest[(at + 1)..];
            rest = rest[..at];
        }

        string[] segments = rest.Split(':');
        if (segments.Length >= 2 && IsSliceLetter(segments[^1]))
        {
            return new FlexSpec(string.Join(':', segments[..^1]), segments[^1].ToUpperInvariant(), station);
        }

        return new FlexSpec(rest, "A", station);
    }

    /// <summary>Opens the Flex triplet for the given device string and DSP rate.</summary>
    /// <param name="device">The <c>flex:…</c> device string.</param>
    /// <param name="dspRate">The channel DSP rate (picks the DAX transport).</param>
    /// <param name="packetBuffer">DAX-RX reorder-ring depth.</param>
    /// <param name="tuning">Headless slice params (frequency/antenna/mode); null = defaults.
    /// Ignored in attach mode.</param>
    /// <param name="cancellation">Cancels the connect + bring-up.</param>
    /// <exception cref="InvalidOperationException">Headless bring-up could not point the
    /// transmitter at DAX (<see cref="FlexStation.TransmitSourceWarning"/>) - the modem would
    /// key and transmit silence.</exception>
    public static async Task<FlexRuntime> OpenAsync(
        string device, int dspRate, int packetBuffer, FlexTuning? tuning, CancellationToken cancellation)
    {
        FlexSpec spec = Parse(device);
        DaxStreamFormat format = DaxStreamFormat.ForDspRate(dspRate);
        tuning ??= new FlexTuning();

        MockFlexRadio? mock = null;
        FlexClient client;
        if (spec.RadioSpec.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            mock = new MockFlexRadio(
                format, MockRxMode.Loopback,
                spec.Headless ? MockSetupMode.Headless : MockSetupMode.Attach,
                station: spec.Station ?? "Flex", sliceLetter: spec.SliceLetter);
            mock.Start();
            client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort, cancellation)
                .ConfigureAwait(false);
            // The mock is a hardware-free fake; deliver its DAX audio in-process (lossless)
            // rather than self-looping over UDP, so flex:mock is deterministic on a loaded box.
            mock.RxDelivery = client.DeliverVitaPacket;
            client.VitaSendHook = mock.DeliverTxPacket;
        }
        else if (spec.RadioSpec.Equals("discover", StringComparison.OrdinalIgnoreCase))
        {
            client = await FlexClient.DiscoverAndConnectAsync(null, TimeSpan.FromSeconds(10), cancellation)
                .ConfigureAwait(false);
        }
        else if (spec.RadioSpec.Contains('=', StringComparison.Ordinal))
        {
            client = await FlexClient.DiscoverAndConnectAsync(
                spec.RadioSpec, TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
        }
        else
        {
            (string host, int port) = SplitHostPort(spec.RadioSpec);
            client = await FlexClient.ConnectAsync(host, port, Vita49.RadioVitaPort, cancellation)
                .ConfigureAwait(false);
        }

        var options = new FlexStationOptions
        {
            SliceLetter = spec.SliceLetter,
            Station = spec.Station ?? "Flex",
            HeadlessStationName = tuning.StationName,
            Frequency = tuning.Frequency,
            Antenna = tuning.Antenna,
            SliceMode = tuning.Mode,
            DaxChannel = tuning.DaxChannel,
            TransmitFilterHighHz = tuning.TransmitFilterHighHz,
            ReceiveFilterLowHz = tuning.ReceiveFilterLowHz,
            ReceiveFilterHighHz = tuning.ReceiveFilterHighHz,
            RfPower = tuning.TxPowerWatts is double watts ? ToRfPowerPercent(watts) : null,
            ReceiveOnly = tuning.ReceiveOnly,
            // A receive-only station never takes a slice back: it has no traffic to put on air,
            // and rebuilding would only contend with whatever displaced it.
            ContentionPolicy = tuning.ReceiveOnly
                ? FlexContentionPolicy.Never
                : FlexContentionPolicy.Default,
        };
        FlexStation station = spec.Headless
            ? await FlexStation.SetUpHeadlessAsync(client, format, options, cancellation).ConfigureAwait(false)
            : await FlexStation.SetUpAsync(client, format, options, cancellation).ConfigureAwait(false);

        // Headless bring-up points the transmitter at DAX (`transmit set dax=1`, Flex 0.7.0) and
        // reads the selection back - on a real radio every DAX enable step returns err=0 whether
        // or not the transmitter is listening to DAX, so without the read-back a mic-sourced
        // transmitter keys and sends silence. That is a dead modem, not a degraded one: fail the
        // bring-up loudly rather than run with it. (Attach mode never selects the source -
        // SmartSDR owns the transmitter there - so its warning stays null and this never fires.)
        if (station.TransmitSourceWarning is string transmitWarning)
        {
            await station.DisposeAsync().ConfigureAwait(false); // disposes the shared client too
            if (mock is not null)
            {
                await mock.DisposeAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException($"flex: {transmitWarning}");
        }

        // Flex's audio/PTT types implement the M0LTE.Radio.Audio seams directly (Flex 0.2.0),
        // which is this modem's seam too - no adapter needed.
        IAudioInput input = station.CreateAudioInput(packetBuffer);
        FlexAudioOutput flexOutput = station.CreateAudioOutput(paceRealTime: true);
        IAudioOutput output = format.SampleRate == dspRate
            ? flexOutput
            : new Channel.UpsamplingAudioOutput(flexOutput, dspRate);
        // Arbitrated keying carries the plan's transmit-filter high cut so every keyup
        // re-asserts it while the radio is quiet - the global, persistent filter is the
        // setting two stations otherwise overwrite under each other.
        IPttControl ptt = tuning.Arbitration
            ? station.CreateArbitratedPtt((tuning.ArbitrationTimings ?? new FlexPttArbitrationOptions()) with
            {
                TransmitFilterHighHz = tuning.TransmitFilterHighHz,
            })
            : station.CreatePtt();

        return new FlexRuntime(mock, station, input, output, ptt);
    }

    private static (string Host, int Port) SplitHostPort(string spec)
    {
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (spec, Vita49.DiscoveryPort)
            : (spec[..colon], int.Parse(spec[(colon + 1)..]));
    }

    /// <summary>
    /// The 6000-series PA size. Every model in the family is 100 W, which the radio confirms as
    /// <c>slice N max_internal_pa_power</c> - so on this family watts and the radio's 0-100 power
    /// number coincide, and the conversion exists to keep the config in the units an operator
    /// thinks in rather than because the arithmetic is hard.
    /// </summary>
    public const double PaWatts = 100.0;

    /// <summary>Converts watts to the radio's 0-100 transmit power number.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The request is negative or above what the PA can produce - a config error worth catching
    /// before it reaches the radio, which would answer with a bare protocol code.
    /// </exception>
    public static int ToRfPowerPercent(double watts)
    {
        if (watts < 0 || watts > PaWatts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(watts), watts, $"transmit power must be between 0 and {PaWatts:F0} W");
        }

        // Round to nearest: the radio takes whole numbers, and 0.5 W of rounding is far below
        // what anything downstream can tell apart.
        return (int)Math.Round(watts / PaWatts * 100.0, MidpointRounding.AwayFromZero);
    }

    private static bool IsSliceLetter(string segment) =>
        segment.Length == 1 && char.ToUpperInvariant(segment[0]) is >= 'A' and <= 'H';
}
