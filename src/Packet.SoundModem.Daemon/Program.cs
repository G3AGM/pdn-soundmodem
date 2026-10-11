using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Rig;
using M0LTE.Dsp;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Station;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Telemetry;
using Packet.SoundModem.Waterfall;
using Packet.SoundModem.Ms110d;

// pdn-soundmodem: headless soundcard packet modem daemon.
//
// The command line is described once, in Usage.cs, which is what --help prints. The flags are
// parsed in the switch below and a --config file is then loaded over them; UsageTests reads that
// switch and fails if its flags and the usage text's differ, so the two cannot drift.

string device = "default";
// "captureDevice" and "playbackDevice": a sound card for one direction when it is not "device".
// Config file only, and null on every station that does not split its audio.
string? captureDeviceKey = null;
string? playbackDeviceKey = null;
int captureRate = 48000;
int kissPort = 8105;
int kissMaxFrameBytes = KissDecoder.DefaultMaxFrame;
string bindAddress = "127.0.0.1";
string sideband = "usb";
bool sidebandWasStated = false;
bool waterfallSidebandWasStated = false;
double? dialFrequency = null;
FrameLogConfig? frameLogConfig = null;
// 300 ms is a RADIO allowance, not a modem requirement - the modems themselves acquire
// from 0-20 ms TXDELAY in every mode (150 ms for qpsk2400 facing a NinoTNC), measured and
// CI-enforced (NinoTncParityTests; docs/dev/bench/ninotnc-loop.md § How short can TXDELAY be?).
// The default budgets for a real transmitter's PTT-to-RF settling, which the wired bench
// cannot see and which routinely needs 100-300 ms on FM gear. Wired links, data-port
// radios and bench rigs should configure this down; issue #3 has the full derivation.
int? txDelay = null;
string? wavPath = null;
string? wavLoopPath = null;
string? pttSpec = null;
string? configPath = null;
int? waterfallPort = null;
double? dialHz = null;
// TX test: one bounded test transmission, then exit. --two-tone SECONDS sends the 700/1900 Hz
// pair for a linearity check; --tone HZ SECONDS sends one tone, for a carrier-level check or an
// FM Bessel-null deviation check (999 Hz nulls at 2.4 kHz deviation, 500 at 1.2, 1248 at 3.0,
// 2079 at 5.0). Both go out through the station's real transmit path at its real level, so what
// is measured is what a frame gets, and both are bounded by "txTest"."maxSeconds".
double? twoToneSeconds = null;
(double Hz, double Seconds)? singleTone = null;
bool qualityFrames = false;
// PSK detection method. --psk-detector overrides it for every PSK mode; unset, every PSK mode
// uses ModemCatalog.DefaultDetectorFor (differential; the history is on that method).
PskDetector? pskDetectorOverride = null;
string? pagingSpec = null;
int? ardopPort = null;
var modemSpecs = new List<string>();
// Headless FlexRadio slice params (--device flex: with no @station). Null = unset here;
// resolved against the config's Flex section, then FlexTuning defaults. --flex-daxch applies
// to both paths (headless and attach) - the DAX channel to claim.
string? flexFreq = null;
string? flexAnt = null;
string? flexMode = null;
string? flexDaxCh = null;
// --mixer-show: print a card's mixer and exit. Reading a mixer does not touch the PCM, so this
// answers "what are my card's control names and what is it set to" without stopping the station.
string? mixerShow = null;

// One line, read by both --version and the start-up banner below, so there is exactly one
// sentence in the whole program that answers "which version is this" (#480).
static string VersionLine() => $"pdn-soundmodem {DaemonVersion.Describe()}";

// Nothing on the command line used to start a station on the default sound card with one
// afsk1200 modem and KISS on 8105. That station is still one flag away (--config with a file
// holding {} gives the same thing, and the installed service always passes --config), but a bare
// command is far more often somebody finding out what the program is, so it gets the usage and
// an exit code that says nothing ran.
if (args.Length == 0)
{
    Console.Error.WriteLine(Usage.Text);
    return Usage.NoArgumentsExitCode;
}

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length
        ? args[i]
        : throw new ArgumentException($"{args[i - 1]} needs a value");
    switch (args[i])
    {
        case "--config": configPath = Next(); break;
        case "--device": device = Next(); break;
        case "--capture-rate": captureRate = int.Parse(Next()); break;
        case "--kiss": kissPort = int.Parse(Next()); break;
        case "--bind": bindAddress = Next(); break;
        case "--modem": modemSpecs.Add(Next()); break;
        case "--ptt": pttSpec = Next(); break;
        case "--txdelay": txDelay = int.Parse(Next()); break;
        case "--wav": wavPath = Next(); break;
        case "--wav-loop": wavLoopPath = Next(); break;
        case "--waterfall": waterfallPort = int.Parse(Next()); break;
        case "--dial": dialHz = double.Parse(Next()); break;
        case "--two-tone": twoToneSeconds = double.Parse(Next()); break;
        case "--tone":
            double singleToneHz = double.Parse(Next());
            singleTone = (singleToneHz, double.Parse(Next()));
            break;
        case "--quality-frames": qualityFrames = true; break;
        case "--psk-detector": pskDetectorOverride = Enum.Parse<PskDetector>(Next(), ignoreCase: true); break;
        case "--paging": pagingSpec = Next(); break;
        case "--ardop": ardopPort = int.Parse(Next()); break;
        case "--flex-freq": flexFreq = Next(); break;
        case "--flex-ant": flexAnt = Next(); break;
        case "--flex-mode": flexMode = Next(); break;
        case "--flex-daxch": flexDaxCh = Next(); break;
        case "--mixer-show": mixerShow = Next(); break;
        // Takes the callsign the token is for, and says so rather than throwing when it is
        // missing: this is often the first thing a new site owner runs.
        case "--uplink-token": return UplinkToken.Print(i + 1 < args.Length ? args[++i] : null);
        case "--version":
            Console.WriteLine(VersionLine());
            return 0;
        case "--help":
            Console.WriteLine(Usage.Text);
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

// The station's own first word: #480 was a live operator unable to answer "which version is
// this" from the software itself, for a station that had been running long enough that nobody
// remembered what was last deployed. journalctl -u pdn-soundmodem | head now answers it, whether
// that station started a minute or a month ago. Every one-shot flag above (--help, --version,
// --uplink-token, --mixer-show) has already returned by here, so this is start-up proper's first
// line, before --config is even read.
Console.WriteLine(VersionLine());

// Before anything else is built: this reads a card and exits, and it deliberately works while
// another process holds the PCM, which is the state a station's mixer is usually asked about in.
if (mixerShow is not null)
{
    string showCard = AlsaMixer.CardFor(mixerShow);
    if (!AlsaMixer.TryOpen(showCard, out AlsaMixer? showMixer, out string showWhy))
    {
        Console.Error.WriteLine($"{MixerSetup.JournalPrefix}{showCard} has no mixer ({showWhy})");
        return 1;
    }

    using (showMixer)
    {
        // Guarded for the same reason the start-up call is: TryOpen catches a missing symbol
        // among the ten entry points it uses, and Apply reaches twenty more.
        if (MixerSetup.TryApply(showMixer!, new MixerSettings(), Console.WriteLine, out _) is null)
        {
            return 1;
        }
    }

    return 0;
}

var modems = new List<ModemConfig>();
List<PolyglotConfig> polyglotPorts = [];
PttConfig? pttConfig = null;
RigConfig? rigConfig = null;
MailcastConfig? mailcastConfig = null;
CarrierSenseConfig? carrierSenseConfig = null;
AlsaConfig? alsaConfig = null;
PagingConfig? paging = null;
FlexConfig? flexConfig = null;
UberSdrConfig? uberSdrConfig = null;
OpenWebRxConfig? openWebRxConfig = null;
WaterfallConfig? waterfallConfig = null;
PublishConfig? publishConfig = null;
SurveyConfig? surveyConfig = null;
MetricsConfig? metricsConfig = null;
FrequencyMatchingConfig? frequencyMatching = null;
RawCaptureConfig? rawCaptureConfig = null;
DeadFeedConfig? deadFeedConfig = null;
bool idBeacons = true;
ApiConfig? apiConfig = null;
TxTestConfig txTestConfig = new();
// What this process is running, verbatim, so the API can serve it back rather than re-serialise
// a parsed object and quietly lose whatever the operator wrote that this version ignores.
string apiConfigJson = "{}";
bool apiEphemeralInForce = false;

if (configPath is not null)
{
    // A one-run configuration left by the API takes precedence for exactly this start-up, and is
    // deleted the moment it has been read - see ConfigApi.ConsumePending for why that ordering is
    // the safety property rather than a detail.
    string? pendingPath = ConfigApi.PendingPath(configPath);
    string loadPath = pendingPath ?? configPath;
    apiEphemeralInForce = pendingPath is not null;

    // A bad config is an operator typo, not a bug: explain it and exit 2. The unit's
    // RestartPreventExitStatus=2 stops systemd retrying, so the journal carries one
    // readable explanation instead of a stack trace every RestartSec.
    // Read before consuming: the API serves back what this process is actually running, and for
    // a one-run configuration the file it came from is about to be deleted.
    apiConfigJson = File.Exists(loadPath) ? File.ReadAllText(loadPath) : "{}";

    DaemonConfig? config = DaemonConfig.TryLoad(loadPath, out string configError);
    if (pendingPath is not null)
    {
        ConfigApi.ConsumePending(configPath);
    }

    if (config is null)
    {
        Console.Error.WriteLine(configError);
        return 2;
    }

    foreach (string warning in config.Warnings)
    {
        Console.Error.WriteLine($"config: WARNING - {warning}");
    }

    device = config.Device;
    captureDeviceKey = config.CaptureDevice;
    playbackDeviceKey = config.PlaybackDevice;
    captureRate = config.CaptureRate;
    kissPort = config.KissPort;
    kissMaxFrameBytes = config.KissMaxFrameBytes;
    bindAddress = config.Bind;
    sideband = config.Sideband;
    sidebandWasStated = config.SidebandWasStated;
    waterfallSidebandWasStated = config.WaterfallSidebandWasStated;
    dialFrequency = config.DialFrequency;
    frameLogConfig = config.FrameLog;
    modems = config.Modems;
    polyglotPorts = config.Polyglot;
    pttConfig = config.Ptt;
    rigConfig = config.Rig;
    mailcastConfig = config.Mailcast;
    carrierSenseConfig = config.CarrierSense;
    alsaConfig = config.Alsa;
    paging = config.Paging;
    flexConfig = config.Flex;
    uberSdrConfig = config.UberSdr;
    openWebRxConfig = config.OpenWebRx;
    waterfallConfig = config.Waterfall;
    publishConfig = config.Publish;
    apiConfig = config.Api;
    surveyConfig = config.Survey;
    metricsConfig = config.Metrics;
    frequencyMatching = config.FrequencyMatching;
    rawCaptureConfig = config.RawCapture;
    deadFeedConfig = config.DeadFeed;
    idBeacons = config.IdBeacons;
    txTestConfig = config.TxTest;
    ardopPort ??= config.Ardop?.Port;
    Console.WriteLine($"config: {configPath}");

    // Before anything asks the catalogue a question - the DSP-rate decision below, the band
    // planner, the transmit-filter plan - because a mode that is not registered yet is a mode
    // that does not exist. Each path is one the config named: nothing is discovered.
    foreach (ModemPluginConfig pluginConfig in config.ModemPlugins)
    {
        ModemPluginLoad load = ModemPluginLoader.Load(pluginConfig.Path);
        if (load.Loaded)
        {
            // The handle is deliberately not kept: plugins load for the life of the process, and
            // a station that unloaded a modem under its own audio would have nothing good to do
            // with the samples already in flight.
            Console.WriteLine(
                $"modem plugin: {load.PluginId} from {load.Path} "
                + $"[{string.Join(", ", load.Modes)}]");
        }
        else
        {
            // Named and non-fatal. A modem entry that wanted one of its modes still fails
            // start-up below, by name, as an unknown mode - which is the right place for it,
            // because that is the station asking for something it cannot do.
            Console.Error.WriteLine($"modem plugin: FAILED {load.Path} - {load.Failure}");
        }
    }

    // Flavour B: this file describes a monitor rather than a station, so nothing below applies to
    // it. There is no device to open, no PTT to check, no KISS port to bind and no transmitter to
    // plan a filter for; the monitor host owns the rest of this process. Dispatched here, after
    // the plugins are loaded and before the first line of station start-up, which is what keeps
    // the station path exactly as it was.
    if (config.Monitor is not null)
    {
        if (twoToneSeconds is not null || singleTone is not null)
        {
            Console.Error.WriteLine(
                "tx test: refused, this configuration describes a monitor rather than a station "
                + "- it fronts other people's web receivers and has no transmitter of its own");
            return 2;
        }

        return await MonitorStartup.RunAsync(config);
    }
}

// One bounded test transmission and out, for a bench with no browser on it. The station is
// built exactly as it would be to carry traffic - same device, same PTT, same level - because
// the whole point is to measure what a frame gets; what does not come up is anything that
// serves somebody else. See TxTestRunner, and the "txTest" section of docs/reference/config.md.
Packet.SoundModem.Waterfall.TxTestRequest? benchTxTest = null;
if (twoToneSeconds is not null && singleTone is not null)
{
    Console.Error.WriteLine("--two-tone and --tone are two ways to run one test; give one of them");
    return 2;
}
else if (twoToneSeconds is double twoToneSecondsAsked)
{
    benchTxTest = new Packet.SoundModem.Waterfall.TxTestRequest(true, 0, twoToneSecondsAsked);
}
else if (singleTone is { } singleToneAsked)
{
    benchTxTest = new Packet.SoundModem.Waterfall.TxTestRequest(
        false, singleToneAsked.Hz, singleToneAsked.Seconds);
}

// A bench test lives for a few seconds, and is the one run that must not retune the rig or log in
// to a BBS behind the operator's back.
if (benchTxTest is not null)
{
    mailcastConfig = null;
}

// --bind is not validated anywhere the way a config file's "bind" is, so an address that is not
// an address used to travel all the way to a null-forgiving ParseBind and surface as a
// NullReferenceException from the KISS listener - or, once the waterfall gained a warning that
// had to know whether the bind was loopback, as a FormatException before that. Said once, here,
// in the same words the config file's own check uses.
if (DaemonConfig.ParseBind(bindAddress) is null)
{
    Console.Error.WriteLine(
        $"--bind {bindAddress} is not an IP address. Use \"127.0.0.1\" for loopback only, "
        + "\"*\" for every interface, or the address of one interface.");
    return 2;
}

// A receiver has no transmitter, so a bench test on one is refused before anything is built
// rather than after the station has come up around a page that will not exist.
if (benchTxTest is not null && DeviceKinds.Of(device).TransmitTestRefusal is string receiverTxTestRefusal)
{
    Console.Error.WriteLine(receiverTxTestRefusal);
    return 1;
}

// --waterfall/--dial override (or stand in for) the config's waterfall section.
if (waterfallPort is int wfPort)
{
    waterfallConfig ??= new WaterfallConfig();
    waterfallConfig.Port = wfPort;
}

if (dialHz is double dial)
{
    if (waterfallConfig is null)
    {
        Console.Error.WriteLine("--dial only means something with a waterfall (--waterfall PORT)");
        return 2;
    }

    waterfallConfig.DialFrequencyHz = dial;
}

// Headless FlexRadio slice params: CLI flags override the config's Flex section, which
// overrides FlexTuning's defaults (14.100000 MHz / ANT1 / DIGU / DAX 2 headless, 1 attached).
// --flex-daxch applies to both headless and attach.
var flexTuning = new FlexTuning
{
    Frequency = flexFreq ?? flexConfig?.Frequency ?? "14.100000",
    Antenna = flexAnt ?? flexConfig?.Antenna ?? "ANT1",
    Mode = flexMode ?? flexConfig?.Mode ?? "DIGU",
    // Unset, a headless client stays off DAX 1: SmartSDR takes that one and the two contend,
    // so defaulting elsewhere makes the order they are started in stop mattering. Attach mode
    // is SmartSDR's slice by definition, so it keeps 1.
    DaxChannel = flexDaxCh ?? flexConfig?.DaxChannel
        ?? (DeviceKinds.Of(device).OwnsTheRadio(device)
            ? FlexConfig.DefaultHeadlessDaxChannel
            : "1"),
    TxPowerWatts = flexConfig?.TxPowerWatts,
    // A stated cut-off is used as it stands; 0 means "leave the radio's own filter alone", and
    // unset (the usual case) is filled in below from what the modems actually occupy.
    TransmitFilterHighHz = flexConfig?.TransmitFilterHighHz is > 0
        ? flexConfig.TransmitFilterHighHz
        : null,
    StationName = flexConfig?.StationName ?? "pdn-soundmodem",
    Arbitration = flexConfig?.Arbitration ?? false,
    ReceiveOnly = flexConfig?.ReceiveOnly ?? false,
};

// Nothing stated: the daemon works the transmit filter out rather than inheriting whatever the
// radio was last left on.
bool deriveTransmitFilter = flexConfig?.TransmitFilterHighHz is null;

// Caught here rather than at the radio, which answers an out-of-range power with a bare
// protocol code and no hint about which setting produced it.
if (flexTuning.TxPowerWatts is double requestedWatts
    && (requestedWatts < 0 || requestedWatts > FlexDevice.PaWatts))
{
    Console.Error.WriteLine(
        $"\"flex\".\"txPowerWatts\" is {requestedWatts:0.#} W, outside the 0-{FlexDevice.PaWatts:F0} W "
        + "a 6000-series PA can produce.");
    return 2;
}

if (pagingSpec is not null)
{
    string[] pagingParts = pagingSpec.Split(':');
    paging = new PagingConfig
    {
        Port = int.Parse(pagingParts[0]),
        Baud = pagingParts.Length > 1 ? int.Parse(pagingParts[1]) : 1200,
    };
}

foreach (string spec in modemSpecs)
{
    string[] specParts = spec.Split(':');
    if (specParts.Length > 3
        || !int.TryParse(specParts[0], out int specSubChannel)
        || (specParts.Length > 2 && !double.TryParse(specParts[2], out _)))
    {
        // The commonest way to land here is a plugin mode: --modem 0:sample:loopback splits into
        // three parts and "loopback" is not a frequency. That is not a typo to correct, it is a
        // grammar collision - this option's own separator is the one a plugin mode uses - so
        // say which way out there is rather than printing a number-format exception.
        Console.Error.WriteLine($"--modem {spec} is not N:MODE[:FREQ]");
        Console.Error.WriteLine(
            "  a plugin mode is written pluginId:mode and already contains the ':' this option "
            + "separates on, so it cannot be spelled here - put it in a config file, which is "
            + "where \"modemPlugins\" has to name the plugin anyway");
        return 2;
    }

    modems.Add(new ModemConfig
    {
        SubChannel = specSubChannel,
        Mode = specParts.Length > 1 ? specParts[1] : "afsk1200",
        Frequency = specParts.Length > 2 ? double.Parse(specParts[2]) : null,
    });
}

// ARDOP now shares the channel with the packet modems instead of excluding them: it is a
// modem entry like any other, and an ARQ session holds packet transmissions off the air
// through the channel's TransmitInhibit rather than through a config-level ban.
// On a Flex the slice mode states the sideband, so it is not something to be configured
// separately and disagreed with: DIGL alongside the default "usb" would mirror every modem
// about the dial and say nothing.
if (DeviceKinds.Of(device).OwnsTheRadio(device))
{
    string? impliedSideband = RfPlan.SidebandForSliceMode(flexTuning.Mode);

    if (impliedSideband is not null)
    {
        bool statedExplicitly = sidebandWasStated;
        if (statedExplicitly && !string.Equals(sideband, impliedSideband, StringComparison.OrdinalIgnoreCase))
        {
            // What the disagreement would cost, which is not the same thing when one of the two
            // is FM: that is not a mirrored plan but a plan for a different kind of radio.
            string consequence =
                RfPlan.IsFmRadio(sideband) || RfPlan.IsFmRadio(impliedSideband)
                    ? "One of those is a channel radio and the other is not, so the modems would "
                      + "be placed for a radio this is not"
                    : "Every modem would land mirrored about the dial";
            Console.Error.WriteLine(
                $"\"sideband\": \"{sideband}\" contradicts the Flex slice mode {flexTuning.Mode}, "
                + $"which is {impliedSideband.ToUpperInvariant()}. {consequence}. "
                + "Drop \"sideband\" - the slice mode already says which it is.");
            return 2;
        }

        sideband = impliedSideband;
    }
}

ModemConfig? ardopModem = modems.FirstOrDefault(m => DaemonConfig.IsArdop(m.Mode));
if (ardopModem is not null && ardopPort is not null)
{
    Console.Error.WriteLine(
        "ARDOP is configured twice - as a modem and with --ardop/\"ardop\". Keep the modem entry.");
    return 2;
}

if (ardopModem is null && ardopPort is not null)
{
    // The top-level section / --ardop remains supported: fold it into a modem entry so there
    // is one code path from here on.
    ardopModem = new ModemConfig
    {
        Mode = "ardop",
        Port = ardopPort,
        SubChannel = Enumerable.Range(0, 16).First(n => modems.All(m => m.SubChannel != n)),
    };
    modems.Add(ardopModem);
}

if (modems.Count == 0)
{
    modems.Add(new ModemConfig());
}
// Where the station's own lines go. This process runs exactly one station, so its journal
// carries no tag and reads byte for byte as it did before there was a Station type; a host
// running several gives each one its slug, so fifty of them in one journal are readable.
var stationJournal = StationJournal.Console();

// Mode names are checked, and the rate the shared channel runs at is settled, before the band
// planner and the transmit-filter plan: both ask the catalogue what a mode occupies, and the
// catalogue answers an unknown mode with its defaults.
// The mailcast receiver is an MS110D modem, which runs at 48 kHz, so it counts here like one.
if (!StationFactory.TryResolveDspRate(
        mailcastConfig is null ? modems : [.. modems, new ModemConfig { Mode = MailcastOnAir.Mode }],
        stationJournal, out int DspRate))
{
    return 2;
}

if (mailcastConfig is not null && StationFactory.TryResolveDspRate(modems, StationJournal.Console(), out int ownRate) && ownRate != DspRate)
{
    stationJournal.Write($"mailcast: the channel runs at {DspRate} Hz rather than {ownRate} Hz, for the MS110D receiver");
}

// The one "publish" check that could not be made while the file was being read: the audio is
// decimated to the published rate rather than resampled, so that rate has to divide the channel's,
// and the channel's is not settled until the modem set is - after any modem plugins have loaded.
if (publishConfig is not null
    && DaemonConfig.PublishRateProblem(publishConfig, DspRate) is { } publishRateProblem)
{
    // Through the same frame every other refusal in the file gets - the file name, the recovery
    // text and the config-reference link - because where a check had to live is not something an
    // operator reading journalctl should be able to tell.
    Console.Error.WriteLine(DaemonConfig.ConfigurationError(configPath!, publishRateProblem));
    return 2;
}

// The device the station runs on. Every question start-up asks about it from here on, what it
// can do and how to open it, is asked of this rather than of the device string.
StationDevice stationDevice;
try
{
    stationDevice = DeviceKinds.Resolve(
        device, wavLoopPath,
        new DeviceSettings(uberSdrConfig, HasWaterfall: waterfallConfig is not null, openWebRxConfig));
}
catch (InvalidDataException malformed)
{
    Console.Error.WriteLine(malformed.Message);
    return 2;
}

// Whether the daemon owns the radio, and so sets the dial and the transmit filter: a headless
// Flex only - in attach mode SmartSDR owns the slice and we would be fighting it.
bool ownsTheRadio = stationDevice.OwnsTheRadio;
// Refusals the device can make from its settings alone, before any band plan is worked out.
if (stationDevice.SettingsProblem is string deviceSettingsProblem)
{
    Console.Error.WriteLine(deviceSettingsProblem);
    return 2;
}

// A FlexRadio provides its own DAX sample clock (24/48 kHz auto-picked from the DSP rate), and
// an UberSDR its own 48 kHz IQ clock, so --capture-rate (an ALSA concept) does not apply to
// either.
if (stationDevice.CaptureRateApplies && captureRate % DspRate != 0)
{
    Console.Error.WriteLine($"--capture-rate must be a multiple of {DspRate}");
    return 2;
}

// A configuration written in RF terms becomes a dial plus an audio centre per modem. Done
// here because it needs the DSP rate (to measure each mode's real occupied width) and must
// land before the modems are built with those centres.
RfPlan.Result? bandPlan;
try
{
    // How far up the audio band this station can be planned. A rig the daemon cannot touch gets
    // the nominal SSB window; a headless Flex, whose transmit and receive filters the daemon sets
    // itself, gets the radio's own ceiling - which is what lets a 3 kHz waveform be planned at all,
    // since one cannot fit inside an ordinary passband however the dial is chosen.
    bandPlan = BandPlanner.Plan(
        modems, sideband, dialFrequency, DspRate,
        ownsTheRadio ? Passband.WideCeilingHz : null);
}
catch (InvalidDataException planFailure)
{
    Console.Error.WriteLine($"band plan: {planFailure.Message}");
    return 2;
}

// Where a self-tuning receiver has to point. A band plan says it outright; failing that the
// operator has to, because an SDR has no dial of its own to read a number off.
double? receiveDialHz = bandPlan?.DialHz ?? dialFrequency;
if (stationDevice.NoReceiveDialRefusal is string noReceiveDial && receiveDialHz is null)
{
    Console.Error.WriteLine(noReceiveDial);
    return 2;
}

if (bandPlan is not null)
{
    BandPlanner.Report(
        bandPlan, Console.Out, radioIsSelfTuning: stationDevice.SelfTunes || rigConfig is not null);
    foreach (string warning in bandPlan.Warnings)
    {
        Console.Error.WriteLine($"band plan: WARNING - {warning}");
    }

    // A Flex is its own dial: rather than telling the operator to set it, set it. Headless
    // only - in attach mode SmartSDR owns the slice and we would be fighting it. The transmit
    // filter is a global, persistent radio setting, so state its high cut from the plan or a
    // previous session's narrow filter silently truncates the top of the band.
    if (ownsTheRadio)
    {
        // Overwriting a stated slice frequency without a word would leave someone upgrading a
        // working Flex config with a number that has quietly stopped meaning anything.
        if (flexConfig?.Frequency is not null || flexFreq is not null)
        {
            Console.Error.WriteLine(
                $"flex: WARNING - the slice frequency you set ({flexFreq ?? flexConfig!.Frequency}) "
                + "is superseded by the band plan, which computed "
                + $"{RfPlan.Mhz(bandPlan.DialHz)}. Remove it, or remove the modems' "
                + "\"rfFrequency\" if you meant to place them by audio centre.");
        }

        int filterHigh = BandPlanner.TransmitFilterHighHz(bandPlan);
        flexTuning = flexTuning with
        {
            Frequency = (bandPlan.DialHz / 1_000_000).ToString("F6", CultureInfo.InvariantCulture),
            // A stated cut-off is the operator overruling the plan, which is theirs to do - the
            // clipping check below still says so if it truncates a modem.
            TransmitFilterHighHz = deriveTransmitFilter ? filterHigh : flexTuning.TransmitFilterHighHz,
        };
        Console.WriteLine(
            $"flex: setting the slice to {RfPlan.Mhz(bandPlan.DialHz)}"
            + (deriveTransmitFilter ? $" and the transmit filter high cut to {filterHigh} Hz" : "")
            + " from the band plan");
    }
}

// What kind of radio the page is told this station is on: the band plan's answer where there is
// one, then the "waterfall" section's own, and failing both the top-level "sideband" - but only
// when it says FM. An SSB page told nothing draws a USB scale, as it always has, and changing
// that would move the page of every LSB station placed by audio centre; an FM page told nothing
// draws an RF scale that is a lie, which is the whole of issue #413. So FM falls through and the
// two sidebands do not.
string pageSideband =
    bandPlan?.Sideband
    ?? (!waterfallSidebandWasStated && RfPlan.IsFmRadio(sideband) ? sideband : null)
    ?? waterfallConfig?.Sideband
    ?? "usb";

// Where the mailcast receiver listens: on the station's own passband if that reaches the signal,
// else on a rig retuned around each slot if the config allows it, else nowhere, said now.
MailcastPlacement? mailcastPlacement = null;
if (mailcastConfig is not null)
{
    MailcastRadio mailcastRadio = MailcastStation.RadioFor(
        stationDevice.MailcastKind, bandPlan, dialFrequency, receiveDialHz, sideband,
        flexTuning, uberSdrConfig, hasRig: rigConfig is not null, openWebRxConfig);
    mailcastPlacement = MailcastPlacement.Decide(
        mailcastConfig, mailcastRadio, MailcastPlacement.HalfWidthHz(), out string? mailcastRefusal);
    if (mailcastPlacement is null)
    {
        Console.Error.WriteLine(mailcastRefusal);
        return 2;
    }
}

// Carrier sense, once for the station: every modem on this channel listens to the same receiver,
// so whether the channel is occupied is a fact about that receiver and not about any waveform.
// Fails open - see StationCarrierSense.
IChannelBusySource? carrierSense = StationCarrierSense.Open(
    carrierSenseConfig, Console.WriteLine, out IDisposable? carrierSenseOwned,
    out bool carrierSenseFromAudio);
using IDisposable? carrierSenseLifetime = carrierSenseOwned;

// Said once, here, because this decides what the station will and will not transmit over and an
// operator should not have to infer it from the absence of a line.
if (carrierSenseFromAudio && DspRate < FmShapeBusyDetector.MinimumSampleRate)
{
    Console.WriteLine(
        $"carrier sense: this channel runs at {DspRate} Hz, and reading carrier sense off the "
        + "shape of the audio is measured not to work below "
        + $"{FmShapeBusyDetector.MinimumSampleRate} Hz. Falling back to the in-band energy "
        + "detector, which on an open-squelch FM radio reads clear through every transmission "
        + "and busy for about ten seconds after it. A control cable to the radio is the fix.");
    carrierSenseFromAudio = false;
}
else if (carrierSenseFromAudio)
{
    Console.WriteLine(
        "carrier sense: no radio to ask, so from the shape of the received audio. That suits an "
        + "open-squelch FM receiver and nothing else; on any other path it reports no opinion and "
        + "the energy detectors decide, each frame waiting on the modems that share its passband.");
}

var channel = new SoundModemChannel(
    DspRate, channelBusySource: carrierSense, audioFallback: carrierSenseFromAudio);
if (stationDevice.ReceiveOnlyReason is string receiveOnlyReason)
{
    // Said once, here, so every path that could put something on the air - KISS, paging, ARDOP -
    // gets the same answer for the same reason, rather than each discovering it differently.
    channel.ReceiveOnlyReason = receiveOnlyReason;
}

// Channel access (TXDELAY, P, SLOTTIME, TXTAIL) belongs to the host, which sets it over KISS
// at runtime - see KissTcpServer. The library's defaults stand until it does; there is
// deliberately no configuration-file equivalent. --txdelay remains as a bench override for
// runs with no host attached.
if (txDelay is int txDelayOverride)
{
    channel.Csma.TxDelayMilliseconds = txDelayOverride;
}

// Where each modem sits, for the log's audio_hz/rf_hz columns. Declared out here because both
// halves of the log - what was heard and what was sent - fill those columns from it, and the
// transmit side is wired further down with the rest of the activity reporting.
var frameLogRfByModem = modems.ToDictionary(
    m => m.SubChannel,
    m => (Audio: m.Frequency, Rf: m.RfFrequency));

// Every frame the station hears, written down.
FrameLog? frameLog = null;
if (frameLogConfig is not null
    && !StationFactory.TryOpenFrameLog(
        frameLogConfig.Path, modems, channel, stationJournal, out frameLog))
{
    return 2;
}

await using var frameLogLifetime = frameLog;

// The PSK detector, for the informational print below: --psk-detector overrides it; unset,
// the catalogue default applies, which is differential for every PSK family (see
// ModemCatalog.DefaultDetectorFor for the bench evidence - an earlier comment here still
// said "QPSK coherent" long after the catalogue changed its mind, which is the kind of
// drift asking the catalogue directly avoids).
PskDetector bpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("bpsk300");
PskDetector qpskDetector = pskDetectorOverride ?? ModemCatalog.DefaultDetectorFor("qpsk2400");

// Every configured modem onto the channel, and a line each saying what was built. Shared with
// the many-receiver flavour, which builds the same modems for every receiver it fronts: two
// copies of this loop would be two answers to what a config actually runs.
if (!StationFactory.TryAddModems(channel, modems, DspRate, pskDetectorOverride, stationJournal))
{
    return 2;
}

// The mailcast receiver: a receive tap beside the modems, never one of them (see MailcastReceiver).
MailcastReceiver? mailcast = null;
if (mailcastConfig is not null && mailcastPlacement is not null)
{
    string mailcastDirectory = mailcastConfig.StateDirectoryFor(MailcastStation.StationStateDirectory(configPath));
    if (MailcastStation.StateDirectoryProblem(mailcastDirectory) is string unwritable)
    {
        Console.Error.WriteLine(unwritable);
        return 2;
    }

    try
    {
        mailcast = MailcastReceiver.Create(
            mailcastConfig, mailcastPlacement, DspRate, mailcastDirectory, TimeProvider.System,
            MailcastStation.Journal(stationJournal));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"mailcast: cannot open its state in {mailcastDirectory}: {e.Message}. Make it writable by "
            + "the service, or set \"mailcast\".\"stateDirectory\" to a folder that is.");
        return 2;
    }

    mailcast.Attach(channel);
    foreach (string line in mailcast.Describe())
    {
        stationJournal.Write(line);
    }
}

await using var mailcastLifetime = mailcast;

// "Measure my filter" (POST /api/mailcast/measure): a receive tap beside the mailcast modem
// itself, averaging the station's own quiet audio rather than anything mailcast sends. The
// transmitter and a mailcast slot both shape the spectrum into something that is not the rig's
// receive filter, so both are skipped - the transmitter through the channel's own event (the
// same one MailcastReceiver.Attach subscribes to), a mailcast slot through the retuner's own
// "on the mailcast dial" flag when there is one, else whether the signal's own listening window
// is open on the station's passband.
MailcastFilterMeasurer? mailcastMeasure = null;
if (mailcast is not null)
{
    MailcastReceiver measuredReceiver = mailcast;
    bool measureTransmitting = false;
    channel.TransmittingChanged += keyed => measureTransmitting = keyed;
    mailcastMeasure = new MailcastFilterMeasurer(DspRate, () => measureTransmitting || MailcastMeasureInSlot(measuredReceiver));
    mailcastMeasure.Attach(channel);
}

// Whether now falls inside a mailcast slot's listening window, for the receiver given: the
// retuner's own flag on a station that moves its rig for it, else the passband window the
// receiver itself follows without retuning.
static bool MailcastMeasureInSlot(MailcastReceiver receiver)
{
    if (receiver.Retuner is { } retuner)
    {
        return retuner.Listening;
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;
    return MailcastHooks.PassbandWindowAt(now, receiver.Slots.Timetable) is { } window
        && now >= window.Opens && now < window.Closes;
}

if (modems.Any(m => m.Mode.StartsWith("bpsk", StringComparison.Ordinal)))
{
    Console.WriteLine($"psk detector (bpsk): {bpskDetector.ToString().ToLowerInvariant()}"
        + (pskDetectorOverride is null ? " [default]" : " [--psk-detector]"));
}

if (modems.Any(m => m.Mode.StartsWith("qpsk", StringComparison.Ordinal)))
{
    Console.WriteLine($"psk detector (qpsk): {qpskDetector.ToString().ToLowerInvariant()}"
        + (pskDetectorOverride is null ? " [default]" : " [--psk-detector]"));
}

// Morse identification, per modem. A data waveform carries nothing a listener can read without
// our software, so a station running one on a real antenna owes anyone sharing the band a
// callsign in a form they can copy by ear. Off unless a modem asks for it: the modes that
// already identify themselves in-band need nothing, and a station that transmits nothing owes
// nothing (see StationIdentifier for why the clock only runs while the modem is transmitting).
var identifiers = new Dictionary<int, StationIdentifier>();
foreach (ModemConfig modemConfig in modems)
{
    if (modemConfig.Identify is not IdentifyConfig id)
    {
        continue;
    }

    int subChannel = modemConfig.SubChannel;
    if (channel.ReceiveOnlyReason is not null)
    {
        // The same shape of refusal "ptt" gets on this device, and for the same reason: there is
        // no transmitter to identify. Queueing one would only turn "cannot" into a timeout.
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" needs a transmitter, and this station receives "
            + "only - drop it, or point the station at a radio");
        return 2;
    }

    if (DaemonConfig.IsArdop(modemConfig.Mode))
    {
        // ARDOP transmits through the delegate path as a whole virtual TNC, so none of its
        // bursts raise the per-sub-channel event this policy counts. Rather than accept the
        // setting and never identify, say so: a silent no-op on a licence condition is the
        // worst of the three outcomes.
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" is not supported on ardop - its ARQ bursts do "
            + "not go out as addressed frames, so there is nothing here to count transmissions "
            + "against. Identify on a packet modem sharing the channel instead.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(id.Callsign))
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" needs a \"callsign\" - there is no default for a "
            + "licence condition");
        return 2;
    }

    if (id.ToneHz is not null && id.RfFrequency is not null)
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\" sets both \"toneHz\" and \"rfFrequency\" - they "
            + "say the same thing two ways. Keep one.");
        return 2;
    }

    if (id.RfFrequency is not null && bandPlan is not null && bandPlan.IsFm)
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\".\"rfFrequency\" has no meaning on FM. The channel "
            + "is the RF and the ident is an audio tone sent over it, not an offset from a dial, "
            + "so there is no arithmetic to turn one into the other. Set "
            + "\"identify\".\"toneHz\" instead.");
        return 2;
    }

    if (id.RfFrequency is not null && bandPlan is null)
    {
        Console.Error.WriteLine(
            $"modem {subChannel}: \"identify\".\"rfFrequency\" needs a band plan - without one "
            + "the daemon does not know this station's dial, so it cannot turn an RF frequency "
            + "into a tone. Give the modems \"rfFrequency\", pin \"dialFrequency\", or set "
            + "\"identify\".\"toneHz\" instead.");
        return 2;
    }

    // Where this modem's audio actually sits, for an ident told to default to it. On SSB the
    // planner has already written that back into "frequency"; on FM it deliberately writes
    // nothing back, because there is nothing to work out, so the plan is the only place the
    // centre is written down. A baseband mode has none either way and is refused below.
    double? plannedCentreHz =
        bandPlan?.Modems.FirstOrDefault(m => m.Slot.SubChannel == subChannel)
            is { AudioCentreHz: > 0 } placed
            ? placed.AudioCentreHz
            : null;

    double? toneHz =
        id.ToneHz
        ?? (id.RfFrequency is double identRf && bandPlan is not null
            ? (bandPlan.IsUpperSideband ? identRf - bandPlan.DialHz : bandPlan.DialHz - identRf)
            : modemConfig.Frequency ?? plannedCentreHz);

    if (toneHz is not double tone)
    {
        // A baseband mode occupies the audio band from DC up and has no centre to borrow.
        Console.Error.WriteLine(
            $"modem {subChannel}: mode '{modemConfig.Mode}' has no audio centre, so there is "
            + "nothing to default the ident tone to - set \"identify\".\"toneHz\"");
        return 2;
    }

    StationIdentifier identifier;
    try
    {
        identifier = new StationIdentifier(
            id.Callsign!,
            id.IncludeMode ? modemConfig.Mode : null,
            tone,
            id.Wpm,
            TimeSpan.FromMinutes(id.IntervalMinutes),
            DspRate,
            id.Amplitude);
    }
    catch (Exception bad) when (bad is ArgumentException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine($"modem {subChannel}: \"identify\" is not usable");
        Console.Error.WriteLine($"  {bad.Message}");
        return 2;
    }

    identifiers[subChannel] = identifier;

    // The ident is this modem's own transmission, so it keeps going out while this modem's
    // sub-channel holds the transmit lease, and waits while anybody else does.
    IdentTransmission.Register(channel, subChannel, identifier);
    Console.WriteLine(
        $"modem {subChannel}: identifying as {identifier.Text} in CW @ {tone:F0} Hz"
        // Not on FM, where the tone is a tone on the channel and adding it to one would be the
        // same false arithmetic the page stopped doing.
        + (bandPlan is not null && !bandPlan.IsFm
            ? $" = {RfPlan.Mhz(bandPlan.IsUpperSideband ? bandPlan.DialHz + tone : bandPlan.DialHz - tone)}"
            : "")
        + $", {id.Wpm:F0} wpm, every {id.IntervalMinutes:F0} min while transmitting "
        + $"({identifier.DurationSeconds:F1} s)");

    if (bandPlan is not null && (tone < bandPlan.Window.LowHz || tone > bandPlan.Window.HighHz))
    {
        // Not fatal: the window is what the plan fitted the modems into, and an operator who
        // moved the ident deliberately may know something the planner does not. But on a Flex
        // the transmit filter is set from that window, so an ident outside it goes out truncated
        // or not at all, and nothing else would say so.
        Console.Error.WriteLine(
            $"modem {subChannel}: WARNING - the ident tone {tone:F0} Hz is outside the "
            + $"{bandPlan.Window.LowHz:F0}-{bandPlan.Window.HighHz:F0} Hz passband this plan "
            + "plays into, so it may be filtered away on transmit. Leave \"toneHz\" unset to "
            + "identify on this modem's own centre.");
    }
}

// Where each modem sits in the audio band, for the radio's transmit filter: from the plan when
// there is one, else measured off the modems as configured. Flex only - it is the one device
// whose transmit filter the daemon can see, and the measurement costs a modulate per modem.
IReadOnlyList<TransmitFilterPlan.Band> txBands = !stationDevice.ReportsTransmitFilter
    ? []
    : bandPlan is not null
        ? [.. bandPlan.Modems.Select(m => new TransmitFilterPlan.Band(
            m.Slot.SubChannel, m.Slot.Mode,
            m.AudioCentreHz - (m.Slot.BandwidthHz / 2),
            m.AudioCentreHz + (m.Slot.BandwidthHz / 2)))]
        : TransmitFilterPlan.Bands(modems, DspRate);

// The transmit filter is a global, persistent radio setting - whatever last touched the radio -
// so a station placed by audio centre inherits some previous session's filter, and a mode wider
// than it (ms110d-* reaches past 3.1 kHz against a 3000 Hz default) is truncated on air with
// nothing said. The band-planned path states the high cut above; this is the same for a station
// with no plan to read it off.
if (ownsTheRadio && deriveTransmitFilter && flexTuning.TransmitFilterHighHz is null
    && TransmitFilterPlan.HighCutFor(txBands) is int derivedFilterHigh)
{
    TransmitFilterPlan.Band widest = txBands.MaxBy(b => b.HighHz);
    flexTuning = flexTuning with { TransmitFilterHighHz = derivedFilterHigh };
    Console.WriteLine(
        $"flex: setting the transmit filter high cut to {derivedFilterHigh} Hz - modem "
        + $"{widest.SubChannel} ({widest.Mode}) reaches {widest.HighHz:F0} Hz");
}

// The receive half of the same question, and the half that cannot be seen from the transmit side:
// the slice's own filter decides what reaches the modems, so a slice left on an ordinary data
// filter hears nothing above ~3 kHz however wide the transmit filter is opened. Slice state rather
// than global, so unlike the transmit filter this one is ours to set without affecting anything
// else the radio does.
// The mailcast signal is heard on the same slice when it is in reach, so the filter opens to it too.
List<(double LowHz, double HighHz)> heardBands = [.. txBands.Select(b => (b.LowHz, b.HighHz))];
if (mailcastPlacement is { Retunes: false } mailcastHeard)
{
    heardBands.Add((mailcastHeard.LowHz, mailcastHeard.HighHz));
}

if (ownsTheRadio && heardBands.Count > 0)
{
    int receiveLow = BandPlanner.LowCutClearing(heardBands.Min(b => b.LowHz));
    int receiveHigh = BandPlanner.HighCutClearing(heardBands.Max(b => b.HighHz));
    flexTuning = flexTuning with
    {
        ReceiveFilterLowHz = receiveLow,
        ReceiveFilterHighHz = receiveHigh,
    };
    Console.WriteLine(
        $"flex: setting the slice receive filter to {receiveLow}-{receiveHigh} Hz, to hear "
        + (heardBands.Count > txBands.Count
            ? "everything the modems are placed across and the mailcast signal"
            : "everything the modems are placed across"));
}

// The transmit side has no per-frame quality to report the mode from, so it comes from the
// configuration - the modem that owns the sub-channel.
Dictionary<int, string> modeBySubChannel = modems
    .Where(m => !DaemonConfig.IsArdop(m.Mode))
    .ToDictionary(m => m.SubChannel, m => m.Mode);

// What the station is doing, one line per frame, in the journal.
StationFactory.JournalReceivedFrames(channel, stationJournal);

// How far off our centre each station we hear is transmitting. Measured always: it costs a
// dictionary write per frame, it is the evidence for whether answering them off-centre is worth
// doing, and the transmit side below will not act without it.
var stationOffsets = new StationFrequencyOffsets
{
    MaxSamples = frequencyMatching?.Samples ?? FrequencyMatchingConfig.DefaultSamples,
    MaxAge = TimeSpan.FromSeconds(
        frequencyMatching?.MaxAgeSeconds ?? FrequencyMatchingConfig.DefaultMaxAgeSeconds),
};
// Replay what the station already knew. A restart otherwise starts deaf to the channel's
// frequencies and cannot correct for anybody until it has heard each station afresh - which
// bites hardest when calling a station it has not heard yet, exactly when the correction would
// have helped. The frames come back with their original timestamps, so the age window still
// governs: a log full of yesterday's traffic seeds nothing, and only a restart short enough for
// those frames to still be current carries anything over. Which is the case that matters, since
// the usual reason this process restarts is that somebody upgraded it.
if (frameLog is not null)
{
    int seeded = 0;
    var seenStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (LoggedFrame logged in frameLog.Recent(500))
    {
        if (logged.Transmitted
            || logged.OffsetHz is not double loggedOffset
            || string.IsNullOrWhiteSpace(logged.From))
        {
            continue;
        }

        stationOffsets.Record(logged.From, loggedOffset, logged.HeardAt);
        seenStations.Add(logged.From);
        seeded++;
    }

    if (seeded > 0)
    {
        int current = stationOffsets.Snapshot().Count;
        Console.WriteLine(
            $"frequency offsets: replayed {seeded} logged frames from {seenStations.Count} "
            + $"station(s); {current} still current enough to use");
    }
}

channel.FrameReceivedWithQuality += (_sub, frame, quality) =>
{
    if (quality.FrequencyOffsetHz is double offsetHz
        && Ax25AddressParser.TryParse(frame, out string source, out string _dest))
    {
        stationOffsets.Record(source, offsetHz);
    }
};

// Answer an off-frequency station where its receiver is listening. On unless switched off,
// including when the section is absent entirely: the shift is clamped to a few tens of Hz, which
// is less than several stations on the band are already scattered across, and the station it
// helps is the one at the other end with a fixed-centre modem.
FrequencyMatchingConfig fmConfig = frequencyMatching ?? new FrequencyMatchingConfig();
if (fmConfig.Enabled)
{
    FrequencyMatchingConfig fm = fmConfig;
    Console.WriteLine(
        $"frequency matching: on - answering a station on its own frequency after "
        + $"{fm.MinSamples} frames, if they agree within {fm.MaxSpreadHz:F0} Hz, "
        + $"up to {fm.MaxTrimHz:F0} Hz in full ({fm.Damping:0.##} for any station that has "
        + "moved under it); backs off for "
        + $"{fm.ChaseCooldownSeconds / 60:F0} min from any station whose own frequency then moves "
        + $"more than {fm.ChaseThresholdHz:F0} Hz, and stops after {fm.MaxChases} such moves");

    var matching = new FrequencyMatchingPolicy(
        stationOffsets,
        new FrequencyMatchingOptions
        {
            MinSamples = fm.MinSamples,
            MaxSpreadHz = fm.MaxSpreadHz,
            MaxTrimHz = fm.MaxTrimHz,
            Damping = fm.Damping,
            ChaseThresholdHz = fm.ChaseThresholdHz,
            ChaseCooldown = TimeSpan.FromSeconds(fm.ChaseCooldownSeconds),
            MaxChases = fm.MaxChases,
        },
        TimeProvider.System);

    matching.StoodDown += stand =>
        Console.Error.WriteLine(
            stand.RetryAfter is TimeSpan retry
                ? $"frequency matching: backing off {stand.Callsign} for "
                    + $"{retry.TotalMinutes:0} min (move {stand.Chases}) - {stand.Detail}"
                : $"frequency matching: giving up on {stand.Callsign} - {stand.Detail}");

    channel.TransmitTrimHz = (_sub, frame) =>
    {
        if (!Ax25AddressParser.TryParse(frame, out string _src, out string destination))
        {
            return 0;
        }

        // A beacon or an ID is for everybody, and aiming it at one correspondent's oscillator
        // aims it away from every other listener. Strip the SSID before comparing: ID-1 is as
        // much a broadcast as ID.
        string bare = destination.Split('-')[0];
        return FrequencyMatchingConfig.BroadcastDestinations.Contains(
            bare, StringComparer.OrdinalIgnoreCase)
            ? 0
            : matching.TrimFor(destination);
    };
}
// One transmitter at a time makes every receiver on this radio deaf, however many there are. So
// after a frame that will be answered, stay off the air long enough for the answer to arrive
// rather than keying over it with somebody else's traffic. Carrier sense cannot do this: at the
// moment we would roll p-persistence the reply has not started, so there is nothing to hear.
// Only AX.25 traffic gets a hold - Ax25ReplyExpectation says "no opinion" for anything it cannot
// read as AX.25, and no opinion means the channel behaves exactly as it always did.
channel.QuietAfterTransmit = (_sub, frame) =>
    Packet.SoundModem.Modems.Ax25ReplyExpectation.ExpectsReply(frame)
        ? channel.TurnaroundHold
        : null;

channel.FrameTransmittedWithReport += (subChannel, frame, report) =>
{
    double trimHz = report.TrimHz;
    Console.WriteLine(ActivityLog.Transmitted(
        subChannel, modeBySubChannel.GetValueOrDefault(subChannel, "?"), frame, trimHz,
        report.HeldFor, report.Waits));

    // And into the station's journal, alongside what it heard: a log that records every frame
    // received and none sent is half a record. Raised after the audio has gone to the device, so
    // a logged row is a frame that actually went on air. Same placement lookup as the receive
    // side, so audio_hz/rf_hz mean the same thing in both directions.
    //
    // The mode is the catalogue identity of what went on air - the modem's own report of
    // itself minus the diversity bank's branch-count suffix, which is receiver construction
    // rather than a property of the transmission (issue #343). That is the spelling the
    // receive side writes into this column (FrameQuality.Mode) and the spelling the waterfall
    // stamps on its TX rows, so one modem's traffic stays under one spelling and "everything
    // on bpsk300-il2pc today" is one query that includes our own. The configured name the
    // console line uses ("bpsk300") stays out of it, as before.
    (double? audio, double? rf) = frameLogRfByModem.TryGetValue(subChannel, out var placement)
        ? placement
        : (null, null);
    frameLog?.RecordTransmitted(
        subChannel,
        frame,
        channel.Modems.TryGetValue(subChannel, out IModem? sender)
            ? ModeNames.Identity(sender.Mode)
            : modeBySubChannel.GetValueOrDefault(subChannel, "?"),
        audio,
        rf,
        trimHz == 0 ? null : trimHz,
        // Written for every transmission, not just the ones the console line mentions: the log
        // is the record a question gets asked of later, and "how often does this station wait,
        // and for how long" cannot be answered from rows that only exist past a threshold.
        (long)report.HeldFor.TotalMilliseconds,
        // And where that wait went, a column per cause. The total on its own cannot tell a busy
        // frequency from a frame sitting behind this station's own window, and those are the two
        // readings an operator has to choose between every time the figure is large.
        waits: report.Waits);
};
// Dropped frames are rate-limited per reason. A station that loses its slice rejects every
// frame it is handed, for as long as the fault lasts: the unmitigated version of this line
// wrote 10,532 identical entries over six days on GB7RDG, which buried the one line that
// mattered and told a reader nothing the first line had not. The first drop of a reason is
// always printed; repeats are counted and folded into a periodic line.
var dropGate = new object();
var dropSuppressed = new Dictionary<string, int>(StringComparer.Ordinal);
var dropLastLogged = new Dictionary<string, long>(StringComparer.Ordinal);
const long DropLogIntervalMs = 60_000;

channel.TransmitRejected += (subChannel, frame, reason) =>
{
    string key = reason.Message;
    bool report;
    int suppressed = 0;
    lock (dropGate)
    {
        long now = Environment.TickCount64;
        report = !dropLastLogged.TryGetValue(key, out long last)
            || now - last >= DropLogIntervalMs;
        if (report)
        {
            dropLastLogged[key] = now;
            dropSuppressed.Remove(key, out suppressed);
        }
        else
        {
            dropSuppressed[key] = dropSuppressed.GetValueOrDefault(key) + 1;
        }
    }

    if (!report)
    {
        return;
    }

    Console.Error.WriteLine(
        ActivityLog.Dropped(subChannel, frame, reason)
        + (suppressed > 0 ? $" (and {suppressed} more like it in the last minute)" : ""));
};

if (wavPath is not null)
{
    var (samples, rate) = WavFile.ReadMono(wavPath);
    Array.Resize(ref samples, samples.Length + rate / 2);
    if (rate != DspRate)
    {
        if (rate % DspRate != 0)
        {
            Console.Error.WriteLine($"wav rate {rate} is not a multiple of {DspRate}");
            return 2;
        }

        var decimator = new Decimator(rate, rate / DspRate);
        var decimated = new float[decimator.MaxOutput(samples.Length)];
        int produced = decimator.Process(samples, decimated);
        samples = decimated[..produced];
    }

    int frames = 0;
    channel.FrameReceived += (_, _) => frames++;
    channel.ProcessReceive(samples);
    Console.WriteLine($"{frames} frames decoded");
    return 0;
}

// The browser waterfall (spectrum + waterfall + per-frame burst attribution). Started
// before audio flows: Start() measures every modem's band off its own modulator and
// registers the channel receive tap.
Packet.SoundModem.Waterfall.WaterfallWebServer? waterfallServer = null;
// Whether the page's Mixer group and /api/mixer answer with no api.key, from
// "waterfall"."enableAudioControls". Decided in the block below, where the page it opens is
// started, and used again at the configuration API further down.
bool openAudioControls = false;
// Not on a --two-tone/--tone run: such a run lives for a few seconds, and a page or a scraper
// that attached to it would lose it again immediately. It also means a bench run does not stop
// with "cannot serve the waterfall" when the operator has left the service holding the port -
// which would be true, and would say nothing about the test they actually asked for.
if (benchTxTest is null && waterfallConfig is not null)
{
    waterfallServer = new Packet.SoundModem.Waterfall.WaterfallWebServer(
        channel,
        waterfallConfig.Port,
        new Packet.SoundModem.Waterfall.WaterfallOptions
        {
            // The band plan (or a pinned "dialFrequency") already knows the dial; the waterfall's
            // RF scale should not have to be told it a second time, and then disagree when one of
            // them is edited.
            DialFrequencyHz = waterfallConfig.DialFrequencyHz != 0
                ? waterfallConfig.DialFrequencyHz
                : receiveDialHz ?? 0,
            Sideband = pageSideband,
            LinesPerSecond = waterfallConfig.LinesPerSecond,
            FftSize = waterfallConfig.FftSize,
            Public = waterfallConfig.Public,
            Title = waterfallConfig.Title,
            About = waterfallConfig.About,
            // What each modem is meant to occupy; see StationFactory.DeclaredBandsFor, which the
            // many-receiver flavour calls too.
            DeclaredBands = StationFactory.DeclaredBandsFor(modems),
            // The decoded-frames panel opens on what the station has already written down, so a
            // browser arriving mid-afternoon is shown the channel rather than an empty list. Null
            // when there is no frame log: nothing to show, and nothing pretending otherwise.
            FrameHistory = frameLog is null ? null : frameLog.Recent,
            // Where a page dropped for not answering says so. Untagged, like every other line
            // this process writes, because it runs one station.
            Log = stationJournal.Write,

            // The input level meter, which lives beside the Mixer group's capture slider and is
            // therefore only worth measuring on a page that will have that group: an operator's
            // page, a station with a card of its own, and an /api/mixer this browser can reach
            // (an api.key or enableAudioControls). Without the last one the endpoint is a 404,
            // the page hides the whole group, and the message would go out several times a
            // second to a browser that draws none of it.
            InputLevelMeter = DaemonConfig.ShowsMixerGroup(device, waterfallConfig, apiConfig),
        },
        // One bind for every listener; the waterfall no longer carries its own.
        bindAddress);

    // The links pane opens on the links the station already knows about; see
    // StationFactory.BackfillLinks, which the many-receiver flavour calls too.
    if (frameLog is not null)
    {
        StationFactory.BackfillLinks(waterfallServer, frameLog);
    }

    try
    {
        waterfallServer.Start();
    }
    catch (Exception e) when (e is HttpListenerException or SocketException)
    {
        Console.Error.WriteLine(
            $"cannot serve the waterfall on {bindAddress}:{waterfallConfig.Port}\n"
            + $"  {e.Message}\n"
            + "  Set by \"waterfall\".\"port\" and the top-level \"bind\". Another process may\n"
            + "  already hold the port; \"*\" or \"0.0.0.0\" serves every interface.");
        return 2;
    }
    catch (ArgumentException e)
    {
        // The spectrum source refusing its geometry - a wrong answer in the config, not a
        // busy port. Without this catch the raw exception took a non-2 exit code, and
        // Restart=on-failure (which only spares exit 2) crash-looped the unit every five
        // seconds - exactly the failure mode the config-validation contract exists to
        // prevent.
        Console.Error.WriteLine(
            "invalid waterfall settings\n"
            + $"  {e.Message}\n"
            + "  Set by \"waterfall\".\"fftSize\" and \"waterfall\".\"linesPerSecond\". The FFT\n"
            + "  size must be a power of two no shorter than one line's hop (the DSP rate\n"
            + "  divided by linesPerSecond), and linesPerSecond must divide the DSP rate.\n"
            + "  Remove the settings to use the defaults.");
        return 2;
    }

    Console.WriteLine($"waterfall: {waterfallServer.Url}");

    // The sound card's controls without a key, for the operator who reaches this page over their
    // own LAN or over SSH. Never on a public page: the group is not on one whatever the config
    // says, so opening the endpoint behind it would put the card in reach of strangers and put
    // nothing on the page for the operator. Said rather than ignored quietly.
    openAudioControls = waterfallConfig.AudioControlsOpen;
    if (waterfallConfig.EnableAudioControls && waterfallConfig.Public)
    {
        Console.Error.WriteLine(
            "waterfall: \"enableAudioControls\" is IGNORED on a \"public\" page. The Mixer group "
            + "is the operator's and a public page never carries it; set \"api\".\"key\" and use "
            + "api/mixer if a script has to reach this card.");
    }

    // The same warning the KISS ports carry, for the same reason and now with a sharper one.
    // The page is read-only on a public deployment, but on an operator's own station it carries
    // the transmit test, and there is no password on it.
    // Through the helper this file already uses rather than IPAddress.Parse, which throws on
    // anything that is not an IP literal: the bind is checked at start-up above, so by here it
    // reads, and asking twice with two different parsers is how the two answers drift apart.
    if (!Equals(DaemonConfig.ParseBind(bindAddress), System.Net.IPAddress.Loopback))
    {
        Console.WriteLine(
            "waterfall: WARNING - listening beyond loopback. The page has no authentication, and "
            + (waterfallConfig.Public
                ? "anything that can reach this port can watch this station."
                : "on an operator's page it carries a transmit test: anything that can reach this "
                  + "port can key your transmitter on your licence.")
            + (openAudioControls
                ? " \"enableAudioControls\" is on, so it also carries this sound card's mixer "
                  + "with no key: anything that can reach this port can change your capture gain "
                  + "and the level your transmitter is driven at."
                : ""));
    }
}

await using var waterfallLifetime = waterfallServer;


// The signal survey: watch the whole passband for transmissions this station cannot read, and
// keep the ones worth looking at later (issue #206). Off unless configured - it writes audio to
// disk unattended for as long as the station runs.
//
// Its own spectrum feed rather than the waterfall's. The two compute the same transform at the
// same rate and it is cheap (a 2048-point FFT thirty times a second), and the alternative is a
// survey that only works when somebody has also asked for a browser page, plus a shared
// accumulator fed from two places. What they do share is the band probe below, so what the
// waterfall draws and what the survey calls "ours" can never disagree.
SignalSurvey? survey = null;
ProspectorWorker? prospectorLifetime = null;
Func<IReadOnlyList<ModemProposal>>? proposals = null;
Func<(long Examined, long Read, long Dropped)>? prospectorCounts = null;
if (surveyConfig is not null)
{
    var surveyBands = new List<ModemBand>();
    foreach ((int sub, IModem modem) in channel.Modems.OrderBy(m => m.Key))
    {
        if (ModemBandProbe.TryMeasure(modem, DspRate, out double low, out double high))
        {
            surveyBands.Add(new ModemBand(sub, modem.Mode, low, high, (low + high) / 2));
        }
    }

    // ARDOP is a receive tap rather than an IModem, so nothing enumerable carries it and nothing
    // can probe it. Left out, every ARDOP burst on the channel would be reported as a signal
    // nobody was listening to - which is the opposite of true.
    foreach (ModemConfig ardop in modems.Where(m => DaemonConfig.IsArdop(m.Mode)))
    {
        double centre = ardop.Frequency ?? ArdopChannelBridge.NativeCentreHz;
        double half = (ardop.Bandwidth ?? ArdopChannelBridge.WidestBandwidthHz) / 2;
        surveyBands.Add(new ModemBand(ardop.SubChannel, "ardop", centre - half, centre + half, centre));
    }

    // And the id-beacon ghosts, for exactly the same reason and by exactly the same oversight.
    // A ghost is a receive tap too, so it is not in channel.Modems and cannot be probed - and a
    // NinoTNC ident landing on its frequency was being reported as a signal nobody was listening
    // to, on a frequency where a receiver is listening specifically for it. Placed from the same
    // arithmetic the ghost itself uses, so the two cannot disagree about where it sits; its
    // width is the afsk300 bank's coverage, which is what a ghost is built from.
    if (idBeacons)
    {
        var ghostCentresSeen = new HashSet<long>();
        foreach (ModemConfig psk in modems.Where(m => IdBeaconGhost.AppliesTo(m.Mode)))
        {
            double centre = IdBeaconGhost.CentreHzFor(psk.Frequency);
            if (!ghostCentresSeen.Add((long)Math.Round(centre)))
            {
                continue;   // two PSK modems idented at the same place; one ghost serves both
            }

            surveyBands.Add(new ModemBand(
                psk.SubChannel, "afsk300 (id beacon)",
                centre - IdBeaconGhost.CoverageHalfWidthHz,
                centre + IdBeaconGhost.CoverageHalfWidthHz,
                centre));
        }
    }

    var surveyOptions = new SignalSurveyOptions
    {
        Directory = surveyConfig.Path,
        MaxBytes = surveyConfig.MaxBytes,
        MaxPerHour = surveyConfig.MaxPerHour,
        CooldownSeconds = surveyConfig.CooldownSeconds,
        MarginSeconds = surveyConfig.MarginSeconds,
        MaxSeconds = surveyConfig.MaxSeconds,
        MinPeakSnrDb = surveyConfig.MinPeakSnrDb,
        // The dial is what turns an audio centre into a band frequency in the sidecar - the whole
        // point of a capture is to say where on 40 m the thing was.
        DialFrequencyHz = waterfallConfig?.DialFrequencyHz is > 0
            ? waterfallConfig.DialFrequencyHz
            : receiveDialHz ?? 0,
        Sideband = pageSideband,
    };

    if (surveyConfig.Capture is { Length: > 0 } wanted)
    {
        var verdicts = new List<SurveyVerdict>();
        foreach (string name in wanted)
        {
            if (Enum.TryParse(name, ignoreCase: true, out SurveyVerdict verdict))
            {
                verdicts.Add(verdict);
            }
            else
            {
                Console.Error.WriteLine(
                    $"survey: ignoring unknown capture kind \"{name}\" "
                    + "(unclaimed, missed, unattributed)");
            }
        }

        if (verdicts.Count > 0)
        {
            surveyOptions.Capture = verdicts;
        }
    }

    try
    {
        // The source has to exist before the survey (it reports the geometry the survey is built
        // from) and the survey before the source's sink can use it; nothing runs until audio flows.
        SignalSurvey? pending = null;
        var surveySource = new WaterfallSource(
            DspRate, (index, line) => pending?.AddLine(index, line.Span), 30, 0);
        var created = new SignalSurvey(
            surveyOptions, surveyBands, DspRate,
            surveySource.BinWidthHz, surveySource.LinesPerSecond, surveySource.LineLength);
        pending = created;
        survey = created;

        // The channel gates its receive tap while transmitting, so audio and lines stop together
        // - which is the invariant the survey needs to map a burst back to the audio that carried
        // it. Audio first: a line is stamped with the ring position at the moment it arrives.
        channel.AddReceiveTap(samples =>
        {
            created.AddAudio(samples);
            surveySource.Process(samples);
        });
        channel.FrameReceivedWithQuality += (sub, frame, quality) =>
            created.NoteDecode(sub, frame, quality);
        channel.TransmittingChanged += keyed =>
        {
            if (keyed)
            {
                created.Reset();
            }
        };

        // Onto the page: what the survey has kept, what a budget refused, and each capture as it
        // lands - drawn where it happened, since a capture's frequency and time are exactly the
        // axes the waterfall already has.
        if (waterfallServer is { } display)
        {
            void PushStatus() => display.SetSurveyStatus(
                created.Captured, created.SkippedForBudget, created.Bytes, created.Directory);

            PushStatus();
            created.StatusChanged += PushStatus;
            created.CaptureWritten += (capture, wav) =>
            {
                display.ReportCapture(
                    capture.Verdict.ToString().ToLowerInvariant(),
                    capture.AudioCentreHz,
                    capture.AudioLowHz,
                    capture.AudioHighHz,
                    capture.DurationSeconds,
                    capture.PeakSnrDb,
                    // Age rather than a line index: the survey's spectrum clock is its own, and
                    // by the time a capture is on disk a moment more has passed.
                    (DateTimeOffset.UtcNow - capture.CapturedAt).TotalSeconds,
                    Path.GetFileName(wav));
                PushStatus();
            };
        }

        Console.WriteLine($"survey: {surveyConfig.Path}");

        // The prospector: read each capture back with every mode that could have carried it,
        // and say what modem would have read the traffic. Off unless configured - it is DSP
        // work beside a real-time receiver, and a station that wants its CPU for its modems
        // should have it.
        if (surveyConfig.Propose)
        {
            var prospector = new ModemProspector(
                new ModemProspectorOptions
                {
                    MinCaptures = Math.Max(1, surveyConfig.ProposeMinCaptures),
                    TimeProvider = TimeProvider.System,
                },
                surveyBands,
                surveyOptions.DialFrequencyHz,
                surveyOptions.Sideband);
            var prospectorWorker = new ProspectorWorker(prospector, DspRate, TimeProvider.System);
            prospectorLifetime = prospectorWorker;
            proposals = prospector.Proposals;
            prospectorCounts = () =>
                (prospector.Examined, prospector.Read, prospectorWorker.Dropped);

            // Fed from the capture writer rather than from the burst: a capture that a budget
            // refused is not on disk to read, and one that is has already been written by the
            // time this hears about it.
            created.CaptureWritten += prospectorWorker.Examine;
            prospector.Proposed += proposal =>
                Console.WriteLine($"propose: {proposal.Summary()}");

            // What it looked at, and what came back. Work that leaves no trace until it succeeds
            // is indistinguishable from work that is not happening - which is exactly how it read
            // from outside on the day it went live, where the only evidence a capture had been
            // examined was a thread's CPU counter in /proc.
            prospector.ExaminedCapture += (capture, readings) =>
            {
                if (readings.Count == 0)
                {
                    // Every twenty-five, not every one: most captures are unreadable and a line
                    // each would bury the station's own traffic. Silence still has to be
                    // accounted for, though, or "is it running?" has no answer on a quiet band.
                    if (prospector.Examined % 25 == 0)
                    {
                        Console.WriteLine(
                            $"propose: {prospector.Examined} capture(s) examined, "
                            + $"{prospector.Read} readable, {prospectorWorker.Dropped} skipped "
                            + "for backlog");
                    }

                    return;
                }

                foreach (CaptureReading reading in readings)
                {
                    string who = reading.Source is string source
                        ? $"{source}{(reading.Destination is string to ? ">" + to : "")}"
                        : "no readable callsigns";
                    string progress = prospector.Progress(capture, reading) is var (banked, needed)
                        ? $", {banked} of {needed} occasion(s)"
                        : ", not counted (no verified check sequence)";
                    Console.WriteLine(
                        $"propose: {capture.AudioCentreHz:F0} Hz reads as {reading.Mode} - "
                        + $"{reading.Frame.Length} B, {who}{progress}");
                }
            };
            Console.WriteLine(
                $"propose: on, {CaptureSweep.ModesFor(DspRate).Count} modes per capture, "
                + $"{surveyConfig.ProposeMinCaptures} capture(s) of evidence needed "
                + $"(a twentieth of one core; the receive path is never waited on)");
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"cannot open the survey directory at {surveyConfig.Path}\n"
            + $"  {e.Message}\n"
            + "  Set by \"survey\".\"path\". The service user must be able to write to it;\n"
            + "  remove the \"survey\" section to run without one.");
        return 2;
    }
}

// What this station hears, for a monitoring system to come and read. Fed from the monitor path
// like the frame log, so a frame the station read and did not pass to a host still counts as
// heard - which is the honest answer to "how well am I copying this station".
StationTelemetry? metrics = null;
if (metricsConfig is { Enabled: true })
{
    metrics = new StationTelemetry(
        TimeProvider.System,
        metricsConfig.MaxStations,
        TimeSpan.FromSeconds(Math.Max(1, metricsConfig.FrameWindowSeconds)),
        TimeSpan.FromHours(Math.Max(0.1, metricsConfig.StationIdleHours)));
    channel.FrameReceivedWithQuality += (sub, frame, quality) => metrics.Record(sub, frame, quality);

    if (waterfallServer is { } metricsServer)
    {
        metricsServer.Metrics = metrics;
        Console.WriteLine(
            $"metrics: {metricsServer.Url}metrics (prometheus) and {metricsServer.Url}metrics/frames "
            + "(one point per frame, influx line protocol). No authentication.");
    }
    else
    {
        Console.Error.WriteLine(
            "metrics: WARNING - nothing to serve them on. They ride the waterfall's listener; "
            + "add a \"waterfall\" section with a port, or remove \"metrics\".");
    }
}

using var surveyLifetime = new Disposer(() => survey?.Dispose());
using var prospectorDisposer = new Disposer(() => prospectorLifetime?.Dispose());

// Continuous raw capture: everything the channel hears, chunked to disk, so the run can be
// re-scored offline against a later receiver. Curated bursts are the survey's job; this is
// the unedited stream, and it costs disk by design (the budget prunes oldest-first).
RawCaptureWriter? rawCapture = null;
if (rawCaptureConfig is not null)
{
    try
    {
        rawCapture = new RawCaptureWriter(
            rawCaptureConfig.Path, rawCaptureConfig.MaxBytes, rawCaptureConfig.ChunkMinutes,
            DspRate);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"cannot open the raw-capture directory at {rawCaptureConfig.Path}\n"
            + $"  {e.Message}\n"
            + "  Set by \"rawCapture\".\"path\". The service user must be able to write to it;\n"
            + "  remove the \"rawCapture\" section to run without one.");
        return 2;
    }

    rawCapture.Failed = Console.Error.WriteLine;
    channel.AddReceiveTap(rawCapture.Process);
    Console.WriteLine(
        $"raw capture: {rawCaptureConfig.Path}, {rawCaptureConfig.ChunkMinutes} min chunks at "
        + $"{DspRate} Hz, budget {rawCaptureConfig.MaxBytes / (1024.0 * 1024 * 1024):F1} GB "
        + "(oldest pruned)");
}

using var rawCaptureLifetime = new Disposer(() => rawCapture?.Dispose());

// Ghost demodulators for the station identifications a NinoTNC sends alongside its PSK SSB data
// modes rather than within them. Wired here rather than earlier because a ghost reports to the
// waterfall and the frame log, and both have to exist first; what one is and why is in
// StationFactory.WireIdBeacons, which the many-receiver flavour calls too.
if (idBeacons)
{
    StationFactory.WireIdBeacons(
        channel, modems, DspRate, waterfallServer, frameLog,
        // The survey learns its decodes from the channel's own event, which a receive tap does
        // not raise; without this an ident the station read would be filed as a burst nothing
        // decoded, and charged to the capture budget.
        (sub, frame, quality) => survey?.NoteDecode(sub, frame, quality),
        stationJournal);
}

// The radio's transmit meters, when there is a radio that has them.
M0LTE.Flex.FlexMeters? flexMeters = null;
using var flexMetersLifetime = new Disposer(() => flexMeters?.Dispose());

// A device that keys itself (a Flex: the slice PTT is an API command) or cannot transmit at all
// (an UberSDR) refuses a conflicting --ptt or configured PTT, in its own words.
if (stationDevice.PttRefusal is string pttRefusal && (pttSpec is not null || pttConfig is not null))
{
    Console.Error.WriteLine(pttRefusal);
    return 2;
}

if (pttSpec is not null)
{
    string[] parts = pttSpec.Split(':');
    if (parts.Length >= 2)
    {
        pttConfig = new PttConfig
        {
            Type = parts[0],
            Device = parts[1],
            Line = parts[0] == "serial" && parts.Length > 2 ? parts[2] : null,
            Gpio = parts[0] == "cm108" && parts.Length > 2 ? int.Parse(parts[2]) : null,
        };
    }
    else
    {
        Console.Error.WriteLine("--ptt expects serial:<dev>[:rts|:dtr] or cm108:<hidraw>[:gpio]");
        return 2;
    }
}

// One bind for every listener - KISS, per-modem ports, waterfall, paging and ARDOP.
System.Net.IPAddress listenAddress = DaemonConfig.ParseBind(bindAddress)!;



// Set when the radio's session dies, so the exit code says "retry me" rather than "I am done".
bool radioLost = false;
// Set by the configuration API once a change has been validated and written: the process ends so
// systemd rebuilds the whole station on it. Exit 1, like a lost radio, because that is the code
// Restart=on-failure reopens - exit 2 is "your configuration is wrong" and is deliberately final.
bool restartRequested = false;

using var cancellation = new CancellationTokenSource();

// Runtime configuration, on the waterfall's listener. Off unless a key is set, and refused
// outright if there is no listener to hang it on - an "api" section on a station with no
// waterfall is a setting that would silently do nothing.
// One other thing installs it: "waterfall"."enableAudioControls", which serves /api/mixer, and
// only /api/mixer, with no key. That flag can only come from a config file and only where the
// waterfall is running, so the two refusals below stay the "api" section's own.
// Held beyond the block below because the sound card's mixer is opened much later, with the
// audio device, and has to be handed to the API once it exists.
ConfigApi? runtimeApi = null;
string apiKey = apiConfig?.Key ?? "";
if (benchTxTest is null && (apiKey.Length > 0 || openAudioControls))
{
    if (waterfallServer is null)
    {
        Console.Error.WriteLine(
            "\"api\" is served on the waterfall's HTTP listener, and this station has no "
            + "\"waterfall\" section - add one, or remove \"api\"");
        return 2;
    }

    if (configPath is null)
    {
        Console.Error.WriteLine(
            "\"api\" needs a --config file to read back and to write changes to; a station "
            + "configured entirely from the command line has nothing for it to act on");
        return 2;
    }

    string ephemeralPath = ConfigApi.EphemeralPathFor(configPath);
    ConfigApi configApi = runtimeApi = new ConfigApi(
        apiKey, configPath, ephemeralPath,
        runningJson: () => apiConfigJson,
        ephemeralInForce: apiEphemeralInForce,
        requestRestart: () =>
        {
            // Exit 1, the same door a lost radio uses, because it is the one systemd's
            // Restart=on-failure reopens. Exit 2 is reserved for "your configuration is wrong"
            // and is deliberately not restarted - which is the wrong answer for a change that
            // has already been validated.
            restartRequested = true;
            cancellation.Cancel();
        },
        openAudioControls: openAudioControls);
    waterfallServer.ApiHandler = configApi.HandleAsync;

    // One line, because this is the station's sound card in reach of whoever can reach the page,
    // and an operator who has forgotten the flag is set should be told at every start-up.
    if (openAudioControls)
    {
        Console.WriteLine(
            $"api: audio controls are OPEN - the page's Mixer group and {waterfallServer.Url}"
            + "api/mixer answer with NO key, because \"waterfall\".\"enableAudioControls\" is "
            + "true. Anything that can reach this port can change this card's levels.");
    }

    if (apiKey.Length > 0)
    {
        if (proposals is not null)
        {
            configApi.ServeProposals(proposals, prospectorCounts!);
            Console.WriteLine(
                $"api: modem proposals over {waterfallServer.Url}api/proposals (key required); "
                + "each carries the configuration to POST back to api/config.");
        }

        Console.WriteLine(
            $"api: configuration over {waterfallServer.Url}api/config (key required). "
            + $"POST replaces it for one run; add ?persist=true to write {configPath}.");

        // Said out loud because the whole apply path assumes something will restart the process.
        // Run by hand, nothing will, and "it applied and then the station stopped" is a bad
        // surprise.
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is null)
        {
            Console.Error.WriteLine(
                "api: WARNING - this daemon does not appear to be running under systemd, so an "
                + "applied change will STOP it rather than restart it onto the new configuration.");
        }
    }
}

if (apiEphemeralInForce)
{
    // Loud, and on every start-up it applies to: a station running something other than its own
    // config file is the kind of thing that gets forgotten and then debugged for an hour.
    Console.Error.WriteLine(
        $"api: this station is running a ONE-RUN configuration applied over the API, not "
        + $"{configPath}. Any restart from here returns it to the file.");
}
// The mailcast panel's numbers, keyless and read-only like the operator's page itself, and never
// on a public page: they name the BBS this station delivers to.
if (mailcast is not null && waterfallServer is not null && waterfallConfig?.Public != true)
{
    waterfallServer.ApiHandler = mailcast.Serve(waterfallServer.ApiHandler);
    Console.WriteLine($"mailcast: status on the station page and at {waterfallServer.Url}api/mailcast");
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    StopDeadline.Arm(StopDeadline.For(mailcastConfig));
    cancellation.Cancel();
};

// systemctl stop sends SIGTERM, and until this existed only Ctrl-C ran the graceful path -
// a stopped service skipped every disposal, which on a headless Flex leaks the created slice
// (the radio keeps it, dead handle and all, until its four-slice limit stalls every later
// bring-up). Same route as Ctrl-C: cancel, drain, dispose, slice removed.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        // The unit waits 400 s for a mailcast "after" hook; without hooks a wedged stop is cut
        // off at the 90 s it always was.
        StopDeadline.Arm(StopDeadline.For(mailcastConfig));
        cancellation.Cancel();
    });

var kissServers = new List<KissTcpServer>();

// The same question the journal lines below answer, as state rather than as events: which ports
// have a host on them right now, onto the waterfall's modem labels. A snapshot of every port each
// time one changes - the server drops it when it says nothing new, and a snapshot cannot leave
// the page holding a count that a missed event would have corrected.
void PublishHostPorts()
{
    if (waterfallServer is null)
    {
        return;
    }

    var ports = new List<Packet.SoundModem.Waterfall.HostPortStatus>(kissServers.Count);
    foreach (KissTcpServer server in kissServers)
    {
        ports.Add(new Packet.SoundModem.Waterfall.HostPortStatus(
            server.LocalPort, server.DedicatedSubChannel, server.ClientCount)
        {
            SubChannels = server.Polyglot?.SubChannels,
        });
    }

    waterfallServer.SetHostPorts(ports);
}

// Who is attached to a KISS port, in the journal. A host that quietly drops its TCP session
// stops passing traffic, and from the modem's side that is indistinguishable from a quiet band -
// so the attach and the loss both get a line, and the loss carries its reason where it had one.
void WatchClients(KissTcpServer server)
{
    server.ClientConnected += e =>
    {
        Console.WriteLine(ActivityLog.ClientConnected(
            server.LocalPort, server.DedicatedSubChannel, e, server.Polyglot?.SubChannels));
        PublishHostPorts();
    };
    server.ClientDisconnected += e =>
    {
        Console.WriteLine(ActivityLog.ClientDisconnected(
            server.LocalPort, server.DedicatedSubChannel, e, server.Polyglot?.SubChannels));
        PublishHostPorts();
    };
    server.AcceptFailed += why => Console.Error.WriteLine(
        $"kiss[{server.LocalPort}] accept failed: {why} - listening continues");

    // A dropped oversize frame, said once per host per ten seconds. Said at all because a
    // frame that vanishes with no word looks exactly like a bad link to the host that sent it,
    // and that is how a 2 KiB cap went unnoticed through a whole throughput campaign; said no
    // more often than this because a host that has decided to send 60 kB frames will send a lot
    // of them and the journal should say so once, not once per frame.
    var lastOversizeLog = new Dictionary<string, long>();
    server.FrameOversize += e =>
    {
        string host = e.Remote?.ToString() ?? "(unknown host)";
        long now = Environment.TickCount64;
        lock (lastOversizeLog)
        {
            if (lastOversizeLog.TryGetValue(host, out long last) && now - last < 10_000)
            {
                return;
            }

            lastOversizeLog[host] = now;
        }

        Console.Error.WriteLine(ActivityLog.FrameOversize(server.LocalPort, e));
    };

    // What a host changed with SETHW, or why it was refused - RAM-only state with no other
    // record, so the journal is where an operator learns which waveform their modem is on.
    server.HardwareCommand += e => Console.WriteLine(e.Applied
        ? $"modem {e.SubChannel}: SETHW -> {e.Description}"
        : $"modem {e.SubChannel}: SETHW ignored - {e.Description}");
}

// KISS serves the packet modems, so it starts whenever there are any - ARDOP sharing the
// channel is no longer a reason to withhold it. (It was, when an ARDOP channel carried nothing
// else; gating on the old top-level "ardop" setting would now silently leave the packet modems
// with no host interface at all.)
// A --two-tone/--tone run offers no KISS port: it lives for a few seconds and a host that
// attached to it would lose its session again immediately.
if (benchTxTest is null && modems.Any(m => !DaemonConfig.IsArdop(m.Mode)))
{
    string shown = Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString();

    // The shared port: every modem, addressed by nibble (the QtSoundModem multiplex model).
    var shared = new KissTcpServer(
        channel, kissPort, listenAddress, maxFrameBytes: kissMaxFrameBytes);
    shared.EmitQualityFrames = qualityFrames;
    WatchClients(shared);
    shared.Start();
    kissServers.Add(shared);
    if (qualityFrames)
    {
        Console.WriteLine("rx-quality frames: on (KISS command 0x07, JSON payload)");
    }

    Console.WriteLine($"kiss tcp: {shown}:{shared.LocalPort} (all modems, by sub-channel nibble, "
        + $"frames to {kissMaxFrameBytes} bytes)");

    // Plus a port to itself for any modem that asked for one, so a host that only speaks
    // KISS channel 0 can still reach a modem that is not sub-channel 0.
    // ardop is excluded: its port speaks the ardopcf host interface, not KISS.
    foreach (ModemConfig modemConfig in modems
                 .Where(m => m.Port is not null && !DaemonConfig.IsArdop(m.Mode)))
    {
        var dedicated = new KissTcpServer(
            channel, modemConfig.Port!.Value, listenAddress, subChannel: modemConfig.SubChannel,
            maxFrameBytes: kissMaxFrameBytes);
        dedicated.EmitQualityFrames = qualityFrames;
        WatchClients(dedicated);
        dedicated.Start();
        kissServers.Add(dedicated);
        Console.WriteLine(
            $"kiss tcp: {shown}:{dedicated.LocalPort} (modem {modemConfig.SubChannel} "
            + $"{modemConfig.Mode} only, as nibble 0)");
    }

    // Overlaid modems behind one port, each frame sent in the mode its next hop was last heard
    // in (#450). Validation has already checked every member is a packet modem on this station.
    string ModeOn(int sub) => modems.FirstOrDefault(m => m.SubChannel == sub)?.Mode ?? "?";
    foreach (PolyglotConfig polyglotConfig in polyglotPorts)
    {
        var router = new PolyglotRouter(
            polyglotConfig.SubChannels, polyglotConfig.Default!.Value,
            TimeSpan.FromMinutes(polyglotConfig.ForgetAfterMinutes));
        var polyglot = new KissTcpServer(
            channel, router, polyglotConfig.Port!.Value, listenAddress, maxFrameBytes: kissMaxFrameBytes);
        polyglot.EmitQualityFrames = qualityFrames;
        WatchClients(polyglot);
        router.Learned += e => Console.WriteLine(
            ActivityLog.PolyglotLearned(polyglot.LocalPort, e, ModeOn, router.DefaultSubChannel));
        polyglot.Start();
        kissServers.Add(polyglot);
        Console.WriteLine(
            $"kiss tcp: {shown}:{polyglot.LocalPort} (polyglot: modems "
            + string.Join(", ", router.SubChannels.Select(s => $"{s} {ModeOn(s)}"))
            + $", as nibble 0; unheard stations get modem {router.DefaultSubChannel}, "
            + $"forgotten after {polyglotConfig.ForgetAfterMinutes:0.##} min)");
    }

    // The opening state, once every port is up: no host attached yet, or - if a node beat the
    // display to it - however many already are. Without this the page would show nothing about
    // attachment until the first connect or disconnect, which on a settled station is never.
    PublishHostPorts();

    if (channel.ReceiveOnlyReason is not null)
    {
        // The usual "anything that can reach this can transmit on your licence" warning is not
        // true here, and saying it anyway would teach operators to ignore it where it is.
        Console.WriteLine(
            "kiss: this station receives only - frames arriving on these ports are refused, not "
            + "transmitted. Everything the modems hear is still delivered.");
    }
    else if (!Equals(listenAddress, System.Net.IPAddress.Loopback))
    {
        Console.WriteLine(
            "kiss: WARNING - listening beyond loopback. KISS has no authentication: anything "
            + "that can reach these ports can transmit on your licence.");
    }
}

await using var kissLifetime = new KissServerSet(kissServers);

M0LTE.Ardop.Host.ArdopHostServer? ardopServer = null;
if (benchTxTest is null && ardopModem is not null)
{
    // ARDOP runs its own channel discipline (ARQ timing budgets, negotiated leaders), so its
    // own bursts must never wait on a p-persistence roll - they go out through the channel's
    // inhibit-bypassing path. The packet modems keep normal CSMA among themselves; what keeps
    // them off an ARQ session is TransmitInhibit, set once the engine exists.
    // Bind the M0LTE.Ardop TNC to this daemon's channel: transmit bursts through the
    // channel-access path, receive audio via a channel tap (the old ForChannel glue,
    // now that the package is audio-device-agnostic).
    // The engine rate, not the channel rate: the centre shift runs at the engine rate however
    // wide the channel is, so its Nyquist is the bound that matters.
    if (ardopModem.Frequency is double ardopCentre
        && ArdopChannelBridge.Concern(
            ardopCentre,
            M0LTE.Ardop.ArdopModulator.SampleRate,
            // The cap, not the widest ARDOP has: on a station held to 500 Hz there is no 2000 Hz
            // session to be clipped, and a warning about one would send an operator looking for
            // a fault that cannot happen.
            M0LTE.Ardop.Arq.ArdopBandwidthExtensions.Hertz(
                ArdopChannelBridge.ArqBandwidthFor(ardopModem.Bandwidth)))
            is string ardopConcern)
    {
        Console.Error.WriteLine($"ardop: WARNING - {ardopConcern}");
    }

    if (channel.ReceiveOnlyReason is not null)
    {
        Console.Error.WriteLine(
            "ardop: WARNING - ARDOP is a connected-mode ARQ protocol and this station cannot "
            + "transmit, so no session will ever complete: it will hear the channel and never "
            + "answer. The host port is still served, and every frame it demodulates - including "
            + "other stations' sessions - is still drawn and written down.");
    }

    var ardopShift = ArdopChannelBridge.For(
        ardopModem.Frequency, M0LTE.Ardop.ArdopModulator.SampleRate, DspRate);

    // A reply that cannot make its turnaround is taken back rather than keyed late: see
    // ArdopReplyWindow for why a late ARQ frame is worse than a missing one.
    var ardopReplies = new ArdopReplyWindow(TimeProvider.System);
    var ardopLeaseQuiet = new LeaseQuietLine(
        TimeProvider.System,
        (holder, more) => Console.Error.WriteLine(
            $"ardop: reply not sent - sub-channel {holder} holds the transmit lease"
            + (more > 0 ? $" (and {more} more in the last minute)" : "")));
    var ardopTnc = new M0LTE.Ardop.Host.ArdopHostTnc(
        captureDevice: captureDeviceKey ?? device, playbackDevice: playbackDeviceKey ?? device)
    {
        // Awaited rather than fire-and-forget: the TNC's transmit worker does not survive an
        // exception out of this delegate, and on a receive-only channel every burst is refused.
        // Catching turns "ARDOP silently stops working" into a line saying why.
        Transmitter = async audio =>
        {
            // Not even queued while a transmit lease is held: the lease refuses every ARDOP burst,
            // and an ARQ session replies several times a second, which would be a journal line
            // each. Said once, then at most once a minute with a count, the same rule the DROPPED
            // line keeps.
            if (channel.TransmitLease.Holder is int leaseHolder)
            {
                ardopLeaseQuiet.Note(leaseHolder);
                return;
            }

            CancellationToken turnaround = ardopReplies.Open();
            try
            {
                await channel.EnqueueTransmit(
                    _ =>
                    {
                        var floats = new float[audio.Length];
                        for (int i = 0; i < audio.Length; i++)
                        {
                            floats[i] = Packet.SoundModem.Audio.Pcm16.ToFloat(audio[i]);
                        }

                        return ardopShift.Transmit(floats);
                    },
                    rejected: null,
                    // ARDOP owns this channel's timing: its bursts wait on neither the inhibit nor
                    // the p-persistence roll.
                    ownsChannelTiming: true,
                    // The shifter identifies the ARDOP transmitter, so its own consecutive bursts
                    // share a keyup and nothing else joins one: an ARQ turnaround must not be held
                    // up by a packet frame appended behind it, nor a packet frame by ARDOP.
                    source: ardopShift,
                    // Taken back if the channel cannot carry it inside its turnaround. Withdrawal
                    // is silent once the keyup is under way, which is the behaviour wanted: past
                    // that point the burst is on the air and there is nothing to decide.
                    withdraw: turnaround).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Not an error and not a failure to transmit - a reply this station declined to
                // send late. Recorded here and reported by name below, where the frame is known.
                // Swallowed rather than rethrown because the TNC's transmit worker does not
                // survive an exception out of this delegate, least of all a cancellation: its
                // own loop treats one as shutdown and stops reading the queue for good.
                ardopReplies.NoteDropped();
            }
            catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"ardop: transmission dropped - {refused.Message}");
            }
            finally
            {
                ardopReplies.Close();
            }
        },
    };
    channel.AddReceiveTap(samples => ardopTnc.ProcessReceive(ardopShift.Receive(samples)));

    // The configured width is the TNC's cap, not just the planner's reservation. Without this
    // the station kept the library default of 2000MAX however narrow a slot the band plan had
    // put it in, and ARQBW governs both ends of that: what it accepts as IRS and what it asks
    // for as ISS (issue #459). CALLBW is deliberately left at UNDEFINED, which means "use
    // ARQBW": two settings that have to agree is a way to be half-configured, and a host that
    // wants a narrower single call can still send its own CALLBW.
    ardopTnc.Config.ArqBandwidth = ArdopChannelBridge.ArqBandwidthFor(ardopModem.Bandwidth);

    // ARDOP demodulates inside the virtual TNC, so its frames never reach the channel event the
    // waterfall and the frame log listen to - without this the ARDOP band is drawn and its
    // bursts paint, but nothing it hears is ever listed. M0LTE.Ardop 0.3.0 raises every frame
    // the demodulator recovers, including other stations' sessions and failed decodes, which is
    // what a monitor wants: "someone transmitted and we could not read them" is information.
    // The survey listens on the same event and for the same reason. It judges a burst against
    // what the station read, and it learns that from NoteDecode on the channel's decode event -
    // which ARDOP does not raise either. Without this, every ARDOP transmission the station
    // successfully copies is still filed as a burst inside a configured band that nothing
    // decoded: "missed". On the live 40 m station that was 15 of 33 misses, the whole ARDOP
    // slot reading as a modem that does not work.
    //
    // Wired unconditionally, not just when a waterfall, a frame log or a survey exists: the
    // journal write below is what closes #478 and journalctl is a station's console whatever
    // else is configured, so it must not depend on any of the three.
    {
        int ardopSub = ardopModem.SubChannel;
        double? ardopAudioHz = ardopModem.Frequency ?? ArdopChannelBridge.NativeCentreHz;
        double? ardopRfHz = ardopModem.RfFrequency;
        ardopTnc.FrameDecoded += frame =>
        {
            // Before anything else, and whether or not it decoded: a burst on the channel is
            // proof that the silence a queued reply belonged in is over, so that reply is taken
            // back rather than transmitted into the exchange that has moved on without it. Raised
            // ahead of the engine seeing the frame, so the fresh reply this may produce opens a
            // window of its own and is unaffected.
            ardopReplies.FarEndTransmitted();

            byte[] data = frame.Data ?? [];
            // Caller/Target are carried in clear by the connect handshake and ID frames; a data
            // frame in someone else's session carries neither, and is listed unattributed.
            string? from = string.IsNullOrWhiteSpace(frame.Caller) ? null : frame.Caller;
            string? to = string.IsNullOrWhiteSpace(frame.Target) ? null : frame.Target;

            // ARDOP measures a signal-to-noise figure for a Ping it decodes, and gets one echoed
            // back in a PingAck; every other frame type carries a zero SnDb that was never
            // computed. Passing that zero straight through claimed a measurement on every row
            // that was not a Ping or a PingAck - a frame that decoded perfectly reading as a
            // station on the edge of the noise (#479). Switched on the frame-type code rather
            // than frame.Name: Name is a display string owned by M0LTE.Ardop for a human to read,
            // and if the package ever renamed it both arms below would silently stop matching -
            // this gating would turn itself off with nothing failing. Type is the wire code the
            // two constants are defined against, so a rename of the label cannot touch it.
            double? snDb = frame.Type switch
            {
                M0LTE.Ardop.ArdopFrameType.Ping => frame.SnDb,
                M0LTE.Ardop.ArdopFrameType.PingAck => frame.PingAckSnDb,
                _ => null,
            };

            // And into the station's journal, alongside what it heard from every other modem:
            // nothing ARDOP heard ever reached journalctl before this, because it demodulates
            // inside the virtual TNC rather than through the channel event the journal
            // subscribes to (#478).
            stationJournal.Write(ActivityLog.ArdopReceived(
                ardopSub, frame.Name, from, to, data.Length, frame.Ok, frame.Quality, snDb));

            waterfallServer?.ReportFrame(
                ardopSub, frame.Name, from, to, data.Length, snDb, frame.Ok, frame.Quality);

            var quality = new FrameQuality(
                "ardop", data.Length, CorrectedBytes: null, CrcValid: frame.Ok,
                FrequencyOffsetHz: null, EmphasisDb: null,
                Quality: frame.Quality, ArdopSnDb: snDb);

            frameLog?.Record(
                ardopSub, data, quality, ardopAudioHz, ardopRfHz,
                modeName: $"ARDOP {frame.Name}");

            // Only a frame that actually decoded says the station read the burst. A failed
            // decode is exactly the case the survey exists to catch, and telling it otherwise
            // would hide the one thing worth capturing in this band.
            if (frame.Ok)
            {
                survey?.NoteDecode(ardopSub, data, quality, ax25: false);
            }
        };

        // And our own transmissions, which the panel never listed at all. ARDOP transmits as a
        // whole virtual TNC through the channel's audio path rather than as an IModem, so the
        // channel's FrameTransmitted event - which is what puts every other mode's traffic in
        // the panel and the log - never fires for it. On the live 40 m station that meant a
        // whole ARQ session left no trace: ten connect requests over forty seconds, an empty
        // panel, and nothing to tell an operator apart from a modem that did nothing (#471).
        // The TNC hands the frame over once the burst has gone out, named the way the station
        // hearing it names it, so our ConReq500M row reads as their row for the same burst
        // does, and a logged transmission is still one that actually went on air.
        ardopTnc.FrameTransmitted += frame =>
        {
            // The TNC raises this for every burst it hands the transmitter, and one it handed
            // over was taken back before it reached the air. Written down as what it was and
            // then dropped here: no journal transmission, no frame-log row and no burst on the
            // waterfall, because a station that did not transmit must not be told it did.
            if (ardopReplies.TakeDrop() is var why && why != ArdopReplyDrop.None)
            {
                stationJournal.Write(ActivityLog.ArdopReplyDropped(ardopSub, frame.Name, why));
                return;
            }

            byte[] data = frame.Data ?? [];
            // Ours to state rather than parse: ARDOP carries both callsigns in clear in the
            // connect handshake and in ID frames, and nothing else it sends carries one.
            string? from = string.IsNullOrWhiteSpace(frame.Caller) ? null : frame.Caller;
            string? to = string.IsNullOrWhiteSpace(frame.Target) ? null : frame.Target;

            // Journalled beside every other transmission this station makes, for the same
            // reason the receive side is above: until now an ARQ session keyed the radio ten
            // times in a row and journalctl said nothing about any of them (#478).
            stationJournal.Write(
                ActivityLog.ArdopTransmitted(ardopSub, frame.Name, from, to, data.Length));

            waterfallServer?.ReportTransmittedFrame(ardopSub, frame.Name, from, to, data.Length);

            // "ardop" in the mode column, as the receive rows carry, and the frame type in the
            // name: one query for what this modem did includes both directions, and the panel
            // still says which frame it was.
            frameLog?.RecordTransmitted(
                ardopSub, data, "ardop", ardopAudioHz, ardopRfHz,
                modeName: $"ARDOP {frame.Name}");
        };
    }

    // Hold the packet modems off the air for the length of an ARQ session. Their frames are
    // queued, not discarded, until TransmitInhibitTimeout gives up on one - an AX.25 host will
    // have retried long before a Winlink session ends.
    M0LTE.Ardop.Arq.ArdopArqEngine ardopEngine = ardopTnc.Engine;
    channel.TransmitInhibit = () => ardopEngine.IsConnected || ardopEngine.IsPending;

    // Tell the TNC when somebody else is using the slot. The package implements no detector by
    // design (no audio device, no spectrum): it exposes ChannelBusy and leaves the decision to
    // whoever owns the audio, which is us. Unwired, the seam is dormant and the station
    // transmits regardless of what is on the channel, which is what it did until now.
    //
    // The band is the ARDOP modem's own slot, not the channel's aggregate ChannelBusy: the
    // question the band plan coordinates is whether THIS slot is in use, and deferring to packet
    // traffic in slots ARDOP does not occupy would be a different and more timid answer. See
    // ArdopBusyDetector for the measured separation.
    //
    // The engine samples this only in protocol state DISC, as ardopcf does, so it gates starting
    // a session and can never stall an in-flight burst. That distinction is load-bearing: an IRS
    // that misses its ACK window loses the link, and ARDOP bursts bypass channel access (#171)
    // precisely so they never wait.
    // Off unless the modem entry asks for it. It reads busy almost continuously on a real
    // band: out-of-band FT4 lifts the noise floor across the whole passband in step with its
    // transmit cycle, and an energy meter cannot tell a lifted floor from in-band signal.
    // See DaemonConfig.ModemConfig.BusyDetect for the measurement and for what would fix it.
    ArdopBusyDetector? ardopBusy = null;
    if (ardopModem.BusyDetect)
    {
        ardopBusy = new ArdopBusyDetector(
            ardopModem.Frequency ?? ArdopChannelBridge.NativeCentreHz,
            M0LTE.Ardop.Arq.ArdopBandwidthExtensions.Hertz(ardopTnc.Config.ArqBandwidth),
            DspRate);
        ArdopBusyDetector detector = ardopBusy;
        channel.AddReceiveTap(detector.Process);
        ardopTnc.Config.ChannelBusy = () => detector.Busy;
    }

    int ardopCommandPort = ardopModem.Port ?? 8515;
    ardopServer = new M0LTE.Ardop.Host.ArdopHostServer(
        ardopTnc, ardopCommandPort, listenAddress, ownsTnc: true);
    ardopServer.Start();
    Console.WriteLine(
        $"ardop host tcp: {(Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString())}:{ardopServer.LocalCommandPort} (data {ardopServer.LocalDataPort}, "
        + $"ardopcf-compatible virtual TNC, modem {ardopModem.SubChannel}, ARQBW "
        + $"{M0LTE.Ardop.Arq.ArdopBandwidthExtensions.Hertz(ardopTnc.Config.ArqBandwidth)}MAX"
        + $"{ardopShift.Describe()}"
        + (ardopBusy is null ? "" : $", busy watch {ardopBusy.LowHz:F0}-{ardopBusy.HighHz:F0} Hz")
        + ")");
}
await using var ardopLifetime = ardopServer;

// Which sub-channel this station's own service transmissions are filed under in the frame log and
// the frames panel: the operator's test tone and a POCSAG page, neither of which is a modem's
// traffic and neither of which has a sub-channel of its own. A label, not a choice: the transmit
// path is shared - one output device and one PTT line, whichever modem's frame is going out - so
// a test measures the same path whatever this says. It is the sub-channel a KISS frame on port 0
// would reach: 0 where there is a modem there, and the lowest configured otherwise.
int serviceTxSubChannel = modems.Any(m => m.SubChannel == 0)
    ? 0
    : modems.Count > 0 ? modems.Min(m => m.SubChannel) : 0;

Packet.SoundModem.Pocsag.PagingTcpServer? pagingServer = null;
if (benchTxTest is null && paging is not null)
{
    var polarity = paging.InvertPolarity
        ? M0LTE.Pocsag.PocsagPolarity.Inverted
        : M0LTE.Pocsag.PocsagPolarity.Normal;
    pagingServer = new Packet.SoundModem.Pocsag.PagingTcpServer(
        channel, paging.Port, paging.Baud, polarity, listenAddress);

    // A page the server said OK to and then could not send has no client left to tell -
    // the journal is the only place the loss can be recorded, exactly as for KISS frames.
    pagingServer.PageDropped += drop => Console.Error.WriteLine(
        $"page[{drop.Id}] to {drop.Ric} DROPPED: {drop.Reason}");

    // And the page that did go out, which was written down nowhere at all: paging hands the
    // channel audio rather than an addressed frame, so it raises none of the events the frames
    // panel and the frame log are built on, and a station that had paged all day answered "what
    // did I put on the air" with silence (#473). Listed like the operator's test tone, which is
    // not a frame either: the payload is the sentence describing what went out, the RIC is who it
    // was to, and there is no audio or RF centre to state - POCSAG is baseband FSK, and the
    // paging section has no frequency of its own to report.
    string pagingMode = pagingServer.Mode;
    pagingServer.PageSent += page =>
    {
        Console.WriteLine($"page[{page.Id}] to {page.Ric} sent ({pagingMode})");
        byte[] record = System.Text.Encoding.ASCII.GetBytes(page.Summary);
        waterfallServer?.ReportTransmittedFrame(
            serviceTxSubChannel, pagingMode, from: null,
            to: page.Ric.ToString(CultureInfo.InvariantCulture), record.Length);
        frameLog?.RecordTransmitted(
            serviceTxSubChannel, record, pagingMode, audioHz: null, rfHz: null);
    };
    pagingServer.AcceptFailed += why => Console.Error.WriteLine(
        $"paging: accept failed: {why} - listening continues");
    pagingServer.Start();
    Console.WriteLine($"paging tcp: {(Equals(listenAddress, System.Net.IPAddress.Any) ? "0.0.0.0" : listenAddress.ToString())}:{pagingServer.LocalPort} ({pagingServer.Mode}, DAPNET/POCSAG-compatible)");
}
await using var pagingLifetime = pagingServer;

// Rig control through Hamlib's rigctld (the "rig" section): the band plan's dial, a rigctld PTT
// and the tuning windows /api/rig/tune opens. Started before the audio and the PTT, because a
// rigctld PTT keys through it, and so the dial is set before the station starts listening. A
// rigctld that does not answer is a warning and is retried in the background, unless
// "rig"."required" says it is worth stopping for. Absent, nothing talks to a rig, as before.
RigControl? rig = null;
if (rigConfig is not null)
{
    RigctldEndpoint rigEndpoint = RigctldEndpoint.TryParse(rigConfig.Rigctld, out _)!;
    // The band plan's dial, in the plan's own sideband, with the plan's passband width unless the
    // operator said otherwise. No plan, no dial: a station placed by audio centre keeps whatever
    // the operator tuned the rig to, exactly as it would without this section.
    RigTuning? rigPlan = bandPlan is null
        ? null
        : new RigTuning(
            (long)Math.Round(bandPlan.DialHz),
            rigConfig.Mode?.ToUpperInvariant() ?? RigModes.ForSideband(bandPlan.Sideband),
            rigConfig.PassbandHz ?? (bandPlan.IsFm ? 0 : (int)Math.Round(bandPlan.Window.WidthHz)));
    rig = new RigControl(new RigControlOptions
    {
        Endpoint = rigEndpoint,
        KeysThroughRig = pttConfig?.Type == "rigctld",
        Plan = rigPlan,
        // A window's restore target is written down while it is open, so a station killed
        // mid-window puts the rig back at its next start-up before it transmits anything.
        RestoreFile = RigStation.RestoreFilePath(configPath, rigEndpoint),
        TransmitPending = () => channel.TransmitQueued,
    });
    rig.Journal += stationJournal.Write;
    rig.Problem += stationJournal.WriteError;
    if (rigPlan is null)
    {
        Console.WriteLine("rig: no band plan (no \"rfFrequency\"), so the dial is left where the rig has it");
    }

    bool rigAnswered = await rig.StartAsync(cancellation.Token);
    if (!rigAnswered && rigConfig.Required)
    {
        Console.Error.WriteLine(
            $"rig: rigctld at {rigEndpoint} did not answer, and \"rig\".\"required\" is true in "
            + $"{configPath}. Start rigctld (rigctld -m <model> -r <port>), or set \"required\" to "
            + "false to run without it. The service keeps retrying.");
        await rig.DisposeAsync();
        return 1;
    }
}

await using var rigLifetime = rig;

// A station whose passband does not reach the mailcast signal: the rig goes to it around each slot.
// Placement only chooses this with a "rig" section, so the rig is there.
if (mailcast is not null && mailcastPlacement is { Retunes: true })
{
    MailcastReceiver retuned = mailcast;
    mailcast.Retuner = new MailcastRetuner(
        rig!, mailcastConfig!, rigConfig?.Mode, () => retuned.Slots.Timetable, TimeProvider.System,
        MailcastStation.Journal(stationJournal), mailcast.Hooks);
}

// Audio + PTT: the device opens itself (see StationDevice and the kinds in Devices/): a FlexRadio
// DAX triplet (--device flex:...), an UberSDR web receiver's IQ stream (--device ubersdr:...,
// receive only), an OpenWebRX receiver's audio (--device openwebrx:..., receive only), a pair
// of pipes, a recording (--wav-loop), or an ALSA card. Each surfaces through the same
// IAudioInput/IAudioOutput/IPttControl the channel already speaks, so KISS packet, POCSAG paging
// and ARDOP all get every transport for free.
// Keyed off the modem entry, not the legacy --ardop flag: a station configuring ARDOP the
// documented way (a "mode": "ardop" modem entry) wants the deeper buffer just as much.
int flexPacketBuffer = ardopModem is null ? 3 : 6;
DeviceOpening opened = await stationDevice.OpenAsync(new DeviceOpenContext
{
    DspRate = DspRate,
    ConfigPath = configPath,
    Journal = stationJournal,
    Channel = channel,
    Waterfall = waterfallServer,
    RadioLost = () =>
    {
        radioLost = true;
        cancellation.Cancel();
    },
    Cancellation = cancellation.Token,
    Sideband = bandPlan?.Sideband ?? sideband,
    ReceiveDialHz = receiveDialHz,
    FlexPacketBuffer = flexPacketBuffer,
    FlexTuning = flexTuning,
    TransmitBands = txBands,
    CaptureRate = captureRate,
    CaptureDeviceKey = captureDeviceKey,
    PlaybackDeviceKey = playbackDeviceKey,
    Alsa = alsaConfig,
    Ptt = pttConfig,
    Rig = rig,
});

if (opened.ExitCode is int openRefused)
{
    return openRefused;
}

IPttControl ptt = opened.Ptt;
IAudioOutput playback = opened.Playback;
IAudioInput input = opened.Input;
FlexRuntime? flex = opened.Flex;
flexMeters = opened.FlexMeters;
// Whether a web receiver input (UberSDR in either of its forms, or OpenWebRX) has a session to be
// starved of. Null for every other device: their quiet is never deliberate.
Func<bool>? webReceiverSessionLive = opened.SessionLive;
// Set only on the ALSA path: the sound card is the one device with xrun counters, and they are
// the difference between "the band is quiet" and "this machine will not schedule us".
AlsaAudioOutput? alsaOut = opened.AlsaOut;
AlsaAudioInput? alsaIn = opened.AlsaIn;
// The card's mixer, on the ALSA path only. Opened whether or not the configuration sets
// anything, so the start-up log records the level the station is actually listening at - but
// nothing is written to the card unless a key in "alsa"."mixer", or a change remembered in the
// state file from an earlier run, said so.
AlsaMixer? mixer = opened.Mixer;
// The transmit card's mixer, on a station that transmits through a different card from the one
// it receives on; null everywhere else, where "mixer" does both.
AlsaMixer? playbackMixer = opened.PlaybackMixer;
MixerRuntime? mixerRuntime = opened.MixerRuntime;
string mixerWhyNot = opened.MixerWhyNot;

using AlsaMixer? mixerLifetime = mixer;
using AlsaMixer? playbackMixerLifetime = playbackMixer;

// The operator page's mixer group and any script that wants the card's state come through here,
// under the same key as every other change - or under no key at all, where
// "waterfall"."enableAudioControls" says so. A station with no mixer says so rather than 404ing,
// so the page can tell "no mixer here" from "this daemon is too old to have the endpoint".
if (mixerRuntime is MixerRuntime liveMixer)
{
    runtimeApi?.ServeMixer(liveMixer);
}
else
{
    runtimeApi?.NoMixer(mixerWhyNot);
}

await using var flexLifetime = flex;

// A keyup that fails must say so: the loop survives it (faulting the queued frames), and
// this line is the only place the operator learns why nothing went out - "another station
// holds the PA" on an arbitrated radio, or a dead PTT lead anywhere else.
channel.PttFailed += failure => Console.Error.WriteLine($"ptt: {failure.Message}");

// A rig's tuning window holds the transmitter: the dial is somewhere this station's modems are
// not, so a frame sent then would land on the wrong frequency. Ordinary frames wait behind the
// inhibit (composed over whatever is already installed, never instead of it) and the PTT itself
// refuses a keyup for anything that gets past it. Installed only with a "rig" section, so a
// station without one is exactly as it was.
IPttControl stationPtt = rig is null ? ptt : rig.HoldTransmissions(channel, ptt);
Task transmitter = channel.RunTransmitterAsync(playback, stationPtt, cancellation.Token);

// The mailcast receiver's delivery into the BBS, its rig retuning and its hooks, once the
// transmitter is held behind the rig: the first window may open at once, on a station started
// mid-slot. On the way out it puts the rig back and then runs an "after" hook still owed, so
// the shutdown below waits for that (up to its timeout; the unit's TimeoutStopSec allows it).
Task? mailcastRun = mailcast is null
    ? null
    : Task.Run(() => mailcast.RunAsync(cancellation.Token));

// A transmitter that dies (the output device failed mid-keyup) must stop the daemon, not
// leave it running as a healthy-looking receive-only station for the rest of the process's
// life - which is what discarding the task's fault used to do, silently. Exit 1 so the
// unit restarts: a device that comes back is exactly what a restart fixes, and exit 2
// stays reserved for "your configuration is wrong".
_ = transmitter.ContinueWith(
    t =>
    {
        Console.Error.WriteLine(
            $"transmit: the audio output failed - {t.Exception!.GetBaseException().Message}. "
            + "Stopping so the service restarts.");
        radioLost = true;
        cancellation.Cancel();
    },
    CancellationToken.None,
    TaskContinuationOptions.OnlyOnFaulted,
    TaskScheduler.Default);

// ---------------------------------------------------------------- TX test
// The operator's transmitter test. Built here because this is the first point at which the
// answer to "can this station key at all" is settled: the PTT line has been opened or found not
// to exist, and the transmitter loop above is running. It is offered three ways - the operator
// page's control, POST /api/txtest, and --two-tone/--tone - and all three go through this one
// runner, so there is one set of rules and one cap rather than three.
string? txTestRefusal =
    !txTestConfig.Enabled
        ? "\"txTest\".\"enabled\" is false in this station's configuration"
    : channel.ReceiveOnlyReason is string txTestReceiveOnly
        ? txTestReceiveOnly
    : ptt is NullPtt
        // Refused rather than left to VOX, deliberately. A test transmission has to be keyed and
        // unkeyed by the station that made it; a VOX trip is the radio deciding, and "the daemon
        // put a carrier up and something else decided when to stop" is not a transmission anybody
        // should be able to start from a web page.
        ? "no \"ptt\" is configured, so this daemon does not key the radio"
    : null;

var txTestRunner = new TxTestRunner(new TxTestOptions
{
    Channel = channel,
    Journal = stationJournal,
    DefaultSeconds = txTestConfig.Seconds,
    MaxSeconds = txTestConfig.MaxSeconds,
    Amplitude = txTestConfig.Amplitude,
    Refusal = txTestRefusal,
    SubChannel = serviceTxSubChannel,
    Report = status => waterfallServer?.ReportTxTest(status),
    Recorded = record =>
    {
        // Written down where transmissions are written down. The frame log's rows are frames and
        // a tone burst is not one, so what the payload holds is the sentence describing what went
        // out - which is what the panel shows beside the row, and what a monitor site is sent for
        // a station that publishes to one. See docs/reference/config.md under "frameLog".
        frameLog?.RecordTransmitted(
            record.SubChannel,
            record.Payload,
            Packet.SoundModem.Waterfall.WaterfallWebServer.TestTransmissionMode,
            record.AudioHz,
            // No rf_hz: a test tone is not a modem with a planned RF centre, and a dial plus an
            // audio frequency is only the answer on one sideband of one kind of station.
            rfHz: null);
        waterfallServer?.ReportTestTransmission(
            record.SubChannel, record.Text, record.Payload.Length);

        // A test is a transmission, so it owes the same identification a frame does. The ident
        // clock is normally started by channel.FrameTransmitted, which a test never raises: it
        // goes out through the delegate overload and carries no sub-channel of its own. Without
        // this a station could key for tones all afternoon and never say who it was.
        if (identifiers.TryGetValue(record.SubChannel, out StationIdentifier? owedForTest))
        {
            owedForTest.NoteTransmission();
        }
    },
});

// Never on a public page: a test transmission is the operator's own act and the control belongs
// on the operator's own page, which is the only page this daemon serves that is not public.
if (waterfallServer is not null && waterfallConfig?.Public != true)
{
    waterfallServer.SetTxTest(txTestRunner.Control);
}

runtimeApi?.ServeTxTest(txTestRunner);

// The transmit lease (POST /api/txlease): one sub-channel takes the transmitter for a while -
// pdn-mailcast's daily broadcast - and everyone else's transmissions are refused until it gives it
// back or stops renewing. The refusals are the channel's own and reach the journal through the
// rate-limited DROPPED line above; what the lease itself does is journalled here.
var txLeaseJournal = new TxLeaseJournal(
    stationJournal,
    sub => channel.Modems.TryGetValue(sub, out IModem? leased) ? leased.Mode : "no modem");
channel.TransmitLease.Changed += txLeaseJournal.Note;
channel.TransmitLease.CarrierWaitCutShort += txLeaseJournal.NoteCarrierWaitCutShort;
runtimeApi?.ServeTxLease(channel, channel.ReceiveOnlyReason);
if (mailcastMeasure is not null)
{
    runtimeApi?.ServeMailcastMeasure(mailcastMeasure);
}
if (rig is not null && runtimeApi is not null && waterfallServer is not null)
{
    runtimeApi.ServeRig(rig);
    Console.WriteLine(
        $"api: rig control over {waterfallServer.Url}api/rig and api/rig/tune (key required)");
}
if (txTestRefusal is null)
{
    stationJournal.Write(
        $"tx test: ready - two-tone {Packet.SoundModem.Audio.TestTone.TwoToneLowHz:F0}+"
        + $"{Packet.SoundModem.Audio.TestTone.TwoToneHighHz:F0} Hz or a single tone, "
        + $"{txTestConfig.Seconds:0.#} s by default, capped at "
        + $"{Math.Clamp(txTestConfig.MaxSeconds, 1, TxTestRunner.CeilingSeconds):0.#} s");
}
else
{
    stationJournal.Write($"tx test: unavailable - {txTestRefusal}");
}

// Identification. Two halves: every frame a configured modem sends starts its clock, and a
// slow poll sends the ident once one falls due. Polling rather than a timer per modem because
// the decision is StationIdentifier's and depends on traffic as well as time - there is no
// instant to schedule in advance, only a condition to notice - and at a ten-minute interval a
// five-second granularity costs nothing.
if (identifiers.Count > 0)
{
    channel.FrameTransmitted += (sub, _) =>
    {
        if (identifiers.TryGetValue(sub, out StationIdentifier? owed))
        {
            owed.NoteTransmission();
        }
    };

    // One identification, sent and written down. Also what a transmit lease closes with: as the
    // lease is released or runs out, the holder's modem identifies before anyone else may key
    // (Ofcom wants an ident at the end of a transmission), if it has transmitted since it last did.
    async Task IdentifyAsync(int sub, StationIdentifier owed)
    {
        try
        {
            await IdentTransmission.SendAsync(channel, owed).ConfigureAwait(false);

            // Stamped only on success: an ident the radio refused was not sent, and
            // clearing the debt for it would mean the station quietly stopped identifying.
            owed.NoteIdentified();
            Console.WriteLine($"id[{sub}] {owed.Text} in CW");

            // And written down where transmissions are written down, on the same terms as
            // the operator's test tone. An ident keys the radio, and until now it left
            // nothing behind but that console line: a station could identify every ten
            // minutes all afternoon and have no record of having transmitted at all
            // (#473). It is not a frame, so the payload is the sentence describing what
            // went out; the callsign is ours to state rather than parse out of prose, so
            // the row is attributed the way an ARDOP one is.
            byte[] identRecord =
                System.Text.Encoding.ASCII.GetBytes(owed.TransmissionRecord);

            // Where the ident actually lands on the band, which is not the modem's own
            // centre: the dial plus its tone on USB, minus it on LSB. Null without a band
            // plan, there being no dial to add to, and null on FM, where the channel is
            // the RF and there is no arithmetic to do - the same rule the start-up line
            // and the operator page already follow.
            double? identRfHz = bandPlan is not null && !bandPlan.IsFm
                ? bandPlan.IsUpperSideband
                    ? bandPlan.DialHz + owed.ToneHz
                    : bandPlan.DialHz - owed.ToneHz
                : null;

            waterfallServer?.ReportTransmittedFrame(
                sub,
                Packet.SoundModem.Waterfall.WaterfallWebServer.IdentTransmissionMode,
                owed.Callsign, to: null, identRecord.Length);
            frameLog?.RecordTransmitted(
                sub, identRecord,
                Packet.SoundModem.Waterfall.WaterfallWebServer.IdentTransmissionMode,
                owed.ToneHz, identRfHz);
        }
        catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"id[{sub}]: identification dropped - {refused.Message}");
        }
    }

    channel.TransmitLease.Closing = IdentTransmission.Closing(identifiers, IdentifyAsync);

    _ = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach ((int sub, StationIdentifier owed) in identifiers)
            {
                if (!IdentTransmission.ShouldSend(channel, owed, held: rig?.HoldsTransmitter == true))
                {
                    continue;
                }

                await IdentifyAsync(sub, owed).ConfigureAwait(false);
            }
        }
    });
}

// Dead-feed protection, two watches for two failure families, thresholds per device (the
// config's "deadFeed" block overrides; 0 = off). What each input actually does when its
// source dies - and therefore which of the two watches can see it - is read from the
// implementations and the two real incidents, and is written down on
// StationOptions.DeviceKind, beside the thresholds it decides.
//
// Which family a device is in is the device's own answer (flex:mock, for one, counts as a bench
// device rather than a Flex - see FlexStationDevice.DeadFeedKind).
DeadFeedDevice deadFeedDevice = stationDevice.DeadFeedKind;

// The uplink to a public monitor site: this station's own display stream, offered outward over
// one socket the station dials out on. Nothing here is reachable without a "publish" block, and
// nothing about a station that has one depends on it - the uplink writes a journal line and
// retries for ever, and it never faults the station, sets the exit code or touches the radio
// (docs/dev/archive/uplink-plan.md, decision 8). It is the waterfall server's relay, so it is offered exactly
// what the page is already being shown.
Packet.SoundModem.Waterfall.UplinkClient? uplink = null;
// Not on a --two-tone run: a monitor site should not be dialled, credited with a station and
// then dropped again for the sake of a five-second bench test.
if (benchTxTest is null && publishConfig is not null)
{
    int publishRate = DaemonConfig.PublishedAudioRate(publishConfig, DspRate);

    // A modem above half the published rate is not in the published picture at all, and finding
    // that out by looking at the site would be a poor way to learn it.
    string outside = string.Join(", ", waterfallServer!.Bands
        .Where(b => b.HighHz > publishRate / 2.0)
        .Select(b => $"modem {b.SubChannel} ({b.Mode}, to {b.HighHz:F0} Hz)"));
    if (outside.Length > 0)
    {
        Console.Error.WriteLine(
            $"publish: WARNING - the published audio spans 0 to {publishRate / 2} Hz, so "
            + $"{outside} will not appear on the site. Raise \"publish\".\"audioRate\" to a "
            + $"divisor of {DspRate} that covers it, or accept the narrower picture.");
    }

    // 4.5's ADSL caveat, said as a fact about their line rather than as a warning about ours.
    if (publishRate >= 48000)
    {
        Console.Error.WriteLine(
            "publish: WARNING - 48000 Hz audio is about 770 kbit/s upstream while somebody is "
            + "watching. That is fine on FTTC or FTTP and most of an ADSL upload; there is no "
            + "codec, by decision, so the lever is \"publish\".\"audioRate\".");
    }

    uplink = new Packet.SoundModem.Waterfall.UplinkClient(
        waterfallServer,
        new Packet.SoundModem.Waterfall.UplinkSettings
        {
            Url = publishConfig.Url!,
            Token = publishConfig.Token!,
            Callsign = publishConfig.Callsign!,
            Operator = publishConfig.Operator,
            Location = publishConfig.Location,
            Radio = publishConfig.Radio,
            Site = publishConfig.Site,
            ChannelRate = DspRate,
            AudioRate = publishRate,
            FramesOnlyWhileWatched = publishConfig.Frames == "watched",
            // The dial and sideband the band plan settled on, so a relayed page draws the same RF
            // scale this station's own page draws rather than being told a second time.
            DialHz = waterfallConfig!.DialFrequencyHz != 0
                ? waterfallConfig.DialFrequencyHz
                : receiveDialHz ?? 0,
            Sideband = pageSideband,
        },
        TimeProvider.System,
        stationJournal.ErrorSink);
    waterfallServer.Relay = uplink;
    uplink.Start();
}

// Disposal runs in reverse declaration order, so this stops after the station has stopped feeding
// it and before the waterfall server it publishes from goes, which is the order a goodbye needs.
await using var uplinkLifetime = uplink;

long frameLogDropsSeen = 0;
long captureDropsSeen = 0;

// The card's own samples, before the decimator, for the two things that have to judge audio on
// the scale the converter actually works on. They want different populations, so the tap is
// composed here rather than either of them growing one of its own: there is one card, one block
// and one place it arrives.
//
//   - The page's CLIP pill wants every station that has a meter, as it has since v0.59.0. On a
//     Flex or an ubersdr feed the samples did not come from a converter of ours, and the pill has
//     always said what it says about them; that is a shipped behaviour and not this feature's to
//     change.
//   - The clip flag a decoded frame carries wants a real sound card and nothing else. A frame
//     that said its card had headroom - or ran out of codes - about a card this station does not
//     have would be a fact invented, and null is what FrameQuality.Clipped's "not measured"
//     means.
ReceiveTap? cardRateTap = null;
if (waterfallServer is { } metered)
{
    cardRateTap = alsaIn is null
        ? metered.MeterInputClipping
        : samples =>
        {
            channel.NoteCardClipping(samples);
            metered.MeterInputClipping(samples);
        };
}
else if (alsaIn is not null)
{
    cardRateTap = channel.NoteCardClipping;
}

// The station: the input, the channel it feeds, the watches that decide the feed has died,
// and the loop that turns between them. One implementation for every device kind, so a
// second flavour of deployment cannot quietly grow a second opinion about a dead feed.
using var station = new Station(
    new StationOptions
    {
        Channel = channel,
        Input = input,
        DspRate = DspRate,
        Journal = stationJournal,
        DeviceKind = deadFeedDevice,
        DeadFeed = deadFeedConfig,
        BlockMilliseconds = ardopModem is null ? 100 : 20,
        SessionLive = webReceiverSessionLive,

        // Built above: the page's clip pill on every station with a meter, and the frame's own
        // clip flag only where there is a converter to have run out of codes.
        CardRateTap = cardRateTap,

        // A station that has deliberately given up its slice is silent on purpose, and restarting
        // it is the one response guaranteed to be wrong. Measured on the live 40 m station,
        // 2026-08-14: the contention policy stood down at 07:07:18 and the silence watch restarted
        // the process at 07:07:45, which took a fresh slice and resumed exactly the fight the
        // stand-down existed to end. Under real contention that is a loop - create, lose, stand
        // down, thirty seconds of silence, restart - and the dead-feed message already warned it
        // would be ("a deliberately muted DAX stream restart-loops this way"), without knowing that
        // the mute could be our own doing.
        //
        // The watch is for a feed that died WITHOUT explanation. This one has one, it is already in
        // the journal above, and the operator instruction there is to stop the other client and
        // restart the service by hand.
        SilenceExcuse = () => flex?.Station.Health == M0LTE.Flex.FlexStationHealth.Contended
            ? "receive feed silent because this station stood down from a contested slice, "
                + "which is deliberate - not restarting. Stop whatever else is claiming the "
                + "radio and restart this service."
            : null,

        // The sound card is the one device with xrun counters, and they are the difference between
        // "the band is quiet" and "this machine will not schedule us".
        XrunCounters = alsaIn is null && alsaOut is null
            ? null
            : () => (alsaIn?.Xruns ?? 0, alsaOut?.Xruns ?? 0),

        HealthChecks =
        [
            // A full disk left a station keeping an empty frame log for weeks with nothing anywhere
            // saying so: this counter was a dead one until the receive loop started reading it.
            () =>
            {
                if (frameLog is null)
                {
                    return null;
                }

                long logDrops = frameLog.Dropped;
                if (logDrops <= frameLogDropsSeen)
                {
                    return null;
                }

                string line =
                    $"frame log: {logDrops - frameLogDropsSeen} frames dropped unwritten "
                    + $"({logDrops} total) - the disk cannot keep up, is full, or is unwritable";
                frameLogDropsSeen = logDrops;
                return line;
            },

            () =>
            {
                if (survey is null)
                {
                    return null;
                }

                long captureDrops = survey.DroppedCaptures;
                if (captureDrops <= captureDropsSeen)
                {
                    return null;
                }

                string line =
                    $"survey: {captureDrops - captureDropsSeen} captures dropped unwritten "
                    + $"({captureDrops} total) - the disk cannot keep up, is full, or is unwritable";
                captureDropsSeen = captureDrops;
                return line;
            },
        ],
    },
    cancellation.Token);

// This process runs one station, so a station fault IS the daemon's fault: journal the
// sentence the station wrote, and take the proven restart contract every one of these
// detectors has always taken - orderly shutdown, exit 1, systemd rebuilds the device session
// from scratch (Restart=on-failure in the shipped unit; the capture campaign's unit is
// Restart=always). That is what each of these paths did for itself before there was a Station
// type. A host running fifty would update one row of a table instead, which is the whole
// reason the decision moved out here.
station.Faulted += fault =>
{
    stationJournal.WriteError(fault.Reason);

    if (fault.Stalled)
    {
        // The receive loop is wedged inside a blocked Read (the ALSA stall family), so the
        // orderly shutdown below can never run. Ending the process is the host's to do and
        // never the station's; systemd restarts either way.
        Environment.Exit(1);
    }

    radioLost = true;
    cancellation.Cancel();
};

// --two-tone / --tone: transmit once and go.
//
// The receive loop goes on a thread of its own here, which is what Station.Run's own docs say a
// host running it alongside anything else must do: it is synchronous and blocks until the station
// stops, because every input's Read is. The station has to be listening for the test to defer to
// a busy channel on the carrier sense a frame would - and calling Run in front of the test rather
// than beside it is a test that never transmits at all, the transmitter having already been shut
// down by the time it is asked. Measured on flex:mock before this was a thread: the burst sat on
// the queue until the wall-clock bound gave up on it.
if (benchTxTest is not null)
{
    var listening = new Thread(station.Run)
    {
        IsBackground = true,
        Name = "tx-test-station",
    };
    listening.Start();

    TxTestOutcome benchOutcome = await txTestRunner.RunAsync(benchTxTest);
    cancellation.Cancel();
    try
    {
        await transmitter;
    }
    catch (Exception)
    {
        // The transmitter ends by cancellation, which is how this path always ends it.
    }

    // The receive loop first, and BEFORE the devices are closed. It notices the cancellation on
    // its next block and returns; until it has, it is inside the input's Read, and closing an
    // ALSA capture handle from under snd_pcm_readi on another thread is not something alsa-lib
    // supports. A background thread, so an input whose Read is wedged cannot hold the process
    // open either way - but it gets its five seconds to come out on its own first.
    listening.Join(TimeSpan.FromSeconds(5));

    if (!stationDevice.ClosesItsOwnStreams)
    {
        (ptt as IDisposable)?.Dispose();
        (playback as IDisposable)?.Dispose();
        (input as IDisposable)?.Dispose();
    }

    // Exit 1 on a refusal, so a bench script can tell "it went out" from "it did not" without
    // reading the journal. Exit 2 stays what it has always been: your configuration is wrong.
    return benchOutcome.Ran ? 0 : 1;
}

station.Run();

try
{
    await transmitter;
}
catch (OperationCanceledException)
{
    // The normal shutdown path: the loop ends by cancellation.
}
catch (Exception)
{
    // Already journalled (and turned into exit 1) by the fault observer above.
}

if (mailcastRun is not null)
{
    try
    {
        await mailcastRun;
    }
    catch (OperationCanceledException)
    {
    }
}

if (!stationDevice.ClosesItsOwnStreams)
{
    (ptt as IDisposable)?.Dispose();
    (playback as IDisposable)?.Dispose();
    (input as IDisposable)?.Dispose();
}

return radioLost || restartRequested ? 1 : 0;

/// <summary>Runs an action on scope exit - for state captured after its `using` must be declared.</summary>
internal sealed class Disposer(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
