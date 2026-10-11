using System.Text;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The command line, as <c>pdn-soundmodem --help</c> prints it. This is the only description of
/// the flags in the source: Program.cs parses them and points here, and UsageTests reads that
/// argument switch and fails if the flags it parses and the flags described here differ.
/// </summary>
internal static class Usage
{
    /// <summary>
    /// What a bare <c>pdn-soundmodem</c> exits with after printing the usage. The same code as an
    /// unknown option, and the one systemd's RestartPreventExitStatus does not retry, because
    /// nothing was started.
    /// </summary>
    internal const int NoArgumentsExitCode = 2;

    /// <summary>
    /// The usage text, with no trailing newline. Lines end in "\n" whatever the source file's
    /// line ending is, so the output and the 80-column test are the same on every checkout.
    /// Every line fits 80 columns.
    /// </summary>
    internal static string Text { get; } = $"""
        pdn-soundmodem: headless soundcard packet modem

        Usage:
          pdn-soundmodem --config FILE
          pdn-soundmodem [--device SPEC] [--modem N:MODE[:FREQ]]... [OPTIONS]
          pdn-soundmodem --mixer-show DEVICE
          pdn-soundmodem --uplink-token CALLSIGN
          pdn-soundmodem --version
          pdn-soundmodem --help

        Options:
          --config FILE           Read the JSON configuration file. The systemd service
                                  runs with /etc/pdn-soundmodem/soundmodem.json.
          --device SPEC           The audio device (default "default"): an ALSA name
                                  such as plughw:CARD=Device,DEV=0, pipe:IN,OUT[,RATE],
                                  flex:RADIO[:SLICE][@STATION], ubersdr:INSTANCE or
                                  openwebrx:URL.
          --capture-rate HZ       ALSA capture and playback rate (default 48000); it
                                  must be a multiple of the modems' DSP rate, 12000 or
                                  48000.
          --kiss PORT             The shared KISS TCP port (default 8105); every packet
                                  modem is on it, addressed by the sub-channel nibble.
          --bind ADDR             The address every TCP listener binds to (default
                                  127.0.0.1); "*" for every interface.
          --modem N:MODE[:FREQ]   Add a modem on sub-channel N (0-15) in MODE, at audio
                                  centre FREQ Hz if given. Repeatable. With no modems at
                                  all the station runs afsk1200 on sub-channel 0.
          --ptt SPEC              How the radio is keyed: serial:DEVICE[:rts|:dtr] or
                                  cm108:HIDRAW[:GPIO]. The line defaults to rts and the
                                  GPIO to 3. Refused with flex:, ubersdr: and openwebrx:
                                  devices, which need none.
          --txdelay MS            PTT-to-data delay in ms, for a bench run with no KISS
                                  host to set it. No config-file equivalent.
          --wav FILE              Decode a recording instead of live audio, print the
                                  frame count, and exit.
          --wav-loop FILE         Replay a recording forever as the capture device, so
                                  the whole station runs with no sound card.
          --waterfall PORT        Serve the station page on PORT. The config file's
                                  "waterfall" section does the same, defaulting to 8107.
          --dial HZ               Preset the rig dial frequency the station page's RF
                                  scale is drawn from. Needs a waterfall.
          --two-tone SECONDS      Send the 700 and 1900 Hz two-tone test for SECONDS,
                                  then exit. "txTest"."maxSeconds" (default 30) caps it.
          --tone HZ SECONDS       Send one tone at HZ for SECONDS, then exit. The same
                                  cap applies. Give one of the two, and a --ptt or a
                                  flex: device to key with.
          --quality-frames        Send per-frame decode diagnostics to KISS hosts as
                                  JSON on KISS command 7. No config-file equivalent.
          --psk-detector coherent|differential
                                  Force the detector for every BPSK and QPSK modem
                                  (default differential). No config-file equivalent.
          --paging PORT[:BAUD]    Start the POCSAG paging endpoint on PORT at BAUD:
                                  512, 1200 or 2400 (default 1200).
          --ardop PORT            Start the ARDOP virtual TNC: command port PORT, data
                                  port PORT+1. --modem N:ardop is the newer way to say
                                  it (host port 8515); giving both is refused.
          --flex-freq MHZ         Headless FlexRadio: the slice frequency (default
                                  14.100000). A band plan supersedes it.
          --flex-ant ANT          Headless FlexRadio: the antenna (default ANT1).
          --flex-mode MODE        Headless FlexRadio: the slice mode (default DIGU).
          --flex-daxch N          FlexRadio: the DAX channel to claim (default 2
                                  headless, 1 when attached to a SmartSDR station).
          --mixer-show DEVICE     Print the sound card's mixer controls, levels and dB
                                  ranges, then exit. Works while a station is running.
          --uplink-token CALLSIGN Mint one uplink token for that station and print it
                                  with the hash for a monitor's "monitor"."uplinks"
                                  entry, then exit.
          --version               Print the version and the commit it was built from,
                                  then exit.
          --help                  Print this and exit.

        With --config, the file's device, captureRate, kissPort, bind and modems are
        used and --device, --capture-rate, --kiss and --bind are ignored, whether or not
        the file states them. --modem adds to the file's modems (a file that lists none
        already has afsk1200 on sub-channel 0). --ardop overrides "ardop", and each
        --flex-* flag its one field of "flex"; --ptt and --paging replace "ptt" and
        "paging" whole; --waterfall and --dial set the port and dial in "waterfall".

        Modes for --modem N:MODE:
        {ModeList()}
          plus ardop, the ARDOP virtual TNC: host port 8515 unless the config file's
          modem entry sets "port".

        Documentation: https://github.com/packet-net/pdn-soundmodem
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// The catalogue's mode names, comma separated and wrapped for an 80 column terminal.
    /// Read from the catalogue rather than typed here so that the list cannot go stale.
    /// </summary>
    private static string ModeList()
    {
        const int Width = 78;
        var text = new StringBuilder();
        int column = 0;

        foreach (string mode in ModemCatalog.KnownModes)
        {
            string item = mode + ",";
            if (column > 0 && column + 1 + item.Length > Width)
            {
                text.Append('\n');
                column = 0;
            }

            if (column == 0)
            {
                text.Append("  ");
                column = 2;
            }
            else
            {
                text.Append(' ');
                column++;
            }

            text.Append(item);
            column += item.Length;
        }

        // The last name carries no comma.
        text.Length--;
        return text.ToString();
    }
}
