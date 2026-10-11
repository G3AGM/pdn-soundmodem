using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Rig;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The audio device a station runs on, as start-up sees it: what it can and cannot do, and how
/// to open it. One subclass per kind of device, each owning its parse, its refusals and its open,
/// so the daemon's start-up asks the device rather than testing the device string at every turn.
/// </summary>
/// <remarks>
/// <para>Made by <see cref="DeviceKinds.Resolve"/> from the <c>device</c> setting. Every kind
/// surfaces as the same <see cref="IAudioInput"/>, <see cref="IAudioOutput"/> and
/// <see cref="IPttControl"/> the channel already speaks, so KISS packet, POCSAG paging and ARDOP
/// get every transport for free.</para>
/// <para>The defaults here are an ordinary sound card's: it uses the capture rate, tunes nothing,
/// transmits, takes a PTT line, and has no radio-side filter the daemon can see.</para>
/// </remarks>
internal abstract class StationDevice
{
    /// <param name="kind">The kind that made this device.</param>
    /// <param name="spec">The device string it was made from.</param>
    protected StationDevice(DeviceKind kind, string spec)
    {
        Kind = kind;
        Spec = spec;
    }

    /// <summary>The kind that made this device.</summary>
    public DeviceKind Kind { get; }

    /// <summary>The device string, as the operator wrote it.</summary>
    public string Spec { get; }

    /// <summary>
    /// Whether <c>--capture-rate</c> applies, and so has to be a multiple of the channel's rate.
    /// False for a device that brings its own sample clock.
    /// </summary>
    public virtual bool CaptureRateApplies => true;

    /// <summary>
    /// Whether the daemon owns the radio outright, and so sets its dial and its filters itself.
    /// True only for a headless Flex: anywhere else something else owns them, and setting them
    /// would be fighting it.
    /// </summary>
    public virtual bool OwnsTheRadio => false;

    /// <summary>
    /// Whether the station points the receiver at the band plan's dial itself, rather than an
    /// operator setting a dial for it to be read off.
    /// </summary>
    public virtual bool SelfTunes => false;

    /// <summary>
    /// What to say when this device has no receive dial to tune to, or null for a device that
    /// does not need one.
    /// </summary>
    public virtual string? NoReceiveDialRefusal => null;

    /// <summary>
    /// Why this station can receive and never transmit, or null for one that can transmit.
    /// </summary>
    public virtual string? ReceiveOnlyReason => null;

    /// <summary>
    /// Why a <c>--ptt</c> or a <c>"ptt"</c> section is refused on this device, or null where one
    /// is taken.
    /// </summary>
    public virtual string? PttRefusal => null;

    /// <summary>
    /// Whether the radio's transmit filter is visible to the daemon, so that where the modems sit
    /// is worth measuring against it.
    /// </summary>
    public virtual bool ReportsTransmitFilter => false;

    /// <summary>What the mailcast receiver's placement is told this station is.</summary>
    public virtual MailcastRadioKind MailcastKind => Kind.MailcastKindOf(Spec);

    /// <summary>Which family of dead-feed defaults this device's input takes.</summary>
    public virtual DeadFeedDevice DeadFeedKind => DeadFeedDevice.Alsa;

    /// <summary>
    /// Whether the device's own runtime closes its streams, so the daemon must not dispose the
    /// input, output and PTT itself at shutdown.
    /// </summary>
    public virtual bool ClosesItsOwnStreams => false;

    /// <summary>
    /// A refusal that can be made from the device and the configuration alone, before any band
    /// plan is worked out, or null if there is none.
    /// </summary>
    public virtual string? SettingsProblem => null;

    /// <summary>
    /// Opens the device: its input, its output and its PTT, plus whatever the rest of start-up
    /// needs from the kind that opened them.
    /// </summary>
    /// <returns>The opened device, or one carrying the exit code start-up stops with.</returns>
    public abstract Task<DeviceOpening> OpenAsync(DeviceOpenContext context);
}

/// <summary>
/// The settings a device is made with, from the configuration file or the command line.
/// </summary>
/// <param name="UberSdr">The <c>"ubersdr"</c> section, or null.</param>
/// <param name="HasWaterfall">Whether the station serves a waterfall page.</param>
/// <param name="OpenWebRx">The <c>"openwebrx"</c> section, or null.</param>
internal sealed record DeviceSettings(UberSdrConfig? UberSdr, bool HasWaterfall, OpenWebRxConfig? OpenWebRx = null);

/// <summary>
/// What a device needs from the rest of start-up to open itself. Settled by the time the device
/// is opened: the channel rate, the band plan's answers, the radio-side tuning and the PTT.
/// </summary>
internal sealed record DeviceOpenContext
{
    /// <summary>The channel's DSP rate.</summary>
    public required int DspRate { get; init; }

    /// <summary>The configuration file, for the diagnostics that name it; null on a station
    /// configured on the command line alone.</summary>
    public required string? ConfigPath { get; init; }

    /// <summary>The station's journal.</summary>
    public required StationJournal Journal { get; init; }

    /// <summary>The channel the device is opened for.</summary>
    public required SoundModemChannel Channel { get; init; }

    /// <summary>The waterfall page, or null when there is none.</summary>
    public required WaterfallWebServer? Waterfall { get; init; }

    /// <summary>
    /// Called when the radio is lost for good: the station stops, and exits 1 so the unit
    /// restarts and tries afresh.
    /// </summary>
    public required Action RadioLost { get; init; }

    /// <summary>The station's lifetime.</summary>
    public required CancellationToken Cancellation { get; init; }

    /// <summary>The sideband the station runs on: the band plan's, else the configured one.</summary>
    public required string Sideband { get; init; }

    /// <summary>Where a self-tuning receiver points, or null where nothing said.</summary>
    public required double? ReceiveDialHz { get; init; }

    /// <summary>DAX-RX reorder-ring depth for a Flex.</summary>
    public required int FlexPacketBuffer { get; init; }

    /// <summary>The Flex slice tuning, as the band plan and the configuration settled it.</summary>
    public required FlexTuning FlexTuning { get; init; }

    /// <summary>Where each modem sits in the audio band, measured against the radio's filters
    /// where it has filters the daemon can see.</summary>
    public required IReadOnlyList<TransmitFilterPlan.Band> TransmitBands { get; init; }

    /// <summary>The sound card's capture and playback rate.</summary>
    public required int CaptureRate { get; init; }

    /// <summary>The <c>"captureDevice"</c> setting, or null where <c>"device"</c> is both.</summary>
    public required string? CaptureDeviceKey { get; init; }

    /// <summary>The <c>"playbackDevice"</c> setting, or null where <c>"device"</c> is both.</summary>
    public required string? PlaybackDeviceKey { get; init; }

    /// <summary>The <c>"alsa"</c> section, or null.</summary>
    public required AlsaConfig? Alsa { get; init; }

    /// <summary>How the radio is keyed, or null for no PTT.</summary>
    public required PttConfig? Ptt { get; init; }

    /// <summary>The rig, where there is a <c>"rig"</c> section.</summary>
    public required RigControl? Rig { get; init; }
}

/// <summary>
/// An opened device: the triple the channel speaks, and what the rest of start-up reads off the
/// kind that opened it. Or, when the open was refused, only the exit code.
/// </summary>
internal sealed class DeviceOpening
{
    /// <summary>What a station with no sound card says when asked for its mixer.</summary>
    public const string NoMixer = "this station has no sound card, so it has no mixer";

    /// <summary>The exit code start-up stops with, or null when the device opened.</summary>
    public int? ExitCode { get; private init; }

    /// <summary>The receive audio.</summary>
    public IAudioInput Input { get; init; } = null!;

    /// <summary>The transmit audio.</summary>
    public IAudioOutput Playback { get; init; } = null!;

    /// <summary>The keying.</summary>
    public IPttControl Ptt { get; init; } = null!;

    /// <summary>The Flex runtime, on a Flex.</summary>
    public FlexRuntime? Flex { get; init; }

    /// <summary>The radio's transmit meters, on a Flex that has them.</summary>
    public M0LTE.Flex.FlexMeters? FlexMeters { get; init; }

    /// <summary>Whether the input has a session to be starved of, on a web receiver. Null for
    /// every other device: their quiet is never deliberate.</summary>
    public Func<bool>? SessionLive { get; init; }

    /// <summary>
    /// The receiver's own tuning, on a device the station tunes through the device itself rather
    /// than through a rig: today an OpenWebRX receiver. Null everywhere else. Nothing at start-up
    /// uses it once the device is open; it is here so that whatever later wants to move the dial
    /// (#585's receive window) asks the device, and does not have to know which kind it is.
    /// </summary>
    public Packet.SoundModem.Rig.IReceiverTuner? Tuner { get; init; }

    /// <summary>The sound card's playback stream, on ALSA.</summary>
    public AlsaAudioOutput? AlsaOut { get; init; }

    /// <summary>The sound card's capture stream, on ALSA.</summary>
    public AlsaAudioInput? AlsaIn { get; init; }

    /// <summary>The card's mixer, on ALSA.</summary>
    public AlsaMixer? Mixer { get; init; }

    /// <summary>The transmit card's mixer, on a split ALSA station.</summary>
    public AlsaMixer? PlaybackMixer { get; init; }

    /// <summary>The mixer's runtime, on ALSA with a mixer.</summary>
    public MixerRuntime? MixerRuntime { get; init; }

    /// <summary>Why there is no mixer, for the page to say so.</summary>
    public string MixerWhyNot { get; init; } = NoMixer;

    /// <summary>A refused open: start-up stops with <paramref name="exitCode"/>.</summary>
    public static DeviceOpening Refused(int exitCode) => new() { ExitCode = exitCode };
}
