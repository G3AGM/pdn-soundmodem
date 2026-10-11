namespace Packet.SoundModem.Daemon;

/// <summary>
/// One kind of device the <c>device</c> setting can name: the prefix it is recognised by, how it
/// is spelled in <c>--help</c>, and the facts about it that the configuration checks need from
/// the device string alone, before anything is opened.
/// </summary>
internal abstract class DeviceKind
{
    /// <summary>A short name for the kind, for logs and tests.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// How the kind is spelled under <c>--device</c> in <c>--help</c>; <c>UsageTests</c> and
    /// <c>DeviceKindTests</c> hold the two to each other.
    /// </summary>
    public abstract string Spelling { get; }

    /// <summary>Whether <paramref name="device"/> names a device of this kind.</summary>
    public abstract bool Matches(string device);

    /// <summary>Makes the device <paramref name="device"/> names.</summary>
    /// <exception cref="InvalidDataException">The string is this kind's but malformed, in words
    /// for the operator.</exception>
    public abstract StationDevice Create(string device, DeviceSettings settings);

    /// <summary>Whether a device of this kind is a sound card with a mixer.</summary>
    public virtual bool HasMixer => false;

    /// <summary>
    /// Whether the daemon owns the radio <paramref name="device"/> names, and so sets its dial and
    /// filters itself; see <see cref="StationDevice.OwnsTheRadio"/>. Answered from the string
    /// alone, for the settings read before the device is made.
    /// </summary>
    public virtual bool OwnsTheRadio(string device) => false;

    /// <summary>What the mailcast receiver's placement is told a station on
    /// <paramref name="device"/> is.</summary>
    public virtual MailcastRadioKind MailcastKindOf(string device) => MailcastRadioKind.SoundCard;

    /// <summary>Why a bench transmit test (<c>--two-tone</c>, <c>--tone</c>) is refused on a
    /// device of this kind, or null where it can transmit.</summary>
    public virtual string? TransmitTestRefusal => null;

    /// <summary>Why a <c>"rig"</c> section is refused on <paramref name="device"/>, or null
    /// where a rig is taken.</summary>
    public virtual string? RigRefusal(string device) => null;

    /// <summary>Why a <c>"publish"</c> section is refused on <paramref name="device"/>, or null
    /// where a station may publish.</summary>
    public virtual string? PublishRefusal(string device) => null;
}

/// <summary>
/// Every kind of device the daemon can open, and the one place a device string is matched to its
/// kind. A new kind is a <see cref="DeviceKind"/> and a <see cref="StationDevice"/> registered
/// here, ahead of the sound card, which takes whatever no other kind claims.
/// </summary>
internal static class DeviceKinds
{
    /// <summary>A sound card: the default, and so last.</summary>
    public static readonly DeviceKind Alsa = new AlsaDeviceKind();

    /// <summary>Two named pipes standing in for a sound card and a radio.</summary>
    public static readonly DeviceKind Pipe = new PipeDeviceKind();

    /// <summary>An UberSDR web receiver, receive only.</summary>
    public static readonly DeviceKind UberSdr = new UberSdrDeviceKind();

    /// <summary>An OpenWebRX or OpenWebRX+ web receiver's audio, receive only.</summary>
    public static readonly DeviceKind OpenWebRx = new OpenWebRxDeviceKind();

    /// <summary>A FlexRadio's DAX audio and slice PTT.</summary>
    public static readonly DeviceKind Flex = new FlexDeviceKind();

    /// <summary>Every kind, in the order a device string is tried against them; the sound card
    /// is last because it claims everything.</summary>
    public static IReadOnlyList<DeviceKind> All { get; } = [Pipe, UberSdr, OpenWebRx, Flex, Alsa];

    /// <summary>The kind <paramref name="device"/> names.</summary>
    public static DeviceKind Of(string device)
    {
        foreach (DeviceKind kind in All)
        {
            if (kind.Matches(device))
            {
                return kind;
            }
        }

        return Alsa;
    }

    /// <summary>
    /// The device a station runs on: the kind <paramref name="device"/> names, or a recording
    /// replayed in its place when <c>--wav-loop</c> gives one.
    /// </summary>
    /// <param name="device">The <c>device</c> setting.</param>
    /// <param name="wavLoopPath">The <c>--wav-loop</c> recording, or null.</param>
    /// <param name="settings">The settings the device is made with.</param>
    /// <exception cref="InvalidDataException">The device string is malformed, in words for the
    /// operator.</exception>
    public static StationDevice Resolve(string device, string? wavLoopPath, DeviceSettings settings)
    {
        StationDevice named = Of(device).Create(device, settings);
        return wavLoopPath is null ? named : new WavLoopStationDevice(named, wavLoopPath);
    }
}
