using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using M0LTE.Dsp;
using Packet.Ax25;
using Packet.Ax25.Monitor;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Waterfall;

/// <summary>Waterfall server tunables (the daemon's <c>waterfall</c> config section).</summary>
public sealed class WaterfallOptions
{
    /// <summary>Rig dial (suppressed-carrier) frequency in Hz, the default the page opens
    /// with - each browser can retune its own copy. 0 = not set: the page shows audio
    /// frequencies only until the operator enters a dial.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>
    /// The kind of radio behind the page: "usb" (RF = dial + audio), "lsb" (RF = dial - audio),
    /// or "fm". FM is a channel radio, whose demodulated audio has no RF meaning at all - the
    /// page then labels audio as audio and shows the dial once, as the channel.
    /// </summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Waterfall line rate (display frame rate). Default 30.</summary>
    public int LinesPerSecond { get; set; } = 30;

    /// <summary>FFT length; 0 picks the rate default (2048 at 12 kHz, 8192 at 48 kHz).</summary>
    public int FftSize { get; set; }

    /// <summary>Clock used to pace our own transmissions onto the display. Injected for tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// What the host says each modem is meant to occupy, by sub-channel. Two things the
    /// waterfall cannot work out for itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The centre.</b> Probing gives measured band edges, and their midpoint is a few
    /// Hz off the centre the operator asked for - enough that a modem placed at 7051600 renders
    /// as 7.05159 and reads like a bug. The configured centre is the truth for a label; the
    /// measured edges remain the truth for the drawn band.</para>
    /// <para><b>Bands that cannot be probed.</b> ARDOP is not an <see cref="IModem"/> - it is a
    /// receive tap with its own transmitter - so nothing enumerable carries it and it was
    /// simply absent from the display. Declared here, it is drawn from its centre and width
    /// like anything else.</para>
    /// </remarks>
    public IReadOnlyList<DeclaredBand> DeclaredBands { get; set; } = [];

    /// <summary>
    /// Where the decoded-frames panel's opening backlog comes from - the station's frame log,
    /// when it keeps one. Called once per browser that connects, with the number of frames
    /// wanted; returns them <b>oldest first</b>. Null (the default) opens the panel empty.
    /// </summary>
    /// <remarks>
    /// <para>A panel that starts empty says nothing about a channel that has been busy all
    /// morning, and on a quiet band it is indistinguishable from a modem that is not working. The
    /// station already writes down every frame it hears and every frame it sends; this is that
    /// record, shown - transmissions included, marked as ours.</para>
    /// <para>A delegate rather than a database handle because the log lives in the daemon and
    /// this server lives in the library, and because whatever provides it should be free to
    /// decide what "recent" means. It is called on the connection's own thread, so it should be
    /// quick and must not throw - an exception here costs the browser its backlog, which is
    /// caught and shrugged off, but it should not be the way that is discovered.</para>
    /// </remarks>
    public Func<int, IReadOnlyList<LoggedFrame>>? FrameHistory { get; set; }

    /// <summary>
    /// The page is on the open internet for anybody to watch, not on a LAN for the operator.
    /// The page hides what only means something to an operator (which KISS hosts are attached)
    /// and shows what a stranger needs instead: a title, a paragraph of what they are looking
    /// at, and whose receiver it is. Nothing is removed from the operator's page; this only
    /// hides.
    /// </summary>
    public bool Public { get; set; }

    /// <summary>The page's title on a public deployment, in the tab and at the top of the page.
    /// Null falls back to the page's own.</summary>
    public string? Title { get; set; }

    /// <summary>One paragraph for the visitor: what the window is, that it is receive only. Null
    /// shows none.</summary>
    public string? About { get; set; }

    /// <summary>
    /// Where the list of receivers is, for a page that is one receiver of a site that offers
    /// several: the page shows a way back to it beside the receiver credit. Null (the default)
    /// shows none, which is right for a station that is its own site - there is nothing to go
    /// back to.
    /// </summary>
    /// <remarks>
    /// Relative, and normally "../": the site may sit behind a tunnel, under a hostname the
    /// daemon has never been told, so an absolute URL built here would be a guess.
    /// </remarks>
    public string? PickerUrl { get; set; }

    /// <summary>
    /// What kind of thing the audio on this page comes from, for the public page's credit line:
    /// <c>"station"</c> for a private station relaying its own receiver over an uplink, and null
    /// (the default) for a web receiver, which is what every page was before there was a second
    /// kind.
    /// </summary>
    /// <remarks>
    /// A word the page switches on rather than a sentence the server writes, and the difference
    /// matters: the credit contains an anchor built around an escaped name, so a server-supplied
    /// sentence would be either unescapable or a second way for somebody else's words to reach a
    /// visitor's browser as markup. Two sentences live in the page, one per kind, and only which
    /// of them to use crosses the wire. See <c>docs/dev/archive/uplink-plan.md</c> 4.4.
    /// </remarks>
    public string? ReceiverKind { get; set; }

    /// <summary>
    /// Where this server's own journal lines go, or null (the default) for a server that says
    /// nothing. One line per page dropped for silence, and nothing else: everything else this
    /// server does is either visible on the page or already reported by whoever owns it.
    /// </summary>
    /// <remarks>
    /// A bare delegate rather than a journal type, because the journal lives in the daemon and
    /// this server lives in the library. The daemon hands over its station's tagged sink, so a
    /// monitor fronting fifty receivers says which of them lost a viewer. Called on the
    /// connection's own thread; a throw here costs the line and nothing else.
    /// </remarks>
    public Action<string>? Log { get; set; }

    // ---------------------------------------------------------------- TX test
    /// <summary>
    /// The transmitter test the operator may ask this station for - a two-tone linearity check or
    /// a single tone - or null (the default) for a page that offers none, which is every public
    /// page, every relayed one and every station without a transmitter of its own.
    /// </summary>
    /// <remarks>
    /// Installed by the host, because what a keyup costs is the daemon's business and not this
    /// library's. See <see cref="TxTestControl"/>.
    /// </remarks>
    public TxTestControl? TxTest { get; set; }

    /// <summary>
    /// Measure the level of the audio arriving from the sound card and send it to the page a few
    /// times a second, so the operator can see what the capture gain is doing.
    /// </summary>
    /// <remarks>
    /// <para>Off by default, and set by the daemon only on a station with a real sound card and
    /// an operator's page (Tom, 2026-09-06: "Some assistance in setting the capture level would
    /// be useful. No way for the user to know what good means."). It is a control-room reading:
    /// a public page never gets it, and neither does a monitor, whose audio arrives over an
    /// uplink from somebody else's card and whose level says nothing about any card the person
    /// looking at it can reach.</para>
    /// <para>Free while nobody is watching, the same rule the audio relay keeps: with no viewers
    /// the meter is reset and not accumulated, so a station serving nobody does no work for it.
    /// See <see cref="Audio.InputLevelMeter"/> for the numbers and where they come from.</para>
    /// </remarks>
    public bool InputLevelMeter { get; set; }
}

/// <summary>
/// One frame out of the station's log, for the decoded-frames panel's opening backlog. The
/// receive-side subset of <see cref="Modems.FrameQuality"/> plus when it happened, which is
/// what a written-down frame has and a live one does not need.
/// </summary>
/// <param name="HeardAt">
/// When the station decoded it, or - on a transmitted frame - when it sent it (UTC); rendered
/// in the viewer's zone. The name matches the log's own <c>heard_at</c> column, which kept its
/// name so that queries written against it kept working.
/// </param>
/// <param name="SubChannel">Which modem heard or sent it.</param>
/// <param name="Mode">Its mode string, as the modem reported it.</param>
/// <param name="From">Source callsign where the frame carried one.</param>
/// <param name="To">Destination callsign where the frame carried one.</param>
/// <param name="LengthBytes">Decoded frame length.</param>
/// <param name="CorrectedBytes">Bytes FEC repaired, where the framing counts them.</param>
/// <param name="CrcValid">CRC verdict, where the framing carries one.</param>
/// <param name="OffsetHz">Measured carrier offset, where the decoder measured one.</param>
/// <param name="Transmitted">
/// True for a frame this station sent - badged TX in the panel rather than read as somebody
/// heard. Defaults to false: a log with nothing to say about direction holds receives.
/// </param>
/// <param name="TxTrimHz">
/// For a transmission, how far it was shifted off the nominal centre to suit the station it was
/// addressed to; null when it went out straight, and on every receive. Distinct from
/// <paramref name="OffsetHz"/>, which measures somebody else's transmitter rather than
/// commanding our own.
/// </param>
/// <param name="MonitorOnly">
/// True for a row the station read and did not pass to its host - Reed-Solomon alone stood
/// behind it (see <see cref="Modems.FrameQuality.MonitorOnly"/>). False on a row logged before
/// the column existed, which is the truth available: whether that frame was withheld was not
/// written down, and treating an unknown as withheld would throw away real history. What reads
/// it is the start-up replay into the links pane, which such a row must not feed.
/// </param>
/// <param name="PeakDbFs">
/// How loud the audio this frame arrived on was, in dBFS, over the frame's own stretch of it
/// (see <see cref="Modems.FrameQuality.PeakDbFs"/>). Null on a transmission, on a row logged
/// before the column existed, and wherever the frame's audio could not be placed - the panel
/// then shows no figure and no badge, which is what it did before there was one.
/// </param>
/// <param name="Clipped">
/// Whether the sound card ran out of codes during that same stretch. Null is "not written down",
/// which is a row from before the column and any row whose station was not handing its card's
/// own samples over; false is a card that had headroom.
/// </param>
/// <param name="Level">
/// The verdict the decoding modem's own limits put on those two when the frame was decoded
/// (<see cref="Modems.FrameQuality.Level"/>), read back out of the log so that a backlog row
/// badges exactly as the live row did. Null on a transmission and on a row written before the
/// column existed - a badge is a claim, and no claim was recorded for those.
/// </param>
/// <param name="PeakWorthShowing">
/// Whether the modem that decoded it said <paramref name="PeakDbFs"/> was a figure worth putting
/// on a row (<see cref="Modems.FrameQuality.PeakWorthShowing"/>), stored so a backlog row shows
/// exactly what the live row showed rather than the panel working it out again from a mode name.
/// Null on a transmission and on a row written before the column existed, and a figure with no
/// answer beside it is drawn as it always was.
/// </param>
/// <param name="PlainIl2p">
/// True for a row read as plain IL2P, with no trailing CRC behind it - Reed-Solomon alone
/// (see <see cref="Modems.FrameQuality.PlainIl2p"/>). What the panel badges <b>RS ONLY</b>, and
/// its own field rather than an inference from a null <paramref name="CrcValid"/>: that is also
/// null on HDLC, on FX.25, on ARDOP and on our own transmissions. False on a row logged before
/// the column existed, for the same reason as <paramref name="MonitorOnly"/>: what stood behind
/// that frame was not written down, and a badge is a claim.
/// </param>
/// <param name="TrailerNearBits">
/// For a row only the plain reading produced, how far the trailer that followed it was from the
/// trailer its payload implies, where that was close enough to corroborate it
/// (<see cref="Modems.FrameQuality.TrailerNearBits"/>). Read back beside
/// <paramref name="ChasedBits"/> so that a replayed row makes the same claim about a station as
/// the live row did: the pair is what separates a reading something agreed with from a reading
/// nothing checked. Null on a row logged before the column existed, which is "nothing said".
/// </param>
/// <param name="ChasedBits">
/// How many wire bits chase decoding flipped to reach this row
/// (<see cref="Modems.FrameQuality.ChasedBits"/>). Null where no chase was needed, where the
/// modem supplied no confidence, and on a row logged before the column existed - all of which
/// read as "not chased", which is the truth available and the reading that keeps a callsign.
/// </param>
/// <param name="Quality">
/// ARDOP's own 0-100 constellation quality (<see cref="Modems.FrameQuality.Quality"/>). Null on
/// every row that is not ARDOP, and on an ARDOP row logged before the column existed.
/// </param>
/// <param name="ArdopSnDb">
/// ARDOP's own reported signal-to-noise in dB (<see cref="Modems.FrameQuality.ArdopSnDb"/>), only
/// on the Ping and PingAck rows it was actually computed for. Null everywhere else, rather than
/// the 0 dB the underlying figure carries when it was never measured (issue #479).
/// </param>
/// <param name="HeldMs">
/// How long this frame waited for the channel before it went out, in milliseconds. Set on a
/// transmission; null on everything received, and on a transmission logged before the column
/// existed - which reads as "nothing said" rather than as a frame that went straight out.
/// </param>
public sealed record LoggedFrame(
    DateTimeOffset HeardAt,
    int SubChannel,
    string Mode,
    string? From,
    string? To,
    int LengthBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? OffsetHz,
    bool Transmitted = false,
    double? TxTrimHz = null,
    bool MonitorOnly = false,
    bool PlainIl2p = false,
    double? PeakDbFs = null,
    bool? Clipped = null,
    Audio.FrameLevel? Level = null,
    bool? PeakWorthShowing = null,
    int? TrailerNearBits = null,
    int? ChasedBits = null,
    int? Quality = null,
    double? ArdopSnDb = null,
    long? HeldMs = null)
{
    /// <summary>
    /// Which cause accounted for most of <see cref="HeldMs"/>, as the frame log spells it
    /// ("busy", "ourtx", "slot", "turnaround", "inhibit", "queue", "mixed"), or null on a row
    /// logged before the column existed.
    /// </summary>
    /// <remarks>
    /// Init properties rather than two more positional parameters. This record already ends in a
    /// run of same-typed optionals, which is the shape that takes two arguments transposed without
    /// complaining, and a caller who wants these can name them.
    /// </remarks>
    public string? HeldCause { get; init; }

    /// <summary>
    /// The sub-channel that asserted carrier sense for most of the wait, or null when the radio
    /// answered for the whole station, when nothing asserted it, or on an older row.
    /// </summary>
    public int? HeldBusySubChannel { get; init; }

    /// <summary>
    /// Whether the callsigns this row was logged with may be presented as a station
    /// (<see cref="Modems.DecodeStanding.CallsignWorthShowing"/>).
    /// </summary>
    /// <remarks>
    /// The same verdict the live row took, reached from the same three facts read back out of the
    /// log rather than stored beside them - they are already columns, and a rule read off the
    /// numbers cannot disagree with the numbers. Without this a browser reloading the page got the
    /// callsign back that the live row had withheld, which is the whole fix undone by a refresh.
    /// A row from before the columns says nothing and keeps its callsign.
    /// </remarks>
    public bool CallsignWorthShowing =>
        Modems.DecodeStanding.CallsignWorthShowing(PlainIl2p, TrailerNearBits, ChasedBits);
}

/// <summary>A band the host declares rather than the waterfall measuring it.</summary>
/// <param name="SubChannel">Which modem, for ordering and labels.</param>
/// <param name="Mode">Its mode string; rendered for display by <see cref="ModeNames"/>.</param>
/// <param name="CentreHz">The audio centre the operator configured.</param>
/// <param name="BandwidthHz">
/// Its width, used only when the band cannot be measured (ARDOP). Null means "measure it".
/// </param>
public sealed record DeclaredBand(int SubChannel, string Mode, double CentreHz, double? BandwidthHz);

/// <summary>
/// One host-facing KISS port and how many hosts hold a session on it, for the page's per-modem
/// attachment indicator.
/// </summary>
/// <param name="Port">The TCP port it listens on.</param>
/// <param name="SubChannel">
/// The one modem it serves, or null for the multiplexed port (which reaches every modem, by
/// nibble). The page uses this to decide which modem labels a session on this port lights up.
/// </param>
/// <param name="Clients">Hosts currently attached.</param>
public readonly record struct HostPortStatus(int Port, int? SubChannel, int Clients)
{
    /// <summary>
    /// The modems a polyglot port reaches, or null for any other port. A polyglot port has no one
    /// <see cref="SubChannel"/> and does not reach every modem either, so it says which.
    /// </summary>
    public IReadOnlyList<int>? SubChannels { get; init; }
}

/// <summary>
/// Where a page said it came from, and the host it reached this station on.
/// </summary>
/// <remarks>
/// Browsers do not apply the same-origin policy to WebSockets and there is no preflight, so
/// without this any page the operator's browser happens to load could open a socket to this
/// station and key the transmitter. A browser sets <c>Origin</c> itself and script cannot change
/// it, so a page from somewhere else cannot pretend to be this one; a non-browser client sends
/// none at all and is left alone. See <see cref="WaterfallWebServer.OriginMayKey"/>.
/// </remarks>
/// <param name="Declared">The <c>Origin</c> header, or empty when there was none.</param>
/// <param name="Served">The <c>Host</c> this request arrived on.</param>
public readonly record struct PageOrigin(string Declared, string Served);

/// <summary>One modem's display band, measured off its own modulator at start-up.</summary>
/// <param name="SubChannel">KISS sub-channel.</param>
/// <param name="Mode">Mode name.</param>
/// <param name="LowHz">Measured 99 % occupied-bandwidth low edge.</param>
/// <param name="HighHz">Measured 99 % occupied-bandwidth high edge.</param>
/// <param name="CentreHz">Band midpoint.</param>
public readonly record struct ModemBand(int SubChannel, string Mode, double LowHz, double HighHz, double CentreHz);

/// <summary>
/// The browser waterfall: an HTTP server (single embedded page, no external assets) plus a
/// WebSocket feed of display-rate spectrum lines and per-frame decode events. The page
/// draws the shared audio passband as a spectrum view over a scrolling waterfall, overlays
/// every configured modem's measured band (audio offset and rig-dial-derived RF centre),
/// and tags each decoded frame's energy burst with its source callsign, band SNR and
/// frequency offset - so a burst on screen reads directly as "who".
/// </summary>
/// <remarks>
/// Modem bands are measured, not tabulated: at <see cref="Start"/> each modem modulates a
/// short throwaway frame and the ITU-R SM.443 99 % occupied bandwidth of that audio is the
/// band the page shades. A new mode gets a correct overlay with no table to maintain.
/// Per-frame SNR/extent come from <see cref="BandActivityTracker"/> over the same lines the
/// display draws. Call <see cref="Start"/> before audio flows (it registers the channel
/// receive tap); received-side only - during transmit the display pauses, as half-duplex
/// hearing does.
/// </remarks>
public sealed class WaterfallWebServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SoundModemChannel _channel;
    private readonly WaterfallOptions _options;

    /// <summary>The local channel audio stream (issue #584), served at
    /// <see cref="ChannelAudioStream.Path"/>.</summary>
    private readonly ChannelAudioStream _channelAudioStream;

    // Null on a server that a router serves (see Routed): the front door owns the port, which is
    // the thing that actually has to be single, and fifty receivers under one hostname must not
    // mean fifty listeners. A station that is its own site keeps its own listener, as it always
    // had, and nothing about that path changes.
    private readonly HttpListener? _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<int, BandActivityTracker> _trackers = [];
    private readonly List<ModemBand> _bands = [];
    private readonly object _clientsLock = new();
    private readonly List<WaterfallClient> _clients = [];
    private readonly List<short> _audioBlock = [];

    /// <summary>
    /// The input level meter, or null on a page that does not offer one. Touched only from the
    /// receive tap, which is one thread.
    /// </summary>
    private readonly Audio.InputLevelMeter? _levelMeter;

    /// <summary>
    /// How many browsers are attached, readable without taking <see cref="_clientsLock"/>.
    /// </summary>
    /// <remarks>
    /// Kept beside the list rather than derived from it because the receive tap asks the question
    /// on every audio block for the life of the daemon, and the answer decides whether to do any
    /// work at all. Written under the lock at the three places the list changes, so it is never
    /// stale by more than one connection arriving or leaving.
    /// </remarks>
    private volatile int _viewerCount;

    // The spectrum source is fed from two threads - the receive loop and the transmitter - and
    // is not itself thread-safe. Half duplex keeps them apart in practice, but not across the
    // instant the transmit flag flips, which is exactly when both are live.
    private readonly object _sourceLock = new();
    private readonly object _transmitLock = new();
    private readonly Queue<float[]> _transmitPending = new();
    private int _transmitPendingSamples;
    private int _transmitPendingOffset;
    private long _transmitPacedAt;
    private float[] _transmitSilence = [];
    private ITimer? _transmitPacer;

    // Set while feeding our own transmission, so the line that comes back out can be told apart
    // from a received one. Lines are emitted synchronously inside Process, so this is exact.
    private bool _lineIsTransmit;

    /// <summary>
    /// One connected browser. Audio is per-client and off by default: a viewer who opened the
    /// page to look at a waterfall should not silently start pulling 24 KB/s, and several
    /// viewers should not each cost that unless they asked.
    /// </summary>
    private sealed class WaterfallClient
    {
        public required Channel<(WebSocketMessageType Kind, byte[] Payload)> Queue { get; init; }

        /// <summary>
        /// How this one connection is told to stop, so the keep-alive sweep can end a page that
        /// has stopped answering without touching any other. Linked to the server's own shutdown,
        /// so stopping the server still stops every page.
        /// </summary>
        /// <remarks>
        /// Cancelling the token the receive is waiting on, rather than <c>WebSocket.Abort</c>:
        /// abort marks the socket Aborted and leaves a receive that is already parked parked, on
        /// a socket that came from <c>HttpListener</c> - measured, .NET 10.0.7 - and the whole
        /// point here is to end a receive that will otherwise wait for ever.
        /// </remarks>
        public required CancellationTokenSource Stop { get; init; }

        /// <summary>Where this page said it came from, and where it reached us.</summary>
        public PageOrigin From { get; init; }

        private int _audio;
        private int _spectrum = 1;
        private long _heard;
        private int _dropped;

        /// <summary>
        /// When this client was last heard from, as a <see cref="TimeProvider"/> timestamp: the
        /// moment it joined, and then every message it sends, the keep-alive answer included.
        /// </summary>
        public long HeardAt
        {
            get => Interlocked.Read(ref _heard);
            set => Interlocked.Exchange(ref _heard, value);
        }

        /// <summary>
        /// Set by the keep-alive sweep on a client it has given up on, so that the receive loop's
        /// finally - which does the uncounting for every kind of departure - knows this one left
        /// because it stopped answering rather than because it said goodbye.
        /// </summary>
        public bool DroppedForSilence
        {
            get => Volatile.Read(ref _dropped) != 0;
            set => Volatile.Write(ref _dropped, value ? 1 : 0);
        }

        public bool AudioEnabled
        {
            get => Volatile.Read(ref _audio) != 0;
            set => Volatile.Write(ref _audio, value ? 1 : 0);
        }

        /// <summary>
        /// Whether this client wants waterfall lines. On by default; a detached links window
        /// turns it off, because it draws no waterfall and thirty lines a second is most of what
        /// a connection carries.
        /// </summary>
        public bool SpectrumEnabled
        {
            get => Volatile.Read(ref _spectrum) != 0;
            set => Volatile.Write(ref _spectrum, value ? 1 : 0);
        }
    }
    private WaterfallSource? _source;
    private byte[]? _surveyMessage;
    private byte[]? _hostPortsMessage;
    private string _surveyDirectory = "";
    private volatile bool _keyed;
    private byte[] _configMessage = [];
    private Task? _acceptLoop;

    // State a browser is handed on arrival and told about on every change: the transmit readout
    // and who is attached to which KISS port. Both are set from other threads (the radio's status
    // thread, the KISS accept loop) while a browser is connecting on its own, so both the update
    // and the connect handshake hold this - taken before _clientsLock, never after, so that a
    // change either lands in the snapshot a new client is given or is broadcast to it, and never
    // both or neither.
    private readonly object _stateLock = new();

    // The transmission being metered: what has been reported since the transmitter came up, so
    // that key-up can leave an average behind rather than whichever instantaneous sample happened
    // to be last.
    private double _transmitWattsSum;
    private int _transmitWattsCount;
    private double _transmitSwrSum;
    private int _transmitSwrCount;
    private byte[]? _transmitMessage;

    /// <summary>
    /// The links pane's source: every AX.25 frame this station hears or sends, read as part of a
    /// link between two stations rather than on its own, so the page can group frames by who is
    /// talking to whom and say in words when one of them is retrying. Public so the daemon can
    /// warm it from the frame log before the first browser arrives, and so a caller can read the
    /// same picture the page shows.
    /// </summary>
    /// <remarks>
    /// <para>Every frame except a <see cref="Modems.FrameQuality.MonitorOnly"/> one: a reading
    /// Reed-Solomon alone stood behind is listed in the frames panel and written to the log, but
    /// it is not evidence that the pair of callsigns in it were ever talking, so it makes no
    /// link. The same rule applies to the start-up replay out of the frame log.</para>
    /// <para>Fed under <see cref="_stateLock"/>, like the transmit readout and the host ports: a
    /// browser connecting takes its opening snapshot under the same lock, so a frame either lands
    /// in that snapshot or is broadcast to it afterwards, never both and never neither.</para>
    /// </remarks>
    public Ax25LinkObserver Links { get; } = new(new Ax25LinkObserverOptions { RecentPerLink = LinkFeedLength });

    /// <summary>
    /// How many frames each link's card opens with. The page's own cap too; a card is a feed of
    /// one conversation, and a hundred lines is a long way back into one.
    /// </summary>
    private const int LinkFeedLength = 100;

    /// <summary>
    /// How often the observer is asked to give up on calls nothing has answered. It only learns
    /// the time from the frames it is handed, and a call nobody answers is followed by no frame,
    /// so without this a card would say "calling" until it was forgotten an hour later. The
    /// observer's own wait is three minutes; ten seconds of slack on top of that is not visible.
    /// </summary>
    internal static readonly TimeSpan LinkExpiryPeriod = TimeSpan.FromSeconds(10);

    private ITimer? _linkExpiry;

    /// <summary>
    /// How often every page is asked whether it is still there. Twenty seconds, the interval the
    /// station uplink already runs its own heartbeat on and for the same reason: neither
    /// Cloudflare nor <c>cloudflared</c> promises to keep an idle WebSocket open, and a message
    /// every twenty seconds is an answer this side controls rather than one it hopes for.
    /// </summary>
    /// <remarks>
    /// A message the page answers, rather than a WebSocket ping frame, because .NET exposes no
    /// way to send one and no way to see a pong arrive. It is also the shape that survives a
    /// phone: a browser throttles a background tab's timers to once a minute or worse, but
    /// delivers WebSocket messages to it unthrottled, so a page that answers what it is sent goes
    /// on answering all night while a page that had to speak first would be dropped for a
    /// throttle it cannot control.
    /// </remarks>
    internal static readonly TimeSpan KeepAlivePing = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a page may say nothing at all before it stops being counted as a viewer. Three
    /// keep-alives missed: enough that a browser busy for a moment, or a tunnel with a hiccup,
    /// keeps its place, and short enough that a receiver held open for a phone that went to sleep
    /// is let go within about a minute rather than all night (#409).
    /// </summary>
    internal static readonly TimeSpan KeepAliveSilence = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the two above are checked. The uplink's watchdog period, and for the same
    /// reason: the deadline is what the wording promises, and a coarse sweep would make the drop
    /// land anywhere up to a whole interval late.
    /// </summary>
    internal static readonly TimeSpan KeepAlivePeriod = TimeSpan.FromSeconds(5);

    /// <summary>What the page is asked; it answers <c>{"type":"pong"}</c>, and any other message
    /// it happens to send counts as an answer too.</summary>
    private static readonly byte[] KeepAliveMessage =
        System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");

    private ITimer? _keepAlive;
    private long _pingedAt;

    /// <summary>Creates a server for <paramref name="channel"/>'s audio on
    /// <paramref name="port"/>.</summary>
    /// <param name="channel">The channel whose audio and decodes feed the display.</param>
    /// <param name="port">HTTP listen port.</param>
    /// <param name="options">Display defaults; null = defaults.</param>
    /// <param name="bind">Bind address; "*" listens on all interfaces.</param>
    public WaterfallWebServer(SoundModemChannel channel, int port, WaterfallOptions? options = null, string bind = "127.0.0.1")
        : this(channel, options)
    {
        Port = port;
        var listener = new HttpListener();
        // HttpListener wants "+" for "every interface" and rejects the literal 0.0.0.0 that a
        // TcpListener is perfectly happy with - the daemon uses one bind setting for both, so
        // translate here rather than make the operator know which listener wants which spelling.
        bool everyInterface = bind is "*" or "0.0.0.0" or "::" or "[::]";
        listener.Prefixes.Add($"http://{(everyInterface ? "+" : bind)}:{port}/");
        _listener = listener;
        Url = $"http://{(everyInterface ? "127.0.0.1" : bind)}:{port}/";
    }

    /// <summary>
    /// Creates a server for <paramref name="channel"/>'s audio with no listener and no port of
    /// its own, to be served by a <see cref="WaterfallRouter"/> under a path base such as
    /// <c>/r/m9psy-1/</c>.
    /// </summary>
    /// <remarks>
    /// Everything else about it is the same server: <see cref="Start"/> still measures the bands
    /// and hooks the channel, and <see cref="TryServeAsync"/> serves the same routes under the
    /// base the router hands it. What it does not do is bind a port, because a site that offers
    /// several receivers has one front door and one port, not one of each per receiver.
    /// </remarks>
    /// <param name="channel">The channel whose audio and decodes feed the display.</param>
    /// <param name="options">Display defaults; null = defaults.</param>
    public static WaterfallWebServer Routed(SoundModemChannel channel, WaterfallOptions? options = null) =>
        new(channel, options);

    private WaterfallWebServer(SoundModemChannel channel, WaterfallOptions? options)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _options = options ?? new WaterfallOptions();
        _txTest = _options.TxTest;
        _levelMeter = _options.InputLevelMeter
            ? new Audio.InputLevelMeter(_options.TimeProvider)
            : null;
        _channelAudioStream = new ChannelAudioStream(channel.SampleRate, line => Journal(line));
        Url = "";
    }

    /// <summary>The listen port: this server's own, or the router's once one is serving it.</summary>
    public int Port { get; private set; }

    /// <summary>A URL the page is reachable at; empty on a routed server until it is registered
    /// with a router, which is what tells it where it is being served from.</summary>
    public string Url { get; private set; }

    /// <summary>Told where a router is serving it from, so that it can say where it is.</summary>
    internal void ServedAt(int port, string url)
    {
        Port = port;
        Url = url;
    }

    /// <summary>
    /// A transmit meter sample - forward power in watts and SWR - or <c>(null, null)</c> for a
    /// transmitter that is not keyed.
    /// </summary>
    /// <param name="watts">Forward power now, or null when the transmitter is down.</param>
    /// <param name="swr">SWR now, where the radio reports one; null or non-finite is "unknown".</param>
    /// <remarks>
    /// <para>Called on every meter update while keyed, and once with nulls at key-up. Keyed
    /// samples go straight out (rounded, and only when the rounded reading changes - the meters
    /// update many times a second). At key-up the samples since key-down are averaged and that
    /// average is what stays on the page, stamped with the time, until the next transmission
    /// replaces it.</para>
    /// <para>Retained rather than cleared because a packet burst is a fraction of a second and
    /// gaps between them are minutes: a readout that only existed during the keyup was one an
    /// operator could not read. The page says which state it is showing - live or last - so a
    /// held reading can never be mistaken for a transmitter that is still up.</para>
    /// <para>Separate from <see cref="SetRadioStatus"/> because it changes on every keyup rather
    /// than once in a session.</para>
    /// </remarks>
    public void SetTransmitReading(double? watts, double? swr)
    {
        byte[] message;
        lock (_stateLock)
        {
            if (watts is double keyed && double.IsFinite(keyed))
            {
                _transmitWattsSum += keyed;
                _transmitWattsCount++;
                if (swr is double s && double.IsFinite(s))
                {
                    _transmitSwrSum += s;
                    _transmitSwrCount++;
                }

                message = TransmitMessage(true, keyed, swr, null);
            }
            else if (_transmitWattsCount > 0)
            {
                double averageWatts = _transmitWattsSum / _transmitWattsCount;
                double? averageSwr = _transmitSwrCount > 0 ? _transmitSwrSum / _transmitSwrCount : null;
                _transmitWattsSum = 0;
                _transmitWattsCount = 0;
                _transmitSwrSum = 0;
                _transmitSwrCount = 0;
                message = TransmitMessage(
                    false, averageWatts, averageSwr, _options.TimeProvider.GetUtcNow());
            }
            else
            {
                // Unkeyed and nothing to summarise: an idle transmitter reporting nothing, over
                // and over. Whatever the last transmission left on the page stands.
                return;
            }

            if (_transmitMessage is { } previous && previous.AsSpan().SequenceEqual(message))
            {
                return;
            }

            _transmitMessage = message;
            // Inside the lock: two meter samples racing must reach the page in the order they
            // were taken, or a keyed reading can land after the key-up that summarised it.
            Broadcast(WebSocketMessageType.Text, message);
        }
    }

    /// <summary>One transmit readout on the wire: live sample or retained average.</summary>
    private static byte[] TransmitMessage(bool keyed, double watts, double? swr, DateTimeOffset? at) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "tx",
                keyed,
                // Rounded here rather than on the page: it is what makes an unchanged reading
                // identical to the last one, which is what keeps the meters off the socket.
                watts = Math.Round(watts, 1),
                swr = swr is double s && double.IsFinite(s) ? Math.Round(s, 1) : (double?)null,
                at,
            },
            Json);

    /// <summary>
    /// The host-facing KISS ports the station serves, and how many hosts hold a session on each.
    /// </summary>
    /// <remarks>
    /// A node that stops passing traffic because its TCP session quietly went away looks, from
    /// the modem's side, exactly like a band that went quiet - and the journal line saying so
    /// scrolled past hours ago. On the page it is a state rather than an event: every modem's
    /// label says whether anything is attached to a port that reaches it.
    /// </remarks>
    /// <param name="ports">Every port, whatever its client count; a snapshot, not a delta.</param>
    public void SetHostPorts(IReadOnlyList<HostPortStatus> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "hosts",
                ports = ports.OrderBy(p => p.Port).Select(p => new
                {
                    port = p.Port,
                    sub = p.SubChannel,
                    subs = p.SubChannels,
                    clients = p.Clients,
                }),
            },
            Json);

        lock (_stateLock)
        {
            if (_hostPortsMessage is { } previous && previous.AsSpan().SequenceEqual(message))
            {
                return;
            }

            _hostPortsMessage = message;
            // Inside the lock, as for the transmit readout: a connect and a disconnect racing
            // must not leave the page holding the older of the two snapshots.
            Broadcast(WebSocketMessageType.Text, message);
        }
    }

    /// <summary>
    /// A line about the radio for the page's top bar - the frequency reference on a FlexRadio.
    /// Null means there is nothing to say (no radio that reports one), and the page shows
    /// nothing rather than an empty label.
    /// </summary>
    /// <remarks>
    /// Set whenever it changes; late-joining browsers get the current value with their config,
    /// so a page opened an hour in is not blank until the next change.
    /// </remarks>
    public void SetRadioStatus(string? status)
    {
        if (status == _radioStatus)
        {
            return;
        }

        _radioStatus = status;
        if (_source is not null)
        {
            _configMessage = BuildConfigMessage(); // before Start, Start's own build picks it up
        }

        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(
            new { type = "radio", status }, Json));

        if (LiveRelay is { } relay)
        {
            try
            {
                relay.Radio(status);
            }
            catch (Exception)
            {
                // As for audio and frames: the relay's message, the relay's problem.
            }
        }
    }

    /// <summary>
    /// The sentence <see cref="SetRadioStatus"/> was last given, or null for a station with
    /// nothing to say. For anything that attaches after it was set and needs the current value
    /// rather than the next change: a browser gets it with its config message, and an
    /// <see cref="Relay"/> attached later has no other way to learn it.
    /// </summary>
    public string? RadioStatus => _radioStatus;

    private string? _radioStatus;

    /// <summary>
    /// Whose receiver the audio comes from, for the public page's credit line: how the receiver
    /// describes itself and where its own page is. It is somebody else's receiver; the page
    /// says so and links to it. Null for either shows what there is.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetRadioStatus"/> because the status says what the session is
    /// doing right now and changes by the minute, while the credit is fixed for the life of the
    /// deployment; a visitor should see whose receiver it is while it is idle too.
    /// </remarks>
    public void SetReceiver(string? description, string? url)
    {
        if (description == _receiverDescription && url == _receiverUrl)
        {
            return;
        }

        _receiverDescription = description;
        _receiverUrl = url;
        if (_source is not null)
        {
            _configMessage = BuildConfigMessage(); // before Start, Start's own build picks it up
        }
    }

    private string? _receiverDescription;
    private string? _receiverUrl;

    /// <summary>
    /// How many browsers have the page open, whenever that changes. Raised after each page
    /// attaches and after each detaches, with the new count, from the connection's own thread
    /// and outside any lock. For a host that only wants to do something while somebody is
    /// watching; the count is the whole message.
    /// </summary>
    public event Action<int>? ViewersChanged;

    /// <summary>Browsers with the page open right now.</summary>
    public int Viewers
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>The measured per-modem display bands (populated by <see cref="Start"/>).</summary>
    public IReadOnlyList<ModemBand> Bands => _bands;

    /// <summary>
    /// Handles requests under <c>/api/</c>, returning true if it dealt with one. Null (the
    /// default) leaves every such path a 404, which is what a station that has not configured an
    /// API serves.
    /// </summary>
    /// <remarks>
    /// <para>The waterfall owns the socket; it deliberately does not own what this does with it.
    /// The handler is responsible for its own authentication - this class applies none, because
    /// the page it serves needs none and the two must not inherit each other's answer.</para>
    /// <para>The second argument is the request path relative to the base this server is served
    /// under, so a handler matches on <c>/api/config</c> whether the station is a site of its own
    /// or one receiver of a site that offers several. Reading
    /// <c>context.Request.Url.AbsolutePath</c> instead would see <c>/r/m9psy-1/api/config</c> and
    /// match nothing.</para>
    /// </remarks>
    public Func<HttpListenerContext, string, Task<bool>>? ApiHandler { get; set; }

    /// <summary>
    /// Called with the union of every channel-audio-stream client's requested band (issue #584)
    /// whenever it changes; null once no connected client has asked for one. Only the caller
    /// knows what a requested band should do, such as widening a headless Flex's slice filter
    /// while it is asked for and putting it back once it is not; this server knows nothing about
    /// Flex. Setting it while a band is already asked for (a program that connected before the
    /// device opened) calls it at once with that band. Called in order, under the stream's own
    /// lock, so it must return promptly and leave anything slow to its own thread.
    /// </summary>
    public Action<(int LowHz, int HighHz)?>? ReceiveBandRequested
    {
        get => _channelAudioStream.BandRequested;
        set => _channelAudioStream.BandRequested = value;
    }

    /// <summary>
    /// What this station has heard, served at <c>/metrics</c> (Prometheus text) and
    /// <c>/metrics/frames</c> (InfluxDB line protocol, one point per frame). Null serves
    /// neither, which is the default: a station publishes what it hears only when asked to.
    /// </summary>
    public Telemetry.StationTelemetry? Metrics { get; set; }

    /// <summary>
    /// Somewhere else this station's display stream goes, alongside the browsers: the uplink to a
    /// public monitor site. Null (the default) is a station that publishes nothing, and is what
    /// every station is until it is given a <c>publish</c> block.
    /// </summary>
    /// <remarks>
    /// <para>Additive in every direction. Nothing a browser is sent depends on this being set,
    /// nothing is diverted to it, and a relay that throws costs its own message and nothing else.
    /// What it is offered is what the page is already being shown: the receive audio, the
    /// station's own transmissions at the rate they are painted, every frame that reaches the
    /// panel, and the status sentence.</para>
    /// <para>Set before <see cref="Start"/>, or at any time after it; each offer reads the
    /// property rather than capturing it.</para>
    /// </remarks>
    public IWaterfallRelay? Relay { get; set; }

    /// <summary>
    /// The relay to offer to, or null - including once this server has been disposed, so that
    /// nothing is offered after a stop.
    /// </summary>
    /// <remarks>
    /// The stop is worth being explicit about rather than leaving to the fact that a disposed
    /// server has no browsers left to disappoint. <see cref="SoundModemChannel"/> has no way to
    /// remove a receive tap, so the tap this server registered in <see cref="Start"/> keeps being
    /// called for as long as the channel lives; without this, a disposed server would go on
    /// handing a relay audio, and a relay is an object with a socket and a lifetime of its own.
    /// </remarks>
    private IWaterfallRelay? LiveRelay => _stopping.IsCancellationRequested ? null : Relay;

    /// <summary>
    /// Whether the audio now being fed to <see cref="SoundModemChannel.ProcessReceive"/> is a
    /// station's own transmission rather than something it heard, so that the line it produces is
    /// marked as ours. Default false, which is every ordinary station.
    /// </summary>
    /// <remarks>
    /// <para>The monitor's side of the flag <see cref="_lineIsTransmit"/> carries on a station.
    /// A relayed station's audio arrives already labelled - the uplink's audio message says which
    /// kind each block is - but it arrives as ordinary receive audio through an
    /// <c>IAudioInput</c>, and there is nothing about it here to tell the two apart.</para>
    /// <para>Set by the input immediately before it returns a block. Reading a block and
    /// processing it are the same thread, and a block is never half transmitted and half
    /// received, so this is exact rather than nearly right.</para>
    /// </remarks>
    public bool IncomingIsTransmit { get; set; }

    /// <summary>
    /// What the signal survey has been doing - captures kept, captures a budget refused, and the
    /// disk it is using. Pushed rather than polled, and only on a change.
    /// </summary>
    /// <remarks>
    /// The refusals are the reason this exists. A survey left running for a week silently becomes
    /// a sample rather than the set when the channel is busier than its rate limit, and nothing
    /// anywhere reported that: an operator would have had to count files per hour and notice the
    /// number was exactly the cap. A count on the page answers it at a glance - and it is state
    /// rather than an event, which is why it belongs on a display and not in a journal.
    /// </remarks>
    /// <param name="captured">Captures written to disk.</param>
    /// <param name="skipped">Bursts worth keeping that a rate limit, cooldown or missing audio
    /// refused.</param>
    /// <param name="bytes">Bytes the capture directory holds.</param>
    /// <param name="directory">Where they are, so their audio can be served from it.</param>
    public void SetSurveyStatus(long captured, long skipped, long bytes, string directory)
    {
        _surveyDirectory = directory;
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(
            new { type = "survey", captured, skipped, bytes }, Json);
        if (_surveyMessage is { } previous && previous.AsSpan().SequenceEqual(message))
        {
            return;
        }

        _surveyMessage = message;
        Broadcast(WebSocketMessageType.Text, message);
    }


    /// <summary>
    /// Measures every modem's band, hooks the channel (receive tap + frame events) and
    /// starts listening. Call before audio flows and after all modems are added.
    /// </summary>
    /// <remarks>
    /// A routed server (see <see cref="Routed"/>) does everything here except the listening,
    /// which is its router's: the band probe, the channel subscriptions and the link expiry timer
    /// are the station's own work either way, and are what a server has to have done before the
    /// first browser arrives.
    /// </remarks>
    public void Start()
    {
        var source = new WaterfallSource(
            _channel.SampleRate, OnLine, _options.LinesPerSecond, _options.FftSize);
        _source = source;

        foreach ((int sub, IModem modem) in _channel.Modems.OrderBy(m => m.Key))
        {
            if (!TryMeasureBand(sub, modem, _channel.SampleRate, out ModemBand band))
            {
                continue;
            }

            // Measured edges, but the configured centre where the host stated one: the
            // midpoint of a measurement is a few Hz off what the operator asked for. A
            // declared centre of 0 is the daemon's "no configured frequency" sentinel, not
            // a statement that the modem sits at DC - the measurement stands then, or the
            // band chip reads "0 Hz" with its centre tick on the left edge of the page.
            if (Declared(sub) is { CentreHz: > 0 } declared)
            {
                band = band with { CentreHz = declared.CentreHz };
            }

            AddBand(band, source);
        }

        // Bands nothing enumerable carries - ARDOP, which is a tap rather than a modem.
        foreach (DeclaredBand declared in _options.DeclaredBands
                     .Where(d => !_channel.Modems.ContainsKey(d.SubChannel) && d.BandwidthHz is > 0)
                     .OrderBy(d => d.SubChannel))
        {
            double half = declared.BandwidthHz!.Value / 2;
            AddBand(
                new ModemBand(
                    declared.SubChannel, declared.Mode,
                    declared.CentreHz - half, declared.CentreHz + half, declared.CentreHz),
                source);
        }

        _bands.Sort((a, b) => a.SubChannel.CompareTo(b.SubChannel));

        _configMessage = BuildConfigMessage();

        // The local channel audio stream (issue #584): always registered, costing a no-op
        // length check on the receive thread when nobody has connected to it, so existing
        // stations see no change at all unless a program asks. Fed both halves of the channel's
        // receive audio: the taps everyone else gets, and the blocks skipped for a keyup, which
        // this stream alone turns into explicitly-marked silence rather than simply losing them;
        // and told when the input lost audio, so its readers can be told too.
        _channel.AddReceiveTap(_channelAudioStream.OnReceive);
        _channel.KeyedReceiveBlock += _channelAudioStream.OnKeyedBlock;
        _channel.ReceiveAudioLost += _channelAudioStream.OnInputLost;

        _channel.AddReceiveTap(samples =>
        {
            // Not while our own transmission is still being painted. The two are separate audio
            // streams and the transform has one accumulator: interleaved, a single window holds
            // part of a burst and part of the band noise, and comes out broadband - a full-width
            // haze over the back half of every keyup, with the line type flickering between the
            // two ramps as it goes. Receive processing is gated during a keyup, but the paced
            // painting outlives it whenever the audio device's Drain returns before the audio has
            // actually left the radio, which is the normal case.
            if (!_keyed && Volatile.Read(ref _transmitPendingSamples) == 0)
            {
                lock (_sourceLock)
                {
                    source.Process(samples);
                }

                // The uplink gets what the picture is drawn from, so it is inside this gate and
                // not beside it. Outside, the drain tail after a key-up - the pacer painting
                // audio the sound card has not finished playing while the input has already
                // resumed delivering - would put received and transmitted blocks on the wire
                // alternately. The monitor draws its picture from those blocks, so it would
                // reproduce in somebody else's browser exactly the broadband haze this gate
                // exists to prevent here, and a listener would hear the keyup and the band mixed
                // together. It also makes a mixed stream impossible to block into the
                // fixed-length audio messages of the uplink plan's 4.2 without breaking 4.3's
                // rule that one block is never both kinds.
                //
                // Before the s16 blocking BroadcastAudio does for a browser, and not conditional
                // on anybody here having pressed Listen.
                OfferAudio(samples, transmitted: false);
            }

            // The listener feed is a stream of its own and has nothing to do with the transform.
            BroadcastAudio(samples);

            // Outside the display gate above, deliberately: this is a reading of the input, and
            // the drain tail after a key-up is audio the card really is delivering. It is kept
            // out of the picture because it would smear the transform, which is a different
            // problem from whether the capture gain is right.
            MeterInput(samples);
        });

        // Draw what we transmit, so the display stays continuous across a keyup instead of
        // freezing and silently compressing the time axis.
        _channel.TransmittedAudio += OnTransmittedAudio;
        _channel.TransmittingChanged += OnTransmittingChanged;
        _channel.FrameReceivedWithQuality += OnFrame;
        _channel.FrameTransmittedWithReport += OnFrameTransmitted;
        _linkExpiry = _options.TimeProvider.CreateTimer(
            _ => ExpireLinks(), null, LinkExpiryPeriod, LinkExpiryPeriod);
        _pingedAt = _options.TimeProvider.GetTimestamp();
        _keepAlive = _options.TimeProvider.CreateTimer(
            _ => SweepKeepAlive(), null, KeepAlivePeriod, KeepAlivePeriod);
        if (_listener is { } listener)
        {
            listener.Start();
            _acceptLoop = AcceptLoopAsync(listener);
        }
    }

    /// <summary>
    /// Measures a modem's occupied band for the overlay. Delegates to
    /// <see cref="ModemBandProbe"/>, which the RF band planner uses too - one measurement, so
    /// what the waterfall draws and what the planner fits can never disagree.
    /// </summary>
    internal static bool TryMeasureBand(int subChannel, IModem modem, int sampleRate, out ModemBand band)
    {
        band = default;
        if (!ModemBandProbe.TryMeasure(modem, sampleRate, out double low, out double high))
        {
            return false;
        }

        band = new ModemBand(subChannel, modem.Mode, low, high, (low + high) / 2);
        return true;
    }

    private DeclaredBand? Declared(int subChannel) =>
        _options.DeclaredBands.FirstOrDefault(d => d.SubChannel == subChannel);

    private void AddBand(ModemBand band, WaterfallSource source)
    {
        _bands.Add(band);
        _trackers[band.SubChannel] = new BandActivityTracker(
            source.BinWidthHz, source.LinesPerSecond, source.LineLength, band.LowHz, band.HighHz);
    }

    private byte[] BuildConfigMessage()
    {
        WaterfallSource source = _source!;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "config",
            sampleRate = _channel.SampleRate,
            binWidthHz = source.BinWidthHz,
            lineLength = source.LineLength,
            linesPerSecond = source.LinesPerSecond,
            dialHz = _options.DialFrequencyHz,
            radioStatus = _radioStatus,
            sideband = _options.Sideband,
            page = Page.Value.Version,
            // Additive, like every other field here: an older tab that has never heard of it
            // just does not draw it. Not on the uplink wire (docs/dev/uplink-wire-format.md) -
            // that document governs the station-to-monitor protocol only, and says outright that
            // this one, the browser protocol, "may change whenever the page does, because the
            // page and the server ship in the same binary" - so nothing there needed touching.
            daemonVersion = new
            {
                version = DaemonVersion.Version,
                commit = DaemonVersion.Commit,
                release = DaemonVersion.IsRelease,
            },
            publicMonitor = _options.Public,
            title = _options.Title,
            about = _options.About,
            receiver = _receiverDescription,
            receiverUrl = _receiverUrl,
            receiverKind = _options.ReceiverKind,
            pickerUrl = _options.PickerUrl,
            // TX test: null on every page that is not the operator's own, so the control is
            // absent rather than hidden - a public page is never sent the shape of it.
            txTest = _txTest is not { } test ? null : new
            {
                defaultSeconds = test.DefaultSeconds,
                maxSeconds = test.MaxSeconds,
                lowToneHz = test.LowToneHz,
                highToneHz = test.HighToneHz,
                refusal = test.Refusal,
                presets = test.Presets.Select(p => new
                {
                    toneHz = p.ToneHz,
                    deviationHz = Math.Round(p.DeviationHz),
                }),
                // Read fresh into every config, including a reconnect's: a TxTestStatus event only
                // ever reaches a page that was already listening when it was sent, so this is the
                // only way a page that just connected or reconnected finds out a test already
                // under way is not its own to assume stopped (#425).
                running = test.IsRunning?.Invoke() ?? false,
            },
            modems = _bands.Select(b => new
            {
                sub = b.SubChannel,
                mode = b.Mode,
                modeName = ModeNames.Display(b.Mode),
                lowHz = Math.Round(b.LowHz, 1),
                highHz = Math.Round(b.HighHz, 1),
                centreHz = Math.Round(b.CentreHz, 1),
            }),
        }, Json);
    }

    /// <summary>
    /// Our own transmitted audio, queued to be painted at the rate it will actually go out.
    /// </summary>
    /// <remarks>
    /// <para>The whole keyup arrives here in one call: the modulator produces the burst as a
    /// single array long before the sound card has played a sample of it. Painting it on arrival
    /// put a two-second burst on the display as sixty lines inside a few milliseconds, followed
    /// by two seconds of nothing at all - receive processing is gated off while transmitting, so
    /// there is no other line source during a keyup. That is the judder: a lurch at key-down and
    /// then a frozen display for the length of the transmission.</para>
    /// <para>This is called <em>before</em> the audio is written to the device, which is what
    /// makes the pacing below line up with reality. A device's write blocks until its buffer has
    /// room, so being told afterwards meant the queue stayed empty for most of the transmission -
    /// and the pacer painted silence throughout it and then the burst all over again. See
    /// <see cref="Channel.SoundModemChannel.TransmittedAudio"/>.</para>
    /// <para>So it is queued and released by <see cref="PaceTransmitLines"/> at the rate real
    /// time passes, which is the rate the audio is leaving the radio. The pacing lives here and
    /// not in the channel because the transmitter must never wait on a picture - this returns
    /// immediately however long the burst is.</para>
    /// </remarks>
    /// <summary>
    /// Key-down and key-up. Painting starts at key-down rather than when the first audio arrives.
    /// </summary>
    /// <remarks>
    /// Receive processing stops the instant the transmitter takes the channel, but the first
    /// transmitted audio does not exist until the frame has been modulated and handed to the
    /// device. Nothing at all is drawn in between, so the waterfall visibly stalls as the PTT
    /// engages. Starting here means the time axis keeps moving through that gap - and through
    /// the gaps between frames in one keyup - with silence, which is what was on the air.
    /// </remarks>
    private void OnTransmittingChanged(bool keyed)
    {
        _keyed = keyed;
        if (keyed)
        {
            StartPacing();
        }
    }

    private void OnTransmittedAudio(ReadOnlyMemory<float> samples)
    {
        if (_source is null || samples.IsEmpty)
        {
            return;
        }

        float[] display = ForDisplay(samples.Span);

        lock (_transmitLock)
        {
            _transmitPending.Enqueue(display);
            Volatile.Write(ref _transmitPendingSamples, _transmitPendingSamples + display.Length);
        }

        StartPacing();
    }

    /// <summary>Starts the pacing clock if it is not already running.</summary>
    private void StartPacing()
    {
        lock (_transmitLock)
        {
            if (_transmitPacer is not null)
            {
                return;
            }

            // Start the clock here, so the first tick releases one tick's worth rather than a
            // backlog measured from whenever the timer last ran.
            _transmitPacedAt = _options.TimeProvider.GetTimestamp();
            var period = TimeSpan.FromMilliseconds(1000.0 / _options.LinesPerSecond);
            _transmitPacer = _options.TimeProvider.CreateTimer(
                _ => PaceTransmitLines(), null, period, period);
        }
    }

    /// <summary>
    /// How far our own transmission is turned down before it is drawn.
    /// </summary>
    /// <remarks>
    /// <para>A modulator emits around −5 dBFS rms - some 35 dB hotter than anything the display
    /// ever sees on receive, and the page's window is −95..−35 dB. Drawn as-is, the transform's
    /// own leakage skirt (40 dB below the peak, buried in the noise for a received signal) lands
    /// well above the display floor and smears across the full span: measured, 1021 of 1024 bins
    /// lit. Turned down by this, the same burst lights 539 and peaks at 77 % brightness, against
    /// 562 and 79 % for a genuinely strong received station.</para>
    /// <para><b>A fixed gain, deliberately, and not a normalisation to a target level.</b>
    /// Normalising each buffer is an automatic gain control, and an AGC's whole purpose is to
    /// make quiet things loud - so the quiet buffers in a keyup (a ramp-down, a tail, an idle
    /// stretch of a shifted ARDOP burst) get multiplied by an enormous gain and their noise floor
    /// fills the entire span. Measured: near-silence at −65 dBFS rms normalised to −40 lights
    /// 1011 of 1024 bins. That is the same full-width haze as the original bug, arrived at from
    /// the opposite direction, and it is why this is a constant. The modulator's level is a
    /// stable property of the system; there is nothing here that needs tracking.</para>
    /// </remarks>
    internal const double TransmitDisplayGainDb = -35;

    /// <summary>
    /// Scales transmitted audio to the level the display is calibrated for.
    /// </summary>
    /// <remarks>
    /// Our own transmit level is not a measurement of anything the display can show: it is
    /// whatever the modulator happens to emit, which differs per mode and which no receiver would
    /// ever see at that strength. Drawing it literally does not produce a hot signal, it produces
    /// a saturated one. Purely a gain - the spectrum's shape, and so the bandwidth and placement
    /// the operator reads off it, is untouched, and quiet stays quiet.
    /// </remarks>
    internal static float[] ForDisplay(ReadOnlySpan<float> samples)
    {
        double gain = Math.Pow(10, TransmitDisplayGainDb / 20);
        var scaled = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            scaled[i] = (float)(samples[i] * gain);
        }

        return scaled;
    }

    /// <summary>
    /// Releases however much transmitted audio real time has passed for, and stops once the
    /// queue is empty.
    /// </summary>
    /// <remarks>
    /// <para>Paced by elapsed time rather than by counting ticks, because a timer that fires late
    /// - and at a 33 ms period on a busy box it will - must still paint the right amount rather
    /// than fall progressively behind the transmission it is drawing. That also removes any need
    /// to detect and correct a backlog: a late tick simply releases proportionally more.</para>
    /// <para>The queue is not a backlog. It is audio that has not gone out yet - the display is
    /// meant to trail the modulator by exactly as much as the sound card does.</para>
    /// </remarks>
    private void PaceTransmitLines()
    {
        WaterfallSource? source = _source;
        if (source is null)
        {
            return;
        }

        long now = _options.TimeProvider.GetTimestamp();
        var due = new List<ArraySegment<float>>();

        lock (_transmitLock)
        {
            double elapsed = _options.TimeProvider.GetElapsedTime(_transmitPacedAt, now).TotalSeconds;
            _transmitPacedAt = now;
            int budget = (int)(elapsed * _channel.SampleRate);

            while (budget > 0 && _transmitPending.Count > 0)
            {
                float[] head = _transmitPending.Peek();
                int available = head.Length - _transmitPendingOffset;
                int take = Math.Min(available, budget);
                due.Add(new ArraySegment<float>(head, _transmitPendingOffset, take));

                budget -= take;
                Volatile.Write(ref _transmitPendingSamples, _transmitPendingSamples - take);
                if (take == available)
                {
                    _transmitPending.Dequeue();
                    _transmitPendingOffset = 0;
                }
                else
                {
                    _transmitPendingOffset += take;
                }
            }

            // Still keyed with nothing queued - the gap between key-down and the first modulated
            // frame, or between frames. Silence is what is on the air, and drawing it is what
            // keeps the time axis moving instead of stalling until audio turns up.
            if (due.Count == 0 && _keyed && budget > 0)
            {
                if (_transmitSilence.Length < budget)
                {
                    _transmitSilence = new float[budget];
                }

                due.Add(new ArraySegment<float>(_transmitSilence, 0, budget));
            }

            if (_transmitPending.Count == 0 && !_keyed)
            {
                _transmitPacer?.Dispose();
                _transmitPacer = null;
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        lock (_sourceLock)
        {
            _lineIsTransmit = true;
            try
            {
                foreach (ArraySegment<float> piece in due)
                {
                    source.Process(piece.AsSpan());
                }
            }
            finally
            {
                _lineIsTransmit = false;
            }
        }

        // The uplink gets our own transmission from here rather than from TransmittedAudio, and
        // that is the whole reason the hook is in this method: released at the rate real time
        // passes, so a relayed picture trails the modulator by exactly as much as the station's
        // own does, for free.
        //
        // Below the lock, not inside it, and this matters more than it looks. _sourceLock is what
        // the receive tap takes to paint, on the station's audio read thread; a relay that was
        // slow inside it would not merely lose its own block, it would park that thread and stop
        // the station consuming from its sound card for as long as it took. A website being
        // unreachable must not do that to a node passing traffic. The same due list, in the same
        // order, so the pacing is exactly what was painted.
        foreach (ArraySegment<float> piece in due)
        {
            OfferAudio(piece.AsSpan(), transmitted: true);
        }
    }

    /// <summary>
    /// Offers a block of audio to the relay, if there is one and anybody at the far end is
    /// watching.
    /// </summary>
    /// <remarks>
    /// <see cref="IWaterfallRelay.Wanted"/> is read before anything is done with the samples, so
    /// a station whose uplink is idle - which is a station nobody has picked, which is nearly
    /// always - spends one property read per block and nothing else.
    /// </remarks>
    private void OfferAudio(ReadOnlySpan<float> samples, bool transmitted)
    {
        if (LiveRelay is not { } relay)
        {
            return;
        }

        try
        {
            if (relay.Wanted)
            {
                relay.Audio(samples, transmitted);
            }
        }
        catch (Exception)
        {
            // The uplink is a courtesy and this is the receive loop. A relay that throws loses
            // this block; the station carries on hearing, decoding and passing traffic, which is
            // what it is for. Saying so is the relay's own job - it has the journal and the
            // context, and this class has neither.
        }
    }



    private void OnLine(long index, ReadOnlyMemory<byte> line)
    {
        ReadOnlySpan<byte> bins = line.Span;
        // Ours because we are transmitting, or ours because the station this audio was relayed
        // from was. A monitor never sets the first and a station never sets the second.
        bool transmit = _lineIsTransmit || IncomingIsTransmit;

        // Our own transmission is not a signal we heard. Feeding it to the trackers would report
        // a huge SNR and attribute it to whatever frame decoded next.
        if (!transmit)
        {
            foreach (BandActivityTracker tracker in _trackers.Values)
            {
                tracker.AddLine(bins);
            }
        }

        bool anyClients;
        lock (_clientsLock)
        {
            anyClients = _clients.Count > 0;
        }

        if (!anyClients)
        {
            return;
        }

        var message = new byte[5 + bins.Length];
        // 0x01 heard, 0x03 transmitted - the page draws them differently, because a burst of
        // your own must not read as a strong station.
        message[0] = transmit ? (byte)0x03 : (byte)0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), (uint)index);
        bins.CopyTo(message.AsSpan(5));
        lock (_clientsLock)
        {
            foreach (WaterfallClient client in _clients)
            {
                if (client.SpectrumEnabled)
                {
                    client.Queue.Writer.TryWrite((WebSocketMessageType.Binary, message));
                }
            }
        }
    }

    /// <summary>Frame event (receive thread): attribute the just-decoded frame to its burst
    /// - callsigns off the frame, SNR/extent off the band tracker, offset off the winning
    /// decoder branch - and fan it out as JSON.</summary>
    private void OnFrame(int subChannel, byte[] frame, FrameQuality quality)
    {
        // The channel's own measurement first (FrameQuality.SnrDb): it is what the frame
        // log, journal and KISS quality frame record, and the panel must show the same
        // figure - two burst-SNR numbers for one frame is the branch-index-offset mistake
        // with a different noun. This server's trackers still supply the burst extent (a
        // display-rate quantity), and the SNR too for a channel wired without the monitor's
        // enrichment (frames arriving through a bare event, as some tests do).
        double? snrDb = quality.SnrDb;
        int? burstLines = null;
        if (_trackers.TryGetValue(subChannel, out BandActivityTracker? tracker)
            && tracker.TryMeasureBurst(out double snr, out int lines))
        {
            snrDb ??= Math.Round(snr, 1);
            burstLines = lines;
        }

        // A callsign on a row is a claim, and a reading nothing checked cannot support one. The
        // verdict is the decode's own (FrameQuality.CallsignWorthShowing, taken where the facts
        // behind it live), and this only obeys it: the frame is still listed, still badged, still
        // logged and still relayed, and simply does not name a station. What that costs is the
        // measured quarter of the class that were real, which were being withheld from the host
        // anyway; what it buys is the other three quarters not appearing on the waterfall as
        // GB7BPQ (docs/dev/false-decodes.md).
        string? from = null;
        string? to = null;
        if (quality.CallsignWorthShowing
            && Ax25AddressParser.TryParse(frame, out string source, out string destination))
        {
            from = source;
            // A blank destination field parses to "" - send null, as the backlog does, and the
            // panel shows "?" beside the source that attributed the frame.
            to = destination.Length > 0 ? destination : null;
        }

        BroadcastFrame(
            subChannel, quality.Mode, from, to, quality.FrameBytes, snrDb, burstLines,
            quality.FrequencyOffsetHz is { } offset ? Math.Round(offset, 1) : null,
            quality.CorrectedBytes, quality.CrcValid,
            // A frame that decoded and would not yield callsigns is where the panel used to pose
            // a question instead of answering one: it said "unattributed" and stopped. It has
            // already passed Reed-Solomon and, on an IL2P+CRC link, the CRC - so the bits are
            // right and the reading of them is not, and which encapsulation carried it is the
            // first thing worth knowing. The bytes come too, because the panel is where an
            // operator notices one of these and the next thing they will want is to copy them.
            // The overload taking the quality also covers the other way a row ends up with no
            // callsign - a header that read perfectly well behind a decode that established
            // nothing - because "unattributed" on bytes that plainly are an address field would
            // otherwise send somebody looking for a parser bug.
            note: Ax25AttributionNote.For(frame, quality),
            headerType: quality.HeaderType?.ToString(),
            frameHex: from is null && to is null ? Convert.ToHexString(frame) : null,
            // Reed-Solomon and nothing else stood behind this frame, and on an -il2pc modem the
            // mode label beside it says the opposite. The panel is the one place an operator sees
            // both at once, so it is the place to say so - and to say whether the frame was
            // handed on, which is the operator's own configuration answering back.
            plainIl2p: quality.PlainIl2p,
            monitorOnly: quality.MonitorOnly,
            // How loud this frame's own audio was, and whether the card railed while it was
            // arriving - measured over the frame's own stretch of input by the channel
            // (FrameLevelMonitor), not re-derived here: the frame log and a monitor watching
            // this station's uplink carry the same two numbers, and three measurements of one
            // burst is the branch-index-offset mistake with a different noun.
            peakDbFs: quality.PeakDbFs,
            clipped: quality.Clipped,
            level: quality.Level,
            peakWorthShowing: quality.PeakWorthShowing,
            // And the same answer about the SNR, made by the decode rather than here: a band
            // reading beside a frame nothing checked is the number that made a fabricated row
            // indistinguishable from a real one (FrameQuality.SnrWorthShowing). The measurement
            // itself is passed along untouched, as the peak is, because a monitor keeps its own
            // copy of this station's log and the figure is evidence there.
            snrWorthShowing: quality.SnrWorthShowing,
            // For a relay, and for nobody else: the two facts a monitor's own copy of this log
            // needs to reach the verdict above for itself, and the bytes it folds its own links
            // out of rather than being sent a summary of them.
            trailerNearBits: quality.TrailerNearBits,
            chasedBits: quality.ChasedBits,
            raw: frame);

        // Everything above lists the frame; this last step makes a claim about the channel, and
        // an RS-only reading is not evidence for one. A withheld frame stood on Reed-Solomon
        // alone and is kept from the host for exactly that reason, so a corrupt or forged
        // callsign pair read out of one must not open a card, name a station as heard, or move a
        // link's state. It stays in the frames panel badged RS ONLY, in the frame log and in the
        // journal, which is where an operator can weigh it for what it is (Tom's call, 2026-09-04).
        //
        // MonitorOnly and not DecodeConfidence.IsEvidence, deliberately, though the metrics
        // endpoint uses the stricter test for its station list: IsEvidence also rejects a plain
        // reading the operator asked to be given, and on a -nocrc port it would reject every
        // frame there is and leave the pane permanently empty. The rule here is "the station
        // itself would not pass this on", which is the operator's own configuration talking.
        if (!quality.MonitorOnly)
        {
            ObserveLink(subChannel, frame, transmitted: false);
        }
    }

    /// <summary>Frame event (transmit thread): list what this station has just sent, so the
    /// panel is a record of the channel rather than of half of it.</summary>
    /// <remarks>
    /// <para>The burst is already drawn - transmitted audio is painted in its own style so a
    /// keyup does not read as a strong station - but until now nothing said <em>what</em> it
    /// was, and an operator watching their own beacon go out had to take it on trust. Raised
    /// after the audio has left, so a listed frame is one that actually went on air.</para>
    /// <para>No SNR, offset, FEC count or CRC: those are receive measurements, and inventing
    /// them for our own transmission would be inventing a measurement of ourselves. The transmit
    /// trim is the one number here that IS ours to state - it is a command we issued, not an
    /// estimate we made - so it is carried in its own field rather than folded into the offset,
    /// which means something else entirely. No burst tag either - a received frame's tag lands on the energy that carried it, but transmitted
    /// audio is queued and repainted in real time while this fires as soon as the device has
    /// taken it, so the tag would sit somewhere up the burst rather than on it.</para>
    /// </remarks>
    /// <summary>
    /// Below this a wait is ordinary channel access, and the row says nothing about it.
    /// </summary>
    /// <remarks>
    /// The same threshold the journal uses, and for the same reason: on a clear channel every
    /// frame waits a slot time or two, and a note on every transmission row is a column of noise
    /// that hides the one row where the wait actually mattered. The frame log stores the figure
    /// either way - the log is the record, the row is what somebody reads.
    /// </remarks>
    internal static readonly TimeSpan HeldWorthShowing = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// What held a transmission, in the two or three words a badge can carry, or null where no
    /// single cause accounts for half the wait.
    /// </summary>
    /// <remarks>
    /// <para>The row already says how long. What it could not say was which of two opposite things
    /// the figure meant: a channel somebody else is occupying, or this station's own window going
    /// out in front of the frame. Those are the same number and they are acted on differently, so
    /// the badge carries the short form and the tooltip the sentence.</para>
    /// <para>Composed here rather than on the page, and from the same words the frame log stores,
    /// so a live row and a row replayed out of the log after a reload say the same thing.</para>
    /// </remarks>
    internal static string? HeldCause(TransmitWaits waits) => waits.Dominant switch
    {
        TransmitWaitCause.ChannelBusy => waits.BusiestSubChannel is int sub
            ? $"busy ch{sub.ToString(CultureInfo.InvariantCulture)}"
            : "busy",
        TransmitWaitCause.OurTransmission => "our tx",
        TransmitWaitCause.OurTurn => "other link",
        TransmitWaitCause.Backoff => "backoff",
        TransmitWaitCause.TurnaroundHold => "turnaround",
        TransmitWaitCause.TransmitInhibit => "inhibit",
        _ => null,
    };

    /// <summary>The same badge text from a logged row's two columns.</summary>
    internal static string? HeldCause(string? cause, int? busySubChannel) => cause switch
    {
        "busy" => busySubChannel is int sub
            ? $"busy ch{sub.ToString(CultureInfo.InvariantCulture)}"
            : "busy",
        "ourtx" => "our tx",
        "queue" => "other link",
        "slot" => "backoff",
        "turnaround" => "turnaround",
        "inhibit" => "inhibit",
        _ => null,
    };

    private void OnFrameTransmitted(int subChannel, byte[] frame, Channel.TransmitReport report)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        string? from = null;
        string? to = null;
        if (Ax25AddressParser.TryParse(frame, out string source, out string destination))
        {
            from = source;
            to = destination.Length > 0 ? destination : null;
        }

        BroadcastFrame(
            subChannel,
            // The catalogue identity, matching the spelling receive rows carry in
            // FrameQuality.Mode: a diversity bank's branch count is receiver construction,
            // not a property of the transmission (issue #343).
            _channel.Modems.TryGetValue(subChannel, out IModem? modem)
                ? ModeNames.Identity(modem.Mode)
                : "?",
            from, to, frame.Length, snrDb: null, burstLines: null, offsetHz: null,
            corrected: null, crc: null, transmitted: true,
            txTrimHz: report.TrimHz == 0 ? null : report.TrimHz,
            // Only where it is worth a note. A frame that went straight out carries nothing,
            // so an ordinary row draws exactly as it did before this existed.
            heldMs: report.HeldFor >= HeldWorthShowing ? (long)report.HeldFor.TotalMilliseconds : null,
            raw: frame,
            heldCause: report.HeldFor >= HeldWorthShowing ? HeldCause(report.Waits) : null);

        ObserveLink(
            subChannel, frame, transmitted: true,
            heldMs: report.HeldFor >= HeldWorthShowing ? (long)report.HeldFor.TotalMilliseconds : null,
            heldCause: report.HeldFor >= HeldWorthShowing ? HeldCause(report.Waits) : null);
    }

    /// <summary>
    /// Reads a frame into <see cref="Links"/> and tells every browser what it meant for its
    /// link, alongside the flat <c>frame</c> row it has already been sent. Nothing for bytes that
    /// are not AX.25: the flat panel already lists those, and there is no link for them to be
    /// part of.
    /// </summary>
    private void ObserveLink(
        int subChannel, byte[] frame, bool transmitted, long? heldMs = null, string? heldCause = null)
    {
        lock (_stateLock)
        {
            Ax25LinkEvent? evt = Links.Observe(
                subChannel.ToString(CultureInfo.InvariantCulture), frame, _options.TimeProvider.GetUtcNow(), transmitted);
            if (evt is null || Links.Snapshot(evt.LinkId) is not { } link)
            {
                return;
            }

            Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "link",
                link = LinkJson(link, withRecent: false),
                @event = LinkEventJson(evt, heldMs, heldCause),
            }, Json));
        }
    }

    /// <summary>
    /// Gives up on the calls and hang-ups nothing has answered, and tells every browser about
    /// each one the same way a frame is told: the card as it now stands and the line for its
    /// feed. Under <see cref="_stateLock"/> for the same reason frames are, so a browser
    /// connecting sees each link either in its opening snapshot or in a broadcast, never both.
    /// </summary>
    private void ExpireLinks()
    {
        lock (_stateLock)
        {
            foreach (Ax25LinkEvent evt in Links.Expire(_options.TimeProvider.GetUtcNow()))
            {
                if (Links.Snapshot(evt.LinkId) is not { } link)
                {
                    continue;
                }

                Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "link",
                    link = LinkJson(link, withRecent: false),
                    @event = LinkEventJson(evt),
                }, Json));
            }
        }
    }

    /// <summary>
    /// Every link the observer holds, for a browser that has just connected: the cards it
    /// opens with, each with its own backlog. Null when nothing has been heard.
    /// </summary>
    private byte[]? BuildLinksMessage()
    {
        IReadOnlyList<Ax25LinkSnapshot> links = Links.Snapshot();
        if (links.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "links",
            links = links.Select(l => LinkJson(l, withRecent: true)),
        }, Json);
    }

    /// <summary>
    /// A link as the page draws its card: the pair, where it stands, what each side has sent,
    /// and the one thing wrong with it if anything is. Field names are short because there is
    /// one of these per frame on a busy channel.
    /// </summary>
    private static object LinkJson(Ax25LinkSnapshot link, bool withRecent) => new
    {
        id = link.Id,
        sub = int.TryParse(link.Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sub) ? sub : (int?)null,
        a = link.A.ToString(),
        b = link.B.ToString(),
        state = LinkStateName(link.State),
        inferred = link.Inferred ? true : (bool?)null,
        modulo = link.Modulo,
        first = link.FirstSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        last = link.LastSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ab = SideJson(link.AtoB),
        ba = SideJson(link.BtoA),
        concern = link.Concern,
        recent = withRecent ? link.Recent.Select(e => LinkEventJson(e)) : null,
    };

    private static object SideJson(Ax25LinkSideStats side) => new
    {
        frames = side.Frames,
        data = side.DataFrames,
        bytes = side.DataBytes,
        resends = side.Resends,
        polls = side.Polls,
        pollsOpen = side.PollsUnanswered,
        rejects = side.Rejects,
        callsOpen = side.CallsUnanswered,
        busy = side.Busy ? true : (bool?)null,
        awaiting = side.AwaitingAck,
    };

    /// <summary>
    /// One frame on a link, as one line of its card's feed. Or the observer giving up on a call
    /// nothing answered, which is a line with no frame behind it: <c>kind</c> is null then.
    /// </summary>
    private static object LinkEventJson(
        Ax25LinkEvent evt, long? heldMs = null, string? heldCause = null) => new
    {
        at = evt.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        from = evt.From.ToString(),
        to = evt.To.ToString(),
        via = evt.Via.Count == 0 ? null : evt.Via,
        kind = evt.FrameType?.Mnemonic(),
        cmd = evt.IsCommand,
        pf = evt.PollFinal ? true : (bool?)null,
        ns = evt.Ns,
        nr = evt.Nr,
        pid = evt.Pid,
        len = evt.InfoLength,
        text = evt.Text,
        say = evt.Narration,
        flags = LinkFlagNames(evt.Flags),
        count = evt.Count,
        state = LinkStateName(evt.State),
        tx = evt.Transmitted ? true : (bool?)null,
        // How long this frame waited for the channel, on our own transmissions only and only where
        // it was long enough to be worth a tag. It belongs on the link's own feed as much as on
        // the flat panel: a card showing a run of unanswered polls reads as a link that is
        // failing, and this is the line that says the polls never reached the air when the
        // sender thought they did. Absent on a replayed line - Ax25LinkEvent does not carry it,
        // so a card rebuilt from Recent shows the tag only on lines seen live.
        heldMs,
        // And what most of that wait was, in two or three words. A card of unanswered polls that
        // says "busy ch2" is a frequency somebody else is using; the same card saying "our tx" is
        // this station talking over its own turnaround, and they are fixed differently.
        heldCause,
    };

    private static string LinkStateName(Ax25LinkState state) => state switch
    {
        Ax25LinkState.Calling => "calling",
        Ax25LinkState.Connected => "connected",
        Ax25LinkState.Disconnecting => "disconnecting",
        Ax25LinkState.Disconnected => "disconnected",
        _ => "unconnected",
    };

    /// <summary>The set flags as lower-camel names ("resend", "poll", "linkUp"), or null for none.</summary>
    private static string[]? LinkFlagNames(Ax25LinkFlags flags)
    {
        if (flags == Ax25LinkFlags.None)
        {
            return null;
        }

        var names = new List<string>();
        foreach (Ax25LinkFlags flag in Enum.GetValues<Ax25LinkFlags>())
        {
            if (flag != Ax25LinkFlags.None && flags.HasFlag(flag))
            {
                string name = flag.ToString();
                names.Add(char.ToLowerInvariant(name[0]) + name[1..]);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Reports a frame heard by a demodulator that is not one of the channel's sub-channel
    /// modems - ARDOP, whose demodulator is inside the virtual TNC and never raises
    /// <see cref="SoundModemChannel.FrameReceivedWithQuality"/>.
    /// </summary>
    /// <remarks>
    /// Without this the panel is silently partial: the ARDOP band is drawn, its bursts paint the
    /// waterfall, and nothing is ever listed for it. SNR comes from the caller because the
    /// demodulator's own measurement is better than anything the band tracker can infer from a
    /// burst that overlaps the packet slots.
    /// </remarks>
    /// <param name="subChannel">The sub-channel the demodulator is filed under.</param>
    /// <param name="mode">The mode the row is labelled with - ARDOP's own frame type name.</param>
    /// <param name="from">The station sending, where the frame type names one.</param>
    /// <param name="to">The station addressed, where the frame type names one.</param>
    /// <param name="lengthBytes">The payload length the row reports.</param>
    /// <param name="snrDb">
    /// The demodulator's own signal-to-noise reading, where it has one to give: better than
    /// anything the band tracker can infer from a burst that overlaps the packet slots.
    /// </param>
    /// <param name="decodedOk">Whether the frame decoded cleanly, where the demodulator judges
    /// that.</param>
    /// <param name="quality">
    /// ARDOP's own 0-100 constellation quality for this frame, or null on a mode that does not
    /// report one on this scale (issue #479). Carried separately from <paramref name="snrDb"/>:
    /// quality is measured for every frame ARDOP decodes, while <paramref name="snrDb"/> is only
    /// ever passed by the caller for a Ping or a PingAck, and the two would otherwise be shown as
    /// if they answered the same question.
    /// </param>
    public void ReportFrame(
        int subChannel,
        string mode,
        string? from,
        string? to,
        int lengthBytes,
        double? snrDb,
        bool? decodedOk,
        int? quality = null)
    {
        if (_source is null)
        {
            return;   // not started; nothing to attribute the frame to and nobody to tell
        }

        BroadcastFrame(
            subChannel, mode, from, to, lengthBytes, snrDb,
            burstLines: null, offsetHz: null, corrected: null, crc: decodedOk, quality: quality);
    }

    /// <summary>
    /// Reports a frame sent by a transmitter that is not one of the channel's sub-channel modems -
    /// ARDOP, which transmits as a whole virtual TNC through the channel's audio path and so never
    /// raises <see cref="Channel.SoundModemChannel.FrameTransmittedWithTrim"/>.
    /// </summary>
    /// <remarks>
    /// The transmit half of <see cref="ReportFrame"/>, and listed the same way
    /// <see cref="OnFrameTransmitted"/> lists a modem's own frame: no SNR, offset, FEC count or
    /// CRC, because those are receive measurements and inventing them for our own transmission
    /// would be inventing a measurement of ourselves. What a station DOES know about its own
    /// burst is who it was to, which the caller states rather than the parser guessing: ARDOP
    /// carries the callsigns in its connect handshake and its ID frames, and nothing else it
    /// sends carries one at all. No transmit trim either - ARDOP owns its own centre.
    /// </remarks>
    /// <param name="subChannel">The sub-channel the transmitter is filed under.</param>
    /// <param name="mode">The mode the row is labelled with, as the receive rows spell it.</param>
    /// <param name="from">The station sending, where the frame type names one.</param>
    /// <param name="to">The station called, where the frame type names one.</param>
    /// <param name="lengthBytes">The payload length the row reports.</param>
    public void ReportTransmittedFrame(
        int subChannel, string mode, string? from, string? to, int lengthBytes)
    {
        if (_source is null)
        {
            return;   // not started; nothing to attribute the frame to and nobody to tell
        }

        BroadcastFrame(
            subChannel, mode, from, to, lengthBytes, snrDb: null, burstLines: null,
            offsetHz: null, corrected: null, crc: null, transmitted: true);
    }

    /// <summary>
    /// Reports a station identification heard by an <see cref="Modems.IdBeaconGhost"/> - the 300
    /// AFSK AX.25 ident a NinoTNC sends alongside, and not inside, its PSK SSB data mode.
    /// </summary>
    /// <remarks>
    /// <para>Listed and tagged like anything else, and marked as an ident so both can say what it
    /// is. What a ghost does not get is a <em>band</em>: it has no slot of its own to shade, being
    /// a second listener on a modem that is already drawn. The tag therefore lands against the
    /// band of the modem it accompanies, which is where the ident sits - a couple of hundred Hz
    /// above it.</para>
    /// <para>No SNR - a ghost has no <see cref="BandActivityTracker"/>, the trackers being keyed
    /// by sub-channel and it sharing the base modem's. The offset is real: the ghost's
    /// demodulator measures the carrier against its own centre, so it says how far the
    /// identifying station's dial sits from ours.</para>
    /// </remarks>
    /// <param name="subChannel">The sub-channel of the modem the ghost accompanies, for the label.</param>
    /// <param name="mode">The ghost's mode, as it reports itself.</param>
    /// <param name="from">The identifying station.</param>
    /// <param name="to">Its destination - <c>IDENT</c> on a NinoTNC.</param>
    /// <param name="lengthBytes">Frame length.</param>
    /// <param name="offsetHz">How far off our centre the ident arrived.</param>
    public void ReportIdBeacon(
        int subChannel, string mode, string? from, string? to, int lengthBytes, double? offsetHz = null)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        BroadcastFrame(
            subChannel, mode, from, to, lengthBytes, snrDb: null, burstLines: null,
            offsetHz: offsetHz is { } offset ? Math.Round(offset, 1) : null,
            corrected: null, crc: true, idBeacon: true);
    }

    /// <summary>
    /// The word a page is sent for a frame's level: <c>loud</c>, <c>quiet</c>, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>A mapping now, not a rule. The verdict is taken when the frame is decoded, by the
    /// modem's own limits (<see cref="Modems.IFrameSpanSource.FrameLevels"/>, measured in
    /// <c>docs/dev/receive-levels.md</c>), and arrives here on
    /// <see cref="Modems.FrameQuality.Level"/> or on a relayed row. This class no longer knows
    /// what a threshold is, which is the point: it used to look one up by mode name, and a name
    /// that did not match left every C4FSK frame taking the wrong pair.</para>
    /// <para><see cref="Audio.FrameLevel.Ok"/> and null both send nothing, because the page
    /// badges only the two verdicts worth acting on and has never had a third value to draw. The
    /// distinction between "measured and fine" and "not measured" is kept where it is needed -
    /// the frame log's column and the uplink's field, which is <see cref="Audio.FrameLevelText"/>
    /// - and dropped here.</para>
    /// </remarks>
    private static string? LevelTag(Audio.FrameLevel? level) => level switch
    {
        Audio.FrameLevel.Loud => "loud",
        Audio.FrameLevel.Quiet => "quiet",
        _ => null,
    };

    /// <summary>
    /// The figure a page is sent for a frame's level, or nothing where the mode it was heard on
    /// makes the number meaningless.
    /// </summary>
    /// <remarks>
    /// <para>A mapping, like <see cref="LevelTag"/> beside it, and for the same reason: the
    /// answer was reached by the modem that decoded the frame
    /// (<see cref="Audio.FrameLevelLimits.PeakWorthShowing"/>) and arrives here on
    /// <see cref="Modems.FrameQuality.PeakWorthShowing"/>, on a logged row or on a relayed one.
    /// This class does not know which modes those are and must not learn: the last thing here
    /// that knew was a lookup by mode name, and the name did not match.</para>
    /// <para>Tom, 2026-09-12: "On the SSB modes I'd be happy with just 'TOO LOUD' if it's
    /// actually too loud. And not showing the dBFS in SSB modes." On the sign-and-angle group a
    /// bit is decided by which side of zero a sample fell, so the figure has no action behind it
    /// at any value it can take; at the rail it has one, and that is the badge. <b>Only the row
    /// loses the number.</b> The frame log's <c>peak_dbfs</c> and the uplink's <c>peakDbFs</c>
    /// carry the measurement whatever the mode, because it is evidence and other tools read
    /// it.</para>
    /// <para>Null in <paramref name="peakWorthShowing"/> is "nothing said" and shows the figure:
    /// that is a relayed row from a station too old to answer, and a backlog row from before the
    /// column, both of which listed their figure before this existed.</para>
    /// </remarks>
    /// <param name="peakDbFs">The measured peak, or null where nothing measured one.</param>
    /// <param name="peakWorthShowing">What the deciding modem's limits said about showing it.</param>
    private static double? ShownPeak(double? peakDbFs, bool? peakWorthShowing) =>
        peakWorthShowing is false || peakDbFs is not { } peak ? null : Math.Round(peak, 1);

    /// <summary>
    /// The burst SNR a page is sent for a frame, or nothing where nothing checked the frame.
    /// </summary>
    /// <remarks>
    /// <para>The third mapping beside <see cref="LevelTag"/> and <see cref="ShownPeak"/>, and the
    /// same shape: the answer belongs to the decode
    /// (<see cref="Modems.DecodeStanding.SnrWorthShowing"/>) and arrives here on
    /// <see cref="Modems.FrameQuality.SnrWorthShowing"/>, on a live row or a relayed one.</para>
    /// <para>Why it is not simply drawn: this figure is in-band power over a rolling
    /// <em>minimum</em> noise floor taken off the display's own FFT lines, not a measurement of
    /// the frame. It cannot report below about 6 dB, because every line in it had to clear the
    /// burst gate at that ratio; its denominator is the quietest of thirty half-second blocks;
    /// and for two seconds after a run ends it repeats the previous burst's number verbatim
    /// (<see cref="BandActivityTracker"/>). Beside a verified frame it is a fair strength cue.
    /// Beside a frame nothing checked it is what made a fabricated row read as a real station at
    /// a plausible strength - and, through the carry-over, at the <em>same</em> strength as the
    /// real transmission just before it, which is exactly what was reported off air.</para>
    /// <para><b>Only the row loses the number.</b> The frame log's <c>snr_db</c> and the uplink's
    /// <c>snrDb</c> carry it whatever the reading was worth, because it is evidence and it is how
    /// this defect was measured in the first place. Null in
    /// <paramref name="snrWorthShowing"/> is "nothing said" and shows the figure: a relayed row
    /// from a station too old to answer listed its figure before this existed.</para>
    /// </remarks>
    /// <param name="snrDb">The measured burst SNR, or null where nothing measured one.</param>
    /// <param name="snrWorthShowing">What the decode said about showing it.</param>
    private static double? ShownSnr(double? snrDb, bool? snrWorthShowing) =>
        snrWorthShowing is false ? null : snrDb;

    // `raw` is the frame's own bytes, where the caller has them, and exists for the relay: a
    // monitor reads them into its own link observer rather than being sent a summary of them.
    // Nothing is sent to a browser that was not sent before - the panel already has everything it
    // draws. Null from the two public Report* entry points, whose callers decoded somewhere this
    // class cannot see and have no bytes to give.
    private void BroadcastFrame(
        int subChannel, string mode, string? from, string? to, int lengthBytes,
        double? snrDb, int? burstLines, double? offsetHz, int? corrected, bool? crc,
        bool idBeacon = false, bool transmitted = false,
        string? note = null, string? headerType = null, string? frameHex = null,
        bool plainIl2p = false, bool monitorOnly = false, double? txTrimHz = null,
        double? peakDbFs = null, bool? clipped = null, Audio.FrameLevel? level = null,
        bool? peakWorthShowing = null, bool? snrWorthShowing = null,
        int? trailerNearBits = null, int? chasedBits = null, byte[]? raw = null,
        int? quality = null, long? heldMs = null, string? heldCause = null)
    {
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "frame",
            line = _source!.NextLineIndex,
            sub = subChannel,
            mode,
            from,
            to,
            lenBytes = lengthBytes,
            // The band's reading of the burst, on the rows where the frame it is drawn beside was
            // checked by something. Absent where nothing checked it (ShownSnr): the figure is a
            // band measurement with a floor and a two-second carry-over, and beside such a frame
            // it is the thing that makes the row look like a real signal. The relayed copy below
            // keeps it either way - it is a measurement, and the far end's own page is not the
            // only thing that reads one.
            snrDb = ShownSnr(snrDb, snrWorthShowing),
            burstLines,
            offsetHz,
            corrected,
            crc,
            // True on an ident, null otherwise - same nullable-optional shape as the fields
            // above, and the page tests it for truthiness either way.
            id = idBeacon ? true : (bool?)null,
            // True on our own transmission: the page lists it and, unlike everything else,
            // does not tag it onto the waterfall (see OnFrameTransmitted).
            tx = transmitted ? true : (bool?)null,
            // How far this transmission was shifted to suit the station it was addressed to.
            // Null when it went out on the nominal centre, which is most of them.
            txTrimHz,
            // How long this transmission waited for the channel before it went out. Null on a
            // received frame - a station cannot know what the other end's channel access did -
            // and null on a transmission that went straight out, which is most of them on a
            // quiet channel. The page draws the note only where there is one.
            heldMs,
            // Which cause took most of that wait, in the two or three words the badge shows.
            // Null where no single cause took half of it, and the badge then says only how long.
            heldCause,
            // Only on a frame whose addresses would not read: why, which IL2P encapsulation
            // carried it, and the bytes themselves.
            why = note,
            il2p = headerType,
            hex = frameHex,
            // True on a frame read as plain IL2P - Reed-Solomon alone stood behind it - and null
            // otherwise, the same shape as id/tx above. Its own field rather than the page
            // inferring it from a null crc: crc is also null on our own transmissions, on HDLC
            // and on ARDOP, and a badge that says "no CRC checked" on a mode that has no CRC to
            // check would be noise on three paths to be right on one.
            plain = plainIl2p ? true : (bool?)null,
            // And whether it stopped here. Only meaningful alongside plain; the badge is the same
            // either way, because the frame is RS-only either way, but the operator reading it
            // wants to know which of the two things their configuration did.
            monitorOnly = monitorOnly ? true : (bool?)null,
            // How loud this frame's own audio was, in dBFS, and whether the card railed during
            // it. One decimal, like the meter's, because the card's own steps are whole dB and a
            // figure that twitches in the second decimal is one nobody can read a trend off.
            // Absent on a transmission, on a relayed row from a station too old to send it, on
            // any frame whose audio could not be placed, and on the modes whose deciding modem
            // says the number means nothing (ShownPeak) - a row without them draws as it always
            // did. The relayed copy below keeps the figure either way: it is a measurement, and
            // the far end's own page is not the only thing that reads one.
            peakDbFs = ShownPeak(peakDbFs, peakWorthShowing),
            clipped = clipped is true ? true : (bool?)null,
            // The verdict, made when the frame was decoded rather than here: the deciding
            // modem's own limits said so (IFrameSpanSource.FrameLevels, and
            // docs/dev/receive-levels.md for where each number was measured), and this only picks
            // the word for it. Absent is a frame with nothing to say about it, which is most of
            // them and is what "the level is fine" looks like.
            level = LevelTag(level),
            // ARDOP's own 0-100 constellation quality, on every frame it decodes; null on every
            // other mode, which reports nothing on this scale (#479).
            quality,
        }, Json);
        Broadcast(WebSocketMessageType.Text, message);

        if (LiveRelay is not { } relay)
        {
            return;
        }

        try
        {
            // Not gated on Wanted, unlike the audio: a frame is a few hundred bytes and they are
            // what makes a quiet band look alive to somebody arriving an hour later, so they go
            // up whether or not anybody is watching this second. What the far end does with one
            // it did not ask for is the far end's business.
            relay.Frame(new RelayedFrame
            {
                SubChannel = subChannel,
                Mode = mode,
                From = from,
                To = to,
                LengthBytes = lengthBytes,
                SnrDb = snrDb,
                BurstLines = burstLines,
                OffsetHz = offsetHz,
                CorrectedBytes = corrected,
                CrcValid = crc,
                IdBeacon = idBeacon,
                Transmitted = transmitted,
                TransmitTrimHz = txTrimHz,
                Note = note,
                HeaderType = headerType,
                FrameHex = frameHex,
                PlainIl2p = plainIl2p,
                MonitorOnly = monitorOnly,
                // And the two facts behind those, which a monitor writes into its own copy of
                // this station's log so that a row replayed out of it withholds the callsign the
                // live row withheld.
                TrailerNearBits = trailerNearBits,
                ChasedBits = chasedBits,
                PeakDbFs = peakDbFs,
                Clipped = clipped,
                // And what this station's own modem made of them, which a monitor cannot work out
                // for itself: it is not running the modem that decoded the frame. Without this a
                // relayed row arrives measured and unjudged, which is what a v0.60.x station
                // sends and is not what this one heard.
                Level = level,
                PeakWorthShowing = peakWorthShowing,
                // And what the decode said about the SNR beside it, for the same reason: the
                // monitor logs what this station measured and shows what this station's own page
                // shows, without holding a copy of a rule about a decode it did not make.
                SnrWorthShowing = snrWorthShowing,
                // ARDOP's own quality, carried the same way: a monitor keeps its own copy of the
                // station's frame log and this is a measurement, not a derived verdict (#479).
                Quality = quality,
                At = _options.TimeProvider.GetUtcNow(),
                Raw = raw,
            });
        }
        catch (Exception)
        {
            // One frame lost off the uplink. The panel has it, the log has it, the journal has
            // it, and the station has not noticed.
        }
    }

    /// <summary>
    /// Lists a frame that arrived over an uplink rather than out of this process's own decoder,
    /// and reads it into the links panel.
    /// </summary>
    /// <remarks>
    /// <para>The monitor's side of the seam, and the reason there is one at all: a relayed
    /// station's decodes are its own, made by its modems with its diversity settings on its
    /// antenna, and a monitor runs no modem for it. <see cref="SoundModemChannel"/>'s frame
    /// events cannot be raised from outside that class and it has no injection point, so this is
    /// the entry point instead - everything <c>OnFrame</c> does after the measurement, with the
    /// fields taken from the argument rather than from a channel event.</para>
    /// <para>The link is folded here, from <see cref="RelayedFrame.Raw"/>, rather than being sent
    /// as a message of its own: one <see cref="Ax25LinkObserver"/> implementation over the same
    /// bytes, so the cards cannot disagree with the station's, and the fold survives the station
    /// going off the air. A frame with no bytes - an ident ghost, or a decoder that hands none
    /// over - is listed and makes no link, exactly as it does on the station.</para>
    /// <para><b>A pushed frame is offered to <see cref="Relay"/> like any other</b>, because this
    /// is <see cref="BroadcastFrame"/> and that is what it does. Nothing is suppressed here and no
    /// flag says otherwise: on a monitor <see cref="Relay"/> is null, because a monitor never
    /// publishes and a station never accepts an uplink, and the daemon refuses a configuration
    /// that asks for both. A server given both anyway would forward the frame whole, bytes
    /// included, which is the less surprising of the two things to arrive at by accident.</para>
    /// <para><see cref="RelayedFrame.At"/> is not read here. It is the station's own clock, and it
    /// is on the wire for the frame log and for holding a frame until the audio that carried it
    /// has been painted, both of which are the caller's business. The row a browser is sent
    /// carries no time at all - the page stamps a live row with its own - and the burst tag comes
    /// from this display's line count rather than from the station's.</para>
    /// </remarks>
    /// <param name="frame">The frame, as the uplink carried it.</param>
    public void PushFrame(RelayedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_source is null)
        {
            return;   // not started; nothing to tag the frame onto and nobody to tell
        }

        BroadcastFrame(
            frame.SubChannel, frame.Mode, frame.From, frame.To, frame.LengthBytes,
            frame.SnrDb, frame.BurstLines, frame.OffsetHz, frame.CorrectedBytes, frame.CrcValid,
            idBeacon: frame.IdBeacon, transmitted: frame.Transmitted,
            note: frame.Note, headerType: frame.HeaderType, frameHex: frame.FrameHex,
            plainIl2p: frame.PlainIl2p, monitorOnly: frame.MonitorOnly,
            txTrimHz: frame.TransmitTrimHz,
            // Both null from a station running a version that does not send them, which is what
            // the wire's tolerance of their absence buys: the row lists as it always did.
            peakDbFs: frame.PeakDbFs, clipped: frame.Clipped, level: frame.Level,
            peakWorthShowing: frame.PeakWorthShowing, snrWorthShowing: frame.SnrWorthShowing,
            trailerNearBits: frame.TrailerNearBits, chasedBits: frame.ChasedBits,
            raw: frame.Raw, quality: frame.Quality);

        // The same rule OnFrame applies, for the same reason: a frame Reed-Solomon alone stood
        // behind is not evidence that the pair of callsigns in it were ever talking, so it is
        // listed and makes no card.
        if (frame.Raw is { Length: > 0 } raw && !frame.MonitorOnly)
        {
            ObserveLink(frame.SubChannel, raw, frame.Transmitted);
        }
    }

    /// <summary>How many audio blocks a second at <see cref="AudioBlockMilliseconds"/> each.</summary>
    private const int AudioBlocksPerSecond = 1000 / AudioBlockMilliseconds;

    /// <summary>
    /// Audio block length. Short enough that a browser can start playing promptly and that a
    /// dropped block is a click rather than a gap; long enough not to spend the whole budget on
    /// WebSocket framing.
    /// </summary>
    private const int AudioBlockMilliseconds = 40;

    /// <summary>
    /// Type byte plus padding to a 4-byte boundary, so the samples that follow can be viewed
    /// directly as 16-bit (or later 32-bit) values without copying.
    /// </summary>
    internal const int AudioHeaderBytes = 4;

    private void TryApplyClientRequest(WaterfallClient client, ReadOnlySpan<byte> utf8)
    {
        try
        {
            using JsonDocument request = JsonDocument.Parse(utf8.ToArray());
            if (!request.RootElement.TryGetProperty("type", out JsonElement type))
            {
                return;
            }

            // TX test: the operator asking this station to key up. Handled apart from the two
            // toggles below because it is not a per-client preference - it puts a signal on the
            // air, so it is the station's business and every open page hears the outcome.
            if (type.GetString() == "txtest")
            {
                ApplyTxTestRequest(request.RootElement, client);
                return;
            }

            if (request.RootElement.TryGetProperty("on", out JsonElement on))
            {
                switch (type.GetString())
                {
                    case "audio":
                        client.AudioEnabled = on.ValueKind == JsonValueKind.True;
                        break;
                    case "spectrum":
                        client.SpectrumEnabled = on.ValueKind == JsonValueKind.True;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A browser sending nonsense loses nothing but its own request.
        }
    }

    // ---------------------------------------------------------------- TX test
    /// <summary>
    /// Whether a WebSocket handshake may be accepted, given where the page that opened it came
    /// from. Only asked of a station that has a transmit control installed.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Browsers do not apply the same-origin policy to WebSockets
    /// and there is no preflight, so without this any page the operator's browser happens to load
    /// can open a socket to this station and key the transmitter. The default bind is loopback,
    /// which does not help at all: the attacking page runs in the operator's own browser, on the
    /// operator's own machine. A station serving its page to the shack over the LAN is reachable
    /// from anything on it.</para>
    /// <para><b>Why an origin check is enough, and why it is not applied everywhere.</b> A
    /// browser sets <c>Origin</c> itself and script cannot change it, so a page from somewhere
    /// else cannot pretend to be this one. A non-browser client - curl, a script, the test suite -
    /// sends no <c>Origin</c> at all, and is left alone: it is not the thing being defended
    /// against, and it has no ambient credentials to be abused. And the check is only applied
    /// where there is something to defend: a public page and a relayed one carry no transmit
    /// control, and they are the pages most likely to sit behind a tunnel or a proxy that could
    /// make a legitimate origin and host disagree.</para>
    /// <para>Public rather than internal because the daemon asks the same question of a keyless
    /// <c>POST /api/mixer</c> ("waterfall"."enableAudioControls"), and one rule with two
    /// implementations is one rule that will drift.</para>
    /// </remarks>
    public static bool OriginMayKey(PageOrigin from)
    {
        if (string.IsNullOrEmpty(from.Declared))
        {
            // Not a browser: curl, a script, the test suite. Nothing to defend against - there
            // are no ambient credentials for somebody else's page to borrow - and refusing these
            // would break every machine client for no gain.
            return true;
        }

        // Compared on the host and port a browser would have connected to, so http and https,
        // and a page served under a path base, all compare equal. A malformed Origin is not a
        // browser's, and is refused rather than parsed generously.
        return Uri.TryCreate(from.Declared, UriKind.Absolute, out Uri? declared)
            && (string.Equals(declared.Authority, from.Served, StringComparison.OrdinalIgnoreCase)
                || string.Equals(declared.Host, from.Served, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One <c>txtest</c> message from the operator's page: start a test, or stop the one running.
    /// </summary>
    /// <remarks>
    /// Nothing is decided here. Whether the station may transmit at all, how long for and at what
    /// level are the daemon's to say - this only reads the request and hands it over, so that a
    /// page cannot ask for anything the CLI switch could not ask for too.
    /// </remarks>
    private void ApplyTxTestRequest(JsonElement request, WaterfallClient client)
    {
        if (_txTest is not { } control)
        {
            // No control installed: a public page, a relayed one, or a station with no
            // transmitter. Silently ignored - there is nobody to refuse on behalf of.
            return;
        }

        if (!OriginMayKey(client.From))
        {
            // Judged here rather than at the handshake, for two reasons. The control is installed
            // several seconds after the listener opens - the sound card and the PTT line come
            // first - so a socket admitted during start-up would never be checked again, and a
            // page retrying in a background tab catches that window on every restart. And
            // refusing the upgrade would cost a station behind a Host-rewriting proxy its whole
            // page, waterfall and frames and all, where this costs it only the button.
            RefuseForeignOrigin(client);
            return;
        }

        if (request.TryGetProperty("stop", out JsonElement stop) && stop.ValueKind == JsonValueKind.True)
        {
            control.Stop();
            return;
        }

        bool twoTone = !request.TryGetProperty("twoTone", out JsonElement pair)
            || pair.ValueKind != JsonValueKind.False;
        double toneHz = request.TryGetProperty("toneHz", out JsonElement tone)
            && tone.ValueKind == JsonValueKind.Number
                ? tone.GetDouble()
                : control.LowToneHz;
        double seconds = request.TryGetProperty("seconds", out JsonElement length)
            && length.ValueKind == JsonValueKind.Number
                ? length.GetDouble()
                : control.DefaultSeconds;

        control.Start(new TxTestRequest(twoTone, toneHz, seconds));
    }

    private TxTestControl? _txTest;

    /// <summary>
    /// How often a refused origin reaches the journal. The refusal is free to send: anything that
    /// can reach the port can send a <c>txtest</c> message as fast as it likes, and one line each
    /// would push everything else the station has to say out of the journal. Same interval and
    /// same wording as the daemon's transmit-drop suppressor, for the same reason.
    /// </summary>
    private static readonly TimeSpan OriginRefusalInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _originGate = new();
    private long _originRefusedAt;
    private int _originRefusalsSuppressed;
    private bool _originRefusedBefore;

    /// <summary>
    /// Turns away a transmit test asked for by a page this station did not serve: the page is
    /// told, every time, and the journal is told at most once a minute.
    /// </summary>
    /// <remarks>
    /// <para><b>The page is always told.</b> A silent refusal is indistinguishable from a broken
    /// button, and the operator most likely to meet one is not an attacker at all - it is a
    /// station behind a proxy that rewrites <c>Host</c>, whose own page is refused and who has
    /// nothing to go on. Sent to the one client that asked rather than broadcast: the others have
    /// not been refused anything and a status is not theirs to see.</para>
    /// <para><b>The journal is rate limited</b>, because the message costs the sender nothing.</para>
    /// </remarks>
    private void RefuseForeignOrigin(WaterfallClient client)
    {
        string declared = Printable(client.From.Declared);
        string served = Printable(client.From.Served);

        client.Queue.Writer.TryWrite((WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "txtest",
            state = "refused",
            text = $"refused, this page was served from {declared} and this station serves its "
                + $"page as {served} - only a page it served may key it",
        }, Json)));

        bool say;
        int suppressed = 0;
        lock (_originGate)
        {
            long now = _options.TimeProvider.GetTimestamp();
            say = !_originRefusedBefore
                || _options.TimeProvider.GetElapsedTime(_originRefusedAt, now) >= OriginRefusalInterval;
            if (say)
            {
                _originRefusedBefore = true;
                _originRefusedAt = now;
                suppressed = _originRefusalsSuppressed;
                _originRefusalsSuppressed = 0;
            }
            else
            {
                _originRefusalsSuppressed++;
            }
        }

        if (say)
        {
            _options.Log?.Invoke(
                $"waterfall: refused a transmit test from a page at {declared} - this station "
                + $"serves its page as {served}, and a page from somewhere else may not key the "
                + "radio. If a proxy in front of this station rewrites the Host header, make it "
                + "pass the original through."
                + (suppressed > 0 ? $" (and {suppressed} more like it in the last minute)" : ""));
        }
    }

    /// <summary>
    /// A header value as it may be printed: plain ASCII, and short.
    /// </summary>
    /// <remarks>
    /// <c>Origin</c> arrives from whoever opened the socket, and this line goes to the journal.
    /// journalctl's pager renders anything above 0x7F as hex escapes under a C locale, and a
    /// header long enough to fill a screen would bury the sentence explaining it - neither is
    /// something the sender should get to choose.
    /// </remarks>
    private static string Printable(string header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return "(none)";
        }

        var clean = new System.Text.StringBuilder(header.Length);
        foreach (char c in header)
        {
            clean.Append(c is >= ' ' and <= '~' ? c : '?');
            if (clean.Length == 80)
            {
                clean.Append("...");
                break;
            }
        }

        return clean.ToString();
    }

    /// <summary>
    /// Installs (or removes) the operator's transmitter test after the server was built.
    /// </summary>
    /// <remarks>
    /// The daemon has to: whether the station can key at all is settled by opening the sound card
    /// and the PTT line, which happens long after the page is being served. Same shape as
    /// <see cref="SetRadioStatus"/> - the config message is rebuilt so the next browser is offered
    /// the current answer - and, like it, this is never called on a public or relayed page.
    /// </remarks>
    public void SetTxTest(TxTestControl? control)
    {
        _txTest = control;
        if (_source is not null)
        {
            _configMessage = BuildConfigMessage(); // before Start, Start's own build picks it up
        }
    }

    /// <summary>
    /// Tells every open page what became of a test transmission. Called by whoever ran it, on its
    /// own thread; a page that arrives afterwards is not told, because a test is an event rather
    /// than a state and the frames panel already carries the record of one that happened.
    /// </summary>
    public void ReportTxTest(TxTestStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "txtest",
            state = status.State,
            text = status.Text,
        }, Json));
    }

    /// <summary>
    /// Lists a test transmission in the frames panel, so that a keyup carrying tones rather than
    /// a frame is not an unexplained burst on the waterfall - here, and on the public monitor of
    /// a station that publishes to one, which is where somebody else watching would otherwise
    /// see a signal nothing accounts for.
    /// </summary>
    /// <remarks>
    /// A frame row, deliberately, rather than a kind of its own: the panel, the frame log, the
    /// backlog a browser opens on and the uplink all already carry transmitted rows, and the one
    /// thing this needs that a frame does not have - what the test actually was - is the note
    /// field an unattributable frame already uses. Nothing new crosses the wire.
    /// </remarks>
    /// <param name="subChannel">The modem the row is filed under; see the daemon's TxTest.</param>
    /// <param name="text">What was sent, in the journal's own wording.</param>
    /// <param name="lengthBytes">The length of the record the frame log keeps for it.</param>
    public void ReportTestTransmission(int subChannel, string text, int lengthBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        BroadcastFrame(
            subChannel, TestTransmissionMode, from: null, to: null, lengthBytes,
            snrDb: null, burstLines: null, offsetHz: null, corrected: null, crc: null,
            transmitted: true, note: text);
    }

    /// <summary>
    /// The mode string a test transmission is filed under, in the panel and in the frame log. Not
    /// a modem and not in the catalogue: it renders as "TX test", it sorts with the station's own
    /// transmissions, and a query for what a modem sent never picks one up by accident.
    /// </summary>
    public const string TestTransmissionMode = "tx-test";

    /// <summary>
    /// The mode string a Morse station identification is filed under, in the panel and in the
    /// frame log. A sibling of <see cref="TestTransmissionMode"/> and for the same reasons: an
    /// ident is a keyup carrying no frame, it renders as "CW ident", and a query for what a modem
    /// sent never picks one up by accident.
    /// </summary>
    public const string IdentTransmissionMode = "cw-ident";

    /// <summary>
    /// Receive audio to whoever asked for it, as [0x02][s16 LE mono] at the channel rate.
    /// </summary>
    /// <remarks>
    /// Nothing is received while transmitting - the channel gates its receive tap on half
    /// duplex - so the stream simply stops for the length of a keyup. The browser hears that as
    /// silence, which is what it is, rather than as a glitch or a desync: blocks carry no
    /// timestamps and are played in arrival order, so a gap costs nothing to recover from.
    /// </remarks>
    private void BroadcastAudio(ReadOnlySpan<float> samples)
    {
        // Plain loops under the lock: this runs per received audio block on the receive
        // thread for the life of the daemon, and the LINQ forms allocated an enumerator and
        // a closure per block while holding the clients lock.
        bool wanted = false;
        lock (_clientsLock)
        {
            foreach (WaterfallClient client in _clients)
            {
                if (client.AudioEnabled)
                {
                    wanted = true;
                    break;
                }
            }
        }

        if (!wanted)
        {
            // Do not accumulate for nobody, and do not carry a stale half-block into the moment
            // somebody starts listening.
            _audioBlock.Clear();
            return;
        }

        int blockSamples = _channel.SampleRate * AudioBlockMilliseconds / 1000;
        foreach (float sample in samples)
        {
            _audioBlock.Add(Audio.Pcm16.FromFloat(sample));
        }

        while (_audioBlock.Count >= blockSamples)
        {
            // 4-byte header, not 1: a browser's Int16Array view needs its byte offset aligned
            // to the element size, and samples starting at byte 1 threw RangeError on arrival.
            var message = new byte[AudioHeaderBytes + (blockSamples * 2)];
            message[0] = 0x02;
            for (int i = 0; i < blockSamples; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    message.AsSpan(AudioHeaderBytes + (i * 2)), _audioBlock[i]);
            }

            _audioBlock.RemoveRange(0, blockSamples);
            lock (_clientsLock)
            {
                foreach (WaterfallClient listener in _clients)
                {
                    if (listener.AudioEnabled)
                    {
                        listener.Queue.Writer.TryWrite((WebSocketMessageType.Binary, message));
                    }
                }
            }
        }
    }

    /// <summary>
    /// One block of audio exactly as the sound card delivered it, for the level meter's clip
    /// indicator. Called before any resampling; does nothing on a page that has no meter.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the host has to hand this over separately.</b> The peak and the RMS come off
    /// the channel's receive tap, which on a 48 kHz card is the decimator's output. Clipping
    /// cannot be judged there: the filter's ripple moves peaks either way, so a signal with real
    /// headroom at the converter can leave the decimator at full scale and light the indicator.
    /// On the bench CM108 that was five intervals in seventeen, on a signal that never railed
    /// (radio1, 2026-09-06). Only the card's own samples can answer it, and only the host has
    /// them.</para>
    /// <para>Call it with the block the device returned, before the decimator, and not while the
    /// station is keyed - our own transmission is not a measurement of what the card is hearing.
    /// Called on the receive thread, like the tap, and subject to the same rules: it returns
    /// promptly, allocates nothing, and does nothing at all while no page is open.</para>
    /// </remarks>
    /// <param name="samples">The block as the device delivered it, at the card's own rate.</param>
    public void MeterInputClipping(ReadOnlySpan<float> samples)
    {
        if (_levelMeter is Audio.InputLevelMeter meter && _viewerCount > 0)
        {
            meter.AddCardSamples(samples);
        }
    }

    /// <summary>
    /// The level of the audio just in from the sound card, sent to every open page five times a
    /// second, so an operator moving the capture gain can see where the level is landing.
    /// </summary>
    /// <remarks>
    /// <para><b>Free when nobody is watching</b>, which is the same rule the audio relay keeps:
    /// with no viewers the meter is reset and nothing is accumulated, so the cost of the feature
    /// on a station serving nobody is one volatile read per audio block. The reset matters as
    /// much as the skip - a half-interval kept across a gap of hours would be handed to the next
    /// viewer as their first reading, showing them a peak from whenever the last one left.</para>
    /// <para><b>Sent rather than subscribed to.</b> It is under 60 bytes five times a second, so
    /// a request/cancel protocol would cost more code than it could ever save bytes; and a page
    /// that opens the Mixer group late (the group appears on the answer to its own probe of
    /// <c>/api/mixer</c>) then has a meter already reading rather than one that starts blank.
    /// Every page that gets this message is an operator's own page: the server only offers it
    /// when the daemon asked for it, and the daemon asks only for a station with a sound card.
    /// </para>
    /// </remarks>
    private void MeterInput(ReadOnlySpan<float> samples)
    {
        if (_levelMeter is not Audio.InputLevelMeter meter)
        {
            return;
        }

        if (_viewerCount == 0)
        {
            meter.Reset();
            return;
        }

        meter.Add(samples);
        if (!meter.TryTake(out Audio.InputLevel level))
        {
            return;
        }

        // One decimal place. The card's own steps are whole dB on every revision seen so far, and
        // a bar that twitches in the second decimal is a bar nobody can read a trend off.
        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "level",
            peak = Math.Round(level.PeakDbFs, 1),
            rms = Math.Round(level.RmsDbFs, 1),
            clip = level.Clipped,
        }, Json));
    }

    private void Broadcast(WebSocketMessageType kind, byte[] payload)
    {
        lock (_clientsLock)
        {
            foreach (WaterfallClient client in _clients)
            {
                client.Queue.Writer.TryWrite((kind, payload));
            }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await HttpListenerLifetime.AcceptAsync(listener, _stopping.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = ServeAsync(context);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        // Our own listener carries nothing but us, so everything on it is under our base, which
        // is the whole site: the one request TryServeAsync declines here is one HttpListener
        // could not give a URL for, which nothing this server serves could have answered anyway.
        if (!await TryServeAsync(context, RootBase).ConfigureAwait(false))
        {
            NotFound(context);
        }
    }

    /// <summary>The base a server that is a site of its own serves under: the whole site.</summary>
    internal const string RootBase = "/";

    /// <summary>
    /// Serves one request that arrived on somebody else's listener, under
    /// <paramref name="pathBase"/> - so that a router can put several receivers' pages on one
    /// port, each under its own prefix.
    /// </summary>
    /// <remarks>
    /// The routes are the ones this server has always had, read relative to the base: the page at
    /// the base itself and at <c>index.html</c> and <c>links</c>, the captures under
    /// <c>survey/</c>, the API and metrics where they are configured, and a WebSocket upgrade at
    /// any path under the base. The base is the router's to know rather than this server's, so
    /// nothing here has to be told twice or kept in step.
    /// </remarks>
    /// <param name="context">The request, which this method answers and closes.</param>
    /// <param name="pathBase">The prefix this server is served under; "/" is the whole site. It
    /// starts and ends with a slash.</param>
    /// <returns>
    /// True if the request was under the base and has been answered (with a 404 of its own if it
    /// named nothing this server serves). False if it was not, in which case nothing has been
    /// written to the response and the caller still owns it.
    /// </returns>
    public async Task<bool> TryServeAsync(HttpListenerContext context, string pathBase)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePathBase(pathBase, nameof(pathBase));
        if (!TryStripBase(context.Request.Url?.AbsolutePath, pathBase, out string requestPath))
        {
            return false;
        }

        try
        {
            // The local channel audio stream (issue #584). Checked by its own path, ahead of the
            // page's own WebSocket below, which otherwise upgrades anything: this one is for a
            // program on the same machine, never a browser, so it is refused rather than served
            // the moment the local-program check fails. No key: loopback, no Origin and no proxy
            // in between is the whole of its authentication.
            if (requestPath == ChannelAudioStream.Path && context.Request.IsWebSocketRequest)
            {
                if (await RefusedUnlessLocalProgramAsync(context, "channel-audio", "the channel audio stream")
                        .ConfigureAwait(false))
                {
                    return true;
                }

                string remoteDescription = context.Request.RemoteEndPoint?.ToString() ?? "a client";
                HttpListenerWebSocketContext streamUpgrade =
                    await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await _channelAudioStream.ServeAsync(
                        streamUpgrade.WebSocket, remoteDescription, _options.DialFrequencyHz, _stopping.Token)
                    .ConfigureAwait(false);
                return true;
            }

            if (context.Request.IsWebSocketRequest)
            {
                // Where this page says it came from, and where it reached us. Recorded now and
                // judged later, per message: see ApplyTxTestRequest.
                var from = new PageOrigin(
                    context.Request.Headers["Origin"] ?? "", context.Request.UserHostName ?? "");
                HttpListenerWebSocketContext upgrade = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await ServeWebSocketAsync(upgrade.WebSocket, from).ConfigureAwait(false);
                return true;
            }

            // Anything under /api/ belongs to whoever installed the handler, not to the
            // waterfall. The seam is here rather than in this class because the meaning of those
            // requests is the daemon's - configuration, validation, the station's own restart -
            // and this library knows nothing about any of that. Unhandled falls through to 404,
            // so a station with no handler installed serves exactly what it always did.
            if (ApiHandler is { } api
                && requestPath.StartsWith("/api/", StringComparison.Ordinal)
                && await api(context, requestPath).ConfigureAwait(false))
            {
                return true;
            }

            // Metrics, unauthenticated by design: a scraper is a machine on a schedule and every
            // monitoring system in use expects to GET a URL and get text. What is served is
            // callsigns and signal reports, which were transmitted in the clear on a shared
            // channel - the same facts the waterfall page already shows anyone who opens it.
            // Off unless a station asks for it (see the daemon's "metrics" config).
            if (context.Request.HttpMethod == "GET"
                && Metrics is { } metrics
                && requestPath is "/metrics" or "/metrics/frames")
            {
                bool frames = requestPath.EndsWith("frames", StringComparison.Ordinal);
                byte[] body = System.Text.Encoding.UTF8.GetBytes(
                    frames ? metrics.LineProtocol() : metrics.Exposition());

                // The version parameter is what a Prometheus scraper negotiates on, and getting
                // it wrong is a scrape that silently parses as nothing.
                context.Response.ContentType = frames
                    ? "text/plain; charset=utf-8"
                    : "text/plain; version=0.0.4; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                context.Response.Close();
                return true;
            }

            // /links is the same page: it reads its own path and opens as the links pane alone,
            // for a window an operator has torn off the waterfall.
            if (context.Request.HttpMethod == "GET" &&
                requestPath is "/" or "/index.html" or "/links")
            {
                byte[] page = Page.Value.Bytes;
                context.Response.ContentType = "text/html; charset=utf-8";
                // The page carries no version in its URL and the daemon served it with no
                // freshness information at all - no Cache-Control, no ETag, no Last-Modified -
                // which leaves a browser free to cache it heuristically for as long as it likes.
                // The result is a station that has been upgraded and a browser that has not: the
                // page keeps whatever markup it fetched first, the newer server sends it fields
                // its elements do not exist for, and the missing readouts fail silently because
                // every setter starts with a null check. That is indistinguishable from a broken
                // feature, and it was reported as one - a transmit power and SWR readout that
                // appeared on a phone and never on a long-lived desktop tab.
                context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                context.Response.ContentLength64 = page.Length;
                await context.Response.OutputStream.WriteAsync(page).ConfigureAwait(false);
                context.Response.Close();
                return true;
            }

            if (context.Request.HttpMethod == "GET"
                && requestPath.StartsWith("/survey/", StringComparison.Ordinal)
                && TryResolveCapture(requestPath["/survey/".Length..], out string file))
            {
                context.Response.ContentType = file.EndsWith(".wav", StringComparison.Ordinal)
                    ? "audio/wav"
                    : "application/json";
                using (FileStream capture = File.OpenRead(file))
                {
                    context.Response.ContentLength64 = capture.Length;
                    await capture.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                }

                context.Response.Close();
                return true;
            }

            NotFound(context);
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }

        return true;
    }

    /// <summary>
    /// Strips the base a server is served under off a request path, so the routes below it can be
    /// compared against the paths this server has always known. "/" is the whole site and strips
    /// nothing at all, which is what keeps a station that is its own site byte-identical to what
    /// it was. False means the request is not under this base and is not this server's business.
    /// </summary>
    internal static bool TryStripBase(string? absolutePath, string pathBase, out string path)
    {
        if (absolutePath is null)
        {
            // A request HttpListener could not give a URL for is under nobody's base.
            path = "";
            return false;
        }

        if (pathBase == RootBase)
        {
            path = absolutePath;
            return true;
        }

        if (absolutePath.StartsWith(pathBase, StringComparison.Ordinal))
        {
            // Cut before the base's own trailing slash, so what is left starts with one: the base
            // itself becomes "/" and /r/x/ws becomes /ws.
            path = absolutePath[(pathBase.Length - 1)..];
            return true;
        }

        path = "";
        return false;
    }

    /// <summary>
    /// The shape of a path base, checked wherever one is accepted: it starts and ends with a
    /// slash, and holds nothing but lower-case letters, digits, hyphens and slashes.
    /// </summary>
    /// <remarks>
    /// The character rule is the plan's slug rule, and it is here so that matching a base against
    /// a request path can stay a plain string comparison. A base with anything else in it would
    /// have to be compared as a browser sends it, percent-encoded, upper and lower case, and a
    /// prefix that matched a page's URL but not its socket's would be a receiver that loads and
    /// never connects.
    /// </remarks>
    internal static void ValidatePathBase(string pathBase, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathBase, parameterName);
        if (!pathBase.StartsWith('/') || !pathBase.EndsWith('/'))
        {
            throw new ArgumentException(
                $"a path base starts and ends with a slash: \"{pathBase}\"", parameterName);
        }

        foreach (char c in pathBase)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '/'))
            {
                throw new ArgumentException(
                    $"a path base holds only lower-case letters, digits, hyphens and slashes: \"{pathBase}\"",
                    parameterName);
            }
        }
    }

    private static void NotFound(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Asks every page whether it is still there, and stops counting the ones that have not said
    /// anything for <see cref="KeepAliveSilence"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole of #411. A page socket whose far end vanished without a close
    /// frame used to be waited on for ever: the receive had no deadline but the server's own
    /// shutdown, and behind a Cloudflare tunnel TCP never fails either, because the server's peer
    /// is a healthy local <c>cloudflared</c> rather than the browser. So a phone that went to
    /// sleep held the viewer count above zero, an on-demand receiver never lingered, and #409's
    /// two receivers were retried at their 300 s cap all night for nobody.</para>
    /// <para>The connection is dropped rather than closed politely. A peer that has not answered
    /// three keep-alives will not complete a close handshake either, and a polite close is
    /// another unbounded wait on the same dead connection. Cancelling the token its receive is
    /// waiting on ends that wait at once, and the receive loop's finally does the uncounting
    /// exactly as it does for a page that said goodbye - same removal, same
    /// <see cref="ViewersChanged"/>.</para>
    /// </remarks>
    private void SweepKeepAlive()
    {
        long now = _options.TimeProvider.GetTimestamp();
        bool ask = _options.TimeProvider.GetElapsedTime(Interlocked.Read(ref _pingedAt))
            >= KeepAlivePing;
        List<WaterfallClient>? silent = null;
        lock (_clientsLock)
        {
            foreach (WaterfallClient client in _clients)
            {
                if (_options.TimeProvider.GetElapsedTime(client.HeardAt) >= KeepAliveSilence)
                {
                    client.DroppedForSilence = true;
                    (silent ??= []).Add(client);
                }
                else if (ask)
                {
                    client.Queue.Writer.TryWrite((WebSocketMessageType.Text, KeepAliveMessage));
                }
            }
        }

        if (ask)
        {
            Interlocked.Exchange(ref _pingedAt, now);
        }

        if (silent is null)
        {
            return;
        }

        // Outside the lock: ending a client's wait releases its receive loop, whose finally takes
        // this same lock to uncount it.
        foreach (WaterfallClient client in silent)
        {
            try
            {
                client.Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It left by another route between the list being taken and this line; its own
                // finally has already uncounted it.
            }
            catch (Exception e)
            {
                // Cancel runs the registrations the receive, the send and the queue left on this
                // token, and wraps anything they throw in an AggregateException rather than
                // swallowing it. Whatever it was belongs to that one connection; an exception out
                // of a timer callback is unhandled on a pool thread and would end the station,
                // and would abandon the rest of this sweep on the way out. The client is still
                // marked and still silent, so the next sweep tries again five seconds later.
                Journal($"page: could not drop a viewer that stopped answering "
                    + $"({e.GetType().Name}); trying again shortly");
            }
        }
    }

    private async Task ServeWebSocketAsync(WebSocket socket, PageOrigin from)
    {
        // Bounded per-client queue, oldest lines dropped: a stalled browser loses history,
        // never stalls the receive thread or other clients.
        var queue = System.Threading.Channels.Channel.CreateBounded<(WebSocketMessageType, byte[])>(
            // Deep enough to hold a second of waterfall lines plus a second of audio blocks;
            // audio that arrives late is worse than audio dropped, so the queue stays shallow.
            new BoundedChannelOptions(_options.LinesPerSecond + AudioBlocksPerSecond)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        // This connection's own stop, so the keep-alive can end this page without touching any
        // other, and so that stopping the server still stops them all.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var client = new WaterfallClient
        {
            Queue = queue,
            Stop = stop,
            // Arriving counts as being heard from: a page has the whole silence deadline to
            // answer its first keep-alive, however long the handshake below takes.
            HeardAt = _options.TimeProvider.GetTimestamp(),
            From = from,
        };

        // The retained state a browser opens with - what the last transmission did, and who is
        // attached to which KISS port - read at the instant this client joins the broadcast list,
        // and under the lock the setters hold while they broadcast. Read either side of that
        // instant instead and a change racing the handshake arrives twice or not at all.
        byte[]? transmit;
        byte[]? hosts;
        byte[]? links;
        int viewers;
        lock (_stateLock)
        {
            transmit = _transmitMessage;
            hosts = _hostPortsMessage;
            links = BuildLinksMessage();
            lock (_clientsLock)
            {
                _clients.Add(client);
                viewers = _clients.Count;
                _viewerCount = viewers;
            }
        }

        RaiseViewersChanged(viewers);

        try
        {
            // txTest.running is the one field of the config that changes on every keyup, not on
            // the rare events (start-up, a radio-status change, a receiver change, a control
            // being installed) that rebuild the cached _configMessage - so the cache answers a
            // joining page with whatever it was at the last unrelated rebuild. Review round 1
            // reproduced both directions: a page connecting during a live test was told
            // running: false, and a radio-status change that happened to land while a test was
            // running baked running: true into the cache for every page that joined afterwards,
            // for the life of the daemon, whether or not a test was still going. Rebuilt fresh
            // here instead, only when a TX test control actually exists - an operator's own page,
            // effectively never more than a tab or two connecting at once - so a public page or a
            // station with a hundred monitor viewers keeps paying nothing for a field it is never
            // even sent.
            byte[] configMessage = _txTest is not null && _source is not null
                ? BuildConfigMessage()
                : _configMessage;
            await socket.SendAsync(configMessage, WebSocketMessageType.Text, true, stop.Token)
                .ConfigureAwait(false);
            if (_surveyMessage is { } survey)
            {
                await socket.SendAsync(survey, WebSocketMessageType.Text, true, stop.Token)
                    .ConfigureAwait(false);
            }

            if (transmit is not null)
            {
                await socket.SendAsync(transmit, WebSocketMessageType.Text, true, stop.Token)
                    .ConfigureAwait(false);
            }

            if (hosts is not null)
            {
                await socket.SendAsync(hosts, WebSocketMessageType.Text, true, stop.Token)
                    .ConfigureAwait(false);
            }

            if (BuildHistoryMessage() is { } history)
            {
                await socket.SendAsync(history, WebSocketMessageType.Text, true, stop.Token)
                    .ConfigureAwait(false);
            }

            if (links is not null)
            {
                await socket.SendAsync(links, WebSocketMessageType.Text, true, stop.Token)
                    .ConfigureAwait(false);
            }

            Task send = SendLoopAsync(socket, queue, stop.Token);
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                WebSocketReceiveResult received =
                    await socket.ReceiveAsync(buffer, stop.Token).ConfigureAwait(false);

                // Anything at all is proof this page is still there - the keep-alive answer, a
                // request to turn audio or the waterfall on or off, or its goodbye.
                client.HeardAt = _options.TimeProvider.GetTimestamp();
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // What a browser asks for: audio on or off, waterfall lines on or off.
                if (received.MessageType == WebSocketMessageType.Text && received.Count > 0)
                {
                    TryApplyClientRequest(client, buffer.AsSpan(0, received.Count));
                }
            }

            queue.Writer.TryComplete();
            await send.ConfigureAwait(false);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A vanished client is normal shutdown for its connection, nothing more.
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
                viewers = _clients.Count;
                _viewerCount = viewers;
            }

            // Before the count is announced, so that the journal reads in the order things
            // happened: the page went, and then the receiver was told how many are left.
            if (client.DroppedForSilence)
            {
                Journal($"page: viewer dropped, no reply for "
                    + $"{KeepAliveSilence.TotalSeconds:F0} s, {viewers} "
                    + $"viewer{(viewers == 1 ? "" : "s")}");
            }

            // Straight after the count changed, as on the way in, and before the tidying up: a
            // watcher that reads Viewers and a watcher that listens for the event should not be
            // able to disagree for as long as it takes to dispose a socket. The invocation stays
            // outside the lock, because a subscriber's work is not this lock's business.
            RaiseViewersChanged(viewers);
            queue.Writer.TryComplete();
            socket.Dispose();
        }
    }

    /// <summary>
    /// One line to whoever owns this server's journal, tagged by them. A sink that throws costs
    /// the line and nothing else - this is called from a finally, where an exception would take
    /// the tidying up with it.
    /// </summary>
    /// <summary>
    /// Refuses, with a plain 403 and one journal line, a request for something meant only for a
    /// program on this machine (see <see cref="ChannelAudioStream.LocalProgramRefusal"/>), and
    /// says whether it did. The one check every such endpoint uses, so they cannot drift apart.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="journalTag">The journal line's prefix, e.g. <c>channel-audio</c>.</param>
    /// <param name="what">What was asked for, in the refusal's own words.</param>
    /// <returns>True when the request was refused and answered.</returns>
    internal async Task<bool> RefusedUnlessLocalProgramAsync(HttpListenerContext context, string journalTag, string what)
    {
        string? refusal = ChannelAudioStream.LocalProgramRefusal(
            context.Request.RemoteEndPoint?.Address, context.Request.Headers);
        if (refusal is null)
        {
            return false;
        }

        Journal($"{journalTag}: refused {context.Request.RemoteEndPoint} - {refusal}");
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/plain; charset=utf-8";
        byte[] reason = System.Text.Encoding.UTF8.GetBytes($"{what} is for a program on this machine only");
        context.Response.ContentLength64 = reason.Length;
        await context.Response.OutputStream.WriteAsync(reason).ConfigureAwait(false);
        context.Response.Close();
        return true;
    }

    private void Journal(string line)
    {
        try
        {
            _options.Log?.Invoke(line);
        }
        catch (Exception)
        {
            // A journal that will not take a line is not this connection's problem.
        }
    }

    /// <summary>
    /// A subscriber's fault stays its own: a viewer count is advisory, and a throw here would
    /// otherwise fault the page's connection (on attach) or be lost in a finally (on detach).
    /// </summary>
    private void RaiseViewersChanged(int viewers)
    {
        try
        {
            ViewersChanged?.Invoke(viewers);
        }
        catch (Exception)
        {
            // The count will be right again on the next change.
        }
    }

    /// <summary>How much of the station's log a browser opens with. Half the panel's own
    /// 100-row cap: enough to say what the channel has been doing, and not so much that the
    /// backlog pushes out live traffic before the operator has read any.</summary>
    private const int HistoryFrames = 50;

    /// <summary>
    /// The decoded-frames panel's opening backlog, or null when there is no log to draw it from.
    /// Sent once per connection, straight after the config and before the send loop starts, so
    /// it can never be interleaved with - or land on top of - live frames decoded meanwhile.
    /// </summary>
    /// <remarks>
    /// Oldest first: the page prepends each row, so the newest ends up on top, and a live frame
    /// arriving during the handshake is queued behind this and lands above it. Marked
    /// <c>hist</c>, so the page lists it without tagging it onto the waterfall - these frames
    /// were heard before the scroll on screen began and belong to no burst on it.
    /// </remarks>
    private byte[]? BuildHistoryMessage()
    {
        if (_options.FrameHistory is not { } source)
        {
            return null;
        }

        IReadOnlyList<LoggedFrame> frames;
        try
        {
            frames = source(HistoryFrames);
        }
        catch (Exception)
        {
            // A log that cannot be read costs this browser its backlog and nothing else. The
            // station is still decoding, and the panel still fills from the air.
            return null;
        }

        if (frames.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "history",
            frames = frames.Select(f => new
            {
                at = f.HeardAt.ToUniversalTime().ToString("O"),
                sub = f.SubChannel,
                mode = f.Mode,
                // The same verdict the live row took, read back off the same columns: a row whose
                // reading nothing checked and whose bits the chase moved names nobody here either.
                // A browser that reloaded the page used to get those callsigns back.
                from = f.CallsignWorthShowing ? f.From : null,
                to = f.CallsignWorthShowing ? f.To : null,
                lenBytes = f.LengthBytes,
                offsetHz = f.OffsetHz is { } offset ? Math.Round(offset, 1) : (double?)null,
                corrected = f.CorrectedBytes,
                crc = f.CrcValid,
                // Same nullable-optional shape as a live frame's, so the page's one row builder
                // badges a logged transmission TX exactly as it badges a live one.
                tx = f.Transmitted ? true : (bool?)null,
                txTrimHz = f.TxTrimHz is { } trim ? Math.Round(trim, 1) : (double?)null,
                // Both under the names a live frame uses, so the page's one row builder badges a
                // replayed RS-only frame exactly as it badged it when it was heard - the badge
                // off plain, the tooltip's wording off monitorOnly. A backlog row that dropped
                // them lost the badge outright, which read as a frame something had checked.
                plain = f.PlainIl2p ? true : (bool?)null,
                monitorOnly = f.MonitorOnly ? true : (bool?)null,
                // Under the same names, and with the verdict and the show-the-figure answer
                // read back out of the log rather than worked out again, so a replayed row
                // carries what it carried when it was heard. Null on a row logged before the
                // columns existed, and on every transmission, and the page then shows nothing
                // new on it.
                peakDbFs = ShownPeak(f.PeakDbFs, f.PeakWorthShowing),
                clipped = f.Clipped is true ? true : (bool?)null,
                level = LevelTag(f.Level),
                // And how long it waited for the channel, so a page that has just been reloaded says
                // the same thing about a transmission as the live row did. Null on a received
                // row, on a transmission that went straight out, and on one logged before the
                // column existed.
                heldMs = f.HeldMs is { } held && held >= HeldWorthShowing.TotalMilliseconds
                    ? held
                    : (long?)null,
                // And what held it, read back out of the log rather than worked out again, so a
                // replayed transmission says what the live row said. Null on a row logged before
                // the columns existed, which draws the note with no cause on it as it always did.
                heldCause = f.HeldMs >= HeldWorthShowing.TotalMilliseconds
                    ? HeldCause(f.HeldCause, f.HeldBusySubChannel)
                    : null,
                hist = true,
            }),
        }, Json);
    }

    /// <summary>Writes what a page is owed, in order, until its queue ends or it stops.</summary>
    /// <param name="socket">The page's socket.</param>
    /// <param name="queue">What it is owed.</param>
    /// <param name="stopping">This connection's own stop, so that a page the keep-alive has given
    /// up on stops being written to as well as stops being waited on.</param>
    private async Task SendLoopAsync(
        WebSocket socket,
        Channel<(WebSocketMessageType Kind, byte[] Payload)> queue,
        CancellationToken stopping)
    {
        try
        {
            await foreach ((WebSocketMessageType kind, byte[] payload) in
                queue.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                await socket.SendAsync(payload, kind, true, stopping).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Resolves a capture file name to a path inside the survey directory, or refuses.
    /// </summary>
    /// <remarks>
    /// Only the exact shape the writer produces - <c>20260804-151909-862hz-unclaimed.wav</c> - is
    /// served, and only from the configured directory. A name is not a path: anything carrying a
    /// separator, a drive or a <c>..</c> is refused before it reaches the filesystem, so this
    /// route cannot be talked into reading the frame log, a key, or /etc/passwd.
    /// </remarks>
    private bool TryResolveCapture(string name, out string file)
    {
        file = "";
        if (_surveyDirectory.Length == 0 || name.Length is 0 or > 128)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
            {
                return false;
            }
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || (!name.EndsWith(".wav", StringComparison.Ordinal)
                && !name.EndsWith(".json", StringComparison.Ordinal)))
        {
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(_surveyDirectory, name));
        string root = Path.GetFullPath(_surveyDirectory);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(candidate))
        {
            return false;
        }

        file = candidate;
        return true;
    }

    /// <summary>
    /// Reports a survey capture - a burst this station could not read, kept for later.
    /// </summary>
    /// <remarks>
    /// <para>Drawn where it happened. A capture has a frequency, a width and a time, which is
    /// exactly what this display's two axes are, so "something we could not read went past
    /// <em>there</em>" is a statement the waterfall can make and a list of filenames cannot.</para>
    /// <para><b>Placed by age, not by line index.</b> The survey runs its own spectrum feed so it
    /// keeps working on a station with nobody watching, and its line clock is therefore not this
    /// one's. Seconds-ago is a quantity both agree on.</para>
    /// </remarks>
    /// <param name="verdict">Why it was kept - <c>unclaimed</c>, <c>missed</c>, <c>unattributed</c>.</param>
    /// <param name="centreHz">Measured audio centre.</param>
    /// <param name="lowHz">Measured low edge.</param>
    /// <param name="highHz">Measured high edge.</param>
    /// <param name="durationSeconds">How long the burst lasted.</param>
    /// <param name="snrDb">Peak SNR over the noise floor.</param>
    /// <param name="secondsAgo">How long ago it ended.</param>
    /// <param name="file">Its audio file name, for the download link.</param>
    public void ReportCapture(
        string verdict, double centreHz, double lowHz, double highHz,
        double durationSeconds, double snrDb, double secondsAgo, string file)
    {
        if (_source is null)
        {
            return;   // not started; nobody to tell
        }

        Broadcast(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "capture",
            verdict,
            centreHz = Math.Round(centreHz, 1),
            lowHz = Math.Round(lowHz, 1),
            highHz = Math.Round(highHz, 1),
            durationSeconds = Math.Round(durationSeconds, 2),
            snrDb = Math.Round(snrDb, 1),
            secondsAgo = Math.Round(secondsAgo, 2),
            file,
        }, Json));
    }

    /// <summary>
    /// The page as served, and the version written into it. The version is a hash of the page's
    /// own text, so it changes with every edit and never has to be remembered by anyone.
    /// </summary>
    /// <remarks>
    /// The config message carries the same version, and the page compares the two: a tab that
    /// loaded the page before an upgrade and kept its socket reconnecting afterwards is running
    /// the old script against the new daemon, and reloads itself once it hears the mismatch. The
    /// no-cache header on the page stops a browser serving a stale copy on the next navigation;
    /// this covers the tab that never navigates, which is how a waterfall is normally left. The
    /// links pane was reported empty on the main page and fine at /links, which is the same page
    /// in a fresh window; a tab from before the upgrade is the one explanation found that fits.
    /// </remarks>
    private static readonly Lazy<(byte[] Bytes, string Version)> Page =
        new(() => EmbeddedPage.Load("waterfall.html"));

    /// <summary>Stops listening and drops every client.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _channel.FrameReceivedWithQuality -= OnFrame;
        _channel.FrameTransmittedWithReport -= OnFrameTransmitted;
        _channel.TransmittedAudio -= OnTransmittedAudio;
        _channel.TransmittingChanged -= OnTransmittingChanged;
        _channel.KeyedReceiveBlock -= _channelAudioStream.OnKeyedBlock;
        _channel.ReceiveAudioLost -= _channelAudioStream.OnInputLost;
        _channelAudioStream.Shutdown();
        _linkExpiry?.Dispose();
        _linkExpiry = null;
        _keepAlive?.Dispose();
        _keepAlive = null;
        lock (_transmitLock)
        {
            _transmitPacer?.Dispose();
            _transmitPacer = null;
            _transmitPending.Clear();
            Volatile.Write(ref _transmitPendingSamples, 0);
            _transmitPendingOffset = 0;
        }

        // Never throws for a browser that has walked away, and never leaves the accept loop
        // below waiting on a listener that failed to finish its own clean-up.
        if (_listener is { } listener)
        {
            HttpListenerLifetime.Shut(listener);
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                client.Queue.Writer.TryComplete();
            }

            _clients.Clear();
            _viewerCount = 0;
        }

        _stopping.Dispose();
    }
}
