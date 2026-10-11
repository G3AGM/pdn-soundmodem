namespace Packet.SoundModem.Daemon;

/// <summary>
/// Operator-facing explanations for a device the configuration names but the machine does not
/// have (or will not let the service user open).
/// </summary>
/// <remarks>
/// This is the message a fresh install produces: the seeded config names a sound card and a
/// CM108 PTT that almost certainly do not match the box, so the very first thing an admin sees
/// in <c>journalctl</c> is one of these. It has to name the setting, name the file to edit, and
/// give the command that lists what is actually present.
///
/// Unlike a malformed config these do NOT exit 2, because they are not always the operator's
/// mistake - a USB interface may simply not have enumerated yet at boot. The service keeps
/// restarting so it comes up on its own once the hardware appears.
/// </remarks>
internal static class DeviceDiagnostics
{
    private const string Retry =
        "  The service will keep retrying: this clears by itself if the device is merely late\n"
        + "  appearing (a USB interface can still be enumerating at boot).";

    /// <summary>
    /// Where the setting lives - the config file if there is one, else the CLI flag. On its own
    /// line: a full /etc path plus prose wraps mid-sentence in the journal and reads badly.
    /// </summary>
    private static string Source(string? configPath, string key, string flag) =>
        configPath is null
            ? $"  Set by `{flag}` on the command line."
            : $"  Set by \"{key}\" in {configPath}";

    internal static string Audio(string device, string? configPath, Exception error) =>
        Audio(device, configPath, error, "device", split: false);

    /// <summary>
    /// <see cref="Audio(string, string?, Exception)"/> naming the key that chose the device, for
    /// a station that receives on one card and transmits through another: an operator with two
    /// cards has to be told which of them would not open.
    /// </summary>
    /// <param name="device">The device that would not open.</param>
    /// <param name="configPath">The config file, or null for a command-line station.</param>
    /// <param name="error">What the open threw.</param>
    /// <param name="key"><c>device</c>, <c>captureDevice</c> or <c>playbackDevice</c>.</param>
    /// <param name="split">Whether the station receives and transmits on different devices, so
    /// that <c>device</c> is only one direction's and must not be described as both.</param>
    /// <param name="capture">Which direction failed, when <paramref name="split"/> is set.</param>
    internal static string Audio(
        string device, string? configPath, Exception error, string key, bool split,
        bool capture = false) =>
        $"""
        cannot open the sound device "{device}"
          {error.Message}

        {Source(configPath, key, "--device")}
          {(!split ? "This is the ALSA device used for both capture and playback."
              : capture ? "This is the ALSA device the station receives from."
              : "This is the ALSA device the station transmits through.")} List what this
          machine actually has:
            aplay -l ; arecord -l ; aplay -L
          Prefer a stable name such as plughw:CARD=Device,DEV=0 over plughw:1,0 - card
          numbers move when devices are re-plugged.
        {Retry}
        """;

    /// <summary>
    /// An UberSDR instance that would not stream. Same "keep retrying" treatment as a sound card:
    /// a web receiver is reachable over the internet, so it can be rebooting, saturated with
    /// listeners, or simply behind a link that is down for a minute - none of which is the
    /// operator's mistake and all of which clear on their own.
    /// </summary>
    internal static string UberSdr(string device, string? configPath, Exception error) =>
        $"""
        cannot stream IQ from "{device}"
          {error.Message}

        {Source(configPath, "device", "--device")}
          This names a public UberSDR web receiver, which the station listens to instead of a
          sound card. Check it is up and serving IQ:
            curl -s https://<instance>/api/description | head -c 400
          The address is the one you would open in a browser - the whole URL works as well as
          the bare host. Session limits, IQ mode and any password live in the "ubersdr" section.
        {Retry}
        """;

    /// <summary>
    /// An OpenWebRX receiver that would not stream, or cannot serve this station's dial. Same
    /// "keep retrying" treatment as an UberSDR, for the same reasons; a dial its band does not
    /// reach can also clear on its own, when somebody moves the receiver back.
    /// </summary>
    internal static string OpenWebRx(string device, string? configPath, Exception error) =>
        $"""
        cannot listen through "{device}"
          {error.Message}

        {Source(configPath, "device", "--device")}
          This names a public OpenWebRX web receiver, which the station listens to instead of a
          sound card. Check it is up by opening the same address in a browser. The address is
          the one you would open there, scheme, port and path included. The profile to ask for
          and the passband live in the "openwebrx" section.
        {Retry}
        """;

    /// <summary>
    /// A FlexRadio that would not open a session. Same "keep retrying" treatment as a sound
    /// card, and for the same reason: at a site power-up the modem host routinely boots
    /// faster than the radio, and a network blip is nobody's configuration mistake.
    /// </summary>
    internal static string Flex(string device, string? configPath, Exception error) =>
        $"""
        cannot reach the FlexRadio "{device}"
          {error.Message}

        {Source(configPath, "device", "--device")}
          This names a FlexRadio reached over the network. Check the radio is powered up,
          finished booting, and on this network - SmartSDR (or `ping` on the radio's
          address) is the quick test. Slice, DAX channel and tuning live in the same
          device string.
        {Retry}
        """;

    internal static string Ptt(PttConfig ptt, string? configPath, Exception error)
    {
        string where = Source(configPath, "ptt", "--ptt");
        string specific = ptt.Type switch
        {
            "cm108" =>
                """
                  List what this machine actually has:
                    ls -l /dev/hidraw*
                  /dev/hidraw* is root-only by default, so the unprivileged service user cannot
                  open it without a udev rule granting the audio group access. The rule to
                  write is under "Choose the PTT line" at
                    https://github.com/packet-net/pdn-soundmodem/blob/main/docs/02-first-station.md
                  "Permission denied" here almost always means the rule is missing rather than
                  the wrong device.
                """,
            "serial" =>
                """
                  List what this machine actually has:
                    ls -l /dev/serial/by-id/ /dev/ttyUSB* /dev/ttyACM*
                  /dev/serial/by-id/ names are stable across re-plugging; /dev/ttyUSB0 is not.
                  The service user must be in the dialout group (the packaged unit already is).
                """,
            _ => "",
        };

        return $"""
            cannot open the {ptt.Type} PTT device "{ptt.Device}"
              {error.Message}

            {where}
              This selects how the radio is keyed; omit it entirely for VOX, or for a
              FlexRadio, which keys itself.
            {specific}
            {Retry}
            """;
    }
}
