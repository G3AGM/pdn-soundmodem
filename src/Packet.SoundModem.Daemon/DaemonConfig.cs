using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>One logical modem on the shared audio channel.</summary>
public sealed class ModemConfig
{
    /// <summary>KISS sub-channel (port nibble), 0-15.</summary>
    public int SubChannel { get; set; }

    /// <summary>Mode name: one of <see cref="Packet.SoundModem.Modems.ModemCatalog.KnownModes"/>
    /// or "ardop" for the ARDOP virtual TNC (both spellable with --modem), or a mode a loaded
    /// plugin registers (config file only, as pluginId:mode). Default afsk1200.</summary>
    public string Mode { get; set; } = "afsk1200";

    /// <summary>Audio centre/carrier frequency override in Hz, applied to both TX and RX
    /// (QtSoundModem-style per-modem tuning; mode default when null). Honoured by the AFSK
    /// tone-pair modes (afsk*, default 1700), the BPSK/QPSK carrier modes (bpsk*/qpsk*,
    /// default 1500; 1650 for qpsk3600), and the spec-fixed waveforms (freedv-*/ms110d-*,
    /// moved by frequency translation around their unchanged DSP - see
    /// <c>FrequencyShiftedModem</c>). The baseband FSK families (fsk*/c4fsk*) have no
    /// settable centre; setting one on those is rejected at start-up, not ignored.</summary>
    public double? Frequency { get; set; }

    /// <summary>Frequency-diversity banks (<c>bpsk*-multi</c>) only: extra decoder branches
    /// either side of centre (0 = a single centred modem). Null uses the mode default (4).
    /// More branches widen off-frequency coverage at a linear CPU cost. Ignored by non-bank
    /// modes.</summary>
    public int? OffsetPairs { get; set; }

    /// <summary>Frequency-diversity banks (<c>bpsk*-multi</c>) only: the Hz step between
    /// adjacent branches. Null uses the mode default (baud/40), sized to the single-branch
    /// offset tolerance. Coverage spans ±<see cref="OffsetPairs"/>·this. Ignored by non-bank
    /// modes.</summary>
    public double? OffsetStepHz { get; set; }

    /// <summary>
    /// IL2P+CRC modes only: also hand up frames that arrive as plain IL2P, with no trailing CRC.
    /// Off by default, because IL2P+CRC is the interop ground truth on the networks this serves
    /// and every mode's default behaviour is Nino's.
    /// </summary>
    /// <remarks>
    /// <para>For a neighbour that sends the CRC-less variant. A BPQ32 node on the live 40 m slot
    /// does exactly that: a station running <c>bpsk300-il2pc</c> logged its bursts as unreadable
    /// for weeks, and one of the survey captures decoded offline as
    /// <c>bpsk300-nocrc @ 2116 Hz -> GB7BPQ&gt;BEACON</c> - right frequency, right modulation,
    /// right baud, wrong IL2P variant.</para>
    /// <para>It is not free. A plain IL2P frame is checked by Reed-Solomon alone, and RS will
    /// occasionally turn noise into a plausible-looking frame, which is the whole reason the
    /// +CRC variant exists. Those frames are logged with <c>crc_valid</c> null rather than true.
    /// Setting it on a mode that does not run IL2P+CRC is refused at start-up rather than
    /// ignored.</para>
    /// </remarks>
    public bool AcceptPlainIl2p { get; set; }

    /// <summary>
    /// A TCP port dedicated to this modem alone; null (the default) means a packet modem is
    /// reachable only through the shared <see cref="DaemonConfig.KissPort"/> by its nibble.
    /// </summary>
    /// <remarks>
    /// <b>The protocol spoken here follows the mode.</b> A packet mode gets KISS: only this
    /// modem's frames, presented as nibble 0, and everything sent to it transmitted on this
    /// modem whatever nibble was used - which is what host software that hardcodes KISS channel
    /// 0 needs to reach a modem that is not sub-channel 0. <c>ardop</c> instead gets the
    /// ardopcf host interface, command on this port and data always on the next one up, because
    /// an ARQ session has no KISS representation.
    /// </remarks>
    public int? Port { get; set; }

    /// <summary>
    /// Where this modem sits on the band, in absolute Hz (7051600 for 7051.6 kHz). Mutually
    /// exclusive with <see cref="Frequency"/>: state a band plan in RF terms and the daemon
    /// works out the dial and every modem's audio centre from it, rather than you doing that
    /// arithmetic and it being silently wrong when the dial moves.
    /// </summary>
    public double? RfFrequency { get; set; }

    /// <summary>
    /// How much room to plan for this modem, in Hz; measured from the modem itself when unset.
    /// Meaningful mainly for <c>ardop</c>, which has no fixed width - its bandwidth is
    /// negotiated per session, so the planner assumes the widest (2000 Hz) unless told
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// On an <c>ardop</c> entry it is also the cap, not just the reservation: it becomes the
    /// TNC's ARQBW at start-up, which governs both what the station accepts as IRS and what it
    /// asks for as ISS, so nothing wider is negotiated in either direction. Only the widths
    /// ARDOP has are accepted there (200, 500, 1000, 2000); any other number is refused when
    /// the file is read, rather than planning for a width no session could occupy.
    /// </remarks>
    public double? Bandwidth { get; set; }

    /// <summary>
    /// Watch this ARDOP modem's own slot and tell the TNC when somebody else is using it
    /// (<c>BUSY TRUE</c>/<c>BUSY FALSE</c>, and <c>ConRejBusy</c> under <c>BUSYBLOCK</c>).
    /// <b>Off by default</b>, and meaningful only on an <c>ardop</c> entry.
    /// </summary>
    /// <remarks>
    /// <para><b>Off by default because it is measurably wrong on a real band.</b> It is a
    /// band-limited energy meter: short-term power in the ARDOP slot against a slowly-adapting
    /// noise floor. On the bench that separates cleanly, with a neighbouring packet slot 50 to
    /// 84 dB down in the watched band. On air at GB7RDG on 2026-09-12 it read busy almost
    /// continuously, and the reason is not in the band at all: FT4 on 7.0475 MHz, below the
    /// sub-band, lifts the noise floor across the whole passband in step with its transmit
    /// cycle, and a floor estimator that adapts upward at 0.002 per block cannot follow a lift
    /// that arrives in four seconds. The lift therefore reads as signal. No threshold fixes
    /// that; it only changes how often.</para>
    /// <para>ardopcf's own detector is immune to this by construction and ours is not, which is
    /// the real lesson: it rank-orders the bins inside its window and compares the top few
    /// against the rest (<c>BusyDetect.c</c>), so a uniform floor lift raises signal and
    /// baseline together and the ratio does not move. Replacing this with that test is the fix.
    /// Until then, leave it off unless you are working on it.</para>
    /// </remarks>
    public bool BusyDetect { get; set; }

    /// <summary>
    /// Identify this modem in Morse. Omit (the default) and it never transmits an identification.
    /// </summary>
    /// <remarks>
    /// Per modem rather than per station, because a station identifies on the signal it is
    /// identifying: the modems on one channel can sit kilohertz apart, and one station-wide ident
    /// would land on a single audio frequency, say nothing about the others, and usually sit on
    /// top of one of them. The modes that already identify themselves in-band (an AX.25 node
    /// sending its own ID frames) need nothing here.
    /// </remarks>
    public IdentifyConfig? Identify { get; set; }

    /// <summary>
    /// <c>ms110d-*</c> only: send frames that are queued together in one burst, up to this many
    /// seconds long, rather than one burst per frame. Omit (the default) for one frame per burst.
    /// </summary>
    /// <remarks>
    /// Every MS110D burst pays for a preamble and an interleaver flush, which on a run of frames
    /// is a large share of the airtime. Packed, the run pays once: one preamble, the frames back
    /// to back, one EOM. The receiver needs nothing, since it already reads frames out of a
    /// continuous bit stream. 1 to <see cref="MaxBurstSecondsCeiling"/> seconds; a single frame
    /// longer than the limit still goes out on its own.
    /// </remarks>
    public double? MaxBurstSeconds { get; set; }

    /// <summary>
    /// With <see cref="MaxBurstSeconds"/>: how long the first frame of a run waits for the rest
    /// to arrive before the station contends for the air, so a host writing frames one by one
    /// still fills a burst. Default <see cref="DefaultBurstGatherSeconds"/>; 0 to
    /// <see cref="BurstGatherSecondsCeiling"/>.
    /// </summary>
    public double? BurstGatherSeconds { get; set; }

    /// <summary>
    /// <c>ms110d-*</c> only: the transmit level, the scale on the symbol stream before pulse
    /// shaping, above 0 and at most 1. Absent means the modem's default of 0.5, which peaks at
    /// about 0.38 of full scale (the waveform's peak-to-average ratio is about 6.5 dB); 1.0 peaks
    /// at about 0.75, close to the transmit test tone's 0.8.
    /// </summary>
    public double? TxAmplitude { get; set; }

    /// <summary>The longest packed burst a configuration may ask for.</summary>
    public const double MaxBurstSecondsCeiling = 120;

    /// <summary>The longest gather a configuration may ask for.</summary>
    public const double BurstGatherSecondsCeiling = 10;

    /// <summary>The gather a packing modem gets when the configuration does not say.</summary>
    public const double DefaultBurstGatherSeconds = 0.5;

    /// <summary>Keys in this modem entry that the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Publishing what this station hears, for a monitoring system to collect - see
/// <see cref="DaemonConfig.Metrics"/>.
/// </summary>
/// <remarks>
/// <para>Served on the <see cref="WaterfallConfig.Port"/> listener, so a station that already
/// publishes a waterfall gains this on the same port rather than another one to open.</para>
/// <para><b>Pull, and deliberately ignorant.</b> Nothing here knows the address, protocol or
/// credentials of any monitoring system: the station serves what it knows and whoever is
/// interested comes and reads it. That is what keeps it generic rather than fitted to one
/// operator's stack.</para>
/// <para><b>Unauthenticated.</b> What is served is callsigns and signal reports, transmitted in
/// the clear on a shared channel - the same facts the waterfall page already shows anyone who
/// opens it. Off by default all the same, because publishing is the operator's decision.</para>
/// </remarks>
public sealed class MetricsConfig
{
    /// <summary>Whether to serve <c>/metrics</c> and <c>/metrics/frames</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Most stations kept. On reaching it the least recently heard is dropped.</summary>
    public int MaxStations { get; set; } = 256;

    /// <summary>How long a frame stays in the per-frame feed, in seconds. Must comfortably
    /// exceed the collector's scrape interval: scraping more slowly loses frames, scraping
    /// faster sees some twice, which is harmless.</summary>
    public double FrameWindowSeconds { get; set; } = 300;

    /// <summary>How long a station keeps its series after its last frame, in hours. A station
    /// that has stopped transmitting should stop being a series rather than hold its last
    /// reading for ever.</summary>
    public double StationIdleHours { get; set; } = 6;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Runtime configuration over HTTP - see <see cref="DaemonConfig.Api"/>.</summary>
/// <remarks>
/// <para>Served on the <see cref="WaterfallConfig.Port"/> listener under <c>/api/</c>, so a
/// station that already publishes a waterfall gains this on the same port rather than a second
/// one to open.</para>
/// <para><b>This can change frequency and transmit power</b>, which makes it a bigger gun than
/// the waterfall it shares a socket with. It does nothing at all until <see cref="Key"/> is set,
/// and every request must carry that key.</para>
/// </remarks>
public sealed class ApiConfig
{
    /// <summary>
    /// The shared secret every request must present, as <c>Authorization: Bearer KEY</c> or
    /// <c>X-API-Key: KEY</c>. No key, no API: there is no default and no unauthenticated mode.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Morse identification for one modem - see <see cref="ModemConfig.Identify"/>.</summary>
public sealed class IdentifyConfig
{
    /// <summary>The callsign to send. Required; there is no sensible default for a licence
    /// condition, and guessing one wrong is worse than refusing to start.</summary>
    public string? Callsign { get; set; }

    /// <summary>
    /// Minutes between identifications, counted from the last one sent. Default 10.
    /// </summary>
    /// <remarks>
    /// The clock only runs while the modem is transmitting: an ident falls due when this has
    /// elapsed <em>and</em> the modem has transmitted since it last identified. A station that
    /// has sent nothing owes nothing, so an idle modem never keys up to announce itself.
    /// </remarks>
    public double IntervalMinutes { get; set; } = 10;

    /// <summary>Sending speed in words per minute (PARIS). Default 20.</summary>
    public double Wpm { get; set; } = 20;

    /// <summary>
    /// The audio tone to key, in Hz. Defaults to <b>this modem's own audio centre</b>, so the
    /// ident goes out on the signal it identifies and follows the band plan without being kept
    /// in step by hand. Set it only to move the ident off its modem deliberately.
    /// </summary>
    /// <remarks>
    /// The default is the whole point of hanging this off a modem. A conventional 700 Hz ident
    /// tone is a trap on a band-planned station: with a dial chosen to centre the ensemble, 700 Hz
    /// is wherever the planner happened to put it, which on a real 40 m layout landed on top of a
    /// neighbouring modem's slot.
    /// </remarks>
    public double? ToneHz { get; set; }

    /// <summary>
    /// Where to identify in absolute Hz (7054000 for 7054.0 kHz), as an alternative to
    /// <see cref="ToneHz"/>. Only meaningful on a band-planned station, which is the only kind
    /// that knows its own dial. Mutually exclusive with <see cref="ToneHz"/>.
    /// </summary>
    public double? RfFrequency { get; set; }

    /// <summary>
    /// Send the mode name after the callsign, e.g. <c>M0LTE FREEDV-DATAC1</c>. Off by default.
    /// Worth turning on for an unusual waveform: it tells a listener who just heard something
    /// they could not read what it actually was.
    /// </summary>
    public bool IncludeMode { get; set; }

    /// <summary>
    /// Key-down peak amplitude. Default 0.8, which is what the modulators themselves use, so the
    /// ident presents the transmitter with the same drive the data does.
    /// </summary>
    public double Amplitude { get; set; } = 0.8;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// One modem plugin to load: an assembly outside this package, providing modes this package does
/// not contain.
/// </summary>
/// <remarks>
/// <para>A path, and nothing but a path. There is no plugins directory to scan, no probing beside
/// the executable and no environment variable, because a file appearing on disk must never change
/// what a station transmits. What this file says is what gets loaded, and the start-up log repeats
/// it.</para>
/// <para>See <c>docs/dev/modem-plugins.md</c>. The modes a plugin provides are named
/// <c>pluginId:mode</c> - <c>sample:loopback</c> - so a mode string always says plainly whether it came
/// from this package.</para>
/// </remarks>
public sealed class ModemPluginConfig
{
    /// <summary>Path to the plugin assembly. Relative paths are resolved against the daemon's
    /// working directory; write an absolute one in anything a service unit starts.</summary>
    public string Path { get; set; } = "";

    /// <summary>Keys in this entry that the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// One polyglot KISS port: several modems overlaid on the same channel behind one port, with each
/// frame the host sends going out in the mode its next hop was last heard in
/// (packet-net/pdn-soundmodem#450).
/// </summary>
/// <remarks>
/// For a station with one frequency and peers on different equipment. Every member modem hears
/// every burst, so which one decoded a station says what that station can do; the host sees one
/// nibble-0 port and never has to choose. A station not heard within
/// <see cref="ForgetAfterMinutes"/> gets <see cref="Default"/>, which should be the mode every
/// peer has. Beacons and broadcasts go to destinations that never transmit, so they always take
/// the default. See <see cref="Kiss.PolyglotRouter"/>.
/// </remarks>
public sealed class PolyglotConfig
{
    /// <summary>The TCP port the host attaches to. Required.</summary>
    public int? Port { get; set; }

    /// <summary>The overlaid modems, by their <see cref="ModemConfig.SubChannel"/>; two or more.</summary>
    public List<int> SubChannels { get; set; } = [];

    /// <summary>The sub-channel a frame goes out on when its next hop has not been heard
    /// recently. Required, and one of <see cref="SubChannels"/>.</summary>
    public int? Default { get; set; }

    /// <summary>How long a station is remembered after it was last heard, in minutes.</summary>
    public double ForgetAfterMinutes { get; set; } = 60;

    /// <summary>Keys in this entry that the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>Whether this entry was written as <see cref="DaemonConfig.PolyglotPort"/> rather
    /// than in the list, so a refusal can name the key the operator actually wrote.</summary>
    [JsonIgnore]
    internal bool FromShorthand { get; init; }
}

/// <summary>PTT configuration.</summary>
/// <summary>
/// Where this station's carrier sense comes from: the radio's own squelch and signal-strength
/// meter, read over the control cable it is already wired for.
/// </summary>
/// <remarks>
/// <para><b>Why the radio and not the audio.</b> An FM receiver with the squelch open, which is
/// how every packet FM station runs, is LOUDER when the channel is idle than when somebody is
/// transmitting: the arriving carrier captures the discriminator and replaces band noise with
/// modulation. Every audio busy detector here asserts on a RISE, so on that path it is not merely
/// deaf but anti-correlated, firing at the END of a transmission rather than during it. Two audio
/// detectors were built for this and both were wrong, the second one badly enough to stop a bench
/// station transmitting at all. The radio, meanwhile, has a squelch, a hardware carrier-detect
/// line and a calibrated RSSI meter, and will simply tell you. See
/// <c>docs/dev/carrier-sense.md</c>.</para>
/// <para><b>It is off unless this section says otherwise</b>, and it fails open when it is on: a
/// port that will not open, a radio in the wrong mode, a cable pulled mid-session all end with a
/// station that transmits exactly as it did before the feature existed. A carrier sense that can
/// silence a station by losing a USB cable is the worse failure by a distance.</para>
/// <para><b>The daemon holds the port while it runs</b>, and a serial port cannot be opened
/// twice. Anything else that talks to the same radio - a programming tool, a separate signal
/// probe - has to wait, or this section has to come out and the daemon restart.</para>
/// </remarks>
public sealed class CarrierSenseConfig
{
    /// <summary>
    /// Which radio driver to use. <c>"tait"</c> is the only one there is; <c>"none"</c> switches
    /// the section off without deleting it.
    /// </summary>
    public string Radio { get; set; } = "tait";

    /// <summary>
    /// The radio's control serial port, for example <c>/dev/ttyUSB0</c>. Omit it and nothing
    /// opens a port, whatever else this section says.
    /// </summary>
    public string? Port { get; set; }

    /// <summary>Port speed. The radio's own data-port programming decides this; the bench
    /// TM8110s are set to 28800.</summary>
    public int Baud { get; set; } = 28800;

    /// <summary>
    /// Also call the channel busy when the radio's own RSSI reads above this many dBm. Omit it to
    /// use the radio's carrier-detect line alone.
    /// </summary>
    /// <remarks>
    /// <para><b>A data station usually wants one.</b> Carrier detect is event driven and costs no
    /// serial traffic, but it reports nothing at all until the first squelch edge after the radio
    /// starts sending unsolicited progress messages, and on a station whose squelch is held open
    /// that edge may never come.</para>
    /// <para><b>It has to be measured per station and cannot be copied.</b> Two nominally
    /// identical radios on one bench read idle noise floors 31 dB apart: -94.3 dBm and
    /// -125.6 dBm, against a far end at about -35 dBm either way. The figures chosen there were
    /// -75 and -110 respectively. Measure the floor on the quiet channel and sit well above it.
    /// </para>
    /// </remarks>
    public double? BusyAboveDbm { get; set; }

    /// <summary>How often to read RSSI, in milliseconds. Ignored without
    /// <see cref="BusyAboveDbm"/>.</summary>
    public int PollMilliseconds { get; set; } = 100;

    /// <summary>
    /// With no radio to ask, whether to read carrier sense off the SHAPE of the received audio
    /// instead. On by default, and ignored entirely on a station that has a radio.
    /// </summary>
    /// <remarks>
    /// <para><b>On by default because the alternative is not neutral.</b> A station without this
    /// consults an in-band energy detector, and on open-squelch FM that detector is
    /// anti-correlated: it reads clear for the whole of every transmission and then asserts for
    /// about ten seconds after it, on the return of the noise. Measured on this bench, a station
    /// two seconds after hearing one burst took 9.9 s to get a frame to air against 0.5 s on a
    /// quiet channel. So "off" is not a safe default, it is a measured ten-second hangover.</para>
    /// <para><b>What it does instead</b> is compare the shape of the received spectrum with the
    /// shape the station's own idle channel was showing a few seconds ago; nothing in the decision
    /// is an absolute level, which is what made the previous attempt at this unsafe. Measured
    /// against real captures it finds 15 of 15 and 11 of 11 transmissions, a median 26 ms after
    /// the carrier arrives, with no false busy in 620 s of idle channel. It answers "no opinion"
    /// on any path it does not recognise, including a squelched receiver, a dead or muted card,
    /// and an SSB or wired path where a signal makes the audio louder rather than quieter.</para>
    /// <para><b>It needs a 48 kHz channel</b>, which is what a station running any 48 kHz mode
    /// gets. On a 12 kHz channel the measurement says plainly that it does not work and it is not
    /// used; the journal says so at start-up.</para>
    /// </remarks>
    public bool AudioFallback { get; set; } = true;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

public sealed class PttConfig
{
    /// <summary>"serial", "cm108" or "rigctld" (omit the whole section for VOX).</summary>
    public string Type { get; set; } = "serial";

    /// <summary>Device path (/dev/ttyUSB0, /dev/hidraw0).</summary>
    public string Device { get; set; } = "";

    /// <summary>Serial line: "rts" (default) or "dtr".</summary>
    public string? Line { get; set; }

    /// <summary>CM108 GPIO pin (default 3).</summary>
    public int? Gpio { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// A radio controlled through Hamlib's rigctld, for a station whose radio is not a FlexRadio.
/// </summary>
/// <remarks>
/// With this section the daemon connects to rigctld, journals what the rig is on, sets the dial
/// and mode from the band plan when the modems are placed by <c>rfFrequency</c>, can key the radio
/// with <c>"ptt": {"type": "rigctld"}</c>, and serves <c>/api/rig</c>. Without it nothing talks to
/// a rig, as before. See <see cref="RigControl"/>.
/// </remarks>
public sealed class RigConfig
{
    /// <summary>Where rigctld listens, <c>host:port</c>.</summary>
    public string Rigctld { get; set; } = RigctldEndpoint.Default;

    /// <summary>
    /// When true, a rigctld that cannot be reached at start-up stops the station (exit 1, so
    /// systemd retries) rather than being a warning.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// The Hamlib mode the band plan sets, when plain USB, LSB or FM is not the one the rig's data
    /// jack wants (PKTUSB on many rigs). It has to agree with the band plan's sideband.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// The passband the band plan asks the rig for, in Hz. Absent: the plan's own passband width
    /// (2400 Hz for the ordinary 300-2700 Hz window). 0: the rig's normal width for the mode.
    /// </summary>
    public int? PassbandHz { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// The operator's transmitter test: a two-tone linearity check, or one tone for a carrier-level
/// or FM deviation check. Present by default; there is nothing here that has to be switched on,
/// because nothing happens until an operator asks for it.
/// </summary>
/// <remarks>
/// A test transmission is a licensed transmission, so what this section holds is the two bounds
/// on one: how long a test runs when nobody says, and the longest one that will be run whatever
/// is asked for. The tones themselves are not settings - 700 and 1900 Hz is the pair everybody
/// measures against, and the single-tone side takes its frequency per test.
/// </remarks>
public sealed class TxTestConfig
{
    /// <summary>
    /// Whether the control exists at all. True by default. Setting it false takes the button off
    /// the operator page, refuses <c>/api/txtest</c> and refuses <c>--two-tone</c>, for a station
    /// whose operator would rather nothing on this box could key the radio without a frame behind
    /// it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a test runs when the request does not say, in seconds. Default 5.</summary>
    public double Seconds { get; set; } = 5;

    /// <summary>
    /// The longest test that will be run, in seconds, whatever is asked for. Default 30, and
    /// clamped to 60 however this is set: a cap is a safety limit, so a typo here must not be able
    /// to hold the PA up for an hour.
    /// </summary>
    public double MaxSeconds { get; set; } = 30;

    /// <summary>
    /// What the burst peaks at, 0 to 1. Default 0.8, which is what the modulators themselves use,
    /// so the test presents the transmitter with the same drive the data does.
    /// </summary>
    public double Amplitude { get; set; } = 0.8;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Sound-card settings that are not the PCM stream itself.</summary>
public sealed class AlsaConfig
{
    /// <summary>The card's mixer; null leaves every control as the card has it.</summary>
    public AlsaMixerConfig? Mixer { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// The sound card's own mixer: capture gain, AGC, mic boost and the transmit-side level.
/// </summary>
/// <remarks>
/// <para><b>Every setting is optional and absent means untouched.</b> The card's mixer is
/// whatever <c>alsamixer</c>, a reboot or a re-plug last left it, and this section exists to pin
/// the ones that matter without taking over the ones that do not. A file with no
/// <c>alsa.mixer</c> block changes nothing about a station that was working before it existed.</para>
/// <para><b>dB, not percentages</b> (Tom, 2026-09-06: "change to dB, far more useful"). A level
/// in dB means the same thing on every card; a percentage of a card's own raw range means nothing
/// until you know the range. The card's range is printed beside the level everywhere it is shown -
/// the start-up journal, <c>--mixer-show</c>, <c>/api/mixer</c> and the operator page - and a
/// value outside it is refused with the range rather than clamped. A card that publishes no dB
/// scale at all is said so and left alone; see <see cref="Packet.SoundModem.Audio.AlsaMixer"/>.</para>
/// <para><b>What is set here is applied at every start-up and wins over the state file.</b> A
/// change made from the operator page or <c>/api/mixer</c> is remembered in
/// <c>mixer-state.json</c> and applied at the next start-up, but only for a control this section
/// says nothing about (Tom, 2026-09-06). So a level pinned here is the level the station comes up
/// on, every time, whatever the page did last week.</para>
/// <para><b>The AGC and the mic boost are not settings here</b> (Tom, 2026-09-06: "AGC should
/// just be forced off, as should mic boost. No need for buttons for these."). Any card that has
/// either has it switched off at every start-up, journalled in one line. They were keys in
/// 0.57.0 and 0.58.x, recommended off in both; a file still carrying one is told at start-up that
/// it is now unconditional, and nothing else happens - the outcome is what the key asked for.
/// See <see cref="ForcedOffKeys"/>.</para>
/// </remarks>
public sealed class AlsaMixerConfig
{
    /// <summary>Capture gain in dB, inside the card's own range; null leaves it.</summary>
    public double? CaptureGainDb { get; set; }

    /// <summary>Transmit-side playback level in dB, inside the card's range; null leaves it.</summary>
    public double? PlaybackDb { get; set; }

    /// <summary>
    /// The mixer card, when it is not the one the <c>device</c> string implies. Rarely needed:
    /// <c>plughw:CARD=Device,DEV=0</c> gives <c>hw:CARD=Device</c> by itself.
    /// </summary>
    public string? Card { get; set; }

    /// <summary>
    /// Where a change made from the operator page or <c>/api/mixer</c> is remembered between
    /// runs; null takes the default.
    /// </summary>
    /// <remarks>
    /// The default is <c>mixer-state.json</c> in the systemd state directory
    /// (<c>/var/lib/pdn-soundmodem</c> under the shipped unit, which the service user owns), or
    /// beside the config file when there is no state directory. Anything a key above pins is
    /// applied from the config file instead, so this file only ever fills in for a control the
    /// file says nothing about. See <see cref="MixerStateFile"/>.
    /// </remarks>
    public string? StateFile { get; set; }

    /// <summary>Names to look for the capture gain under, in order; null takes the built-in list.</summary>
    public List<string>? CaptureControls { get; set; }

    /// <summary>
    /// Names to look for the AGC switch under, in order; null takes the built-in list. It is
    /// looked for so that it can be switched OFF - that is the only thing done with it.
    /// </summary>
    public List<string>? AgcControls { get; set; }

    /// <summary>
    /// Names to look for the mic boost under, in order; null takes the built-in list. Also only
    /// ever switched off.
    /// </summary>
    public List<string>? MicBoostControls { get; set; }

    /// <summary>Names to look for the playback level under, in order; null takes the built-in list.</summary>
    public List<string>? PlaybackControls { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>This section as the mixer layer wants it.</summary>
    public MixerSettings ToSettings() => new()
    {
        CaptureGainDb = CaptureGainDb,
        PlaybackDb = PlaybackDb,
        CaptureControls = Names(CaptureControls, MixerSettings.DefaultCaptureControls),
        AgcControls = Names(AgcControls, MixerSettings.DefaultAgcControls),
        MicBoostControls = Names(MicBoostControls, MixerSettings.DefaultMicBoostControls),
        PlaybackControls = Names(PlaybackControls, MixerSettings.DefaultPlaybackControls),
    };

    /// <summary>
    /// The percentage keys this section used to have, and the dB key that replaced each.
    /// </summary>
    /// <remarks>
    /// Not aliased, deliberately. 60 meant 60% of a raw range and now means 60 dB, which on the
    /// bench CM108's -12..23 dB capture would be off the end of the card - so quietly reading the
    /// old key as the new one would set a level nobody asked for. They are gone, and a file or a
    /// request that still carries one is told which key to use instead.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> RenamedKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["captureGainPercent"] = MixerSetup.CaptureKey,
            ["playbackPercent"] = MixerSetup.PlaybackKey,
        };

    /// <summary>
    /// What to say about a percentage key that is still in a file or a request body, or null if
    /// <paramref name="key"/> is not one. One sentence, so the start-up warning and the API's 400
    /// cannot drift apart.
    /// </summary>
    /// <param name="key">The key that was found.</param>
    public static string? WhyRenamed(string key) =>
        RenamedKeys.TryGetValue(key ?? "", out string? now)
            ? $"{key} is no longer read; use {now}, the card's range is shown by --mixer-show"
            : null;

    /// <summary>
    /// The keys this section used to have for the two switches, which are now unconditional.
    /// </summary>
    /// <remarks>
    /// Kept as names to refuse rather than quietly dropped into the unknown-key bucket, for the
    /// same reason the percentage keys are: an operator whose 0.58.x file says
    /// <c>"agc": false</c> is entitled to be told that the daemon now does that whatever the file
    /// says, rather than to read "unknown setting" and wonder whether their AGC came back on.
    /// </remarks>
    public static readonly IReadOnlySet<string> ForcedOffKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agc", "micBoost" };

    /// <summary>
    /// What to say about an <c>agc</c> or <c>micBoost</c> key that is still in a file or a
    /// request body, or null if <paramref name="key"/> is not one.
    /// </summary>
    /// <remarks>
    /// <para>One sentence, so the start-up warning and the API's 400 cannot drift apart - the
    /// same rule <see cref="WhyRenamed"/> keeps.</para>
    /// <para><b>The value matters.</b> "The card ends up the way it asked for either way" is true
    /// of <c>false</c> and a lie about <c>true</c>, which is exactly the file this message is
    /// written for: somebody who deliberately turned their AGC on is now getting the opposite,
    /// and being told it made no difference would send them looking somewhere else.</para>
    /// </remarks>
    /// <param name="key">The key that was found.</param>
    /// <param name="asked">What it was set to, or null when that cannot be read.</param>
    public static string? WhyForcedOff(string key, bool? asked = null) =>
        ForcedOffKeys.Contains(key ?? "")
            ? $"{key} is no longer a setting: AGC and mic boost are switched off at every "
                + "start-up on any card that has them, because automatic gain fights the modem's "
                + "own level tracking and a boost left on puts the receive path into clipping. "
                + (asked == true
                    ? "This station switches it off whatever the key says, so remove it: as "
                        + "written it is asking for the opposite of what happens."
                    : "Remove the key; the card ends up the way it asked for either way.")
            : null;

    /// <summary>An override list if there is a usable one, else the built-in fallbacks.</summary>
    private static IReadOnlyList<string> Names(List<string>? given, IReadOnlyList<string> fallback)
    {
        if (given is null)
        {
            return fallback;
        }

        List<string> usable = [.. given.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())];
        return usable.Count > 0 ? usable : fallback;
    }
}

/// <summary>POCSAG paging endpoint (DAPNET/POCSAG-compatible waveform; local paging
/// API, pdn) - see PagingTcpServer for the line grammar.</summary>
public sealed class PagingConfig
{
    /// <summary>Paging TCP listen port.</summary>
    public int Port { get; set; } = 8106;

    /// <summary>POCSAG bit rate: 512, 1200 (DAPNET, default) or 2400.</summary>
    public int Baud { get; set; } = 1200;

    /// <summary>Invert the TX baseband polarity (for radios whose data path inverts;
    /// the spec convention '0' = high frequency is the default).</summary>
    public bool InvertPolarity { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>ARDOP virtual TNC (ardopcf-compatible TCP host interface; Winlink/Pat).
/// Per the dedicated-channel policy the ARDOP channel hosts no packet modems or
/// paging - configuring this alongside Modems/Paging is rejected.</summary>
public sealed class ArdopConfig
{
    /// <summary>Host-interface command port (ardopcf convention 8515); the data port
    /// always listens on the next port up.</summary>
    public int Port { get; set; } = 8515;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Headless FlexRadio slice-creation params (used when Device is
/// <c>flex:&lt;radio&gt;</c> with no <c>@station</c> - the daemon owns the radio and creates
/// its own slice). Ignored in attach mode (a <c>@station</c> device string). Defaults match
/// docs/dev/archive/flex-integration.md §8.</summary>
public sealed class FlexConfig
{
    /// <summary>
    /// Slice frequency (MHz, six-decimal Flex form). Null takes the default, and a band plan
    /// supersedes it entirely - with <c>rfFrequency</c> modems the dial is computed, so setting
    /// this as well says two different things.
    /// </summary>
    public string? Frequency { get; set; }

    /// <summary>RX/TX antenna. Default "ANT1".</summary>
    public string Antenna { get; set; } = "ANT1";

    /// <summary>Slice demod mode. Default "DIGU".</summary>
    public string Mode { get; set; } = "DIGU";

    /// <summary>
    /// The DAX channel the client claims, on both the headless and attach paths. Unset, a
    /// headless client takes <see cref="DefaultHeadlessDaxChannel"/> so that it coexists with
    /// SmartSDR without being told to.
    /// </summary>
    /// <remarks>
    /// A running SmartSDR grabs DAX channel 1 and the two contend (live finding, 2026-07-17 -
    /// docs/dev/archive/flex-integration.md §8). Defaulting elsewhere means the order the two are started in
    /// stops mattering, which is the whole problem with picking 1 and hoping.
    /// </remarks>
    public string? DaxChannel { get; set; }

    /// <summary>Out of SmartSDR's way, and valid on every 6000-series model (a 6500 has four).</summary>
    /// <remarks>
    /// It dodges SmartSDR; it does <b>not</b> dodge a second pdn-soundmodem, and two headless
    /// instances that both take the default land on the same channel. That is how a receive-only
    /// capture instance displaced GB7RDG's 40 m modem on 2026-08-07 and took it off air for six
    /// days. If you run a second instance against one radio, give it its own
    /// <see cref="DaxChannel"/> and set <see cref="ReceiveOnly"/>.
    /// </remarks>
    public const string DefaultHeadlessDaxChannel = "2";

    /// <summary>
    /// Receive only: never write the radio's global transmit state, and never contend for a
    /// slice. Default false.
    /// </summary>
    /// <remarks>
    /// For a capture or monitoring instance sharing a radio with a transmitting station. The
    /// transmit audio source, the transmit filter and RF power are global and persistent rather
    /// than per-slice, so a monitor that runs the normal bring-up quietly changes what the
    /// station on the same radio puts on air, and the change outlives the monitor's process.
    /// Setting this also stops the instance rebuilding a slice it has lost, so it can never
    /// fight the station for one.
    /// </remarks>
    public bool ReceiveOnly { get; set; }

    /// <summary>
    /// Transmit power in watts. Null (the default) leaves whatever the radio is already set to.
    /// </summary>
    /// <remarks>
    /// <para>Watts rather than the radio's 0-100 percentage because that is what an operator
    /// means; the daemon converts using the PA size the radio reports (100 W on the 6000
    /// series, so the two happen to coincide there).</para>
    /// <para>The radio <b>rejects</b> a power above its Max Power Level rather than reducing to
    /// it, so a value above your ceiling fails at startup with a message naming both numbers -
    /// it does not quietly transmit at less than you asked for.</para>
    /// <para>Only the client that owns the transmit slice can set this, which in a headless
    /// station is the daemon itself: with pdn-soundmodem holding the slice, an external tool's
    /// request is accepted and discarded. That is why this lives in the config rather than being
    /// left to the rig.</para>
    /// </remarks>
    public double? TxPowerWatts { get; set; }

    /// <summary>
    /// Transmit-filter high cut in Hz. Null (the default) derives it from the modems - the
    /// highest one's upper edge plus a little margin. 0 leaves whatever the radio was already
    /// set to.
    /// </summary>
    /// <remarks>
    /// <para>The transmit filter is a global, persistent radio setting rather than a slice one,
    /// so an inherited one from a previous session silently truncates anything wider than it -
    /// which is why the daemon states it rather than hoping. The default 3000 Hz passes every
    /// audio-band packet mode but clips the top of <c>ms110d-*</c> (a 3 kHz waveform at an 1800 Hz
    /// centre reaches past 3.1 kHz).</para>
    /// <para>Only the high cut is settable through the station API; the low cut and the receive
    /// filter are not, so a modem below the radio's low cut is reported at start-up and has to be
    /// fixed at the rig.</para>
    /// </remarks>
    public int? TransmitFilterHighHz { get; set; }

    /// <summary>
    /// The station name this client registers with the radio (headless only, best-effort).
    /// Default "pdn-soundmodem", so per-station state (transmit power) and anyone else's
    /// diagnostics name this daemon instead of a generic "Flex" - which matters the moment a
    /// second transmitting client (a test instance, the sm-ota harness) shares the radio.
    /// </summary>
    public string StationName { get; set; } = "pdn-soundmodem";

    /// <summary>
    /// Key through the arbitrated PTT: every keyup waits for the radio to be quiet, re-asserts
    /// the transmit filter and the TX slice, and only believes a keyup the radio confirms -
    /// so a second transmitting client cannot be transmitted over, and cannot silently steal
    /// this daemon's TX slice between bursts. Default false until the shared-PA hardware
    /// probes pass (docs/dev/archive/flex-integration.md names them); the default path is exactly what it
    /// always was.
    /// </summary>
    public bool Arbitration { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Stream parameters used only when Device is <c>ubersdr:&lt;instance&gt;</c> - a receive-only
/// station listening to a public UberSDR web receiver's IQ. Ignored for every other device.
/// Where to tune is not here: that comes from the band plan, as it does on a Flex.
/// </summary>
public sealed class UberSdrConfig
{
    /// <summary>The receiver's IQ mode. <c>iq48</c> (48 kHz complex, ±24 kHz) is what every
    /// public instance offers; <c>iq96</c> where one allows it.</summary>
    public string Mode { get; set; } = "iq48";

    /// <summary>Password for a protected instance; null for a public one.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Edges of the SSB filter to synthesise, in Hz above the dial. Holding the complex baseband
    /// means the receive filter is ours to choose rather than a rig's, and the default (150-3450)
    /// clears the whole 300-2700 Hz band the planner will place modems in.
    /// </summary>
    public double? SsbLowHz { get; set; }

    /// <summary>Upper edge of the synthesised SSB filter, Hz above the dial. See
    /// <see cref="SsbLowHz"/>.</summary>
    public double? SsbHighHz { get; set; }

    /// <summary>Audio discarded after each connect, in ms. The instances ramp their level over
    /// the first ~1 s of a stream; the default second of guard keeps that out of the modems.</summary>
    public int? StartupGuardMs { get; set; }

    /// <summary>Linear gain on the demodulated audio; 1.0 (the default) is the receiver's own
    /// scaling. For bringing a quiet instance up to soundcard-like levels on the waterfall.</summary>
    public double? Gain { get; set; }

    /// <summary>
    /// Hold a session on the receiver only while somebody has the waterfall page open. For a
    /// public monitor: with nobody watching there is nothing to show, and the receiver's slot
    /// and this address's daily allowance are better left alone. Off (the default) connects at
    /// start-up and stays connected, as a station feeding a node must.
    /// </summary>
    /// <remarks>
    /// With this on, a receiver that cannot be reached is not fatal: the daemon keeps the page
    /// up, tells the viewer, and tries again on the usual ladder for as long as anyone waits.
    /// The pre-flight at start-up still runs, so a wrong host or a refused IQ mode is still a
    /// configuration error.
    /// </remarks>
    public bool OnDemand { get; set; }

    /// <summary>
    /// With <see cref="OnDemand"/>, how long the session is kept after the last viewer leaves,
    /// in seconds. Default 60: a page refresh, a tab switch or a flaky connection should not
    /// cost the receiver a tear-down and rebuild each time. 0 closes at once.
    /// </summary>
    public int LingerSeconds { get; set; } = 60;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Stream parameters used only when Device is <c>openwebrx:&lt;url&gt;</c> - a receive-only
/// station listening to a public OpenWebRX or OpenWebRX+ receiver's demodulated audio. Ignored
/// for every other device. Where to tune is not here: that comes from the band plan.
/// </summary>
/// <remarks>
/// The receiver demodulates, so this chooses only which demodulator (from the band plan's
/// sideband: USB, LSB, or narrow FM for <c>"sideband": "fm"</c>) and the passband it is asked
/// for. Its AGC and its audio compression are the receiver operator's.
/// </remarks>
public sealed class OpenWebRxConfig
{
    /// <summary>
    /// The receiver profile to ask for: its id (<c>sdr|profile</c>) or its name as the
    /// receiver's page lists it. Asking moves the receiver for everyone listening to it, so it is
    /// asked once a session and never fought over. Null (the default) listens on whatever band
    /// the receiver is on, and start-up stops if that band does not reach the dial.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>Bottom of the SSB passband to ask for, in Hz from the dial (mirrored for LSB).
    /// Default 150.</summary>
    public int? SsbLowHz { get; set; }

    /// <summary>Top of the SSB passband to ask for, in Hz from the dial. Default 3450; at most
    /// 6000, the top of the receiver's 12 kHz audio.</summary>
    public int? SsbHighHz { get; set; }

    /// <summary>Half the width of the FM channel filter to ask for, in Hz, for
    /// <c>"sideband": "fm"</c>. Default 5000; at most 6000.</summary>
    public int? FmHalfWidthHz { get; set; }

    /// <summary>Audio discarded after each connect, in ms, while the receiver's AGC settles.
    /// Default 1000.</summary>
    public int? StartupGuardMs { get; set; }

    /// <summary>Linear gain on the received audio; 1.0 (the default) is the receiver's own
    /// scaling.</summary>
    public double? Gain { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Flavour B: one daemon fronting many UberSDR web receivers, with a page that lists them and a
/// visitor picking one. Null (the default) is flavour A, one receiver or one radio per process.
/// </summary>
/// <remarks>
/// <para>Mutually exclusive with <see cref="DaemonConfig.Device"/>. The two say incompatible
/// things about what this process is: a station has one input and can transmit through it, while
/// a monitor has one input per receiver and can transmit through none of them.</para>
/// <para>Receive only, and no host interfaces: a monitor configures no KISS, no PTT, no config
/// API, no survey and no paging, and none of them are reachable on its port. What it serves is
/// the picker, one page per receiver, and the JSON the picker polls.</para>
/// <para>See <c>docs/dev/archive/monitor-plan.md</c> and <c>docs/reference/config.md</c>'s <c>monitor</c> section.</para>
/// </remarks>
public sealed class MonitorConfig
{
    /// <summary>
    /// This site's own address as the world reaches it, e.g.
    /// <c>"https://monitor.ukpacketradio.network"</c>. Empty (the default) works it out from each
    /// request's <c>Host</c> header instead.
    /// </summary>
    /// <remarks>
    /// <para>What it is for is the <c>welcome</c> a station gets on its uplink: the address of
    /// its own page here, so its journal reads "publish: live at
    /// https://monitor.ukpacketradio.network/r/gb7rdg-2/" rather than naming its slug. The
    /// header-derived answer is null behind a tunnel that rewrites <c>Host</c>, which is what
    /// this site itself runs behind, and the site owner is the one who knows the name the world
    /// uses.</para>
    /// <para>A scheme, a host and an optional port, and nothing after them, because the site is
    /// served from the root of its port. A trailing slash is accepted and normalised away while
    /// the file is read, so everything downstream sees one shape.</para>
    /// </remarks>
    public string PublicUrl { get; set; } = "";

    /// <summary>Where the list of receivers comes from: the UberSDR project's public
    /// directory, or anything that serves the same JSON. An absolute http or https URL.</summary>
    public string Directory { get; set; } = "https://instances.ubersdr.org/api/instances";

    /// <summary>How often the directory is fetched again, in minutes. 0 fetches once, at
    /// start-up, and never again.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>How long a receiver's session is held after the last viewer of that receiver
    /// leaves, in seconds. The same linger the single-receiver flavour uses, per receiver.</summary>
    public int LingerSeconds { get; set; } = 60;

    /// <summary>
    /// When non-empty, the only hosts offered - which is how a smoke test runs the picker
    /// against two receivers rather than fifty. Matched on the directory's <c>host</c>, case
    /// insensitively.
    /// </summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>
    /// Hosts never offered, whatever else says otherwise: <see cref="Deny"/> beats
    /// <see cref="Allow"/>. This is how an operator who asks not to be listed is not listed,
    /// so it is not a convenience and it is tested.
    /// </summary>
    public List<string> Deny { get; set; } = [];

    /// <summary>
    /// The modems every receiver is given, in the same schema as the top-level
    /// <see cref="DaemonConfig.Modems"/>. Inside this section rather than at the top level so
    /// that one file cannot half-describe a station in one flavour and a monitor in the other.
    /// </summary>
    public List<ModemConfig> Modems { get; set; } = [];

    /// <summary>
    /// Private stations this site accepts an uplink from, each with the callsign it must say it
    /// is, the slug its page is served under, and the SHA-256 of the token issued to it. Empty
    /// (the default) accepts none, and <c>/uplink</c> refuses every connection.
    /// </summary>
    /// <remarks>
    /// The other half of <c>publish</c>, which is what a station puts in its own config. See
    /// <c>docs/dev/archive/uplink-plan.md</c> and <c>docs/reference/config.md</c>'s <c>monitor.uplinks</c>.
    /// </remarks>
    public List<UplinkConfig> Uplinks { get; set; } = [];

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// One private station this monitor will list: who it is, where its page goes, and the hash of
/// the token that proves it.
/// </summary>
/// <remarks>
/// <para>The site issues the token; a station cannot mint one and cannot ask for a slug. Both of
/// those are decisions somebody took, written down here, and the wire carries neither: a
/// connection presents a token and says which callsign it is, and everything else about how it
/// appears comes from this entry. See <c>docs/dev/archive/uplink-plan.md</c> 4.4.</para>
/// <para>Removing an entry needs a restart, which is accepted (uplink-plan section 8).</para>
/// </remarks>
public sealed class UplinkConfig
{
    /// <summary>
    /// The callsign this station must say it is in its <c>hello</c>, and the name its row on the
    /// picker carries. A station whose <c>publish.callsign</c> does not match is refused.
    /// </summary>
    public string Callsign { get; set; } = "";

    /// <summary>
    /// The path segment its page is served under, <c>/r/&lt;slug&gt;/</c>. Lower-case letters,
    /// digits and hyphens; normally the callsign lower-cased, so <c>GB7RDG-2</c> is
    /// <c>gb7rdg-2</c>. Written down rather than derived, so the URL a visitor bookmarks is a
    /// decision somebody took and not a function that might change.
    /// </summary>
    public string Slug { get; set; } = "";

    /// <summary>
    /// The SHA-256 of the token issued to this station, as 64 hex characters.
    /// <c>pdn-soundmodem --uplink-token</c> prints a token and this hash together.
    /// </summary>
    /// <remarks>
    /// Hashed at rest because the monitor only ever compares: it never needs the plaintext, and a
    /// config file that leaks does not hand out working uplinks. Plain SHA-256 with no salt and
    /// no work factor is deliberate rather than an oversight - the token is 256 bits from
    /// <c>RandomNumberGenerator</c>, so there is no dictionary to defend against, and a work
    /// factor would only cost this process time on every connection.
    /// </remarks>
    public string TokenSha256 { get; set; } = "";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Frame log: every frame heard, written to a SQLite file. Null = not kept.</summary>
public sealed class FrameLogConfig
{
    /// <summary>
    /// Where to keep it. The packaged service runs as an unprivileged user, so the default sits
    /// under its own state directory rather than somewhere it cannot write: <c>frames.db</c>
    /// under <c>$STATE_DIRECTORY</c>, which a template instance has to itself (see
    /// <see cref="StateDirectory"/>).
    /// </summary>
    public string Path { get; set; } = StateDirectory.PathFor("frames.db");

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Signal survey: watch the whole passband for transmissions this station cannot read, and keep
/// the ones worth looking at later. Null = not surveying.
/// </summary>
/// <remarks>
/// Off unless configured, and budgeted when it is: this writes audio to disk unattended for as
/// long as the station runs. See <see cref="Survey.SignalSurvey"/> for what counts as worth
/// keeping and why.
/// </remarks>
public sealed class SurveyConfig
{
    /// <summary>Where captures are written - a WAV and a JSON sidecar per burst. The packaged
    /// service runs unprivileged, so the default sits under its own state directory
    /// (<c>survey</c> under <c>$STATE_DIRECTORY</c>, see <see cref="StateDirectory"/>).</summary>
    public string Path { get; set; } = StateDirectory.PathFor("survey");

    /// <summary>Byte budget for that directory. On reaching it the oldest captures are deleted
    /// to make room, so a station left collecting for a week keeps its recent past rather than
    /// stopping on day one and leaving an empty tail.</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Most captures in any rolling hour.</summary>
    public int MaxPerHour { get; set; } = 30;

    /// <summary>How long the same part of the spectrum is left alone after a capture. One station
    /// working an unclaimed frequency is one discovery, not two hundred.</summary>
    public double CooldownSeconds { get; set; } = 120;

    /// <summary>Audio kept either side of the burst.</summary>
    public double MarginSeconds { get; set; } = 1.0;

    /// <summary>Longest burst still plausibly a packet. This, not width, is what separates a
    /// voice over from a wideband data burst - they occupy much the same 2.4 kHz.</summary>
    public double MaxSeconds { get; set; } = 20;

    /// <summary>Weakest burst worth keeping, in dB over the noise floor.</summary>
    public double MinPeakSnrDb { get; set; } = 6;

    /// <summary>
    /// Which verdicts to write out: <c>unclaimed</c> (outside every configured band),
    /// <c>missed</c> (inside one, nothing decoded), <c>unattributed</c> (a frame decoded with no
    /// readable AX.25 addresses). Empty = the default three.
    /// </summary>
    public string[]? Capture { get; set; }

    /// <summary>
    /// Whether to read each capture back with every mode that could have carried it, and
    /// propose the modems that would have read the traffic. Off by default: it is DSP work
    /// beside a real-time receiver, and a station that wants its CPU for its modems should
    /// have it.
    /// </summary>
    /// <remarks>
    /// Bounded by construction rather than by a rate limit - see <c>ProspectorWorker</c> - at a
    /// twentieth of one core whatever the station hears. Proposals are read from
    /// <c>/api/proposals</c>, which needs <see cref="ApiConfig.Key"/> set; without one they
    /// still reach the journal as they are made.
    /// </remarks>
    public bool Propose { get; set; }

    /// <summary>
    /// Separate captures a proposal needs behind it - separate transmissions, on separate
    /// occasions, each carrying a frame whose own check sequence verified. One decode is a
    /// decode; a modem slot is a standing commitment.
    /// </summary>
    public int ProposeMinCaptures { get; set; } = 3;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Continuous raw capture of the channel's receive audio - chunked WAVs under a byte budget,
/// so the whole run can be re-scored offline against a later receiver; null = off. Unlike the
/// <see cref="SurveyConfig">survey</see>, which curates bursts, this keeps the unedited
/// stream - see <see cref="RawCaptureWriter"/> for why both exist.
/// </summary>
public sealed class RawCaptureConfig
{
    /// <summary>Where chunks are written. The packaged service runs unprivileged, so the
    /// default sits under its own state directory (<c>raw</c> under <c>$STATE_DIRECTORY</c>, see
    /// <see cref="StateDirectory"/>).</summary>
    public string Path { get; set; } = StateDirectory.PathFor("raw");

    /// <summary>Byte budget for the directory; the oldest chunks are pruned to fit. The
    /// default keeps roughly two days at a 12 kHz DSP rate - size it to the disk and the
    /// campaign, not the other way round.</summary>
    public long MaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Audio minutes per chunk. Fifteen matches the GB7RDG benchmark methodology -
    /// long enough that a burst almost never straddles a boundary, short enough to copy
    /// around.</summary>
    public int ChunkMinutes { get; set; } = 15;

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>Which kind of audio input the station runs on, for resolving the dead-feed
/// defaults - each family has its own way of dying and its own legitimate quiet.</summary>
public enum DeadFeedDevice
{
    /// <summary>An ALSA sound card.</summary>
    Alsa,

    /// <summary>A FlexRadio DAX stream (<c>flex:</c> device).</summary>
    Flex,

    /// <summary>An UberSDR web receiver's IQ stream (<c>ubersdr:</c> device).</summary>
    UberSdr,

    /// <summary>An OpenWebRX web receiver's audio stream (<c>openwebrx:</c> device). The
    /// UberSDR family's watches: a receiver with its squelch open always carries noise, and a
    /// session that has delivered and then stops is a hung stream.</summary>
    OpenWebRx,

    /// <summary>A bench input with no radio behind it: a recording replayed as the capture
    /// device (<c>--wav-loop</c>), or the in-process mock radio (<c>flex:mock</c>), whose
    /// DAX-RX path deliberately delivers nothing between injected frames.</summary>
    WavLoop,

    /// <summary>
    /// A private station's audio arriving over an uplink socket, on a monitor
    /// (<c>monitor.uplinks</c>). Silence off, starvation on.
    /// </summary>
    /// <remarks>
    /// Silence is off because what arrives is 16-bit PCM of somebody else's band, and a quiet
    /// band quantised to 16 bits can genuinely be exact zeros for a while - so the watch that
    /// catches a Flex padding a dead stream would here be catching a quiet evening. Starvation is
    /// on and means something precise: this site asked for audio and the station's socket
    /// delivered none, which is a half-open connection rather than a quiet band, and the answer
    /// is to drop the socket so the station reconnects.
    /// </remarks>
    Uplink,
}

/// <summary>
/// Dead-feed protection thresholds; null (no section) = the per-device defaults. Two watches,
/// two failure families: <c>silenceSeconds</c> catches a feed that keeps delivering samples
/// which are all exactly zero (a dead Flex VITA stream pads silence at full rate - the
/// 2026-08-07 incident recorded 6.8 hours of it); <c>starvationSeconds</c> catches a feed
/// that stops delivering samples at all (a hung network stream, a stalled or unplugged
/// card). Either firing takes the proven recovery: an orderly shutdown with exit 1, so the
/// unit restarts and rebuilds the device session from scratch.
/// </summary>
public sealed class DeadFeedConfig
{
    /// <summary>
    /// Seconds of unbroken digital silence that declare the feed dead; 0 = off; null = the
    /// device default - 30 for flex and ubersdr (their streams always carry noise-floor
    /// energy when healthy, so half a minute of exact zeros is a dead feed with certainty),
    /// off for ALSA and --wav-loop.
    /// </summary>
    /// <remarks>
    /// Off for ALSA by deliberate default, not oversight: genuinely-silent wired inputs
    /// exist, and a disconnected cable must not restart-loop the service. Set it here if
    /// your input always carries audible noise floor and you want the same protection.
    /// </remarks>
    public double? SilenceSeconds { get; set; }

    /// <summary>
    /// Seconds without any samples delivered that declare the feed starved; 0 = off; null =
    /// the device default - 30 for flex, ubersdr and ALSA, off for --wav-loop (a recording
    /// paces itself and cannot starve).
    /// </summary>
    public double? StarvationSeconds { get; set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>
    /// The thresholds to run <paramref name="device"/> with: the device family's defaults,
    /// overridden field-by-field where <paramref name="config"/> states a value. 0 in either
    /// place means that watch is off.
    /// </summary>
    public static (double SilenceSeconds, double StarvationSeconds) Resolve(
        DeadFeedConfig? config, DeadFeedDevice device)
    {
        (double silence, double starvation) = device switch
        {
            DeadFeedDevice.Flex or DeadFeedDevice.UberSdr or DeadFeedDevice.OpenWebRx => (30.0, 30.0),
            DeadFeedDevice.Alsa or DeadFeedDevice.Uplink => (0.0, 30.0),
            _ => (0.0, 0.0),
        };

        return (config?.SilenceSeconds ?? silence, config?.StarvationSeconds ?? starvation);
    }
}

/// <summary>Browser waterfall endpoint (spectrum + waterfall + per-frame burst
/// attribution); null = disabled. See WaterfallWebServer.</summary>
public sealed class WaterfallConfig
{
    /// <summary>HTTP listen port.</summary>
    public int Port { get; set; } = 8107;

    /// <summary>Rig dial frequency in Hz, the page's opening default (each browser can
    /// retune its own copy). 0 = unset: audio frequencies only until the operator enters
    /// one.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>
    /// What kind of radio the page is drawing for: "usb" (RF = dial + audio, the default), "lsb"
    /// (RF = dial - audio), or "fm". On FM there is no arithmetic at all - the dial is the
    /// channel, the audio is audio, and the page shows no RF scale.
    /// </summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Waterfall line rate / display frame rate. Default 30.</summary>
    public int LinesPerSecond { get; set; } = 30;

    /// <summary>FFT length override; 0 = the rate default (2048 at 12 kHz, 8192 at 48 kHz).</summary>
    public int FftSize { get; set; }

    /// <summary>
    /// The page is for the public, not the operator: it takes the <see cref="Title"/> and
    /// <see cref="About"/> below, credits and links the receiver it listens through, and hides
    /// the KISS host badges, which name ports on a box a visitor cannot reach. Nothing else
    /// changes, and nothing is removed from the operator's page. Off by default.
    /// </summary>
    public bool Public { get; set; }

    /// <summary>
    /// The operator's page carries the sound card's Mixer group, and <c>/api/mixer</c> answers,
    /// with no <c>api.key</c> at all. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>For a station whose page is already reachable only by whoever is allowed to reach it
    /// - a bench station on a home LAN, or a page behind SSH - where making the operator paste a
    /// shared secret into a browser to nudge a capture gain buys nothing. It opens exactly one
    /// endpoint: everything else under <c>/api/</c> can retune the radio and key the transmitter,
    /// and still wants the key.</para>
    /// <para>It lives here rather than under <c>alsa.mixer</c> because it says who may reach this
    /// page and this port, not what the card is set to. The exposure it creates is the
    /// waterfall's, and it is the waterfall's own <see cref="Public"/> page that must never carry
    /// it.</para>
    /// </remarks>
    public bool EnableAudioControls { get; set; }

    /// <summary>
    /// Whether this station actually serves the open controls: <see cref="EnableAudioControls"/>
    /// and not a <see cref="Public"/> page.
    /// </summary>
    /// <remarks>
    /// One property rather than the same conjunction at the start-up block, because this single
    /// "and not public" is the whole of "never on a public page" - for the page, the GET and the
    /// POST at once, since all three follow from whether the API is installed open. Written where
    /// a test can reach it: a tidy-up that dropped the clause in <c>Program.cs</c>, which is
    /// top-level statements no test constructs, would have left the suite green.
    /// </remarks>
    [JsonIgnore]
    internal bool AudioControlsOpen => EnableAudioControls && !Public;

    /// <summary>Public page title, in the tab and at the top of the page. Null keeps the
    /// page's own.</summary>
    public string? Title { get; set; }

    /// <summary>One paragraph for the visitor on a public page: what the window is, and that it
    /// is receive only. Null shows none.</summary>
    public string? About { get; set; }

    /// <summary>
    /// Whether the file actually said <c>"port"</c>, as opposed to taking the default. A station
    /// on a LAN may reasonably let the default stand; a monitor is a public site, and coming up
    /// on a port nobody chose is not something to do quietly.
    /// </summary>
    [JsonIgnore]
    public bool PortWasStated { get; internal set; }

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>
/// Publishing this station to a public monitor site: the station dials out, the site lists it
/// while the socket is up, and a visitor gets the page this station's own operator sees. Null
/// (the default) publishes nothing, and is what every station is until somebody adds this block.
/// </summary>
/// <remarks>
/// <para>Section 4.3 of <c>docs/dev/archive/uplink-plan.md</c>. Strictly one way: audio, frames and a status
/// sentence go up, a viewer count comes down, and there is nothing in the protocol that could
/// transmit, retune or reconfigure anything here. Leaving is deleting this block and restarting.
/// </para>
/// <para>Mutually exclusive with <see cref="DaemonConfig.Monitor"/>: a station publishes and a
/// monitor accepts, and one process is not both.</para>
/// </remarks>
public sealed class PublishConfig
{
    /// <summary>The site's uplink endpoint, an absolute <c>ws</c> or <c>wss</c> URL. Required.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The token the site owner issued this station. Required, and there is no default: issued
    /// once, pasted in once, not edited by hand, exactly as <c>api.key</c> is.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// This station's callsign, with an optional SSID. Required: a station on a public page that
    /// will not say who it is has no business being there, and the site checks it against the
    /// token.
    /// </summary>
    public string? Callsign { get; set; }

    /// <summary>Who runs it, for the credit line and the picker row. Optional.</summary>
    public string? Operator { get; set; }

    /// <summary>Roughly where it is, for the picker row. Optional.</summary>
    public string? Location { get; set; }

    /// <summary>The radio and antenna, for the credit line. Optional.</summary>
    public string? Radio { get; set; }

    /// <summary>The operator's own page, an absolute http or https URL. Optional.</summary>
    public string? Site { get; set; }

    /// <summary>
    /// What rate the audio is published at, in Hz. An integer divisor of the channel's DSP rate,
    /// 6000 to 48000; unset takes the DSP rate capped at 12000. The relayed picture spans 0 to
    /// half of this, so a modem above that edge is not published and start-up says so. It is also
    /// the only lever an operator on a thin upload has, there being no codec: 12000 costs about
    /// 194 kbit/s upstream while somebody is watching, 6000 about 98, and 48000 about 770.
    /// </summary>
    public int? AudioRate { get; set; }

    /// <summary>
    /// <c>"always"</c> (the default) publishes decoded frames whether or not anybody is watching,
    /// which is what makes a quiet band look alive to somebody arriving an hour later and costs
    /// well under a kilobit a second. <c>"watched"</c> holds them back until somebody is.
    /// </summary>
    public string Frames { get; set; } = "always";

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}

/// <summary>pdn-soundmodem daemon configuration file. JSON, with comments and trailing
/// commas accepted (see <see cref="Options"/>) and case-insensitive key matching - the
/// shipped soundmodem.example.json carries one such comment. Full reference:
/// docs/reference/config.md.</summary>
public sealed class DaemonConfig
{
    /// <summary>ALSA device for capture and playback.</summary>
    public string Device { get; set; } = "default";

    /// <summary>
    /// The sound card to receive from, when it is not <see cref="Device"/>; null receives from
    /// <see cref="Device"/>. See <see cref="ValidateSplitDevices"/>.
    /// </summary>
    public string? CaptureDevice { get; set; }

    /// <summary>
    /// The sound card to transmit through, when it is not <see cref="Device"/>; null transmits
    /// through <see cref="Device"/>.
    /// </summary>
    public string? PlaybackDevice { get; set; }

    /// <summary>The device the station actually receives from.</summary>
    [JsonIgnore]
    public string CaptureDeviceInUse => CaptureDevice ?? Device;

    /// <summary>The device the station actually transmits through.</summary>
    [JsonIgnore]
    public string PlaybackDeviceInUse => PlaybackDevice ?? Device;

    /// <summary>Capture rate; card-native (48000) recommended - the daemon decimates.</summary>
    public int CaptureRate { get; set; } = 48000;

    /// <summary>KISS TCP listen port - shared by every modem, addressed by sub-channel nibble.
    /// Individual modems can also get a port to themselves; see <see cref="ModemConfig.KissPort"/>.</summary>
    public int KissPort { get; set; } = 8105;

    /// <summary>
    /// The most bytes one KISS frame from a host may carry, on the shared port and the per-modem
    /// ports alike. A frame that reaches it is dropped and the journal says so.
    /// </summary>
    /// <remarks>
    /// The bound is on what is buffered per host before a frame's closing delimiter arrives, so
    /// it is a memory bound and not a statement about any mode: a frame too long for the mode it
    /// is addressed to is refused by that mode when it is asked to modulate it. The default is
    /// above anything a mode here carries (see <see cref="Kiss.KissDecoder.DefaultMaxFrame"/>);
    /// a station running a plugin modem that carries more raises it here.
    /// </remarks>
    public int KissMaxFrameBytes { get; set; } = Kiss.KissDecoder.DefaultMaxFrame;

    /// <summary>
    /// Address every TCP listener binds to - KISS, the per-modem ports, the waterfall, paging
    /// and ARDOP alike; "*" or "0.0.0.0" for all interfaces. One setting rather than one per
    /// service: they are all on the same machine facing the same network.
    /// </summary>
    /// <remarks>
    /// Loopback by default because KISS has no authentication whatsoever - anything that can
    /// reach the port can transmit on your licence.
    /// </remarks>
    public string Bind { get; set; } = "127.0.0.1";

    /// <summary>
    /// Which kind of radio this station is on, for turning RF frequencies into audio ones: "usb"
    /// (RF = dial + audio, the data-mode norm), "lsb" (RF = dial - audio), or "fm" for a channel
    /// radio, where there is nothing to turn: "rfFrequency" states the channel, the audio tones
    /// are tones on it, and neither the planner nor the page does any arithmetic between them.
    /// </summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>
    /// Whether the file actually said "sideband", as opposed to taking the default. On a Flex
    /// the slice mode states the sideband, so a value that was merely defaulted is silently
    /// corrected while one that was written down and contradicts the radio is an error.
    /// </summary>
    [JsonIgnore]
    public bool SidebandWasStated { get; private set; }

    /// <summary>
    /// Whether the <c>waterfall</c> section stated a <c>sideband</c> of its own, as opposed to
    /// taking the default.
    /// </summary>
    /// <remarks>
    /// The page's kind comes from the band plan where there is one and from the section's own
    /// value otherwise, so a station placed by audio centre has always had to say it twice. That
    /// stays true for the two sidebands - a page told nothing draws a USB scale, as it always
    /// has - but not for FM, where a page told nothing draws an RF scale that is a lie, which is
    /// the whole of issue #413. So a defaulted value here lets a top-level "fm" through.
    /// </remarks>
    [JsonIgnore]
    public bool WaterfallSidebandWasStated { get; private set; }

    /// <summary>
    /// Whether the file actually said "device", as opposed to taking the default. "device" and
    /// "monitor" are exclusive, and refusing a monitor config because <c>Device</c> holds the
    /// string "default" that nobody wrote would be refusing it for a value the operator never
    /// typed.
    /// </summary>
    [JsonIgnore]
    public bool DeviceWasStated { get; private set; }

    /// <summary>
    /// Pins the dial instead of letting the daemon choose one - for a net frequency, or to
    /// match another application. Only meaningful alongside <see cref="ModemConfig.RfFrequency"/>.
    /// Unset (the default) the daemon picks a dial that centres every modem in the passband and
    /// prints it; pinned, it is used as-is and merely checked, because you can see your radio
    /// and the passband it checks against is only nominal.
    /// </summary>
    public double? DialFrequency { get; set; }

    /// <summary>The logical modems sharing the audio channel.</summary>
    public List<ModemConfig> Modems { get; set; } = [];

    /// <summary>
    /// Modem plugins to load before the modems are built: assemblies outside this package that
    /// provide modes it does not contain. Empty by default, which is the usual case.
    /// </summary>
    /// <remarks>
    /// <b>This loads and runs code from outside the package</b>, which is why it is a list of
    /// explicit paths rather than any kind of discovery: nothing gets loaded that this file does
    /// not name. A plugin that fails to load is reported by name and the daemon carries on without
    /// its modes - but a modem configured to use one of those modes is then an unknown mode, and
    /// that does stop start-up.
    /// </remarks>
    public List<ModemPluginConfig> ModemPlugins { get; set; } = [];

    /// <summary>
    /// Polyglot KISS ports: overlaid modems behind one port, each frame sent in the mode its next
    /// hop was last heard in. Empty by default.
    /// </summary>
    public List<PolyglotConfig> Polyglot { get; set; } = [];

    /// <summary>
    /// Shorthand for the usual <see cref="Polyglot"/> port: every packet modem on it, the first
    /// in <see cref="Modems"/> as the default, remembered for the default time. Null for none.
    /// </summary>
    public int? PolyglotPort { get; set; }

    /// <summary>PTT control; null = VOX / none.</summary>
    public PttConfig? Ptt { get; set; }

    /// <summary>A radio controlled through Hamlib's rigctld; null = no rig control, as before.
    /// See <see cref="RigConfig"/>.</summary>
    public RigConfig? Rig { get; set; }

    /// <summary>The built-in pdn-mailcast receiver; null = none, the default. See
    /// <see cref="MailcastConfig"/>.</summary>
    public MailcastConfig? Mailcast { get; set; }

    /// <summary>
    /// Carrier sense read from the radio rather than inferred from the audio; null leaves the
    /// station on the audio-derived answer, which is what it has always had and which is wrong on
    /// FM. See <see cref="CarrierSenseConfig"/>.
    /// </summary>
    public CarrierSenseConfig? CarrierSense { get; set; }

    /// <summary>
    /// The operator's transmitter test - a two-tone or single-tone keyup on demand. The defaults
    /// stand when this is absent; there is nothing to switch on.
    /// </summary>
    public TxTestConfig TxTest { get; set; } = new();

    /// <summary>
    /// Sound-card settings beyond the PCM stream - today, the card's mixer. Null (the default)
    /// leaves every mixer control exactly as the card has it, which is what every station did
    /// before this section existed.
    /// </summary>
    public AlsaConfig? Alsa { get; set; }

    /// <summary>POCSAG paging endpoint; null = disabled.</summary>
    public PagingConfig? Paging { get; set; }

    /// <summary>ARDOP virtual TNC with the channel to itself; null = disabled. Exclusive
    /// with Modems/Paging. The other form is an `ardop` modem entry, which shares the channel
    /// with the packet modems; docs/dev/ardop-design.md section 2.2 designed only this one.</summary>
    public ArdopConfig? Ardop { get; set; }

    /// <summary>Headless FlexRadio slice params (Device <c>flex:</c> with no <c>@station</c>);
    /// null = defaults. Ignored for ALSA devices and attach-mode Flex.</summary>
    public FlexConfig? Flex { get; set; }

    /// <summary>UberSDR stream params (Device <c>ubersdr:</c>); null = defaults. Ignored for
    /// every other device.</summary>
    public UberSdrConfig? UberSdr { get; set; }

    /// <summary>OpenWebRX stream params (Device <c>openwebrx:</c>); null = defaults. Ignored for
    /// every other device.</summary>
    public OpenWebRxConfig? OpenWebRx { get; set; }

    /// <summary>Browser waterfall endpoint; null = disabled.</summary>
    public WaterfallConfig? Waterfall { get; set; }

    /// <summary>Flavour B: many web receivers behind one page. Null = the single-station
    /// flavour, which is everything else in this file. Exclusive with <see cref="Device"/>.</summary>
    public MonitorConfig? Monitor { get; set; }

    /// <summary>Publishing this station to a public monitor site; null publishes nothing, which
    /// is the default. Exclusive with <see cref="Monitor"/>. See <see cref="PublishConfig"/>.</summary>
    public PublishConfig? Publish { get; set; }

    /// <summary>
    /// Change this station's configuration at runtime over HTTP. Omit it (the default) and there
    /// is no such surface at all.
    /// </summary>
    public ApiConfig? Api { get; set; }

    /// <summary>Frame log; null = frames are heard and not written down.</summary>
    public FrameLogConfig? FrameLog { get; set; }

    /// <summary>Signal survey; null = signals this station cannot read go unrecorded.</summary>
    public SurveyConfig? Survey { get; set; }

    /// <summary>Publishing what this station hears, for a monitoring system to collect;
    /// null publishes nothing. Served on the waterfall's listener.</summary>
    public MetricsConfig? Metrics { get; set; }

    /// <summary>Answering off-frequency stations on their frequency; null = measure only.</summary>
    public FrequencyMatchingConfig? FrequencyMatching { get; set; }

    /// <summary>Continuous raw receive-audio capture; null = off.</summary>
    public RawCaptureConfig? RawCapture { get; set; }

    /// <summary>Dead-feed protection thresholds; null = the per-device defaults
    /// (see <see cref="DeadFeedConfig"/>).</summary>
    public DeadFeedConfig? DeadFeed { get; set; }

    /// <summary>
    /// Whether to listen for the station identifications a NinoTNC sends alongside its PSK SSB
    /// data modes rather than within them - 300 AFSK AX.25, 200 Hz above the carrier. On by
    /// default: it costs one cheap demodulator per PSK modem, changes nothing a host sees, and
    /// turns a recurring unreadable burst in the middle of the channel into a callsign.
    /// </summary>
    /// <remarks>
    /// Applies only to the four modes the TNC behaves this way in (<c>bpsk300</c>, <c>qpsk600</c>,
    /// <c>bpsk1200</c>, <c>qpsk2400</c> and their aliases) - see
    /// <see cref="Modems.IdBeaconGhost"/>. A station running none of them is unaffected either way.
    /// </remarks>
    public bool IdBeacons { get; set; } = true;

    /// <summary>
    /// Settings present in the file that this version does not know. Kept so start-up can say
    /// so out loud: System.Text.Json drops unknown members silently, which turns a typo - or a
    /// setting that has since been withdrawn, like the old "csma" block - into a config that
    /// looks accepted and does something else.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }

    /// <summary>Non-fatal complaints raised while loading; the daemon prints them at start-up.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>
    /// How the file is read. Internal so that the documentation test reflects the property
    /// names with the same options the daemon deserialises with.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads and validates a configuration file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="asPath">
    /// The path to validate as though the document lived at, when that is not where it is being
    /// read from. <c>POST /api/config</c> parses a proposed document out of a temporary file, and
    /// without this the checks that compare a setting against the config file's own location -
    /// today <c>alsa.mixer.stateFile</c> - would compare it against the temporary name and pass
    /// anything. The refusal has to arrive at the POST, not at the restart afterwards.
    /// </param>
    public static DaemonConfig Load(string path, string? asPath = null)
    {
        var config = JsonSerializer.Deserialize<DaemonConfig>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException(
                "the file contains only `null` - there is nothing to configure from. A minimal "
                + "working file is {\"device\": \"default\", \"modems\": [{\"subChannel\": 0, "
                + "\"mode\": \"afsk1200\"}]}");
        config.DeviceWasStated = StatesKey(path, "device");
        if (config.Waterfall is not null)
        {
            config.Waterfall.PortWasStated = StatesKey(path, "waterfall", "port");
        }

        // Before the flavour split, because "publish" is refused on both sides of it: a station
        // has to be told what is wrong with its block, and a monitor has to be told that a
        // monitor does not publish.
        if (config.Publish is not null)
        {
            ValidatePublish(config);
        }

        ValidateSplitDevices(config);
        ValidateAlsa(config, asPath ?? path);

        // Above the flavour split: a monitor is a radio kind's business too - it plans its
        // receivers in RF terms and draws pages off the answer - and below the return it would
        // never be asked, so a monitor file could say anything at all here and be taken as USB.
        RequireKnownRadioKind(config);

        // Above the flavour split too: a monitor listens to a radio like anything else, and a
        // misspelled driver name there should be refused rather than silently ignored.
        RequireKnownCarrierSenseRadio(config);

        // Above the flavour split as well, so that a monitor file with a "mailcast" section is
        // told so rather than having it silently ignored.
        MailcastConfig.Validate(config);

        if (config.Monitor is not null)
        {
            ValidateMonitor(config);

            // Everything below this is about a station: an ARDOP TNC, a KISS port, a dial, a
            // PTT. A monitor has none of them, and letting the station checks run over an empty
            // "modems" list would answer a monitor file with a diagnostic about a modem the
            // operator never wrote. The monitor's own modems were checked above.
            config.ModemPlugins ??= [];
            RequireBind(config);
            config.Warnings = CollectWarnings(config);
            return config;
        }

        // ARDOP is no longer exclusive with the packet modems: it shares the channel, and the
        // daemon holds packet transmissions while an ARQ session is up (SoundModemChannel's
        // TransmitInhibit). What is not allowed is asking for it twice.
        int ardopModems = config.Modems.Count(m => IsArdop(m.Mode));
        if (ardopModems > 1)
        {
            throw new InvalidDataException(
                "two modems have \"mode\": \"ardop\". One ARDOP TNC per channel - it is a whole "
                + "virtual TNC with its own host interface, not a demodulator you can run twice.");
        }

        if (ardopModems > 0 && config.Ardop is not null)
        {
            throw new InvalidDataException(
                "ARDOP is configured twice - once as a modem entry and once in the top-level "
                + "\"ardop\" section. Keep the modem entry (it can also carry \"frequency\" and "
                + "\"port\") and delete the \"ardop\" section.");
        }

        // "bandwidth" on an ardop entry is the TNC's ARQBW as well as the planner's reservation,
        // and ARDOP has exactly four widths to negotiate. Any other number used to be taken
        // quietly: the plan reserved a width no session could ever occupy, and there was nothing
        // legal to cap the TNC at.
        foreach (ModemConfig ardop in config.Modems
                     .Where(m => IsArdop(m.Mode) && m.Bandwidth is not null))
        {
            if (!ArdopChannelBridge.TryArqBandwidth(ardop.Bandwidth!.Value, out _))
            {
                throw new InvalidDataException(
                    $"modem {ardop.SubChannel} is \"mode\": \"ardop\" with \"bandwidth\": "
                    + ardop.Bandwidth.Value.ToString(
                        "0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ". ARDOP negotiates "
                    + $"{ArdopChannelBridge.NegotiableBandwidths} Hz and nothing else - use one "
                    + "of those, or remove \"bandwidth\" to plan for the widest (2000) as before.");
            }
        }

        foreach (ModemConfig packing in config.Modems
                     .Where(m => m.MaxBurstSeconds is not null || m.BurstGatherSeconds is not null))
        {
            ValidatePacking(packing);
        }

        foreach (ModemConfig levelled in config.Modems.Where(m => m.TxAmplitude is not null))
        {
            double level = levelled.TxAmplitude!.Value;
            if (!levelled.Mode.StartsWith("ms110d-", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"modem {levelled.SubChannel} is \"mode\": \"{levelled.Mode}\" with \"txAmplitude\". "
                    + "Only the ms110d-* modes take a transmit level - remove it from this entry.");
            }
            if (!double.IsFinite(level) || level <= 0 || level > 1)
            {
                throw new InvalidDataException(
                    $"modem {levelled.SubChannel} has \"txAmplitude\": "
                    + level.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ". Use a level above 0 and at most 1 (1.0 peaks at about 0.75 of full scale), or remove it for the default 0.5.");
            }
        }

        if (config.Modems.Count == 0 && config.Ardop is null)
        {
            config.Modems.Add(new ModemConfig());
        }

        var duplicates = config.Modems.GroupBy(m => m.SubChannel).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidDataException(
                $"two modems share \"subChannel\": {duplicates[0].Key}. Each modem needs its own "
                + "KISS sub-channel (0-15) - renumber one of them.");
        }

        // Not on a channel radio, where the two are unrelated rather than the same thing twice:
        // "rfFrequency" is the channel the set is on and "frequency" is where the tones sit in
        // its audio, and an FM station may well have to say both.
        ModemConfig? bothWays = RfPlan.IsFmRadio(config.Sideband)
            ? null
            : config.Modems.FirstOrDefault(m => m.RfFrequency is not null && m.Frequency is not null);
        if (bothWays is not null)
        {
            throw new InvalidDataException(
                $"modem {bothWays.SubChannel} sets both \"frequency\" ({bothWays.Frequency}) and "
                + $"\"rfFrequency\" ({bothWays.RfFrequency}). Those say the same thing two ways - "
                + "\"frequency\" is an audio offset, \"rfFrequency\" is a place on the band. Keep one.");
        }

        // A band plan is all-or-nothing: the dial is shared, so a modem pinned in audio terms
        // would drift across the band as the dial is chosen for the others.
        var rfAddressed = config.Modems.Where(m => m.RfFrequency is not null).ToList();
        if (rfAddressed.Count > 0 && rfAddressed.Count != config.Modems.Count)
        {
            string audioOnly = string.Join(", ", config.Modems
                .Where(m => m.RfFrequency is null)
                .Select(m => $"modem {m.SubChannel} ({m.Mode})"));
            throw new InvalidDataException(
                $"some modems have \"rfFrequency\" and some do not ({audioOnly}). Give every modem "
                + "an \"rfFrequency\" or none of them: the dial is shared, so one pinned to an audio "
                + "offset would sit at whatever RF the dial chosen for the others happens to put it.");
        }

        // 0 is the documented "leave the radio's own filter alone"; anything else has to be a
        // filter an SSB transmitter could plausibly be set to, or the operator has typed a
        // frequency (14100000) where a cut-off belongs and the radio would take it.
        if (config.Flex?.TransmitFilterHighHz is int filterHigh && filterHigh is not 0 and (< 500 or > 10_000))
        {
            throw new InvalidDataException(
                $"\"flex\".\"transmitFilterHighHz\" is {filterHigh}. That is an audio cut-off in Hz "
                + "(3000 is a radio's usual default, 3400 clears ms110d) - use 500-10000, 0 to "
                + "leave the radio's own filter alone, or remove it to have it set from the modems.");
        }

        // Negative seconds is a sign error or a misunderstood off-switch either way; 0 is the
        // documented "that watch is off", so say so rather than letting a -30 disable a watch
        // the operator believed they had tightened.
        foreach ((string name, double? seconds) in (ReadOnlySpan<(string, double?)>)
            [
                ("silenceSeconds", config.DeadFeed?.SilenceSeconds),
                ("starvationSeconds", config.DeadFeed?.StarvationSeconds),
            ])
        {
            if (seconds is < 0)
            {
                throw new InvalidDataException(
                    $"\"deadFeed\".\"{name}\" is {seconds}. That is how many seconds the watch "
                    + "waits before declaring the feed dead and restarting the service - use a "
                    + "positive number of seconds, 0 to turn that watch off, or remove it for "
                    + "the device's default.");
            }
        }

        ValidateRig(config);

        // "modemPlugins": null deserialises to a null list, not to the property initialiser, and
        // every read below would then throw where an operator expects a message about their file.
        config.ModemPlugins ??= [];

        // An entry with no path is a half-written line, not a request to load nothing: it would
        // otherwise become a "no path given" load failure at start-up, which reads as the plugin
        // mechanism misbehaving rather than as the file being unfinished.
        if (config.ModemPlugins.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Path)) is not null)
        {
            throw new InvalidDataException(
                "a \"modemPlugins\" entry has no \"path\". Each entry names one assembly to load, "
                + "as {\"path\": \"/opt/pdn/plugins/MyModem.dll\"} - there is no directory to "
                + "scan and no default location, deliberately.");
        }

        RequireBind(config);

        config.SidebandWasStated = StatesKey(path, "sideband");
        config.WaterfallSidebandWasStated = StatesKey(path, "waterfall", "sideband");
        ValidateTxTest(config);
        ValidatePolyglot(config);
        ValidatePorts(config);
        ValidateKissFrames(config);
        config.Warnings = CollectWarnings(config);
        return config;
    }

    /// <summary>
    /// Refuses a <c>rig</c> section, or a rigctld PTT, that cannot do what it says.
    /// </summary>
    private static void ValidateRig(DaemonConfig config)
    {
        bool rigctldPtt = string.Equals(config.Ptt?.Type, "rigctld", StringComparison.Ordinal);
        if (config.Rig is not { } rig)
        {
            if (rigctldPtt)
            {
                throw new InvalidDataException(
                    "\"ptt\": {\"type\": \"rigctld\"} keys the radio through the \"rig\" section's "
                    + "rigctld, and this file has no \"rig\" section. Add \"rig\": {} (it looks for "
                    + $"rigctld at {RigctldEndpoint.Default}), or \"rig\": {{\"rigctld\": \"host:port\"}}.");
            }

            return;
        }

        if (DeviceKinds.Of(config.Device).RigRefusal(config.Device) is string rigRefusal)
        {
            throw new InvalidDataException(rigRefusal);
        }

        if (RigctldEndpoint.TryParse(rig.Rigctld, out string why) is null)
        {
            throw new InvalidDataException(
                $"\"rig\".\"rigctld\" is \"{rig.Rigctld}\": {why}. Write it as host:port, for example "
                + $"\"{RigctldEndpoint.Default}\", or leave it out for that default.");
        }

        if (rig.Mode is string mode)
        {
            if (!RigModes.IsKnown(mode))
            {
                throw new InvalidDataException(
                    $"\"rig\".\"mode\" is \"{mode}\", which is not a mode this station asks a rig for. "
                    + $"Use one of {RigModes.List}, or leave it out for plain USB, LSB or FM.");
            }

            string? implies = RigModes.SidebandOf(mode);
            if (implies is null || !string.Equals(implies, config.Sideband, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"\"rig\".\"mode\" is \"{mode}\", "
                    + (implies is null ? "which is not a sideband or FM mode" : $"which is {implies.ToUpperInvariant()}")
                    + $", and \"sideband\" is \"{config.Sideband}\". The band plan sets this mode, so the "
                    + "two have to agree: change one of them.");
            }
        }

        if (rig.PassbandHz is int passband && (passband < 0 || passband > RigModes.MaxPassbandHz))
        {
            throw new InvalidDataException(
                $"\"rig\".\"passbandHz\" is {passband}. That is the receive filter width in Hz the band "
                + "plan asks the rig for - use 0 for the rig's normal width, a width such as 2400 or "
                + "3000, or leave it out for the plan's own.");
        }
    }

    /// <summary>
    /// Whether the operator's page will carry the Mixer group, and with it the input level meter.
    /// </summary>
    /// <remarks>
    /// <para>Three things have to be true at once, and each of them removes the group on its own:
    /// the station has a sound card to mix, the page is the operator's rather than the public
    /// flavour, and <c>/api/mixer</c> answers this browser at all - which needs either an
    /// <c>api.key</c> or <c>waterfall.enableAudioControls</c>. Without the last one the endpoint
    /// is a 404, the page hides the whole group, and a level meter drawn inside it is a message
    /// nothing renders.</para>
    /// <para>Here rather than inline in the daemon's top-level statements so that it can be
    /// tested: the flavour gate is the whole "never on a public page, a monitor or a FlexRadio"
    /// claim, and it was one uncovered expression.</para>
    /// </remarks>
    /// <param name="device">The <c>device</c> setting.</param>
    /// <param name="waterfall">The <c>waterfall</c> section, or null when there is no page.</param>
    /// <param name="api">The <c>api</c> section, or null when there is none.</param>
    public static bool ShowsMixerGroup(string device, WaterfallConfig? waterfall, ApiConfig? api) =>
        waterfall is { Public: false }
        && IsSoundCard(device)
        && (waterfall.AudioControlsOpen || !string.IsNullOrWhiteSpace(api?.Key));

    /// <summary>
    /// Whether a device string names a sound card, as opposed to a FlexRadio, a web receiver or
    /// a pair of pipes. Only the first kind has a mixer.
    /// </summary>
    /// <param name="device">The <c>device</c> setting.</param>
    public static bool IsSoundCard(string device) =>
        !string.IsNullOrWhiteSpace(device)
        && DeviceKinds.Of(device).HasMixer;

    /// <summary>
    /// Refuses a burst-packing setting that would not do what it says: on a mode that cannot pack,
    /// out of range, or a gather with nothing to gather for.
    /// </summary>
    private static void ValidatePacking(ModemConfig modem)
    {
        string invariant(double value) =>
            value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        if (!modem.Mode.StartsWith("ms110d-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"modem {modem.SubChannel} is \"mode\": \"{modem.Mode}\" with \"maxBurstSeconds\" "
                + "or \"burstGatherSeconds\". Only the ms110d-* modes pack several frames into one "
                + "burst - remove them from this entry.");
        }

        if (modem.MaxBurstSeconds is not double max)
        {
            throw new InvalidDataException(
                $"modem {modem.SubChannel} has \"burstGatherSeconds\" without \"maxBurstSeconds\". "
                + "The gather only matters when frames are packed - add \"maxBurstSeconds\" (for "
                + "example 60) or remove \"burstGatherSeconds\".");
        }

        if (!double.IsFinite(max) || max < 1 || max > ModemConfig.MaxBurstSecondsCeiling)
        {
            throw new InvalidDataException(
                $"modem {modem.SubChannel} has \"maxBurstSeconds\": {invariant(max)}. Use 1 to "
                + $"{invariant(ModemConfig.MaxBurstSecondsCeiling)} seconds, or remove it for one frame per burst.");
        }

        if (modem.BurstGatherSeconds is double gather
            && (!double.IsFinite(gather) || gather < 0 || gather > ModemConfig.BurstGatherSecondsCeiling))
        {
            throw new InvalidDataException(
                $"modem {modem.SubChannel} has \"burstGatherSeconds\": {invariant(gather)}. Use 0 to "
                + $"{invariant(ModemConfig.BurstGatherSecondsCeiling)} seconds; the default is "
                + $"{invariant(ModemConfig.DefaultBurstGatherSeconds)}.");
        }
    }

    /// <summary>
    /// What an <c>alsa</c> section has to say before this station can use it.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored, the same rule the <c>api</c> section keeps: a mixer block on
    /// a FlexRadio would silently do nothing, and a setting that silently does nothing is worse
    /// than one that says so. Exit 2, so systemd's RestartPreventExitStatus=2 stops retrying and
    /// the journal carries one readable sentence.
    /// <para>The dB levels themselves are NOT checked here, because nothing at load time knows
    /// what the card's range is. They are checked against the open card at apply time, where a
    /// value off the end of the range is journalled with the range and that one control is
    /// skipped - a warning rather than an exit, because a station must not be stopped from
    /// receiving by a level it could have carried on at.</para>
    /// </remarks>
    private static void ValidateAlsa(DaemonConfig config, string configPath)
    {
        if (config.Alsa?.Mixer is not AlsaMixerConfig mixer)
        {
            return;
        }

        if (config.Monitor is not null)
        {
            throw new InvalidDataException(
                "this file sets both \"monitor\" and \"alsa\".\"mixer\". A monitor fronts web "
                + "receivers and has no sound card of its own, so there is no mixer to set - "
                + "remove the \"alsa\" section.");
        }

        if (!IsSoundCard(config.Device))
        {
            throw new InvalidDataException(
                $"\"alsa\".\"mixer\" is set but \"device\" is \"{config.Device}\", which is not a "
                + "sound card. The capture gain and the transmit level are the card's mixer; a "
                + "FlexRadio or a web receiver has none. Remove the \"alsa\" section, or point "
                + "\"device\" at the card.");
        }

        // The one way the promise "this daemon never writes your config file" could be broken is
        // by the operator aiming the state file at it. Then the first change made on the page
        // would replace a hand-written JSONC file, comments and all, with six lines of levels.
        // Refused at start-up rather than discovered afterwards, because afterwards is too late:
        // the file it would have destroyed is the only copy of what the station was meant to be.
        if (mixer.StateFile is { Length: > 0 } state && SamePath(state, configPath))
        {
            throw new InvalidDataException(
                $"\"alsa\".\"mixer\".\"stateFile\" is \"{mixer.StateFile}\", which is this "
                + "configuration file. That file is never written by this daemon and a mixer "
                + "change would overwrite it; point \"stateFile\" somewhere else, or remove it "
                + $"to take the default ({MixerStateFile.DefaultName} in the state directory).");
        }
    }

    /// <summary>
    /// What <c>captureDevice</c> and <c>playbackDevice</c> have to say before this station can
    /// use them.
    /// </summary>
    /// <remarks>
    /// <para>For a station whose receive and transmit audio are on two different sound cards: a
    /// radio with a separate receive output, or two USB dongles. Each key overrides
    /// <c>device</c> for one direction and nothing else, so a file that states neither is
    /// exactly the station it always was.</para>
    /// <para>Sound cards only, on both sides and in <c>device</c> too. A FlexRadio, a web
    /// receiver and a pair of pipes each carry both directions in one thing, so there is nothing
    /// for a split to mean there, and a key that silently did nothing would be worse than one
    /// that says so. Exit 2, the same as every other refusal in this file.</para>
    /// </remarks>
    private static void ValidateSplitDevices(DaemonConfig config)
    {
        foreach ((string key, string? value) in (ReadOnlySpan<(string, string?)>)
            [
                ("captureDevice", config.CaptureDevice),
                ("playbackDevice", config.PlaybackDevice),
            ])
        {
            if (value is null)
            {
                continue;
            }

            if (config.Monitor is not null)
            {
                throw new InvalidDataException(
                    $"this file sets both \"{key}\" and \"monitor\". A monitor fronts web "
                    + $"receivers and has no sound card of its own - remove \"{key}\".");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                    $"\"{key}\" is empty. Name the sound card, as \"plughw:CARD=Device,DEV=0\", "
                    + "or remove the key to use \"device\" for that direction too.");
            }

            if (!IsSoundCard(value))
            {
                throw new InvalidDataException(
                    $"\"{key}\" is \"{value}\", which is not a sound card. Only a sound card can "
                    + "be split into a receive device and a transmit device; a FlexRadio, a web "
                    + "receiver or a pipe carries both directions itself and goes in \"device\".");
            }

            if (!IsSoundCard(config.Device))
            {
                throw new InvalidDataException(
                    $"\"{key}\" is set but \"device\" is \"{config.Device}\", which is not a "
                    + "sound card. A FlexRadio, a web receiver or a pipe carries both directions "
                    + $"itself; remove \"{key}\", or point \"device\" at a sound card.");
            }
        }
    }

    /// <summary>
    /// Whether two paths name the same file, as far as can be told without opening either.
    /// </summary>
    /// <remarks>
    /// Full-path comparison, so "soundmodem.json" and "./soundmodem.json" and an absolute path to
    /// the same file all match. It does not resolve symlinks or hard links, so it is a floor and
    /// not a proof - which is the right shape for a guard whose job is to catch the plausible
    /// mistake rather than to defeat somebody determined to make it.
    /// </remarks>
    private static bool SamePath(string one, string other)
    {
        try
        {
            return string.Equals(Trimmed(one), Trimmed(other), StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException
                                    or PathTooLongException)
        {
            // An unusable path is somebody else's error to report; this one only answers
            // "are these the same file", and a path that cannot be resolved is not.
            return false;
        }

        // Path.GetFullPath keeps a trailing separator, so "soundmodem.json/" would compare
        // unequal to the same file and the friendly refusal would not fire. That case fails
        // safe rather than dangerously - the write lands on "soundmodem.json/.<pid>.tmp" and
        // errors - but the operator then gets a write-failure note instead of being told at
        // start-up what they actually typed wrong. Trimmed here so they get the sentence.
        static string Trimmed(string path)
        {
            string full = Path.GetFullPath(path);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? full : trimmed;
        }
    }

    /// <summary>The bind setting, which every flavour has and every listener uses.</summary>
    private static void RequireBind(DaemonConfig config)
    {
        if (ParseBind(config.Bind) is null)
        {
            throw new InvalidDataException(
                $"\"bind\": \"{config.Bind}\" is not an IP address. Use \"127.0.0.1\" for "
                + "loopback only, \"*\" for every interface, or the address of one interface.");
        }
    }

    /// <summary>
    /// Refuses a <c>sideband</c> that names no radio this daemon knows, in either place one can
    /// be written.
    /// </summary>
    /// <remarks>
    /// Checked at load rather than left to the band planner, which only ever sees the value on a
    /// station configured in RF terms. Written down and misspelt, it would otherwise be taken as
    /// "usb" without a word, and every frequency the page drew would be wrong in a way the
    /// operator had no way to see.
    /// </remarks>
    private static void RequireKnownRadioKind(DaemonConfig config)
    {
        Check(config.Sideband, "\"sideband\"");
        if (config.Waterfall is not null)
        {
            Check(config.Waterfall.Sideband, "\"waterfall\".\"sideband\"");
        }

        static void Check(string? kind, string where)
        {
            if (kind is not null
                && (kind.Equals("usb", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("lsb", StringComparison.OrdinalIgnoreCase)
                    || RfPlan.IsFmRadio(kind)))
            {
                return;
            }

            throw new InvalidDataException(
                $"{where}: {(kind is null ? "null" : $"\"{kind}\"")} is not a kind of radio this "
                + $"knows. Use {RfPlan.RadioKinds} "
                + "- the two sidebands, where RF is the dial plus or minus the audio, or FM, "
                + "where the channel is the RF and the audio is only audio.");
        }
    }

    /// <summary>
    /// A <c>carrierSense</c> section has to name a driver this build has, or say <c>"none"</c>.
    /// </summary>
    /// <remarks>
    /// Misspelt and taken quietly, the station would start, print nothing, open no port and run
    /// on the audio-derived carrier sense the operator wrote this section to get away from. The
    /// whole failure would be invisible, which is the shape of defect this feature exists to fix.
    /// </remarks>
    private static void RequireKnownCarrierSenseRadio(DaemonConfig config)
    {
        if (config.CarrierSense is not { } sense)
        {
            return;
        }

        // Null is a real possibility and not a nullable-annotation nicety: System.Text.Json does
        // not enforce C#'s annotations, so an explicit "radio": null lands on this non-nullable
        // property as a null and would take an unguarded Equals with it. Several siblings in the
        // same section genuinely are nullable, so writing it is a reasonable mistake. Refused in
        // words, exactly as "sideband" already refuses one.
        if (sense.Radio is { } radio
            && (radio.Equals("tait", StringComparison.OrdinalIgnoreCase)
                || radio.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InvalidDataException(
            "\"carrierSense\".\"radio\": "
            + (sense.Radio is null ? "null" : $"\"{sense.Radio}\"")
            + " is not a radio this build can read "
            + "carrier sense from. Use \"tait\" (a TM8100 or TM9100 series set with its data "
            + "port in command mode), or \"none\" to switch the section off without deleting "
            + "it.");
    }

    /// <summary>
    /// What a <c>monitor</c> section has to say before this process can be one, every failure a
    /// sentence naming the setting and what to do about it.
    /// </summary>
    /// <remarks>
    /// Refused here rather than at start-up so the operator gets one exit 2 with a reason and
    /// systemd's RestartPreventExitStatus=2 stops retrying, which is the contract every other
    /// configuration error in this file already keeps.
    /// </remarks>
    private static void ValidateMonitor(DaemonConfig config)
    {
        MonitorConfig monitor = config.Monitor!;

        if (config.DeviceWasStated)
        {
            throw new InvalidDataException(
                $"this file sets both \"device\" (\"{config.Device}\") and \"monitor\". They say "
                + "incompatible things about what this process is: \"device\" is one radio or one "
                + "receiver with a KISS port and a transmitter, \"monitor\" is many web receivers "
                + "behind one page with neither. Remove whichever one you did not mean.");
        }

        if (config.Polyglot is { Count: > 0 } || config.PolyglotPort is not null)
        {
            string key = config.PolyglotPort is not null ? "polyglotPort" : "polyglot";
            throw new InvalidDataException(
                $"this file sets both \"{key}\" and \"monitor\". A polyglot port is a KISS "
                + "port for a node to transmit through, and a monitor has neither. Remove "
                + $"\"{key}\".");
        }

        if (monitor.Modems.Count == 0)
        {
            throw new InvalidDataException(
                "\"monitor\".\"modems\" is empty. Every receiver gets the same modems, and a "
                + "monitor with none would connect to receivers and decode nothing. Give it the "
                + "band plan you want watched, e.g. [{\"subChannel\": 0, \"mode\": "
                + "\"afsk300-il2pc\", \"rfFrequency\": 7050300}].");
        }

        if (config.Waterfall is null)
        {
            throw new InvalidDataException(
                "\"monitor\" needs a \"waterfall\" section: the picker and every receiver's page "
                + "are served on its \"port\", and the page's viewers are what asks for a "
                + "receiver. Add {\"waterfall\": {\"port\": 8099, \"title\": \"...\"}}.");
        }

        if (!config.Waterfall.PortWasStated)
        {
            throw new InvalidDataException(
                "\"waterfall\" has no \"port\". A monitor serves its whole site on that one port "
                + $"and would otherwise come up on {config.Waterfall.Port}, which is the "
                + "single-station default and not a decision anybody made - and this is a site "
                + "meant to be reached from outside, usually through a tunnel pointed at a port "
                + "somebody chose. Set it, e.g. {\"waterfall\": {\"port\": 8099}}.");
        }

        // Forced rather than checked. A picker is a page for strangers by definition - it lists
        // other people's receivers and invites anyone to watch one - so an operator's console
        // dressing on it would be a setting that could only ever be wrong.
        config.Waterfall.Public = true;

        foreach ((string name, int seconds) in (ReadOnlySpan<(string, int)>)
            [
                ("refreshMinutes", monitor.RefreshMinutes),
                ("lingerSeconds", monitor.LingerSeconds),
            ])
        {
            if (seconds < 0)
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"{name}\" is {seconds}. That is a number of "
                    + (name == "refreshMinutes" ? "minutes" : "seconds")
                    + " to wait, so it cannot be negative - use 0 or a positive number.");
            }
        }

        if (!Uri.TryCreate(monitor.Directory, UriKind.Absolute, out Uri? directory)
            || (directory.Scheme != Uri.UriSchemeHttp && directory.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException(
                $"\"monitor\".\"directory\" is \"{monitor.Directory}\", which is not an absolute "
                + "http or https URL. It is where the list of receivers is fetched from; the "
                + "public one is https://instances.ubersdr.org/api/instances.");
        }

        if (monitor.PublicUrl is { Length: > 0 } publicUrl)
        {
            // Before anything is quoted back, and it is the one refusal here that does not
            // quote: a URL with a username and password in it would put the password in the
            // journal, which is the last place a message about a mistake should leave it.
            if (Uri.TryCreate(publicUrl, UriKind.Absolute, out Uri? site)
                && site.UserInfo.Length > 0)
            {
                throw new InvalidDataException(
                    "\"monitor\".\"publicUrl\" carries credentials, and this message does not "
                    + "repeat it back because that would write them to the journal. It is the "
                    + "address visitors reach this site at, which is a scheme, a host and an "
                    + "optional port and nothing else: nothing signs in to a public monitor "
                    + "page. Write it as \"https://monitor.ukpacketradio.network\".");
            }

            if (site is null
                || (site.Scheme != Uri.UriSchemeHttp && site.Scheme != Uri.UriSchemeHttps)
                || site.AbsolutePath != "/"
                || site.Query.Length > 0
                || site.Fragment.Length > 0)
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"publicUrl\" is {Quoted(publicUrl)}. It is this site's own "
                    + "address as the world reaches it, which is a scheme, a host and an optional "
                    + "port and nothing after them, e.g. "
                    + "\"https://monitor.ukpacketradio.network\" - with or without a trailing "
                    + "slash, both being the same address. A path, a query or a fragment is "
                    + "refused because the site is served from the root of its port and every "
                    + "link on its pages is written from there. Leave it out to work the address "
                    + "out from each request instead, which is right for a site nothing rewrites "
                    + "the Host header in front of.");
            }

            // Normalised as the file is read, so the one place that uses it can append
            // "/r/<slug>/" without wondering whether the operator wrote the slash. IdnHost rather
            // than Authority because this string goes into the journal, and a punycode host is
            // what the wire carries anyway. An IPv6 literal is taken from Host instead, which
            // keeps the square brackets: without them the colon before a port is the address's
            // own, and the URL means something else or nothing at all.
            string host = site.HostNameType == UriHostNameType.IPv6 ? site.Host : site.IdnHost;
            monitor.PublicUrl = $"{site.Scheme}://{host}"
                + (site.IsDefaultPort ? "" : $":{site.Port}");
        }

        foreach ((string list, List<string> hosts) in (ReadOnlySpan<(string, List<string>)>)
            [("allow", monitor.Allow), ("deny", monitor.Deny)])
        {
            foreach (string host in hosts)
            {
                if (!IsPlausibleHostname(host))
                {
                    throw new InvalidDataException(
                        $"\"monitor\".\"{list}\" contains \"{host}\", which is not a hostname. "
                        + "Each entry is a receiver's host exactly as the directory gives it, "
                        + "e.g. \"m9psy-1.instance.ubersdr.org\" - no scheme, no port, no path.");
                }
            }
        }

        ValidateUplinks(monitor);
    }

    /// <summary>
    /// What a <c>publish</c> section has to say before this station can appear on somebody else's
    /// website, every failure a sentence naming the setting and what to do about it.
    /// </summary>
    /// <remarks>
    /// <para>Section 4.3 of <c>docs/dev/archive/uplink-plan.md</c>. Refused here rather than at start-up for
    /// the reason every other check in this file is: one exit 2 with a reason, and systemd's
    /// <c>RestartPreventExitStatus=2</c> stops retrying instead of crash-looping on a typo.</para>
    /// <para>The one check that is not here is <c>audioRate</c> dividing the channel's DSP rate,
    /// which is <see cref="PublishRateProblem"/>: the DSP rate is settled from the modem set after
    /// any plugins have loaded, so this file cannot know it yet without guessing at a mode it has
    /// not been told about.</para>
    /// </remarks>
    private static void ValidatePublish(DaemonConfig config)
    {
        PublishConfig publish = config.Publish!;

        if (config.Monitor is not null)
        {
            throw new InvalidDataException(
                "this file sets both \"publish\" and \"monitor\". A station publishes itself to a "
                + "monitor site and a monitor accepts stations; one process is not both. Remove "
                + "whichever one you did not mean - a site lists a station through its own "
                + "\"monitor\".\"uplinks\", not through a \"publish\" block of its own.");
        }

        // A kind that cannot be published says why rather than just refusing.
        if (DeviceKinds.Of(config.Device).PublishRefusal(config.Device) is string publishRefusal)
        {
            throw new InvalidDataException(publishRefusal);
        }

        if (config.Waterfall is null)
        {
            throw new InvalidDataException(
                "\"publish\" needs a \"waterfall\" section: the uplink publishes what the "
                + "waterfall server already computes - the audio, the frames and the status "
                + "sentence - and without one there is nothing to publish. Add "
                + "{\"waterfall\": {\"port\": 8107}}, or remove \"publish\".");
        }

        if (!Uri.TryCreate(publish.Url, UriKind.Absolute, out Uri? url)
            || (url.Scheme != "ws" && url.Scheme != "wss"))
        {
            throw new InvalidDataException(
                $"\"publish\".\"url\" is {Quoted(publish.Url)}, which is not an absolute ws or wss "
                + "URL. It is the site's uplink endpoint, given to you with the token, e.g. "
                + "\"wss://monitor.ukpacketradio.network/uplink\".");
        }

        if (publish.Token is not { Length: >= MinimumTokenLength })
        {
            throw new InvalidDataException(
                "\"publish\".\"token\" is "
                + (string.IsNullOrEmpty(publish.Token) ? "missing" : "too short")
                + $" - it is the credential the site issued this station, at least "
                + $"{MinimumTokenLength} characters, and there is no default. Ask the site owner "
                + "for one, paste it in as it was given, and do not edit it by hand.");
        }

        if (!IsPlausibleCallsign(publish.Callsign))
        {
            throw new InvalidDataException(
                $"\"publish\".\"callsign\" is {Quoted(publish.Callsign)}, which is not a callsign "
                + "with an optional SSID (e.g. \"GB7RDG-2\"). A station on a public page has to "
                + "say whose it is, and the site checks this against the token it issued.");
        }

        if (publish.Site is { Length: > 0 }
            && (!Uri.TryCreate(publish.Site, UriKind.Absolute, out Uri? site)
                || (site.Scheme != Uri.UriSchemeHttp && site.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidDataException(
                $"\"publish\".\"site\" is {Quoted(publish.Site)}, which is not an absolute http or "
                + "https URL. It is linked from a public page in your name, so it goes through "
                + "the same check the site applies to every other URL it is handed - remove it, "
                + "or write it out in full as \"https://example.org/\".");
        }

        foreach ((string name, string? value, int limit) in (ReadOnlySpan<(string, string?, int)>)
            [
                ("callsign", publish.Callsign, 16),
                ("operator", publish.Operator, 40),
                ("location", publish.Location, 60),
                ("radio", publish.Radio, 60),
            ])
        {
            if (value is not null && value.Length > limit)
            {
                throw new InvalidDataException(
                    $"\"publish\".\"{name}\" is {value.Length} characters and the limit is "
                    + $"{limit}. Said here rather than cut in half on somebody else's website: "
                    + "shorten it to something that reads as a picker row.");
            }
        }

        if (publish.Frames is not ("always" or "watched"))
        {
            throw new InvalidDataException(
                $"\"publish\".\"frames\" is {Quoted(publish.Frames)}. It is \"always\" (the "
                + "default: decoded frames go up whether or not anybody is watching, which costs "
                + "well under a kilobit a second and is what makes a quiet band look alive) or "
                + "\"watched\".");
        }

        if (publish.AudioRate is { } rate && rate is < MinimumAudioRate or > MaximumAudioRate)
        {
            throw new InvalidDataException(
                $"\"publish\".\"audioRate\" is {rate} Hz, and the range is {MinimumAudioRate} to "
                + $"{MaximumAudioRate}. It is the rate this station's audio is published at, so "
                + "the relayed picture spans 0 to half of it; leave it out for the default of "
                + "12000, which is about 194 kbit/s upstream while somebody is watching.");
        }
    }

    /// <summary>Shortest token accepted. The site issues 43 url-safe base64 characters.</summary>
    private const int MinimumTokenLength = 32;

    /// <summary>Publishable audio rates, in Hz (4.3).</summary>
    private const int MinimumAudioRate = 6000;

    /// <summary>Publishable audio rates, in Hz (4.3).</summary>
    private const int MaximumAudioRate = 48000;

    /// <summary>The default published rate: the channel's own rate, capped (4.5, decision "no codec").</summary>
    internal const int DefaultAudioRate = 12000;

    /// <summary>
    /// What is wrong with <c>publish.audioRate</c> against the channel this station actually runs,
    /// or null if nothing is. Separate from <see cref="ValidatePublish"/> because the DSP rate is
    /// not settled until the modem set is known and any modem plugins have loaded, which is after
    /// this file has been read.
    /// </summary>
    /// <param name="publish">The section, already through <see cref="ValidatePublish"/>.</param>
    /// <param name="dspRate">The rate the station's audio channel runs at.</param>
    internal static string? PublishRateProblem(PublishConfig publish, int dspRate)
    {
        int rate = PublishedAudioRate(publish, dspRate);
        if (dspRate % rate == 0)
        {
            return null;
        }

        var divisors = new List<int>();
        for (int candidate = MinimumAudioRate; candidate <= Math.Min(dspRate, MaximumAudioRate); candidate++)
        {
            if (dspRate % candidate == 0)
            {
                divisors.Add(candidate);
            }
        }

        string problem =
            $"\"publish\".\"audioRate\" is {rate} Hz and this station's channel runs at "
            + $"{dspRate} Hz, which {rate} does not divide. The audio is decimated rather than "
            + "resampled, so it has to be an integer divisor";

        // No station runs a channel below 6000 Hz today, so the list is never empty in practice.
        // Ending the sentence "an integer divisor: ." if one ever did would not be honest.
        return divisors.Count > 0
            ? $"{problem}: {string.Join(", ", divisors)}."
            : $"{problem}, and this channel has none in {MinimumAudioRate} to {MaximumAudioRate}.";
    }

    /// <summary>
    /// A problem found after the file was read, in the same frame every refusal inside
    /// <see cref="Load"/> comes out in: the file, the sentence, and how to recover.
    /// </summary>
    /// <remarks>
    /// For the checks that cannot run while the file is being read because they need something
    /// settled later - <see cref="PublishRateProblem"/> is the only one today, and it needs the
    /// channel's DSP rate. An operator reading <c>journalctl</c> should not be able to tell which
    /// kind of check refused their station.
    /// </remarks>
    internal static string ConfigurationError(string path, string problem) =>
        Describe(path, problem);

    /// <summary>
    /// The rate this station publishes at: what the operator asked for, or the channel's own rate
    /// capped at <see cref="DefaultAudioRate"/> - so a 48 kHz station that says nothing gets a 0
    /// to 6 kHz picture at 194 kbit/s rather than 770.
    /// </summary>
    internal static int PublishedAudioRate(PublishConfig publish, int dspRate) =>
        publish.AudioRate ?? Math.Min(dspRate, DefaultAudioRate);

    /// <summary>
    /// What a <c>monitor.uplinks</c> entry has to say before this site will accept a station's
    /// connection on it. Every failure is an exit 2 naming the entry and what to do about it.
    /// </summary>
    /// <remarks>
    /// All three fields are the site owner's decisions rather than the station's, so all three
    /// are required and none is derived: a station cannot ask for a slug, cannot claim a callsign
    /// it was not issued a token for, and cannot mint a token at all. See
    /// <c>docs/dev/archive/uplink-plan.md</c> 4.4.
    /// </remarks>
    private static void ValidateUplinks(MonitorConfig monitor)
    {
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (UplinkConfig uplink in monitor.Uplinks)
        {
            string named = uplink.Callsign.Length > 0
                ? $"\"{uplink.Callsign}\""
                : uplink.Slug.Length > 0 ? $"the entry for \"{uplink.Slug}\"" : "an entry";

            if (!IsPlausibleCallsign(uplink.Callsign))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" has {named} for a \"callsign\". A station on a "
                    + "public page that will not say who it is has no business being there, and "
                    + "the callsign is what its token is issued against: one to six letters and "
                    + "digits with an optional -SSID, e.g. \"GB7RDG-2\".");
            }

            if (!IsUsableSlug(uplink.Slug))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} the slug \"{uplink.Slug}\", which "
                    + "cannot be a path segment. It is lower-case letters, digits and hyphens "
                    + "with no hyphen at either end - normally the callsign lower-cased, so "
                    + $"\"{uplink.Callsign}\" would be "
                    + $"\"{UberSdrDirectory.SlugForCallsign(uplink.Callsign)}\", and the page is "
                    + $"served at /r/{UberSdrDirectory.SlugForCallsign(uplink.Callsign)}/.");
            }

            if (!IsSha256Hex(uplink.TokenSha256))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} a \"tokenSha256\" that is not 64 hex "
                    + "characters. This site stores the HASH of the token it issued, never the "
                    + $"token: run \"pdn-soundmodem --uplink-token {uplink.Callsign}\", paste the "
                    + "hash here and give the token to the station's operator.");
            }

            if (slugs.TryGetValue(uplink.Slug, out string? already))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives both {already} and {named} the slug "
                    + $"\"{uplink.Slug}\". One page cannot be two stations: give each its own, "
                    + "normally its own callsign lower-cased.");
            }

            if (!callsigns.Add(uplink.Callsign))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" lists {named} twice. One entry per station: a "
                    + "second token for the same callsign would let two stations claim to be it, "
                    + "and only one of them could have the page.");
            }

            if (!hashes.Add(uplink.TokenSha256))
            {
                throw new InvalidDataException(
                    $"\"monitor\".\"uplinks\" gives {named} a \"tokenSha256\" another entry "
                    + "already has. A token names one station, so the same one twice cannot say "
                    + "which of them is connecting: issue each a token of its own.");
            }

            slugs[uplink.Slug] = named;
        }
    }

    /// <summary>
    /// Whether a string could be a callsign with an optional SSID: up to six letters and digits,
    /// optionally <c>-0</c> to <c>-15</c>. The same shape <c>Ax25AddressParser</c> reads off the
    /// air, deliberately not a validity check against any licensing authority's real format -
    /// this is a label on a page, and the token is what says the station is who it claims.
    /// </summary>
    internal static bool IsPlausibleCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return false;
        }

        string[] parts = callsign.Split('-');
        if (parts.Length > 2 || parts[0].Length is 0 or > 6)
        {
            return false;
        }

        foreach (char c in parts[0])
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return parts.Length == 1
            || (parts[1].Length is 1 or 2 && int.TryParse(parts[1], out int ssid) && ssid is >= 0 and <= 15);
    }

    /// <summary>A value as it should read in a message: quoted, or the word for its absence.</summary>
    private static string Quoted(string? value) =>
        value is null ? "missing" : $"\"{value}\"";

    /// <summary>
    /// Whether a string can be the path segment a station's page is served under: exactly what
    /// <c>WaterfallWebServer.ValidatePathBase</c> accepts, checked here so that a typo is one
    /// sentence at start-up rather than an exception from inside a route registration.
    /// </summary>
    private static bool IsUsableSlug(string? slug) =>
        !string.IsNullOrEmpty(slug)
        && slug.Length <= 63
        && !slug.StartsWith('-')
        && !slug.EndsWith('-')
        && slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Whether a string is 64 hex characters, which is a SHA-256 written down.</summary>
    private static bool IsSha256Hex(string? hash) =>
        hash is { Length: 64 }
        && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>
    /// Whether a string could be a hostname: labels of letters, digits and hyphens separated by
    /// dots, no scheme, no port, no path. Deliberately not a DNS resolution - an allow list has
    /// to be writable for a receiver that is temporarily down - and deliberately not permissive,
    /// because "https://host/" in a deny list would silently match nothing and leave an operator
    /// believing they had been taken off the list.
    /// </summary>
    internal static bool IsPlausibleHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.StartsWith('.')
            || host.EndsWith('.'))
        {
            return false;
        }

        foreach (string label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Things worth saying out loud that are not worth refusing to start over.</summary>
    private static List<string> CollectWarnings(DaemonConfig config)
    {
        var warnings = new List<string>();

        // Every section that captures unknown keys is reported here. frameLog and survey used
        // to capture theirs and never surface them - the doc's "reported at start-up" promise
        // held for some sections and quietly not for others - and ptt/paging/ardop/flex did not
        // capture at all, so a typo there vanished into the deserialiser.
        void Unknown(string? section, Dictionary<string, JsonElement>? settings)
        {
            foreach (string key in settings?.Keys ?? Enumerable.Empty<string>())
            {
                string prefix = section is null ? "" : $"{section}: ";
                warnings.Add(
                    $"{prefix}\"{key}\" is not a setting this version knows, and is being "
                    + $"IGNORED. Check the spelling against {ConfigDocUrl}");
            }
        }

        Unknown(null, config.UnknownSettings);
        Unknown("waterfall", config.Waterfall?.UnknownSettings);
        Unknown("monitor", config.Monitor?.UnknownSettings);
        foreach (ModemConfig modem in config.Monitor?.Modems ?? [])
        {
            Unknown($"monitor modem {modem.SubChannel}", modem.UnknownSettings);
        }

        foreach (UplinkConfig station in config.Monitor?.Uplinks ?? [])
        {
            Unknown(
                $"monitor uplink {(station.Callsign.Length > 0 ? station.Callsign : station.Slug)}",
                station.UnknownSettings);
        }

        Unknown("api", config.Api?.UnknownSettings);
        Unknown("ubersdr", config.UberSdr?.UnknownSettings);
        Unknown("openwebrx", config.OpenWebRx?.UnknownSettings);
        Unknown("frameLog", config.FrameLog?.UnknownSettings);
        Unknown("survey", config.Survey?.UnknownSettings);
        Unknown("rawCapture", config.RawCapture?.UnknownSettings);
        Unknown("deadFeed", config.DeadFeed?.UnknownSettings);
        Unknown("ptt", config.Ptt?.UnknownSettings);
        Unknown("rig", config.Rig?.UnknownSettings);
        foreach ((string section, Dictionary<string, JsonElement>? settings) in MailcastConfig.UnknownSections(config.Mailcast))
        {
            Unknown(section, settings);
        }
        Unknown("carrierSense", config.CarrierSense?.UnknownSettings);
        Unknown("txTest", config.TxTest.UnknownSettings);
        Unknown("paging", config.Paging?.UnknownSettings);
        Unknown("ardop", config.Ardop?.UnknownSettings);
        Unknown("flex", config.Flex?.UnknownSettings);
        Unknown("alsa", config.Alsa?.UnknownSettings);
        Unknown("alsa mixer", config.Alsa?.Mixer?.UnknownSettings);
        foreach (PolyglotConfig polyglot in config.Polyglot ?? [])
        {
            Unknown($"polyglot port {polyglot.Port}", polyglot.UnknownSettings);
        }

        // The generic line above says a key is being ignored; this one says what to write
        // instead. Both, because "captureGainPercent is not a setting this version knows" on a
        // file that worked in 0.57.0 is true and useless - the operator needs the new name and
        // the fact that its unit changed, or they will type 60 into a dB field.
        foreach (string key in config.Alsa?.Mixer?.UnknownSettings?.Keys ?? Enumerable.Empty<string>())
        {
            if (AlsaMixerConfig.WhyRenamed(key) is string renamed)
            {
                warnings.Add($"alsa mixer: {renamed}");
            }

            if (AlsaMixerConfig.WhyForcedOff(key, Asked(key)) is string forced)
            {
                warnings.Add($"alsa mixer: {forced}");
            }
        }
        // What a stale switch key was actually set to, so the sentence can be honest about it.
        // Anything that is not a JSON true or false has no answer, and the wording falls back to
        // the one that is true whatever the value was.
        bool? Asked(string key) =>
            config.Alsa?.Mixer?.UnknownSettings?.TryGetValue(key, out JsonElement value) == true
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                }
                : null;

        foreach (ModemConfig modem in config.Modems)
        {
            Unknown($"modem {modem.SubChannel}", modem.UnknownSettings);
            Unknown($"modem {modem.SubChannel} identify", modem.Identify?.UnknownSettings);
        }

        for (int i = 0; i < config.ModemPlugins.Count; i++)
        {
            Unknown($"modemPlugins[{i}]", config.ModemPlugins[i].UnknownSettings);
        }

        Unknown("publish", config.Publish?.UnknownSettings);

        // Plain ws off the machine is the shape of a smoke test and the shape of a mistake, and
        // only the operator knows which: the token and everything this station says about itself
        // would cross their network in clear. Warned rather than refused, deliberately (4.3).
        if (config.Publish?.Url is { Length: > 0 } published
            && Uri.TryCreate(published, UriKind.Absolute, out Uri? uplink)
            && uplink.Scheme == "ws"
            && !uplink.IsLoopback)
        {
            warnings.Add(
                $"publish: \"url\" is \"{published}\", which is unencrypted ws to {uplink.Host}. "
                + "The token and everything this station publishes cross the network in clear. "
                + "Use wss unless this is a test on your own wire.");
        }

        return warnings;
    }

    /// <summary>True for the mode name that is an ARDOP virtual TNC rather than a packet modem.</summary>
    internal static bool IsArdop(string? mode) =>
        string.Equals(mode, "ardop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The transmitter test's two numbers, checked when the file is read rather than when the
    /// button is pressed.
    /// </summary>
    /// <remarks>
    /// <c>TestTone</c> refuses an amplitude outside 0 to 1 and a duration of zero, and without
    /// this that refusal arrives on the first press - a station that journals <c>tx test: ready</c>
    /// at start-up and then does nothing at all when asked. A configuration error should stop the
    /// daemon with a sentence, which is what <c>identify</c>'s own amplitude check already does.
    /// The cap needs no check of its own: it is clamped to 1..60 in force whatever it says, and
    /// the clamp is the safety property rather than a correction.
    /// </remarks>
    private static void ValidateTxTest(DaemonConfig config)
    {
        TxTestConfig test = config.TxTest;
        if (!double.IsFinite(test.Amplitude) || test.Amplitude <= 0 || test.Amplitude > 1)
        {
            throw new InvalidDataException(
                $"\"txTest\".\"amplitude\": {test.Amplitude} is not a level. It is the peak the "
                + "test burst reaches, above 0 and at most 1; the default 0.8 is what the "
                + "modulators use, so the test presents the transmitter with the drive the data "
                + "does.");
        }

        if (!double.IsFinite(test.Seconds) || test.Seconds <= 0)
        {
            throw new InvalidDataException(
                $"\"txTest\".\"seconds\": {test.Seconds} is not a length. It is how long a test "
                + "runs when the request does not say, in seconds, and it must be above zero.");
        }
    }

    /// <summary>
    /// Rejects two services asking for the same TCP port. Left to the OS this surfaces as a
    /// bind failure from whichever listener happens to start second, naming neither setting.
    /// </summary>
    /// <summary>
    /// A frame cap below the smallest useful frame is a misconfiguration, not a preference: a
    /// station whose host cannot get a 256-byte information field through has no packet radio.
    /// </summary>
    private static void ValidateKissFrames(DaemonConfig config)
    {
        const int Least = 512;
        if (config.KissMaxFrameBytes < Least)
        {
            throw new InvalidDataException(
                $"\"kissMaxFrameBytes\": {config.KissMaxFrameBytes} is below {Least}. It is the "
                + "most bytes one KISS frame from a host may carry; leave it out for the default "
                + $"of {Kiss.KissDecoder.DefaultMaxFrame}, or set it above what your modems carry.");
        }
    }

    /// <summary>A week: longer than any station stays on one radio without being heard, and far
    /// short of the figure where turning minutes into a time span would overflow.</summary>
    private const double MaxForgetAfterMinutes = 7 * 24 * 60;

    private static void ValidatePolyglot(DaemonConfig config)
    {
        config.Polyglot ??= [];

        // The shorthand becomes an ordinary entry, so everything after this - the checks, the
        // port claim, the daemon - sees one kind of polyglot port.
        if (config.PolyglotPort is int shorthand)
        {
            List<int> packet = [.. config.Modems.Where(m => !IsArdop(m.Mode)).Select(m => m.SubChannel)];
            if (packet.Count < 2)
            {
                throw new InvalidDataException(
                    $"\"polyglotPort\": {shorthand} puts every packet modem behind one port, and "
                    + $"this station has {packet.Count}. It needs two or more in \"modems\" to "
                    + "choose between; with one, use \"kissPort\" or that modem's own \"port\".");
            }

            config.Polyglot.Add(new PolyglotConfig
            {
                Port = shorthand,
                SubChannels = packet,
                Default = packet[0],
                FromShorthand = true,
            });
        }
        foreach (PolyglotConfig? entry in config.Polyglot)
        {
            if (entry is not { } polyglot)
            {
                throw new InvalidDataException(
                    "a \"polyglot\" entry is empty (null). Each entry is one port, e.g. "
                    + "{\"port\": 8120, \"subChannels\": [0, 1], \"default\": 0}.");
            }

            if (polyglot.Port is not int port)
            {
                throw new InvalidDataException(
                    "a \"polyglot\" entry has no \"port\". It is the KISS port your node attaches "
                    + "to, e.g. {\"port\": 8120, \"subChannels\": [0, 1], \"default\": 0}.");
            }

            string where = polyglot.FromShorthand ? $"\"polyglotPort\": {port}" : $"\"polyglot\" port {port}";
            polyglot.SubChannels ??= [];
            if (polyglot.SubChannels.Count < 2)
            {
                throw new InvalidDataException(
                    $"{where} lists {polyglot.SubChannels.Count} sub-channel(s) in \"subChannels\". "
                    + "It overlays two or more modems; for one modem on a port of its own, give that "
                    + "modem entry a \"port\" instead.");
            }

            if (polyglot.SubChannels.GroupBy(s => s).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            {
                throw new InvalidDataException(
                    $"{where} lists sub-channel {repeated.Key} twice in \"subChannels\". List each "
                    + "modem once.");
            }

            foreach (int sub in polyglot.SubChannels)
            {
                ModemConfig? modem = config.Modems.FirstOrDefault(m => m.SubChannel == sub);
                if (modem is null)
                {
                    throw new InvalidDataException(
                        $"{where} lists sub-channel {sub}, and no modem entry has \"subChannel\": "
                        + $"{sub}. Every sub-channel it lists has to be one of your \"modems\".");
                }

                if (IsArdop(modem.Mode))
                {
                    throw new InvalidDataException(
                        $"{where} lists sub-channel {sub}, which is \"mode\": \"ardop\". ARDOP is "
                        + "an ARQ session with its own host interface, not a packet modem, so it "
                        + "cannot sit behind a KISS port. Take it out of \"subChannels\".");
                }
            }

            if (polyglot.Default is not int fallback)
            {
                throw new InvalidDataException(
                    $"{where} has no \"default\". It is the sub-channel a frame goes out on when "
                    + "the station it is for has not been heard recently, so it should be the mode "
                    + "every peer has, e.g. your afsk1200 modem.");
            }

            if (!polyglot.SubChannels.Contains(fallback))
            {
                throw new InvalidDataException(
                    $"{where} has \"default\": {fallback}, which is not in its \"subChannels\" "
                    + $"[{string.Join(", ", polyglot.SubChannels)}]. The default has to be one of "
                    + "the modems it overlays.");
            }

            if (!(polyglot.ForgetAfterMinutes is > 0 and <= MaxForgetAfterMinutes))
            {
                throw new InvalidDataException(
                    $"{where} has \"forgetAfterMinutes\": {polyglot.ForgetAfterMinutes}. It is how "
                    + $"long a station is remembered after it was last heard, and has to be above 0 "
                    + $"and at most {MaxForgetAfterMinutes} (a week); leave it out for 60.");
            }
        }
    }

    private static void ValidatePorts(DaemonConfig config)
    {
        var claimed = new Dictionary<int, string>();
        void Claim(int port, string what)
        {
            if (claimed.TryGetValue(port, out string? already))
            {
                throw new InvalidDataException(
                    $"{what} and {already} both want TCP port {port}. Give them different ports.");
            }

            claimed[port] = what;
        }

        if (config.Ardop is null)
        {
            Claim(config.KissPort, "\"kissPort\"");
        }

        foreach (PolyglotConfig polyglot in config.Polyglot)
        {
            Claim(polyglot.Port!.Value, polyglot.FromShorthand ? "\"polyglotPort\"" : "a \"polyglot\" port");
        }

        foreach (ModemConfig modem in config.Modems.Where(m => m.Port is not null))
        {
            Claim(modem.Port!.Value, $"the \"port\" of modem {modem.SubChannel}");
            if (IsArdop(modem.Mode))
            {
                // ardopcf's convention, not ours to move: data is always command + 1.
                Claim(modem.Port!.Value + 1, $"the ARDOP data port of modem {modem.SubChannel}");
            }
        }

        if (config.Waterfall is not null)
        {
            Claim(config.Waterfall.Port, "the waterfall");
        }

        if (config.Paging is not null)
        {
            Claim(config.Paging.Port, "the paging endpoint");
        }

        if (config.Ardop is not null)
        {
            Claim(config.Ardop.Port, "the ARDOP command port");
            // ardopcf's convention, not ours to move: data is always command + 1.
            Claim(config.Ardop.Port + 1, "the ARDOP data port");
        }
    }

    /// <summary>
    /// Whether the file names a top-level key at all, as against taking its default. Read from
    /// the document rather than the object, because a defaulted value and a value written down
    /// that happens to equal the default are indistinguishable once deserialized.
    /// </summary>
    private static bool StatesKey(string path, params string[] keys)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            JsonElement at = document.RootElement;
            foreach (string key in keys)
            {
                if (at.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                JsonProperty found = at.EnumerateObject().FirstOrDefault(
                    p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (found.Value.ValueKind == JsonValueKind.Undefined)
                {
                    return false;
                }

                at = found.Value;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a bind setting; "*" means every interface. Null when it is not an address. Unset
    /// or blank stays on loopback - the safe reading, since the alternative would silently put
    /// an unauthenticated transmit interface on every interface because a value was empty.
    /// </summary>
    internal static System.Net.IPAddress? ParseBind(string? bind) =>
        string.IsNullOrWhiteSpace(bind) ? System.Net.IPAddress.Loopback
        : bind == "*" ? System.Net.IPAddress.Any
        : System.Net.IPAddress.TryParse(bind, out System.Net.IPAddress? parsed) ? parsed : null;

    /// <summary>
    /// Loads a configuration file, turning every failure into an operator-facing explanation
    /// instead of an exception. Returns null with <paramref name="error"/> set on failure.
    /// </summary>
    /// <remarks>
    /// This is what the daemon calls. A bad config is an operator typo, not a bug, and the
    /// person who has to act on it reads it in `journalctl` - a .NET stack trace there tells
    /// them nothing they can use, and buries the one line that names the problem.
    /// </remarks>
    /// <param name="path">The file to read.</param>
    /// <param name="error">What an operator should read, when this returns null.</param>
    /// <param name="asPath">The path to validate as though the document lived at; see
    /// <see cref="Load(string, string?)"/>.</param>
    public static DaemonConfig? TryLoad(string path, out string error, string? asPath = null)
    {
        try
        {
            error = "";
            // A truncated file is a likely half-finished edit, and "does not contain any JSON
            // tokens" is a poor way to be told the file is empty.
            if (File.Exists(path) && File.ReadAllText(path).AsSpan().IsWhiteSpace())
            {
                error = Describe(path, "the file is empty");
                return null;
            }

            return Load(path, asPath);
        }
        catch (FileNotFoundException)
        {
            error = Describe(path, $"no such file: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            error = Describe(path, $"no such directory: {Path.GetDirectoryName(path)}");
        }
        catch (UnauthorizedAccessException)
        {
            error = Describe(path, "permission denied reading the file");
        }
        catch (JsonException e) when (e.Path?.StartsWith("$.mailcast.hooks", StringComparison.OrdinalIgnoreCase) == true)
        {
            error = Describe(path, MailcastConfig.HooksReadProblem(e));
        }
        catch (JsonException e)
        {
            // System.Text.Json counts lines from 0; humans and editors count from 1.
            string at = e.LineNumber is { } line
                ? $"line {line + 1}, position {(e.BytePositionInLine ?? 0) + 1}: "
                : "";
            string detail = e.Message.Split(" Path:")[0];
            error = Describe(path, $"not valid JSON - {at}{detail}");
        }
        catch (InvalidDataException e)
        {
            error = Describe(path, e.Message);
        }
        catch (IOException e)
        {
            error = Describe(path, $"could not be read: {e.Message}");
        }

        return null;
    }

    /// <summary>Formats a config failure as "what is wrong" followed by "what to do".</summary>
    private static string Describe(string path, string problem)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"configuration error in {path}");
        text.AppendLine($"  {problem}");
        text.AppendLine();
        // Commands are given bare, as root, rather than with a sudo prefix: Debian only
        // installs sudo when the root password is left blank at install time, so a "sudo …"
        // line is a command that does not exist on a good number of the machines this runs on.
        text.AppendLine("  The service will not start until this is fixed. As root, to start");
        text.AppendLine("  from a known-good file:");
        if (File.Exists(ExamplePath))
        {
            text.AppendLine($"    cp {ExamplePath} {path}");
        }
        else
        {
            text.AppendLine($"    copy the example over {path}");
        }

        text.AppendLine("  Then edit it for your sound device and PTT, and:");
        text.AppendLine("    systemctl restart pdn-soundmodem");
        text.Append("  Every setting is documented at " + ConfigDocUrl);
        return text.ToString();
    }

    /// <summary>Where the .deb puts the example config.</summary>
    internal const string ExamplePath = "/usr/share/pdn-soundmodem/soundmodem.example.json";

    internal const string ConfigDocUrl =
        "https://github.com/packet-net/pdn-soundmodem/blob/main/docs/reference/config.md";
}

/// <summary>
/// Answering a station on the frequency its receiver is actually listening on.
/// </summary>
/// <remarks>
/// <para>A rig's transmit and receive conversions share one master oscillator, so a station
/// heard 5 Hz high is also listening 5 Hz high. Shifting our reply by the offset measured on
/// its own frames puts our signal where its demodulator expects it. Our own reference error
/// cancels - it is in the measurement and in the transmission with the same sign, through the
/// same oscillator - so this needs no calibrated or GPS-locked reference, only one that does
/// not move appreciably between hearing them and answering.</para>
/// <para><b>The benefit is theirs, not ours.</b> This station finds them regardless: the 300
/// baud modes run offset-diversity decoder banks. A correspondent running a fixed-centre modem
/// with no such bank is the one that cannot hear us, which is why the correction is worth
/// making and why it is transmit-only.</para>
/// <para><b>On by default</b>, and bounded rather than timid: every shift is clamped to
/// <see cref="MaxTrimHz"/>, so the worst this can do to a signal is put it 50 Hz off the channel
/// centre, which is less than several of the stations on the band are off it already. Set
/// <c>enabled: false</c> to measure and report without touching the transmitter.</para>
/// </remarks>
public sealed class FrequencyMatchingConfig
{
    /// <summary>Actually shift the transmitter. True by default; false measures and reports only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Frames kept per station for the estimate. Default 8.</summary>
    public int Samples { get; set; } = DefaultSamples;

    /// <inheritdoc cref="Samples" />
    public const int DefaultSamples = 8;

    /// <summary>How old a frame may be and still count, in seconds. Default 600.</summary>
    public double MaxAgeSeconds { get; set; } = DefaultMaxAgeSeconds;

    /// <inheritdoc cref="MaxAgeSeconds" />
    public const double DefaultMaxAgeSeconds = 600;

    /// <summary>
    /// Frames required before the estimate is acted on. Default 3.
    /// </summary>
    /// <remarks>
    /// Low on purpose. The correction only has to hold for the exchange it is used in, so a
    /// handful of recent frames is the right evidence; a long run would average across drift and
    /// describe neither end of it.
    /// </remarks>
    public int MinSamples { get; set; } = 3;

    /// <summary>
    /// Largest spread across those frames, in Hz, that still counts as settled. Default 20.
    /// </summary>
    /// <remarks>
    /// This is what separates a rig that is merely off frequency from one that is wandering.
    /// Measured on the live 40 m station: GB7WEM-7 held 0.6 Hz of spread across 467 frames and
    /// GB7OXF-2 held 0.7, while GB7NOT ranged over 54 Hz. The first two are worth correcting
    /// for and the third is not.
    /// </remarks>
    public double MaxSpreadHz { get; set; } = 20;

    /// <summary>Largest shift that will ever be applied, in Hz. Default 50.</summary>
    /// <remarks>
    /// The safety cap, and the reason the rest of this can be on by default. It bounds the damage
    /// independently of every other guard: even two stations chasing each other with the detector
    /// switched off could not walk further than this off the channel centre, and 50 Hz is inside
    /// what the measured stations on the live 40 m port are already scattered across. The channel
    /// clamps again at its own hard ceiling, so a bug upstream cannot get past both.
    /// </remarks>
    public double MaxTrimHz { get; set; } = 50;

    /// <summary>
    /// Fraction of the measured offset applied to a station that has already moved under our
    /// correction once. Default 0.5. A station that has never moved gets the whole correction.
    /// </summary>
    /// <remarks>
    /// <para>Conditional, because damping only ever fixes a feedback loop and in the normal case
    /// there is no loop: our transmitter is not in the path by which we measure theirs, so
    /// correcting for a station that is not itself correcting is open-loop. Damping there
    /// stabilises nothing and simply leaves half the error uncorrected. Noise is handled by
    /// averaging the window and gating on its spread; a wild estimate is bounded by
    /// <see cref="MaxTrimHz"/>.</para>
    /// <para>It earns its keep only in a two-sided chase too small to trip
    /// <see cref="ChaseThresholdHz"/>, where two undamped stations alternate between aligned and
    /// fully offset every exchange and a steady small error is the kinder outcome. So it applies
    /// where there is evidence of a peer that reacts, and nowhere else.</para>
    /// </remarks>
    public double Damping { get; set; } = 0.5;

    /// <summary>
    /// How far a station's own frequency may move, in Hz, after we start answering it off-centre
    /// before we stop doing so. Default 10.
    /// </summary>
    /// <remarks>
    /// Our transmitter cannot change what we measure of theirs, so a station whose offset shifts
    /// once we begin correcting has moved itself: either it is correcting for us in turn, which
    /// leaves both of us worse off than if only one had, or its reference is drifting. Neither is
    /// worth chasing, so the correction latches off for that station and says why.
    /// </remarks>
    public double ChaseThresholdHz { get; set; } = 10;

    /// <summary>
    /// How long to leave a station alone after its frequency moved under our correction, in
    /// seconds. Default 1800 (30 minutes).
    /// </summary>
    /// <remarks>
    /// Backing off is not the same as giving up. A station that moves once has most likely just
    /// moved - a knocked dial, a rig warming up - and will sit happily at its new offset; writing
    /// it off forever would mean never correcting for it again because of something it did once.
    /// After the cooldown its new offset is measured and corrected for like anybody else's.
    /// </remarks>
    public double ChaseCooldownSeconds { get; set; } = 1800;

    /// <summary>
    /// How many times a station may move under our correction before we stop trying. Default 3;
    /// 0 retries indefinitely.
    /// </summary>
    /// <remarks>
    /// Repetition is what separates a rig that moved from a station correcting for us in turn. A
    /// moved rig stays put afterwards; a peer running this same algorithm moves again every time
    /// we correct, and two of those trade adjustments indefinitely without either landing on the
    /// right answer.
    /// </remarks>
    public int MaxChases { get; set; } = 3;

    /// <summary>
    /// Destinations never worth aiming at, because they are not one station.
    /// </summary>
    /// <remarks>
    /// A beacon or an ID is heard by everybody, and aiming it at one correspondent's oscillator
    /// aims it away from every other listener. In practice these exclude themselves - we never
    /// receive frames <em>from</em> "BEACON", so no estimate for it can exist - but saying so
    /// makes the intent legible rather than incidental.
    /// </remarks>
    public static readonly string[] BroadcastDestinations =
        ["ID", "BEACON", "CQ", "QST", "ALL", "NODES", "MAIL", "APRS", "TEST"];

    /// <summary>Keys in this section the daemon does not know; reported at start-up.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownSettings { get; set; }
}
