# Command-line reference

Every flag `pdn-soundmodem` accepts, what it takes, what it defaults to, and what happens when the same thing is also set in the configuration file. The flags are parsed in `src/Packet.SoundModem.Daemon/Program.cs`; the usage text at the end of this page is the one `--help` prints, from `src/Packet.SoundModem.Daemon/Usage.cs`.

## Invocation

```
pdn-soundmodem --config FILE
pdn-soundmodem [--device SPEC] [--modem N:MODE[:FREQ]]... [OPTIONS]
pdn-soundmodem --mixer-show DEVICE
pdn-soundmodem --uplink-token CALLSIGN
pdn-soundmodem --version
pdn-soundmodem --help
```

The installed service runs `/usr/bin/pdn-soundmodem --config /etc/pdn-soundmodem/soundmodem.json` and nothing else; see [packaging/pdn-soundmodem.service](../../packaging/pdn-soundmodem.service). A station started by hand can run from flags alone, with no file.

Flags are read left to right. A flag given twice keeps the last value, except `--modem`, which adds a modem each time. `--help`, `--version` and `--uplink-token` act as soon as they are read, so flags after them are not looked at. A flag that needs a value and has none, or a value that does not parse, is not checked by the modem: the .NET runtime aborts on the unhandled exception, printing `Unhandled exception. System.ArgumentException: --kiss needs a value` (or the runtime's own message for a value that does not parse) and a stack trace on stderr, with exit code `134`. The message names the argument before the gap, so `--tone 1000` with no SECONDS says `1000 needs a value`. The one exception is `--uplink-token`, which says what it needs and exits `2`.

| Command line | Prints | Exit code |
|---|---|---|
| No arguments | The usage text, on stderr. Nothing is started. | `2` |
| `--help` | The usage text, on stdout. | `0` |
| `--version` | The version and the commit it was built from, on stdout. | `0` |
| An unknown option | `unknown option --x`, on stderr. | `2` |

### Exit codes

| Code | Meaning |
|---|---|
| `0` | The station stopped normally, or a one-shot flag did its job. |
| `1` | A run-time failure: the sound card or PTT device could not be opened or was lost, the radio or web receiver went away, a restart was requested over the API, `--mixer-show` found no mixer or could not read it, or a `--two-tone` or `--tone` test was refused or withdrawn after the station came up. |
| `2` | A usage error or a refused configuration: no arguments, an unknown option, a flag the station cannot honour, or a config file that does not load or fails validation. The message on stderr says what to change. |

The service unit sets `Restart=on-failure`, `RestartSec=5` and `RestartPreventExitStatus=2`. Exit `1` is retried every 5 seconds, so a card that was slow to appear comes up by itself. Exit `2` is not retried, so the journal carries one explanation of the refused configuration and the service stays stopped until it is fixed and started again.

### Start-up

The very first line the daemon writes, before `--config` is even read, is the same line `--version` prints: `pdn-soundmodem VERSION, commit SHA`. `journalctl -u pdn-soundmodem | head` answers "which version is this" for a station that has been running for a month, which was otherwise only answerable from outside the program - `dpkg -l pdn-soundmodem` on a box where the `.deb` was the last thing to write the binary, or `strings` on the binary, neither of which survives a hand-copied build ([#480](https://github.com/packet-net/pdn-soundmodem/issues/480)). A build that got no version of its own reads `pdn-soundmodem 0.0.0-dev (dev build, not a numbered release), commit SHA` rather than something that looks like a release.

## Station flags

These configure the station the process runs. Defaults apply when neither the flag nor the config file sets the value.

| Flag | Argument | Default | What it does |
|---|---|---|---|
| `--config` | `FILE` | none | Read the JSON configuration file. The section below says what the file wins over. A file that is missing, empty, malformed or refused by validation exits `2`. |
| `--device` | `SPEC` | `default` | The audio device: an ALSA name such as `plughw:CARD=Device,DEV=0`, `pipe:IN,OUT[,RATE]`, `flex:RADIO[:SLICE][@STATION]` or `ubersdr:INSTANCE`. |
| `--capture-rate` | `HZ` | `48000` | The ALSA capture and playback rate. It must be a multiple of the modems' DSP rate, 12000 or 48000, or the modem exits `2`. Not used with `flex:`, `ubersdr:` or `openwebrx:` devices, which bring their own clock. |
| `--kiss` | `PORT` | `8105` | The shared KISS TCP port. Every packet modem is on it, addressed by the sub-channel nibble. |
| `--bind` | `ADDR` | `127.0.0.1` | The address every TCP listener binds to. `*` (or `0.0.0.0`) is every interface. Anything that is not an IP address exits `2`. |
| `--modem` | `N:MODE[:FREQ]` | one `afsk1200` on sub-channel 0 when nothing names a modem | Add a modem on sub-channel `N` (0 to 15) in `MODE`, centred at `FREQ` Hz when given. Repeatable. `N` alone means `afsk1200`. With `--config`, a file that lists no modems has already been given `afsk1200` on sub-channel 0, so `--modem 1:bpsk300` runs two modems and `--modem 0:bpsk300` is refused as two modems on one sub-channel. A plugin mode cannot be written here, because its `pluginId:mode` name contains this flag's separator; it goes in the config file, and the flag says so and exits `2`. |
| `--ptt` | `SPEC` | none; the radio is not keyed | How the radio is keyed: `serial:DEVICE`, `serial:DEVICE:rts`, `serial:DEVICE:dtr` (the line defaults to `rts`, and any name other than `dtr` is taken as `rts`) or `cm108:HIDRAW[:GPIO]` (the GPIO defaults to 3; one that is not a number aborts as under Invocation). Any other shape exits `2`. Refused with `flex:`, `ubersdr:` and `openwebrx:` devices, exit `2`. A device that cannot be opened exits `1`. |
| `--txdelay` | `MS` | `300` until a host sets TXDELAY over KISS | The PTT-to-data delay in milliseconds, for a bench run with no host attached to set it. |
| `--wav-loop` | `FILE` | none | Replay a recording forever as the capture device. The whole station runs with no sound card: the file stands in for `--device` whatever that names, transmit audio is discarded and no PTT is keyed. The file's rate must be a multiple of the DSP rate, or the modem exits `2`; a file that cannot be read aborts as under Invocation. |
| `--waterfall` | `PORT` | none; the config section defaults to `8107` | Serve the station page on `PORT`. |
| `--dial` | `HZ` | none | Preset the rig dial frequency the station page's RF scale is drawn from. Without a waterfall from `--waterfall` or the config file it exits `2`. |
| `--quality-frames` | none | off | Send per-frame decode diagnostics to hosts as JSON on KISS command 7, on every KISS port. |
| `--psk-detector` | `coherent`, `differential` or `mlse` | `differential` | Force the detector for every BPSK and QPSK modem. Case-insensitive; any other value aborts as under Invocation. `mlse` is BPSK-only and `--help` does not list it: every BPSK modem then builds with the MLSE equaliser, and a QPSK modem refuses to build, so the modem exits `2`. |
| `--paging` | `PORT[:BAUD]` | none; `BAUD` is `1200` | Start the POCSAG paging endpoint on `PORT` at `BAUD`. The encoder supports 512, 1200 and 2400; the modem does not check the number, and what the POCSAG library does with another value is not documented. |
| `--ardop` | `PORT` | none | Start the ARDOP virtual TNC on the lowest free sub-channel: command port `PORT`, data port `PORT+1`. `--modem N:ardop` is the newer way to say it, with host port 8515 unless the config file's modem entry sets `port`. Giving both exits `2`. |

Every port and the station page bind to the `--bind` address.

## One-shot flags

These print, or transmit, and exit. None of them serves a KISS port, the station page, paging, ARDOP, the API or an uplink. The fourth column is the exit code, because a one-shot flag has no default.

| Flag | Argument | What it does | Exit code |
|---|---|---|---|
| `--wav` | `FILE` | Decodes the recording through the configured modems instead of live audio, prints one line per frame heard and then `N frames decoded`. No sound card, PTT or port is opened. `--config` and `--modem` choose the modems as they would for a live station. With a config file that has a `frameLog` section the decoded frames are written to that log as if heard on air; use a file without one, or `--modem` alone, to keep a recording out of the station's history. A file that cannot be read aborts as under Invocation. | `0`; `2` if the file's rate is not a multiple of the DSP rate. |
| `--two-tone` | `SECONDS` | Brings the station up, keys the radio, sends the 700 and 1900 Hz two-tone test for `SECONDS` through the normal transmit path at the station's transmit level, unkeys and stops. `SECONDS` is capped at `txTest.maxSeconds` (default 30, never above 60); zero or less sends the file's `txTest.seconds` (default 5 s). | `0` when the tones went out; `1` when the test was refused or withdrawn; `2` for a refused configuration. |
| `--tone` | `HZ SECONDS` | The same with one tone at `HZ`, for a carrier level check or an FM deviation check by Bessel null. Two values, in that order. | As `--two-tone`. |
| `--mixer-show` | `DEVICE` | Lists every control the card has, then reports on one line the level and dB range of the capture and playback controls and the state of the AGC and mic boost switches it recognises by name (the same lists the station uses at start-up, under [`alsa`](config.md#alsa)); a card with none of those is said so. Every line is prefixed `alsa: mixer:`. Runs before anything else, reads the mixer only, and works while a station holds the card. `DEVICE` is an ALSA device name; the card is taken from it, so `plughw:CARD=Device,DEV=0` shows card `Device`. | `0`; `1` if the card has no mixer or it could not be read. |
| `--uplink-token` | `CALLSIGN` | Mints one uplink token for that station and prints the token once, as the `token` line for the station's `publish` section, and its SHA-256 hash once, inside a ready-made `monitor.uplinks` entry for the monitor's own file. Nothing is written to disk, and the token is not shown again. | `0`; `2` if `CALLSIGN` is missing or is not one to six letters and digits with an optional `-SSID`. |
| `--version` | none | Prints the version and the commit it was built from, on stdout: what `packaging/build-deb.sh` set with `-p:Version` for a released build, or, for a build that got no version of its own (a plain `dotnet build`, or a binary copied by hand), says so plainly rather than printing something that looks like a release. The same line the daemon writes first to the journal at start-up (see [start-up](#start-up) below, and [#480](https://github.com/packet-net/pdn-soundmodem/issues/480)). | `0`. |
| `--help` | none | Prints the usage text on stdout. | `0`. |

A `--two-tone` or `--tone` run is refused, with the reason on stderr, when:

| Condition | Exit code |
|---|---|
| `--two-tone` and `--tone` are both given; they are two ways to run one test. | `2` |
| The config file describes a monitor (`monitor` section), which has no transmitter. | `2` |
| The device is `ubersdr:` or `openwebrx:`, a receiver with nothing to key. | `1` |
| Nothing keys the radio: no `--ptt` or `ptt` section and not a `flex:` device. `--wav-loop` and `pipe:` devices have no PTT either. | `1` |
| `txTest.enabled` is `false` in the config file. | `1` |
| The channel stayed busy for 60 s, so the test was withdrawn without keying. | `1` |

## FlexRadio flags

Used with `--device flex:RADIO[:SLICE][@STATION]`. The first three apply in headless mode only, a `flex:` device with no `@STATION`, where the modem creates and owns the slice. In attach mode SmartSDR owns the slice; the values are still passed to the M0LTE.Flex package, whose documentation says it ignores them there. The Flex keys the radio itself, so `--ptt` is refused with it.

| Flag | Argument | Default | What it does |
|---|---|---|---|
| `--flex-freq` | `MHZ` | `14.100000` | The slice frequency. When the modems carry `rfFrequency` the band plan sets the slice instead, and a value given here is reported as superseded. |
| `--flex-ant` | `ANT` | `ANT1` | The antenna. |
| `--flex-mode` | `MODE` | `DIGU` | The slice mode. It also fixes the station's sideband: `DIGU` and `USB` mean `usb`, `DIGL` and `LSB` mean `lsb`, `FM` and `NFM` mean `fm`. A `sideband` in the config file that contradicts it exits `2`. |
| `--flex-daxch` | `N` | `2` headless, `1` attached | The DAX channel to claim. Applies in both modes. |

## Flags and the config file

When `--config` is given, the file is read after every flag has been parsed and the two combine as this table says, one row per flag. Where the file wins it wins whether or not the file states the key; the key's default stands in for a flag the file made no mention of.

| Flag | With `--config` |
|---|---|
| `--config` | Names the file. Given twice, the last wins. |
| `--device` | Ignored. The file's `device` is used, `default` if the file does not state it. |
| `--capture-rate` | Ignored. The file's `captureRate` is used, `48000` if unstated. |
| `--kiss` | Ignored. The file's `kissPort` is used, `8105` if unstated. |
| `--bind` | Ignored. The file's `bind` is used, `127.0.0.1` if unstated. |
| `--modem` | Added after the file's `modems`. A file that lists none has already been given one `afsk1200` on sub-channel 0 (unless it has a top-level `ardop` section), so `--modem 1:bpsk300` runs two modems; put the modems in the file, or use a free sub-channel. |
| `--ptt` | Replaces the file's `ptt` section whole. |
| `--txdelay` | Applies. No config equivalent. |
| `--wav` | Applies, decoding with the file's modems and writing the frames to the file's `frameLog` when it has one. |
| `--wav-loop` | Applies. The recording is the capture device whatever the file's `device` says. |
| `--waterfall` | Sets `waterfall.port`, creating the section if the file has none. The section's other keys stand. |
| `--dial` | Sets `waterfall.dialFrequencyHz`. Exits `2` if neither the file nor `--waterfall` gives a waterfall. |
| `--two-tone` | Applies, capped by the file's `txTest.maxSeconds` and refused when `txTest.enabled` is `false`. |
| `--tone` | As `--two-tone`. |
| `--quality-frames` | Applies. No config equivalent. |
| `--psk-detector` | Applies. No config equivalent. |
| `--paging` | Replaces the file's `paging` section whole: `baud` returns to 1200 unless the flag gives one, and `invertPolarity` is off. |
| `--ardop` | Overrides the file's `ardop.port`. Exits `2` if the file also has a modem entry in mode `ardop`. |
| `--flex-freq` | Overrides `flex.frequency`. |
| `--flex-ant` | Overrides `flex.antenna`. |
| `--flex-mode` | Overrides `flex.mode`. |
| `--flex-daxch` | Overrides `flex.daxChannel`. |
| `--mixer-show` | Exits before the file is read. |
| `--uplink-token` | Exits while the flags are being read; the file is not opened. |
| `--version` | As `--uplink-token`. |
| `--help` | As `--uplink-token`. |

Flags with no config-file equivalent: `--txdelay`, `--wav`, `--wav-loop`, `--quality-frames`, `--psk-detector`, `--mixer-show`, `--uplink-token`, `--version` and `--help`.

Config-file sections and keys with no flag: `sideband`, `dialFrequency`, `modemPlugins`, `polyglot`, `polyglotPort`, `txTest`, `alsa`, `ubersdr`, `monitor`, `publish`, `api`, `frameLog`, `survey`, `metrics`, `frequencyMatching`, `rawCapture`, `deadFeed` and `idBeacons`. Within sections a flag does reach: a modem entry's `port`, `rfFrequency`, `bandwidth`, `offsetPairs`, `offsetStepHz`, `acceptPlainIl2p` and `identify`; `flex.txPowerWatts`, `flex.transmitFilterHighHz`, `flex.stationName`, `flex.arbitration` and `flex.receiveOnly`; `paging.invertPolarity`; and every `waterfall` key other than `port` and `dialFrequencyHz`. The [configuration reference](config.md) documents each of them.

## The usage text

What `pdn-soundmodem --help` prints, and what a bare `pdn-soundmodem` prints on stderr before exiting `2`. The mode list is read from the modem catalogue at run time, so a plugin mode never appears in it.

```
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
  afsk1200, afsk1200-fx25, afsk1200-fx25rx, afsk1200-multi, afsk1200-il2p,
  afsk1200-il2p-nocrc, afsk300, afsk300-il2p, afsk300-il2pc, bpsk300,
  bpsk300-multi, bpsk300-nocrc, bpsk1200, bpsk1200-multi, qpsk600, qpsk2400,
  qpsk3600, fsk9600, fsk9600-il2p, fsk4800-il2p, c4fsk9600, c4fsk19200,
  freedv-datac0, freedv-datac1, freedv-datac3, freedv-datac4, freedv-datac13,
  freedv-datac14, ms110d-wn0, ms110d-wn1, ms110d-wn2, ms110d-wn3, ms110d-wn4,
  ms110d-wn5, ms110d-wn6, ms110d-wn7, ms110d-wn8, ms110d-wn13, ofdm-fm-narrow,
  ofdm-fm-6k, ofdm-fm-6k-fast, ofdm-fm-8k, ofdm-fm-8k-r56, ofdm-fm-8k-r78,
  ofdm-fm-8k-follow, ofdm-fm-8k-adaptive
  plus ardop, the ARDOP virtual TNC: host port 8515 unless the config file's
  modem entry sets "port".

Documentation: https://github.com/packet-net/pdn-soundmodem
```

Related: [the guide](../README.md), [configuration reference](config.md), [ports and endpoints](ports-and-endpoints.md), [files and directories](files.md).
