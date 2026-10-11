# Configuration reference

Every key the modem reads from its configuration file, with type, default and one sentence each. The keys are defined in `src/Packet.SoundModem.Daemon/DaemonConfig.cs`, which also refuses a file it cannot run; where a value is applied is `src/Packet.SoundModem.Daemon/Program.cs`. How to choose values for a particular station is [the guide](../README.md)'s business; this page says what each key is and what the modem refuses.

## The file

| | |
|---|---|
| Path | `/etc/pdn-soundmodem/soundmodem.json`; the systemd unit passes it as `--config`. There is no search path and no environment variable; without `--config` the modem runs from flags and defaults alone (see the [command-line reference](command-line.md)). |
| Format | JSON. `//` and `/* */` comments and trailing commas are accepted. Keys match case-insensitively, so `kissPort`, `KissPort` and `kissport` are the same key. |
| Applying a change | Restart the service. The file is read once at start-up. |
| Written by the modem | Never, with one exception: `POST /api/config?persist=true` replaces it on an operator's explicit instruction. |
| Seeded from | `/usr/share/pdn-soundmodem/soundmodem.example.json`, copied in by the package on first install. |

An empty object `{}` is a valid file. It gives a station on ALSA device `default` at 48000 Hz capture, one `afsk1200` modem on sub-channel 0 at 1700 Hz, KISS on `127.0.0.1:8105`, no PTT line (the radio's VOX or nothing), the transmitter test enabled but unusable without a `ptt`, id-beacon listening and frequency matching on, and no browser page, frame log or other service.

An unknown key anywhere in the file is reported at start-up and ignored. The journal line is `config: WARNING - <section>: "<key>" is not a setting this version knows, and is being IGNORED. Check the spelling against <url>`, where `<section>` is the object it was found in (`waterfall`, `modem 1`, `alsa mixer`, `monitor uplink GB7RDG-2` and so on) and is omitted for a top-level key. Unknown keys inside `metrics` and `frequencyMatching` are the two exceptions: those objects keep them but nothing reports them.

An invalid file makes the modem exit with status 2 after printing what is wrong and what to do (the exit codes are listed in the [command-line reference](command-line.md#exit-codes)). The unit sets `RestartPreventExitStatus=2`, so systemd leaves the service stopped and the journal holds one explanation:

```
configuration error in /etc/pdn-soundmodem/soundmodem.json
  two modems share "subChannel": 0. Each modem needs its own KISS sub-channel (0-15) - renumber one of them.

  The service will not start until this is fixed. As root, to start
  from a known-good file:
    cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json
  Then edit it for your sound device and PTT, and:
    systemctl restart pdn-soundmodem
  Every setting is documented at https://github.com/packet-net/pdn-soundmodem/blob/main/docs/reference/config.md
```

The same frame carries `no such file: <path>`, `no such directory: <dir>`, `permission denied reading the file`, `the file is empty`, ``the file contains only `null` - there is nothing to configure from`` and `not valid JSON - line L, position P: <detail>` (counted from 1, as an editor does). The frame is used for every refusal raised while the file is read, which is everything `DaemonConfig` checks: the file-level errors above, `bind`, the port claims, sub-channels and the `rfFrequency` rules, `txTest`, `modemPlugins`, `alsa`, `flex.transmitFilterHighHz`, `rig`, `deadFeed`, the sideband kinds, `monitor` and `publish`, plus the `publish.audioRate` divisor check, which waits for the modems. Refusals raised later in start-up are one or two bare lines on stderr with exit 2 and no recovery text: an unknown mode and the mode rules under `modems`, every `identify` refusal, `ptt`, `captureRate`, `ubersdr`, `openwebrx`, `flex.txPowerWatts` and the sideband contradiction, ARDOP given twice via `--ardop`, the band plan, the page's port and settings, `api`, `frameLog`, `survey`, `rawCapture`, and a monitor's own start-up checks. Where a section below says a line is a warning, start-up continues.

Exit status 1 is different: hardware the file names but the machine does not have (a sound card that has not enumerated, a `/dev/hidraw0` that is not there, a radio still booting) exits 1 with a message naming the key and the file, and the service keeps retrying every five seconds.

## Top-level keys

In the order `DaemonConfig` declares them. `sideband`, `dialFrequency` and each modem's `rfFrequency` are covered together under [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency) at the end of the page.

| Key | Type | Default | What it is |
|---|---|---|---|
| `device` | string | `"default"` | The audio input and output: an ALSA name, `null`, `pipe:`, `flex:`, `ubersdr:` or `openwebrx:`. |
| `captureDevice` | string | absent: `device` | The sound card to receive from, when it is not `device`. See [Two cards](#two-cards-capturedevice-and-playbackdevice). |
| `playbackDevice` | string | absent: `device` | The sound card to transmit through, when it is not `device`. See [Two cards](#two-cards-capturedevice-and-playbackdevice). |
| `captureRate` | int | `48000` | ALSA capture and playback rate in Hz; the modem decimates to its DSP rate. |
| `kissPort` | int | `8105` | The shared KISS TCP port carrying every packet modem by sub-channel nibble. |
| `kissMaxFrameBytes` | int | `8192` | The most bytes one KISS frame from a host may carry, on every KISS port. A longer frame is dropped and the journal says so. |
| `bind` | string | `"127.0.0.1"` | The address every listener binds to: KISS, per-modem ports, the station page, paging and ARDOP. `"*"` or `"0.0.0.0"` for all interfaces. |
| `sideband` | string | `"usb"` | What kind of radio this is, `"usb"`, `"lsb"` or `"fm"`, for turning `rfFrequency` into audio. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `dialFrequency` | number | chosen by the modem | Pins the dial in Hz instead of letting the band plan choose one. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `modems` | array | one `afsk1200` on sub-channel 0; none when a top-level `ardop` section is present | The modems sharing the audio channel. |
| `modemPlugins` | array | `[]` | Assemblies outside the package that provide extra modes. |
| `polyglot` | array | `[]` | KISS ports that overlay several modems, sending each frame in the mode its next hop was last heard in. |
| `polyglotPort` | int | absent: none | Shorthand for one `polyglot` port over every packet modem, the first as the default. |
| `ptt` | object | absent: no keying line | How the radio is keyed: `serial`, `cm108` or `rigctld`. |
| `carrierSense` | object | absent: read from the audio | Read carrier sense from the radio's own squelch and signal meter over its control cable. Strongly recommended on FM. |
| `txTest` | object | enabled, 5 s, cap 30 s | Bounds on the operator's two-tone and single-tone transmitter test. |
| `alsa` | object | absent: levels left alone | The sound card's mixer levels. |
| `paging` | object | absent: off | The POCSAG paging endpoint. |
| `ardop` | object | absent: off | Legacy way to start the ARDOP virtual TNC; a modem entry with `"mode": "ardop"` is the current form. |
| `flex` | object | absent: defaults | Slice parameters for a headless FlexRadio. |
| `rig` | object | absent: no rig control | A radio controlled through Hamlib's rigctld: the band plan's dial, a rigctld PTT and tuning windows. |
| `mailcast` | object | absent: off | The built-in pdn-mailcast receiver: hear GB7RDG's bulletins on 40 m and forward them into your BBS. |
| `ubersdr` | object | absent: defaults | Stream parameters for a public UberSDR web receiver. |
| `openwebrx` | object | absent: defaults | Stream parameters for a public OpenWebRX or OpenWebRX+ receiver. |
| `waterfall` | object | absent: no page | The station page: spectrum, waterfall, frames, links, and the HTTP listener that `api` and `metrics` share. |
| `monitor` | object | absent: not a monitor | Turns the process into a monitor site fronting many web receivers. Exclusive with `device`. |
| `publish` | object | absent: publishes nothing | Dials out to a monitor site and offers this station on it. Exclusive with `monitor`. |
| `api` | object | absent: no API | The runtime configuration API under `/api/` on the page port; `key` only. |
| `frameLog` | object | absent: not kept | A SQLite log of every frame heard and sent. |
| `survey` | object | absent: not surveying | Records bursts the station could not read. |
| `metrics` | object | absent: not served | `/metrics` and `/metrics/frames` on the page port. |
| `frequencyMatching` | object | absent: on with defaults | Answering an off-frequency station on its own frequency. |
| `rawCapture` | object | absent: off | Continuous chunked recording of the receive audio. |
| `deadFeed` | object | absent: per-device defaults | When a silent or stalled input restarts the service. |
| `idBeacons` | bool | `true` | Listen for the 300 baud AFSK idents a NinoTNC sends beside its PSK modes. |

## `device`

```json
{ "device": "plughw:CARD=Device,DEV=0" }
```

| Value | What it opens |
|---|---|
| `default`, `plughw:1,0`, `plughw:CARD=Device,DEV=0` | An ALSA capture and playback device. `aplay -L` lists the stable `CARD=` names. |
| `null` | ALSA's null device: no audio in or out. |
| `pipe:<in>,<out>[,<rate>]` | Two FIFOs standing in for a sound card, for two modems on the same air with no hardware between them. |
| `flex:<radio>[:slice][@station]` | A FlexRadio over the LAN. `<radio>` is `discover`, `host[:port]`, `serial=...`, `name=...` or `mock`; slice `A` to `H`; `@station` attaches to a running SmartSDR's slice instead of creating one. |
| `ubersdr:<instance>` | A public UberSDR web receiver's IQ stream, receive only. A host, a `host:port`, or the `https://` URL you would open in a browser. |
| `openwebrx:<url>` | A public OpenWebRX or OpenWebRX+ receiver's demodulated audio, receive only. The URL you would open in a browser, path included; a bare host or `host:port` is taken as `http` on port 8073. |

- `captureRate` applies to ALSA and pipe devices only. It must be a multiple of the channel's DSP rate (12000, or 48000 when any 48 kHz mode is configured), or start-up refuses with `--capture-rate must be a multiple of N`. A pipe's own rate has the same rule: `pipe rate N is not a multiple of the channel's N Hz`.
- A `flex:`, `ubersdr:` or `openwebrx:` device provides its own clock; `captureRate` is ignored.
- A device that will not open exits 1 with a message naming the key and the file, and the service retries.

### Two cards: `captureDevice` and `playbackDevice`

```json
{ "device": "plughw:CARD=Device,DEV=0", "playbackDevice": "plughw:CARD=Device_1,DEV=0" }
```

- Each key overrides `device` for one direction: `captureDevice` for receive, `playbackDevice` for transmit. Leave both out and `device` does both, as before.
- Sound cards only. Refused, exit 2: either key naming a `pipe:`, `flex:`, `ubersdr:` or `openwebrx:` device (`"captureDevice" is "X", which is not a sound card`); either key when `device` itself is not a sound card (`"captureDevice" is set but "device" is "X", which is not a sound card`); either key beside `monitor`; an empty value.
- Both directions run at `captureRate`.
- The start-up line names both: `audio: capture <capture device> 48000 Hz -> 12000 Hz, playback <playback device>`.
- When the two are on different cards, the capture gain, AGC and mic boost are set on the receive card's mixer and `playbackDb` on the transmit card's; see [`alsa`](#alsa).
- Config file only; the command line has no flag for either.

## `kissPort` and `bind`

```json
{ "kissPort": 8105, "bind": "127.0.0.1" }
```

- `bind` must parse as an IP address, or be `"*"`; anything else is refused with `"bind": "<value>" is not an IP address. Use "127.0.0.1" for loopback only, "*" for every interface, or the address of one interface.` A blank value stays on loopback.
- KISS has no authentication. Binding beyond loopback prints a `kiss: WARNING - listening beyond loopback` line at start-up; the text is under [Every listener](ports-and-endpoints.md#every-listener).
- Two services asking for one TCP port are refused before anything opens: `<this> and <that> both want TCP port N. Give them different ports.` The claims are `"kissPort"`, `"polyglotPort"`, `a "polyglot" port`, `the "port" of modem N`, `the ARDOP data port of modem N` (its `port` plus one), `the waterfall`, `the paging endpoint`, `the ARDOP command port` and `the ARDOP data port`. With a top-level `ardop` section `"kissPort"` is not claimed, so a clash between it and the ARDOP ports fails when the listener binds rather than at validation; see [`ardop`](#ardop).
- Channel access (TXDELAY, persistence, slot time, TXTAIL) has no key here; the host sets it over KISS at runtime.
- `kissMaxFrameBytes` bounds what is buffered per host before a frame's closing delimiter arrives. It is a memory bound, not a statement about any mode: a frame too long for the mode it is addressed to is refused by that mode, and a `frame rejected` line says so. The default, 8192, is above anything a built-in mode carries; a plugin modem that carries more is the reason to raise it. Below 512 the file is refused: `"kissMaxFrameBytes": N is below 512.` A frame that hits the cap is dropped and `kiss[PORT] HOST sent a frame over N bytes; dropped.` goes to the journal, at most once per host per ten seconds. It used to be 2048 and silent, which capped the throughput of any burst modem whose frames could usefully be longer, and did so with nothing in any log.

## `modems`

```json
{
  "modems": [
    { "subChannel": 0, "mode": "afsk1200", "frequency": 1700 },
    { "subChannel": 1, "mode": "bpsk300", "rfFrequency": 7051600, "port": 8110 }
  ]
}
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `subChannel` | int | `0` | The KISS sub-channel (port nibble) this modem answers to, 0 to 15. |
| `mode` | string | `"afsk1200"` | A catalogue mode name (see [modes](../05-modes.md)), `"ardop"` for the ARDOP virtual TNC, or a plugin mode written `pluginId:mode`. |
| `frequency` | number | the mode's own centre | Audio centre in Hz, transmit and receive. 1700 for `afsk*`, 1500 for `bpsk*` and `qpsk*` (1650 for `qpsk3600`), the spec centre for `freedv-*` and `ms110d-*`. |
| `rfFrequency` | number | none | Where this modem sits on the band in absolute Hz; the modem then works out the dial and this modem's audio centre. See [Band placement](#band-placement-sideband-dialfrequency-and-rffrequency). |
| `bandwidth` | number | measured from the modem; 2000 for `ardop` | How much room the band plan and the survey allow this modem, in Hz. On `ardop` it is also the TNC's ARQBW, so no session is accepted or asked for wider; only 200, 500, 1000 and 2000 are accepted there. |
| `busyDetect` | bool | `false` | On an `ardop` entry, watch that modem's own slot and tell the TNC when somebody else is using it (`BUSY TRUE`/`BUSY FALSE`, and `ConRejBusy` under `BUSYBLOCK`). Off by default: it is an energy meter, and out-of-band interference that lifts the noise floor faster than the floor estimator follows reads as in-band signal, which on 40 m at GB7RDG meant almost continuously busy. Meaningless on any other mode. |
| `port` | int | none | A TCP port for this modem alone. A packet mode gets KISS there with this modem presented as nibble 0; `ardop` gets the ardopcf host interface, command on this port and data on the next one up (default 8515 and 8516). |
| `offsetPairs` | int | 4, 5 or 0 by mode | Diversity-bank modes only: decoder branches either side of centre. 0 is a single modem. |
| `offsetStepHz` | number | by mode | Diversity-bank modes only: Hz between adjacent branches. |
| `acceptPlainIl2p` | bool | `false` | IL2P+CRC modes only: also pass frames that arrive as plain IL2P with no CRC to the host. |
| `identify` | object | none | Morse identification for this modem; see [`modems[].identify`](#modemsidentify). |
| `maxBurstSeconds` | number | none | `ms110d-*` only: frames queued together go out in one burst of up to this many seconds (1 to 120), with one preamble and one EOM, instead of one burst each. Each burst is a keyup of its own, so receive, idents and other modems get a turn between bursts. A single frame longer than this still goes out on its own. Each frame is still reported and acknowledged (ACKMODE) on its own, but a burst's acknowledgements arrive together when it has gone out. Meant for runs of UI frames; leave it off on a port that carries connected-mode traffic. Omit it for one frame per burst. |
| `txAmplitude` | number | `0.5` | `ms110d-*` only: the transmit level, above 0 and at most 1. The default 0.5 peaks at about 0.38 of full scale, because the waveform's peak is about 6.5 dB above its average; 1.0 peaks at about 0.75, close to the transmit test tone's 0.8, and sends about 6 dB more power on air. Watch ALC on an SSB rig when raising it. |
| `burstGatherSeconds` | number | `0.5` | With `maxBurstSeconds`: how long the first frame of a run waits for the rest to arrive before the modem contends for the air, so a host writing frames one at a time still fills a burst. 0 to 10. |

Diversity-bank defaults by mode:

| Modes | `offsetPairs` | `offsetStepHz` |
|---|---|---|
| `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `qpsk600` | 4 | 7.5 (baud / 40) |
| `bpsk1200`, `bpsk1200-multi`, `qpsk2400` | 4 | 30 (baud / 40) |
| `qpsk3600` | 0 | 45 (baud / 40) |
| `afsk300`, `afsk300-il2p`, `afsk300-il2pc` | 5 | 35 |
| `afsk1200-multi` | fixed at 3; the keys are ignored | |
| every other mode | ignored | ignored |

Rules and refusals:

- A `frequency` on a baseband mode (`fsk9600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200`) is refused: `modem N: mode 'X' occupies the audio band from DC upwards and has no centre frequency to move - drop the frequency override`. Every other mode, including `freedv-*` and `ms110d-*`, accepts one.
- `acceptPlainIl2p` on a mode that does not run IL2P+CRC is refused: `modem N: mode 'X' does not run IL2P+CRC, so it has no separate plain-IL2P reading to release - drop "acceptPlainIl2p"`. It applies to `afsk300-il2pc`, `afsk1200-il2p`, `bpsk300`, `bpsk300-multi`, `bpsk1200`, `bpsk1200-multi`, `qpsk600`, `qpsk2400`, `qpsk3600`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200`, `freedv-*` and `ms110d-*`. With it on, the journal says once per modem that plain IL2P frames are checked by Reed-Solomon alone.
- `maxBurstSeconds` or `burstGatherSeconds` on a mode other than `ms110d-*` is refused (`Only the ms110d-* modes pack several frames into one burst`), as are a `maxBurstSeconds` outside 1 to 120, a `burstGatherSeconds` outside 0 to 10, and a `burstGatherSeconds` without `maxBurstSeconds`. With packing on, the journal says `modem N: frames queued together share one burst, up to S s (gathering for G s first)`.
- Two entries with the same `subChannel`: `two modems share "subChannel": N. Each modem needs its own KISS sub-channel (0-15) - renumber one of them.`
- A `mode` that is not a catalogue mode, `ardop` or a loaded plugin's mode: `modem N: unknown mode 'X'`, followed by `did you mean: ...` for a near miss and a link to the mode list. For a `pluginId:mode` name the second line says whether the plugin is loaded and what it provides.
- `frequency` and `rfFrequency` on one entry, on USB or LSB: `modem N sets both "frequency" (F) and "rfFrequency" (R). Those say the same thing two ways ... Keep one.` On FM both are allowed; `rfFrequency` is the channel and `frequency` is where the tones sit in its audio.
- `rfFrequency` on some entries and not others: `some modems have "rfFrequency" and some do not (...). Give every modem an "rfFrequency" or none of them`.
- Two entries with `"mode": "ardop"`: `two modems have "mode": "ardop". One ARDOP TNC per channel`.
- An `ardop` entry beside a top-level `ardop` section: `ARDOP is configured twice - once as a modem entry and once in the top-level "ardop" section. Keep the modem entry ... and delete the "ardop" section.`
- A plugin mode whose declared rate is neither 12000 nor 48000 is refused with a sentence naming that mode and the two rates a channel runs at; one that differs from the rate the other modems settle the channel at is refused with a sentence naming it and the built-in mode that fixed the rate. Built-in modes share a channel at either rate.
- An `ardop` entry whose `bandwidth` is not one of the four widths ARDOP negotiates: `modem N is "mode": "ardop" with "bandwidth": B. ARDOP negotiates 200, 500, 1000 or 2000 Hz and nothing else - use one of those, or remove "bandwidth" to plan for the widest (2000) as before.` The value that is accepted becomes the TNC's ARQBW at start-up, reported on the `ardop host tcp:` line; an entry with no `bandwidth` keeps ARDOP's own default of `2000MAX`.
- An `ardop` entry whose `frequency` leaves less room inside a nominal 300-2700 Hz passband than its `bandwidth` asks for (2000 Hz when it states none) prints `ardop: WARNING - centre F Hz leaves room for an ARDOP bandwidth of W Hz ...`; a centre at or beyond the engine's 6000 Hz Nyquist prints the same prefix with `is outside the 0-6000 Hz band`.
- ARDOP shares the channel with the packet modems. An ARQ session holds packet transmissions until it ends.
- The ARDOP entry without a `port` listens on 8515, data on 8516.

### `modems[].identify`

```json
{ "identify": { "callsign": "M0LTE", "intervalMinutes": 10 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `callsign` | string | none; required | The callsign sent. |
| `intervalMinutes` | number | `10` | Minutes between idents. The clock runs only while this modem transmits; an idle modem never keys to identify. |
| `wpm` | number | `20` | Sending speed, PARIS words per minute. |
| `toneHz` | number | the modem's `frequency` when written, else the band plan's centre for it | The audio tone keyed. |
| `rfFrequency` | number | none | Where to identify in absolute Hz, as an alternative to `toneHz`; needs a band plan. |
| `includeMode` | bool | `false` | Send the mode name after the callsign, as in `M0LTE FREEDV-DATAC1`. |
| `amplitude` | number | `0.8` | Key-down peak, 0 to 1. |

- Refused: `identify` on a receive-only station (`"identify" needs a transmitter, and this station receives only`); on an `ardop` entry (`"identify" is not supported on ardop`); without a `callsign` (`"identify" needs a "callsign" - there is no default for a licence condition`); with both `toneHz` and `rfFrequency` (`they say the same thing two ways. Keep one.`); `rfFrequency` on FM (`has no meaning on FM`); `rfFrequency` with no band plan (`needs a band plan`); no `toneHz` on a modem that has neither a written `frequency` nor a band plan (`mode 'X' has no audio centre, so there is nothing to default the ident tone to`), which is every baseband mode and also any mode left on its default centre with no `rfFrequency` anywhere, so a single-modem file that identifies needs `toneHz` or `frequency` written down. An amplitude, speed or interval the identifier will not take is `"identify" is not usable` with the reason under it. Each line is prefixed `modem N:`.
- The RF form of a tone is the same arithmetic as Band placement: on USB the tone is `rfFrequency - dial`, on LSB `dial - rfFrequency`.
- A tone outside the band plan's passband is a warning, not a refusal: `modem N: WARNING - the ident tone F Hz is outside the L-H Hz passband this plan plays into, so it may be filtered away on transmit.`
- A transmitter test transmission counts as a transmission for the ident clock.

## `modemPlugins`

```json
{ "modemPlugins": [ { "path": "/opt/pdn/plugins/MyModem.dll" } ] }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | none; required | The assembly to load. A relative path resolves against the working directory, so write an absolute one under systemd. |

- Nothing is discovered: only the paths listed load, and the journal repeats each one as `modem plugin: <id> from <path> [<modes>]`. The modes a plugin provides are written `pluginId:mode` in `modems[].mode` and cannot be spelt with `--modem`, whose separator is the same colon.
- An entry with no `path` is refused: `a "modemPlugins" entry has no "path". Each entry names one assembly to load`.
- A plugin that fails to load is reported as `modem plugin: FAILED <path> - <reason>` and start-up continues; a modem that asked for one of its modes then fails as an unknown mode. See [modem plugins](../dev/modem-plugins.md).

## `polyglot`

```json
{ "polyglotPort": 8120 }
```

The short form: every packet modem in `modems` goes behind port 8120, the first one listed is the `default`, and stations are remembered for 60 minutes. `ardop` entries are left out. It needs at least two packet modems: `"polyglotPort": N puts every packet modem behind one port, and this station has 1.` Write the full form below when you want some of the modems, a different default or a different memory.

```json
{ "polyglot": [ { "port": 8120, "subChannels": [0, 1], "default": 0, "forgetAfterMinutes": 60 } ] }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | none; required | The KISS TCP port your node attaches to. It speaks sub-channel 0 only. |
| `subChannels` | array of int | none; required | The modems it overlays, by their `subChannel`. Two or more, each a packet modem in `modems`. |
| `default` | int | none; required | Where a frame goes when the station it is for has not been heard recently. One of `subChannels`; make it the mode every peer has. |
| `forgetAfterMinutes` | number | `60` | How long a station is remembered after it was last heard. At most 10080, a week. |

- Every frame the listed modems decode reaches the port as sub-channel 0. The modem that decoded it is remembered against the station it came from: the last digipeater that repeated it, or the source when none did.
- A frame from the host goes out on the modem its next hop was last heard on: the first digipeater that has not repeated it yet, or the destination. A station not heard within `forgetAfterMinutes` gets `default`. So does a beacon or broadcast sent direct, because `ID`, `BEACON` and `NODES` never transmit; one sent via a digipeater follows that digipeater, like any other frame. An alias a digipeater repeated under, such as `WIDE2`, is remembered like a station.
- When a station's frames move to a different modem the journal says so: `polyglot[8120]: G4ABC-1 heard on modem 1 fsk9600, frames for it go there now (was modem 0 afsk1200, the default)`. A station heard only on the default gets no line.
- A frame two listed modems both decode within a second reaches the host once. The same frame twice from one modem, such as two digipeaters repeating it, reaches the host twice, as on the shared port. SETHW on this port goes to the `default` modem.
- Refusals: an empty entry; an entry with no `port` or no `default`; fewer than two `subChannels`, or one listed twice; a sub-channel with no modem entry, or an `ardop` one; a `default` not in `subChannels`; `forgetAfterMinutes` of 0 or less, or above 10080; and `"polyglot"` or `"polyglotPort"` in a monitor's file. [06-connect-your-software.md](../06-connect-your-software.md#one-port-for-several-modes) has a worked example.

## `ptt`

```json
{ "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `type` | string | `"serial"` | `"serial"` for an RTS or DTR line, `"cm108"` for the GPIO on a CM108-class interface, `"rigctld"` for Hamlib's `T 1` and `T 0` through the [`rig`](#rig) section. |
| `device` | string | `""` | The device path: `/dev/ttyUSB0` for serial, `/dev/hidraw0` for CM108. |
| `line` | string | `"rts"` | Serial only: `"rts"` or `"dtr"`. |
| `gpio` | int | `3` | CM108 only: the GPIO pin. |

- Omit the whole section for a radio keyed by VOX, or one that has no keying line. A FlexRadio keys itself and a web receiver has no transmitter.
- `"rigctld"` takes no other key here; it keys through the `rig` section's rigctld and is refused without one (`"ptt": {"type": "rigctld"} keys the radio through the "rig" section's rigctld, and this file has no "rig" section`). It is config-file only; the `--ptt` flag has no form for it.
- Refused: a `type` other than `serial`, `cm108` or `rigctld` (`unknown ptt type 'X'`); any `ptt` with a `flex:` device (`--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)`); any `ptt` with a `ubersdr:` or `openwebrx:` device (`--device ubersdr: is a receive-only station ... Remove "ptt".`).
- A device that cannot be opened exits 1, with the file, the key, an `ls` to run and the udev note for `/dev/hidraw*`, and the service retries. The [`--ptt` flag](command-line.md#station-flags) replaces this section.
- Without a `ptt` the transmitter test is refused: `tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio`.

## `carrierSense`

```json
{ "carrierSense": { "radio": "tait", "port": "/dev/ttyUSB0", "baud": 28800, "busyAboveDbm": -75, "pollMilliseconds": 100 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `radio` | string | `"tait"` | Which radio driver to use. `"tait"` is the only one; `"none"` switches the section off without deleting it. |
| `port` | string | absent | The radio's control serial port. With no `port` nothing is opened, whatever else this section says. |
| `baud` | int | `28800` | Port speed, set by the radio's own data-port programming. |
| `busyAboveDbm` | number | absent: carrier detect alone | Also call the channel busy when the radio's RSSI reads above this many dBm. |
| `pollMilliseconds` | int | `100` | How often to read RSSI. Ignored without `busyAboveDbm`. |
| `audioFallback` | bool | `true` | With no radio to ask, read carrier sense off the SHAPE of the received audio instead. Ignored on a station that has a radio, and on a 12 kHz channel. |

**On FM this is not an optimisation, it is the difference between working and not.** An FM receiver with the squelch open, which is how every packet FM station runs, is LOUDER when the channel is idle than when somebody is transmitting: the arriving carrier captures the discriminator and replaces band noise with modulation. Measured on the bench, a transmission reads 2.9 dB BELOW the idle channel in band. Every audio busy detector here asserts on a rise, so without this section an FM station's carrier sense is not merely deaf, it is pointing the wrong way. With a source configured, the station's answer becomes the radio's, ored with any modem's packet carrier detect, and the audio energy detectors stop contributing at all.

- **Measure `busyAboveDbm` on your own station.** Two nominally identical radios on one bench read idle noise floors 31 dB apart, -94.3 dBm and -125.6 dBm, against a far end at about -35 dBm either way; the thresholds chosen there were -75 and -110. A figure copied from somebody else is a guess.
- **A data station usually needs it.** The radio's carrier-detect line costs no serial traffic but reports nothing at all until the first squelch edge after unsolicited reporting is enabled, and on a station holding its squelch open that edge may never come.
- **It fails open.** A port that will not open, a radio in the wrong mode, a cable pulled mid-session: each costs carrier sense and nothing else, with a line in the journal, leaving the station transmitting exactly as it did before. A carrier sense that can silence a station by losing a USB cable would be the worse failure by a distance.
- **The daemon holds the port while it runs**, and a serial port cannot be opened twice. A programming tool or a separate signal probe on the same radio has to wait, or this section comes out and the daemon restarts.
- **A radio's answer is the station's; the audio answer is each sub-channel's.** With no radio configured, a frame waits only for its own modem's detector and for any modem whose passband overlaps it, rather than for every modem on the station. That matters on a station whose modems sit on different RF frequencies inside one passband: on 2026-09-21 GB7RDG's four modems were individually busy 35 to 81 % of one 228 second stretch and their union was busy 96.4 %, so the modem that had the traffic saw one usable gap where its own channel had fourteen. Overlap is measured from each modem's occupied bandwidth plus a 50 Hz guard for how far off tune a caller can sit, so modems a hundred Hz or so apart still defer to each other; see [carrier-sense.md](../dev/carrier-sense.md#what-526-changed-who-the-answer-is-for).
- Refused at load: a `radio` that is not `tait` or `none`.
- The radio needs its data port programmed for command mode. See [the Tait TM8100 wiring guide](../hardware/tait-tm8100-cm108.md).

### No control cable: `audioFallback`

A station with no serial link to its radio, or one that is not a Tait, reads carrier sense off the shape of the received spectrum instead. It compares the ratio of power below 8 kHz to power above it against the ratio the station's own idle channel was showing a few seconds ago; **nothing in the decision is an absolute level**, which is what made an earlier attempt at this unsafe.

- **It is on by default, because off is not a neutral choice.** Without it an FM station consults an in-band energy detector, and on open-squelch FM that detector is anti-correlated: it reads clear through the whole of every transmission and then asserts for about ten seconds afterwards, on the return of the noise. Measured on the bench, a station two seconds after hearing one burst took 9.9 s to get a frame to air, against 0.5 s on a quiet channel.
- **Measured** against real captures: 15 of 15 and 11 of 11 transmissions found, a median 26 ms after the carrier arrives, with no false busy in 620 s of idle channel.
- **It answers "no opinion" on any path it does not recognise**, which includes a squelched receiver, a dead or muted card, the station's own receive audio while it transmits, and any SSB or wired path where a signal makes the audio LOUDER. On those the modems' own energy detectors decide, as they always have, which is right for them.
- **It needs a 48 kHz channel**, which a station gets by running any 48 kHz mode. At 12 kHz the band split would land inside a wideband mode's own occupancy and the statistic stops separating; it is not used there and the journal says so.
- A station that has a radio to ask never uses this, whatever the setting says: a measurement beats an inference.


## `txTest`

```json
{ "txTest": { "enabled": true, "seconds": 5, "maxSeconds": 30, "amplitude": 0.8 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Whether the test exists at all: the button on the station page, `POST /api/txtest` and `--two-tone` / `--tone`. |
| `seconds` | number | `5` | How long a test runs when the request does not say. |
| `maxSeconds` | number | `30` | The longest test that will run whatever is asked for; clamped to between 1 and 60 however it is set. |
| `amplitude` | number | `0.8` | Peak level of the burst, above 0 and at most 1. |

- The section is present by default; there is nothing to switch on. The tones are not settings: two-tone is 700 and 1900 Hz, and a single tone takes its frequency per request.
- Refused at load: an `amplitude` that is not above 0 and at most 1 (`"txTest"."amplitude": A is not a level`) and a `seconds` of zero or below (`"txTest"."seconds": S is not a length`).
- The test is unavailable, with the reason in the journal, when `enabled` is false, when the station receives only, or when there is no `ptt`. The control is never on a public page. On a monitor `--two-tone` and `--tone` are refused with exit 2.
- While a [transmit lease](ports-and-endpoints.md#the-transmit-lease) is held, a test runs only when `POST /api/txtest` names the holder's `subChannel`; the page's button is refused until the lease ends.

### The channel probe

A test asked for through `POST /api/txtest` can follow its tone with a channel-sounding probe: `"probe": {"kind": "zc255", "gapSeconds": 1.5, "audioHz": 1800}`. The tone, the silent gap and the probe go out in one keyup, so the cap, Stop, the channel wait, the lease and the Morse ident treat them as one transmission. pdn-mailcast's head end sends it after the calibration tone in every slot.

- `zc255` is the only kind, with the id `zc255-2400-rrc015-v1`: a Zadoff-Chu sequence of length 255 (root 1) at 2400 chips/s with root-raised-cosine roll-off 0.15, 61 periods of 106.25 ms with 10 ms ramps, 6.5 s in all. At an 1800 Hz centre it occupies 420 to 3180 Hz. Its envelope peaks at `amplitude`, as the tone does, and its average power sits about 3 dB below the tone's.
- `gapSeconds` defaults to 1.5 and `audioHz` to 1800. The whole band must fit the channel, so `audioHz` must be more than 1380 Hz from 0 Hz and from the channel's Nyquist frequency.
- These are refused with 400 and nothing is sent: an unknown or missing `kind`, a negative `gapSeconds`, an `audioHz` that puts the band outside the channel, and any other field inside `probe` (a misspelt `gapSecs` would otherwise quietly mean the default).
- The tone, gap and probe together must fit within `maxSeconds`. A test that would not is refused (409), never cut short. Mailcast's slot (a 10 s tone, the 1.5 s gap and the 6.5 s probe) needs `maxSeconds` of at least 18; the default 30 is enough.
- The reply's `probe` is the id of the probe when all of it went out, or null, and `probeComplete` is true when it did, false when a stop cut it short or kept it off the air, and null when no probe was asked for. A station too old to know the field leaves both keys out, so a head end can tell it sent the tone alone.
- `{"stop": true}` always wins: whatever else the body carries, it stops the test and is answered 200.
- The renderer, `Packet.SoundModem.Audio.ProbeSignal`, is public in the library, with every parameter in its `ProbeDescriptor`, so a receiver can build the identical reference.

## `alsa`

```json
{ "alsa": { "mixer": { "captureGainDb": -12, "playbackDb": 0 } } }
```

`alsa` holds one object, `mixer`. The mixer is opened and read at every start-up whatever the file says. Absent, no level is set from the file; a capture or playback level remembered in the state file from an earlier page or `/api/mixer` change is still applied, and AGC and mic boost are still switched off.

| Key | Type | Default | What it is |
|---|---|---|---|
| `captureGainDb` | number | absent: left alone | Capture gain in dB, inside the card's own range. |
| `playbackDb` | number | absent: left alone | Transmit-side playback level in dB, inside the card's range. |
| `card` | string | derived from `device` | The mixer card when it is not the one the device string implies. With `captureDevice` or `playbackDevice`, it names one mixer for both directions. |
| `stateFile` | string | `mixer-state.json` in the state directory | Where a change made on the station page or over `/api/mixer` is remembered between runs. |
| `captureControls` | string array | `Mic`, `Mic Capture`, `Capture` | Control names to look for the capture gain under, in order. |
| `agcControls` | string array | `Auto Gain Control`, `AGC`, `Mic AGC` | Control names to look for the AGC switch under; it is only ever switched off. |
| `micBoostControls` | string array | `Mic Boost`, `Mic Boost (+20dB)`, `Internal Mic Boost`, `Mic Capture Boost` | Control names to look for the mic boost under; it is only ever switched off. |
| `playbackControls` | string array | `Speaker`, `PCM`, `Master`, `Headphone` | Control names to look for the playback level under, in order. |

- Names are matched case-insensitively; a list of only blank strings falls back to the built-in one.
- The card's range is printed by [`--mixer-show DEVICE`](command-line.md#one-shot-flags) and in the start-up journal. A level outside it is one journal line naming the range, and that control is left alone; start-up continues.
- A level pinned here is applied at every start-up and wins over the state file; the state file fills in only for a control this section says nothing about. AGC and mic boost are switched off at every start-up on any card that has them.
- The default state file is `$STATE_DIRECTORY/mixer-state.json`, which is `/var/lib/pdn-soundmodem/mixer-state.json` under the shipped unit, else a file beside the config file.
- Removed keys are warned about by name. `captureGainPercent` and `playbackPercent`: `alsa mixer: <key> is no longer read; use <captureGainDb or playbackDb>, the card's range is shown by --mixer-show`. `agc` and `micBoost`: `alsa mixer: <key> is no longer a setting: AGC and mic boost are switched off at every start-up on any card that has them ...`, with a second sentence saying to remove the key.
- Refused: `alsa.mixer` beside `monitor` (`A monitor fronts web receivers and has no sound card of its own`); `alsa.mixer` with a `device` that is not a sound card (`"alsa"."mixer" is set but "device" is "X", which is not a sound card`); a `stateFile` that names the configuration file itself (`which is this configuration file. That file is never written by this daemon and a mixer change would overwrite it`).
- A card with no mixer is not a failure: `alsa: mixer: <card> has no mixer (<why>); the capture gain and the transmit level are left as the card has them, and there is no AGC or mic boost to switch off`.
- With [`captureDevice` or `playbackDevice`](#two-cards-capturedevice-and-playbackdevice) on two different cards, and no `card`, each card's mixer is found from its own device. `captureGainDb`, AGC and mic boost go to the receive card, `playbackDb` to the transmit card. The journal lists each card as `<card> (receive) has ...` and `<card> (transmit) has ...`. A card with no mixer costs only its own side: `alsa: mixer: <card> (transmit) has no mixer (<why>); the transmit level is left as the card has it`. The state file is stamped with both devices, so one written for a single card is not applied to the pair.

## `paging`

```json
{ "paging": { "port": 8106, "baud": 1200, "invertPolarity": false } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8106` | The line-based paging TCP port. |
| `baud` | int | `1200` | POCSAG bit rate. The encoder supports 512, 1200 and 2400; the modem does not check the number, and what the POCSAG library does with another value is not documented. |
| `invertPolarity` | bool | `false` | Invert the transmit baseband, for a radio whose data path inverts. |

- Present means on. Paging shares the channel, its carrier sense and the PTT line with the packet modems.
- The `--paging PORT[:BAUD]` flag replaces this section. A port already claimed by another service is refused as under [`kissPort` and `bind`](#kissport-and-bind).

## `ardop`

```json
{ "ardop": { "port": 8515 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8515` | Host-interface command port; data always listens on the next port up. |

- Legacy. A modem entry with `"mode": "ardop"` does the same and can also carry `frequency`, `rfFrequency`, `port` and `bandwidth`. Given only this section, the modem folds it into an `ardop` entry on the lowest free sub-channel at start-up. With no `modems` beside it the file gets no default `afsk1200` and no KISS port, so `{ "ardop": {} }` is an ARDOP-only station.
- Refused beside an `ardop` modem entry, see [`modems`](#modems). The `--ardop PORT` flag wins over this section when both are given, and is refused beside an `ardop` modem entry: `ARDOP is configured twice - as a modem and with --ardop/"ardop". Keep the modem entry.`
- With this section present the port check does not compare `kissPort` against the ARDOP ports, a hold-over from when ARDOP excluded the packet modems, so a `port` equal to `kissPort` beside packet modems passes validation and fails when the KISS listener binds.

## `flex`

```json
{ "device": "flex:10.45.0.76", "flex": { "antenna": "ANT1", "daxChannel": "3", "receiveOnly": true } }
```

`frequency`, `antenna`, `mode` and `stationName` are read for a headless `flex:` device (no `@station`); the M0LTE.Flex package documents the first three as ignored in attach mode. `daxChannel` and `arbitration` apply in attach mode too. `txPowerWatts`, `receiveOnly` and `transmitFilterHighHz` are handed to that package on both paths, and whether attach mode honours them is decided inside it. The two range refusals below fire whatever `device` names; otherwise the section is ignored for any other device.

| Key | Type | Default | What it is |
|---|---|---|---|
| `frequency` | string | `"14.100000"` | Slice frequency in MHz, six-decimal Flex form. A band plan supersedes it. |
| `antenna` | string | `"ANT1"` | Receive and transmit antenna. |
| `mode` | string | `"DIGU"` | Slice demodulation mode. It states the sideband. |
| `daxChannel` | string | `"2"` headless, `"1"` attach | The DAX channel this client claims. Two headless instances on one radio need different channels. |
| `receiveOnly` | bool | `false` | Never write the radio's global transmit state and never contend for a slice; for a second instance sharing a radio with a transmitting station. |
| `txPowerWatts` | number | absent: radio's own setting | Transmit power in watts, 0 to 100. |
| `transmitFilterHighHz` | int | absent: derived from the modems | Transmit-filter high cut in Hz, 500 to 10000; `0` leaves the radio's own filter alone. |
| `stationName` | string | `"pdn-soundmodem"` | The station name this client registers with the radio. |
| `arbitration` | bool | `false` | Key through the arbitrated PTT: wait for the radio to be quiet, re-assert the filter and slice, believe only a confirmed keyup. |

- The `--flex-freq`, `--flex-ant`, `--flex-mode` and `--flex-daxch` flags override `frequency`, `antenna`, `mode` and `daxChannel`.
- Refused: `transmitFilterHighHz` outside 500 to 10000 and not 0 (`"flex"."transmitFilterHighHz" is N. That is an audio cut-off in Hz ... use 500-10000, 0 to leave the radio's own filter alone, or remove it`); `txPowerWatts` below 0 or above 100 (`"flex"."txPowerWatts" is N W, outside the 0-100 W a 6000-series PA can produce`); a stated top-level `sideband` that contradicts what `mode` implies (`"sideband": "usb" contradicts the Flex slice mode DIGL, which is LSB ... Drop "sideband"`). A defaulted `sideband` is corrected to the slice mode's without a word.
- With a band plan, a stated `frequency` is superseded and warned about: `flex: WARNING - the slice frequency you set (F) is superseded by the band plan, which computed D`. The modem sets the slice, the transmit filter high cut and the slice receive filter from the plan and says so.
- A modem outside the radio's transmit or receive filter is a warning naming the modem, its edges and the filter; it is clipped or deaf, not refused.
- `ptt` and `alsa.mixer` are refused with a `flex:` device.

## `rig`

```json
{ "rig": { "rigctld": "127.0.0.1:4532", "mode": "PKTUSB" }, "ptt": { "type": "rigctld" } }
```

A radio controlled through Hamlib's `rigctld`, for any device but `flex:`, `ubersdr:` and `openwebrx:`. Absent, nothing talks to a rig and the station is as it always was.

| Key | Type | Default | What it is |
|---|---|---|---|
| `rigctld` | string | `"127.0.0.1:4532"` | Where rigctld listens: `host:port`, `host` for port 4532, or `[v6address]:port`. |
| `required` | bool | `false` | Stop at start-up (exit 1, so the service retries) when rigctld does not answer, rather than warn and carry on. |
| `mode` | string | absent: `USB`, `LSB` or `FM` from the band plan | The Hamlib mode the band plan sets, such as `PKTUSB` for a rig that takes data-jack audio only in a data mode. |
| `passbandHz` | int | absent: the plan's passband width, 2400 Hz | The passband the band plan asks for; `0` is the rig's normal width for the mode. |

- At start-up the modem connects, unkeys the radio if `ptt` is `rigctld`, and journals what the rig is on: `rig: rigctld at 127.0.0.1:4532: 7.049450 MHz USB (2400 Hz passband)`.
- With a band plan (modems placed by `rfFrequency`) it sets the mode and passband, then the dial: `rig: setting the rig to 7.049450 MHz USB (2400 Hz passband) from the band plan`. Without one the dial is never touched. After a reconnect that finds the rig somewhere else, it is set back to the plan.
- A passband the rig will not take falls back to its normal width, with a `rig: WARNING - the rig would not take a 2400 Hz passband` line; one it sets differently is warned about with what it reports. A dial it refuses is a warning naming where to set the rig by hand.
- A rigctld that does not answer is a warning, `rig: WARNING - cannot reach rigctld at ...`, and is retried every 1 to 30 seconds. One that goes away mid-session is the same, `rig: WARNING - lost rigctld at ...; reconnecting`. Anything owed while it was away (an unkey, a restore, the plan's dial) is done as soon as it answers.
- The modem polls the rig every 5 seconds, except while a frame is waiting to go, and journals a dial or mode changed outside it, at most once a minute.
- A rig that answers with an error (switched off: `RPRT -5`) is treated like a missing rigctld: one warning, then retried quietly until it answers.
- rigctld started with `--vfo` is not supported: the station says so (`rigctld was started with --vfo, which this station does not speak`) and keeps retrying. Start it without `--vfo`.
- With `ptt` `rigctld`, the `T 1` round trip over CAT (often 20 to 100 ms, more on a slow serial link) comes out of TXDELAY, because the preamble starts as soon as rigctld answers. Allow for it if the far end misses the start of your frames.
- A `T 1` that fails is followed at once by `T 0`. An unkey rigctld does not confirm is retried every second with a warning each time, and nothing is keyed or retuned until it goes.
- Refused: `rig` with a `flex:`, `ubersdr:` or `openwebrx:` device; a `rigctld` that is not `host:port`; a `mode` outside `USB`, `PKTUSB`, `LSB`, `PKTLSB`, `FM`, `FMN`, `PKTFM`, `PKTFMN` (other Hamlib modes are for tuning windows only), or one that disagrees with `sideband`; a `passbandHz` below 0 or above 20000.
- `POST /api/rig/tune` and `GET /api/rig` need an [`api`](#api) key; see [rig tuning windows](ports-and-endpoints.md#rig-tuning-windows).
- The rigctld connection has no authentication, which is rigctld's own design. Keep it on loopback or a trusted network.

## `mailcast`

```json
{ "mailcast": { "bbs": { "type": "linBpq", "port": 8011, "password": "pick-one" }, "retune": true, "sources": ["GB7RDG", "M0LTE"] } }
```

The built-in [pdn-mailcast](../14-mailcast.md) receiver. Absent, there is none. Present, the station runs an MS110D receive modem on the mailcast signal beside its own modems, rebuilds the packet mail bulletins its `sources` send to `MCAST`, and forwards each into the BBS by FBB B1F as a forwarding partner. It never transmits, has no KISS port, takes no mail from the BBS, and delivers no other kind of content.

| Key | Type | Default | What it is |
|---|---|---|---|
| `bbs` | object | required | Where the BBS is and how to log in; the keys below. |
| `dialKHz` | number | `7052.0` | The USB dial in kHz a retuned rig is put on. Only a retuned rig needs this set: a sound-card rig that hears the signal on its own passband is tuned to whatever dial puts the signal in the middle of its receive filter (see [the dial rule](../14-mailcast.md#choosing-your-dial)), not to this. 1800 to 30000. |
| `stateDirectory` | string | absent: `mailcast/` in the state directory | Where the pieces, the rebuilt bulletins (`store/`) and the record of deliveries (`deliveries.jsonl`) are kept. |
| `retune` | bool | `false` | When the station's passband does not reach the signal, retune the rig to `dialKHz` USB (a 3000 Hz passband, or the rig's normal width if it refuses) from 1 minute before each slot to 12 minutes after. Needs a [`rig`](#rig) section. |
| `sources` | array of strings | `["GB7RDG", "M0LTE"]` | The callsigns the bulletins are taken from: frames to `MCAST` from anyone else are ignored. Case doesn't matter, and any SSID is accepted, so an SSID given here is ignored. Left out, the default is used and the journal says so at start-up. |
| `hooks` | object | absent: none | Your own commands, run before and after each slot: `before` and `after`, each with the keys below. See [`mailcast.hooks`](#mailcasthooks). |

`bbs`:

| Key | Type | Default | What it is |
|---|---|---|---|
| `type` | string | `"linBpq"` | `"linBpq"` for LinBPQ's mail on its Telnet port's `FBBPORT`, or `"fbb"` for Linux FBB. |
| `host` | string | `"127.0.0.1"` | The BBS's address. |
| `port` | int | `8011` | LinBPQ's `FBBPORT`, or FBB's telnet port. |
| `login` | string | `"Q0CAST"` | The receiver's own login on the BBS, set up as a BBS forwarding partner; never your callsign or one of the `sources`. FBB is sent `.Q0CAST`, asking for a binary session. |
| `password` | string | required | The login's password. Never logged, never served; `GET /api/config` reads it as `(set, not shown)`. |
| `command` | string | `"BBS"` | LinBPQ only: the node command that reaches the mail application. |

- Where it listens is decided at start-up. A station whose passband already holds the whole signal listens there all the time: a headless Flex slice within reach (a dial from about 7.046 to 7.052 MHz), whose receive filter is opened to it; a web receiver whose `ssbLowHz` to `ssbHighHz` window covers it; a Flex in attach mode or an SDR dial within 100 Hz of 7.052 MHz; or, for any other sound-card rig, a dial (from the band plan or `dialFrequency`) that puts the signal's audio centre anywhere from 1000 to 2000 Hz - see [choosing your dial](../14-mailcast.md#choosing-your-dial), which `POST /api/mailcast/measure` works out for you. Otherwise, with `retune` and a `rig` section, the rig is retuned around each slot through the rig's [tuning windows](ports-and-endpoints.md#rig-tuning-windows), to `dialKHz`, and nothing is transmitted until it is put back. Otherwise the station refuses to start: `mailcast: the signal on 7.0524 to 7.0552 MHz is outside what this station hears. ...`, saying what to change.
- The slots are GB7RDG's own (hourly on the hour, in daylight from 2 hours after sunrise to 30 minutes before sunset at IO91lk) until its directory is heard; from then on, the directory's.
- The channel runs at 48 kHz, as it does with any `ms110d-*` modem. On a station that retunes for it, the receiver only runs while the rig is on the mailcast dial.
- `POST /api/config` makes the same placement and state directory checks start-up does, and refuses a configuration that would fail them.
- `POST /api/mailcast/measure` (key required, the same as `/api/txtest` and `/api/mixer`) averages about 10 s of the station's own quiet receive audio - never while transmitting, never during a mailcast slot - and returns the passband edges it found, their width and the dial they suggest; see [Measure or type your dial](../14-mailcast.md#choosing-your-dial). 404 on a station with no `mailcast` section; 409 if a measurement is already running; 504 if 10 s of clear audio never turned up within 2 minutes.
- A rebuilt bulletin stays in the outbox until the BBS has answered for it, and is offered again after a failure (30 s, then up to 30 minutes apart) or 10 minutes after the BBS says "later". Mail the BBS offers back is answered "later", and the journal warns.
- Refused: no `bbs` section; an empty `password`; a `type` other than `linBpq` or `fbb`; a `port` outside 1 to 65535; an empty `host`; a `login` that is not one word; a `password` or `command` over more than one line; a `dialKHz` outside 1800 to 30000; an empty `stateDirectory`; an empty `sources`, or one that is not a callsign of 1 to 6 letters and digits (with an optional SSID 0 to 15); a hook whose `command` is not a full path to a program the service's user may run, whose `timeoutSeconds` is outside 1 to 300, or with any other key (in `hooks` too); `mailcast` in a `monitor` configuration; and, at start-up, a station that cannot hear the signal and cannot retune for it, or an FM station.
- Not on a `--two-tone` or `--tone` run.

### `mailcast.hooks`

```json
{ "mailcast": { "bbs": { "password": "pick-one" }, "hooks": { "before": { "command": "/usr/local/bin/mailcast-hook", "args": ["stop"] }, "after": { "command": "/usr/local/bin/mailcast-hook", "args": ["start"] } } } }
```

Your own programs, run before and after each slot the receiver listens to, for a radio shared with something pdn-soundmodem does not key (Ardopcf, say). [Running your own commands around each slot](../14-mailcast.md#running-your-own-commands-around-each-slot) has a worked example.

`before` and `after`, either of which can be left out:

| Key | Type | Default | What it is |
|---|---|---|---|
| `command` | string | required | The full path of the program to run, directly and not through a shell. It must exist and be executable by the service's user. |
| `args` | array of strings | `[]` | Its arguments, each passed exactly as written. Never logged; `GET /api/config` reads them as `(set, not shown)`. |
| `timeoutSeconds` | int | `30` | How long it may run, 1 to 300. Past it, it and what it started are killed. "before" starts this long ahead of the listening window. |

- "before" starts its `timeoutSeconds` ahead of the listening window, so it is done before the rig is retuned and the transmitter held; "after" runs once the window is over, the rig is back and the station can transmit again. A station that hears the signal without retuning runs them from 2 minutes before each slot to 12 minutes after.
- A "before" that fails (exits non-zero, runs out of time or cannot be started) means the rig is not retuned for that slot, since whatever it was meant to stop may still be transmitting. "after" still runs.
- If the station stops during a slot, "after" runs on the way out, and systemd waits for it. One that did not finish with exit 0 runs again at the next start; until then `hooks.json` sits in the mailcast state directory.
- Each command is told about the slot in `MAILCAST_HOOK` (`before` or `after`), `MAILCAST_SLOT_UTC`, `MAILCAST_DIAL_KHZ`, `MAILCAST_CENTRE_KHZ` and, for "after", `MAILCAST_BEFORE_OK` (`1` or `0`). What it prints goes to the journal on `mailcast: hooks:` lines. Its arguments are never logged, and `/api/config` does not show them.
- The commands run as the service's user, `pdn-soundmodem`, with the service's protections: no `sudo`, no `/home`, and `/usr` and most of `/etc` read-only. Keep scripts somewhere like `/usr/local/bin`, and an ssh key in the state directory.

## `ubersdr`

```json
{ "device": "ubersdr:m9psy-1.instance.ubersdr.org", "ubersdr": { "mode": "iq48", "onDemand": true } }
```

Read for a `ubersdr:` device and for the receivers a `monitor` fronts. Ignored otherwise. The key is matched case-insensitively, so `ubersdr` and `uberSdr` name the same section.

| Key | Type | Default | What it is |
|---|---|---|---|
| `mode` | string | `"iq48"` | The receiver's IQ mode; `iq96` where an instance allows it. |
| `password` | string | absent | Password for a protected instance. |
| `ssbLowHz` | number | `150` | Lower edge of the SSB filter synthesised from the IQ, Hz above the dial. |
| `ssbHighHz` | number | `3450` | Upper edge of that filter. |
| `startupGuardMs` | int | `1000` | Audio discarded after each connect, in ms. |
| `gain` | number | `1.0` | Linear gain on the demodulated audio. |
| `onDemand` | bool | `false` | Hold a session on the receiver only while somebody has the station page open. |
| `lingerSeconds` | int | `60` | With `onDemand`, how long the session is kept after the last viewer leaves; 0 closes at once. |

- The receiver is tuned by the band plan: every modem needs an `rfFrequency`, or `dialFrequency` must be set. Otherwise: `the UberSDR instance at X has to be told where to listen. Give every modem an "rfFrequency" ... or set "dialFrequency" to pin it`.
- Refused: `"sideband": "fm"` (`cannot be served by X: a web receiver is an SSB receiver`); `onDemand` without a `waterfall` section (`"ubersdr"."onDemand" needs a "waterfall" section`); a negative `lingerSeconds` with `onDemand` (`"ubersdr"."lingerSeconds" cannot be negative`); any `ptt`, `alsa.mixer`, `publish` or `identify`, each named under its own section.
- The station receives only. Frames arriving over KISS are refused with `tx[N] DROPPED ... this station receives only`, and the transmitter test is unavailable.
- A receiver that cannot be reached at start-up exits 1 either way, and the service retries. With `onDemand`, a receiver that goes away later, or refuses a session, is retried while the page stays up and says so.

## `openwebrx`

```json
{ "device": "openwebrx:http://sdr.example.org:8073/", "openwebrx": { "profile": "40m" } }
```

Read for an `openwebrx:` device. Ignored otherwise. The receiver demodulates: this section picks its demodulator's passband and, if you name one, the receiver profile to ask for. The demodulator follows the band plan's sideband: `usb`, `lsb`, or narrow FM for `"sideband": "fm"`.

| Key | Type | Default | What it is |
|---|---|---|---|
| `profile` | string | absent: the band the receiver is on | The receiver profile to ask for, by the name its page lists or by its id (`sdr\|profile`). |
| `ssbLowHz` | int | `150` | Bottom of the SSB passband asked for, Hz from the dial; mirrored for LSB. |
| `ssbHighHz` | int | `3450` | Top of that passband; at most 6000. |
| `fmHalfWidthHz` | int | `5000` | Half the width of the FM channel filter asked for, with `"sideband": "fm"`; at most 6000. |
| `startupGuardMs` | int | `1000` | Audio discarded after each connect, in ms, while the receiver's AGC settles. |
| `gain` | number | `1.0` | Linear gain on the received audio. |

- The receiver is tuned by the band plan, as for `ubersdr`: every modem needs an `rfFrequency`, or `dialFrequency` must be set (`the OpenWebRX receiver at X has to be told where to listen ...`).
- Asking for a `profile` moves the receiver for everyone listening to it, so it is asked once per session, ten seconds after connecting (OpenWebRX+ counts a quicker change against the client as a robot). If somebody else moves the receiver off the dial later, the station says so in the journal and waits for it to come back; the next session asks again.
- Without a `profile`, a receiver whose band does not reach the dial stops start-up with exit 1, naming the band it is on and the profiles it offers.
- The receiver sends 12 kHz audio. The channel's DSP rate has to be a whole multiple of 12000; the 12 kHz and 48 kHz mode families both are.
- The audio may be 4-bit ADPCM. That is the receiver operator's setting, and the start-up line says which.
- Refused, exit 2: `ssbLowHz` and `ssbHighHz` that do not rise from 0 to at most 6000; an `fmHalfWidthHz` outside 1 to 6000; a negative `startupGuardMs`; a `gain` of 0 or less; an empty `profile`; any `ptt`, `rig`, `alsa.mixer`, `publish` or `identify`, each named under its own section.
- The station receives only, as on `ubersdr:`. A receiver that cannot be reached, or will not serve the dial, at start-up exits 1 and the service retries. A busy receiver (`Too many clients`) does not stop start-up; the station waits and asks again.

## `waterfall`

```json
{ "waterfall": { "port": 8107, "public": false, "title": "GB7RDG 40 m" } }
```

There is no `bind` key here. The page listens on the top-level `bind`.

| Key | Type | Default | What it is |
|---|---|---|---|
| `port` | int | `8107` | The HTTP and WebSocket port; `api` and `metrics` share it. |
| `dialFrequencyHz` | number | `0` | The page's opening dial in Hz. 0 takes the band plan's dial or `dialFrequency`, else audio frequencies only. |
| `sideband` | string | `"usb"` | What the page draws for: `"usb"`, `"lsb"` or `"fm"`. The band plan's answer wins when there is one; a top-level `"fm"` wins when this is not stated. |
| `linesPerSecond` | int | `30` | Waterfall line rate. Must divide the DSP rate. |
| `fftSize` | int | `0` | FFT length; 0 takes 2048 at 12 kHz or 8192 at 48 kHz. Must be a power of two no shorter than one line's hop. |
| `public` | bool | `false` | A page for visitors: shows `title` and `about`, credits the receiver, hides the KISS host badges, never carries the mixer or the transmitter test. |
| `enableAudioControls` | bool | `false` | Serve the mixer group and `/api/mixer` with no `api.key`, on an operator's page only. |
| `title` | string | the page's own | Public page title. |
| `about` | string | none | One paragraph for a visitor on a public page. |

- The `--waterfall PORT` flag stands in for or overrides `port`; `--dial HZ` sets `dialFrequencyHz` and is refused without a page.
- Refused: a `sideband` that is not `usb`, `lsb` or `fm` (`"waterfall"."sideband": "X" is not a kind of radio this knows`); a port already claimed (see [`kissPort` and `bind`](#kissport-and-bind)); a port that cannot be opened (`cannot serve the waterfall on ADDR:PORT ... Set by "waterfall"."port" and the top-level "bind"`); an `fftSize` or `linesPerSecond` the spectrum source will not take (`invalid waterfall settings ... Set by "waterfall"."fftSize" and "waterfall"."linesPerSecond"`).
- `enableAudioControls` with `public` is ignored and said so: `waterfall: "enableAudioControls" is IGNORED on a "public" page`.
- A `bind` beyond loopback prints a `waterfall: WARNING - listening beyond loopback` line naming the transmitter test on an operator's page and, when open, the mixer; the text is under [Every listener](ports-and-endpoints.md#every-listener).
- The mixer group and its level meter appear only on an operator's page of a sound-card station where `/api/mixer` answers, which needs `api.key` or `enableAudioControls`.

## `monitor`

```json
{
  "bind": "*",
  "waterfall": { "port": 8099, "title": "UK packet monitor" },
  "monitor": {
    "publicUrl": "https://monitor.example.org",
    "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ],
    "uplinks": [ { "callsign": "GB7RDG-2", "slug": "gb7rdg-2", "tokenSha256": "<64 hex>" } ]
  }
}
```

A file with `monitor` describes a site, not a station. It reads `bind`, `sideband`, `dialFrequency`, `modemPlugins`, `waterfall`, `ubersdr`, `frameLog`, `idBeacons`, `deadFeed` and this section; it serves no KISS, PTT, API, survey, paging, ARDOP, metrics or transmitter test. The other station sections (`kissPort`, `captureRate`, `ptt`, `api`, `survey`, `metrics`, `rawCapture`, `frequencyMatching`, `paging`, `ardop`, `txTest`) are known keys, so a monitor file accepts them without a word and never reads them; only `device`, `publish` and `alsa.mixer` are refused.

| Key | Type | Default | What it is |
|---|---|---|---|
| `publicUrl` | string | `""` | This site's address as the world reaches it, scheme and host and optional port only; empty derives it from each request's `Host` header. |
| `directory` | string | `https://instances.ubersdr.org/api/instances` | Where the receiver list is fetched from; an absolute http or https URL. |
| `refreshMinutes` | int | `5` | How often the directory is fetched again; 0 fetches once at start-up. |
| `lingerSeconds` | int | `60` | How long a receiver's session is held after its last viewer leaves. |
| `allow` | string array | `[]` | When non-empty, the only hosts offered, matched on the directory's `host` case-insensitively. |
| `deny` | string array | `[]` | Hosts never offered; `deny` beats `allow`. |
| `modems` | array | `[]`; required | The modems every receiver runs, in the `modems` schema; every entry needs an `rfFrequency`. |
| `uplinks` | array | `[]` | Private stations this site accepts an uplink from. Empty refuses every connection to `/uplink`. |

Each `uplinks` entry:

| Key | Type | Default | What it is |
|---|---|---|---|
| `callsign` | string | required | The callsign the station must say it is; one to six letters and digits with an optional `-SSID`. |
| `slug` | string | required | The path segment its page is served under, `/r/<slug>/`: lower-case letters, digits and hyphens, no hyphen at either end. |
| `tokenSha256` | string | required | The SHA-256 of the token issued to the station, 64 hex characters, as printed by [`--uplink-token`](command-line.md#one-shot-flags). |

Rules and refusals:

- `waterfall.public` is forced true on a monitor.
- Refused: `device` written in the file (`this file sets both "device" ("X") and "monitor"`); `monitor.modems` empty (`"monitor"."modems" is empty`); no `waterfall` section (`"monitor" needs a "waterfall" section`); a `waterfall` with no `port` written down (`"waterfall" has no "port". A monitor serves its whole site on that one port`); no `monitor.modems` entry with an `rfFrequency` (`"monitor"."modems" has no "rfFrequency"`; a list where only some entries have one is not caught and fails with an unhandled exception); `"sideband": "fm"` (`cannot be served by a monitor`); a negative `refreshMinutes` or `lingerSeconds`; a `directory` that is not an absolute http or https URL; a `publicUrl` with credentials in it (the message does not repeat the value) or with anything after the host and port; an `allow` or `deny` entry that is not a bare hostname; `alsa.mixer` or `publish` beside `monitor`; a modem the station could not build, with the same message a station gets.
- `uplinks` refusals: a `callsign` that is not one, a `slug` that cannot be a path segment, a `tokenSha256` that is not 64 hex characters, and two entries sharing a slug, a callsign or a hash. Each names the entry and what to write instead.
- `frameLog.path` is a directory on a monitor, one `frames-<slug>.db` per receiver; a path that is a file or ends `.db` is refused with a sentence saying so.

## `publish`

```json
{ "publish": { "url": "wss://monitor.example.org/uplink", "token": "<issued>", "callsign": "GB7RDG-2", "operator": "M0LTE" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `url` | string | required | The site's uplink endpoint, an absolute `ws` or `wss` URL. |
| `token` | string | required | The token the site issued this station, at least 32 characters, pasted in as given. |
| `callsign` | string | required | This station's callsign, one to six letters and digits with an optional `-SSID` as under `monitor.uplinks`; the site checks it against the token. |
| `operator` | string | absent | Who runs it, up to 40 characters. |
| `location` | string | absent | Roughly where it is, up to 60 characters. |
| `radio` | string | absent | The radio and antenna, up to 60 characters. |
| `site` | string | absent | The operator's own page, an absolute http or https URL. |
| `audioRate` | int | the DSP rate capped at 12000 | The rate audio is published at, 6000 to 48000 and an integer divisor of the DSP rate; the relayed picture spans 0 to half of it. |
| `frames` | string | `"always"` | `"always"` publishes decoded frames whether or not anybody is watching; `"watched"` holds them until somebody is. |

- One way only: audio, frames and a status sentence go up, a viewer count comes down. Nothing on the wire can transmit, retune or reconfigure the station.
- Refused: `publish` beside `monitor` (`one process is not both`); on a `ubersdr:` device (`A receiver like that is already on the monitor site in its own right`) or an `openwebrx:` one (`which is somebody else's public web receiver`); without a `waterfall` section (`"publish" needs a "waterfall" section`); a `url` that is not an absolute ws or wss URL; a `token` missing or under 32 characters; a `callsign` that is not one; a `site` that is not an absolute http or https URL; an `operator`, `location` or `radio` over its limit (`is N characters and the limit is L`); a `frames` other than `always` or `watched`; an `audioRate` outside 6000 to 48000, or one that does not divide the DSP rate once the modems are known (`which N does not divide. The audio is decimated rather than resampled, so it has to be an integer divisor: <list>`).
- Warnings: a plain `ws` URL off the machine (`publish: "url" is "...", which is unencrypted ws to <host> ... Use wss unless this is a test on your own wire.`); a modem above half the published rate (`publish: WARNING - the published audio spans 0 to N Hz, so modem ... will not appear on the site`); a 48000 Hz rate (`about 770 kbit/s upstream while somebody is watching`).
- Roughly 194 kbit/s upstream at 12000 Hz while somebody is watching, 98 at 6000, 770 at 48000. There is no codec.

## `api`

```json
{ "waterfall": { "port": 8107 }, "api": { "key": "<long random string>" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `key` | string | absent: no API | The shared secret every request must present, as `Authorization: Bearer KEY` or `X-API-Key: KEY`. |

- Served under `/api/` on the page port. There is no unauthenticated mode: no key, no API, except `/api/mixer` alone when `waterfall.enableAudioControls` is true.
- Refused: `api` without a `waterfall` section (`"api" is served on the waterfall's HTTP listener, and this station has no "waterfall" section - add one, or remove "api"`); `api` on a station run without `--config` (`"api" needs a --config file to read back and to write changes to`).
- `POST /api/config` takes a whole document, validates it with the same checks as start-up, and restarts the process onto it for one run; `?persist=true` writes it to the config file, the only time the modem writes that file. The mechanics are under [the API](ports-and-endpoints.md#the-api-under-api).
- The start-up journal names the endpoints: `api: configuration over http://.../api/config (key required)`, and `api: modem proposals over .../api/proposals` when `survey.propose` is on.
- `POST /api/txlease` gives one sub-channel the transmitter for up to five minutes at a time, renewable, and refuses every other sub-channel's transmissions until it is released or runs out. It is for a scheduled transmission that shares the radio with ordinary traffic; there is nothing to configure. A modem with `identify` sends a closing ident when its lease ends. See [the transmit lease](ports-and-endpoints.md#the-transmit-lease).

## `frameLog`

```json
{ "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `frames.db` in the state directory: `/var/lib/pdn-soundmodem/frames.db` under the shipped unit, `/var/lib/pdn-soundmodem/NAME/frames.db` under `pdn-soundmodem@NAME` | The SQLite file. On a monitor, the directory the per-receiver files go in. |

- Present means every frame heard and every frame sent is written down, with its audio and RF placement and the decode quality.
- Refused when the file cannot be opened: `cannot open the frame log at <path> ... Set by "frameLog"."path". The service user must be able to write to its directory; remove the "frameLog" section to run without one.`
- With a log, the page's frames panel and links pane open on what is already recorded, and frequency matching replays recent offsets across a restart.

## `survey`

```json
{ "survey": { "path": "/var/lib/pdn-soundmodem/survey", "maxBytes": 536870912, "propose": true } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `survey` in the state directory (`/var/lib/pdn-soundmodem/survey` under the shipped unit) | Where captures go, a WAV and a JSON sidecar per burst. |
| `maxBytes` | int64 | `536870912` (512 MiB) | Byte budget for the directory; the oldest captures are deleted to make room. |
| `maxPerHour` | int | `30` | Most captures in any rolling hour. |
| `cooldownSeconds` | number | `120` | How long the same part of the spectrum is left alone after a capture. |
| `marginSeconds` | number | `1.0` | Audio kept either side of the burst. |
| `maxSeconds` | number | `20` | Longest burst still treated as a packet. |
| `minPeakSnrDb` | number | `6` | Weakest burst kept, in dB over the noise floor. |
| `capture` | string array | the three verdicts | Which verdicts to write: `unclaimed`, `missed`, `unattributed`. |
| `propose` | bool | `false` | Read each capture back with every mode that could have carried it and propose modems. |
| `proposeMinCaptures` | int | `3` | Separate verified captures a proposal needs; values below 1 are treated as 1. |

- There is no `decodeClaimSeconds` key.
- An unknown `capture` entry is skipped with `survey: ignoring unknown capture kind "X" (unclaimed, missed, unattributed)`; if none is usable the default three apply.
- Refused when the directory cannot be opened: `cannot open the survey directory at <path> ... Set by "survey"."path".`
- Proposals reach the journal as `propose: ...` and, with an `api.key`, `GET /api/proposals`. The captures are served at `/survey/<file>` on the page port.

## `metrics`

```json
{ "waterfall": { "port": 8107 }, "metrics": { "enabled": true } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Whether to serve `/metrics` (Prometheus text) and `/metrics/frames` (InfluxDB line protocol). |
| `maxStations` | int | `256` | Most stations kept; the least recently heard is dropped on reaching it. |
| `frameWindowSeconds` | number | `300` | How long a frame stays in the per-frame feed; values below 1 are treated as 1. |
| `stationIdleHours` | number | `6` | How long a station keeps its series after its last frame; values below 0.1 are treated as 0.1. |

- Served on the page port with no authentication. Without a `waterfall` section the metrics are collected and a warning says there is nothing to serve them on: `metrics: WARNING - nothing to serve them on. They ride the waterfall's listener; add a "waterfall" section with a port, or remove "metrics".`
- Unknown keys in this section are not reported.
- A Grafana dashboard for these series is at [grafana/pdn-soundmodem.json](grafana/pdn-soundmodem.json).

## `frequencyMatching`

```json
{ "frequencyMatching": { "enabled": true, "maxTrimHz": 50 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `enabled` | bool | `true` | Shift the transmitter towards a station's measured offset. `false` measures and reports only. |
| `samples` | int | `8` | Frames kept per station for the estimate. |
| `maxAgeSeconds` | number | `600` | How old a frame may be and still count. |
| `minSamples` | int | `3` | Frames required before the estimate is acted on. |
| `maxSpreadHz` | number | `20` | Largest spread across those frames that still counts as settled. |
| `maxTrimHz` | number | `50` | Largest shift ever applied. |
| `damping` | number | `0.5` | Fraction of the offset applied to a station that has already moved under a correction once. |
| `chaseThresholdHz` | number | `10` | How far a station's frequency may move after correction starts before the correction stops. |
| `chaseCooldownSeconds` | number | `1800` | How long that station is left alone before its new offset is corrected for again. |
| `maxChases` | int | `3` | How many such moves before the station is given up on; 0 retries for ever. |

- On by default, including when the section is absent; the journal opens with `frequency matching: on - ...` and the thresholds in force. Offsets are measured either way.
- Broadcast destinations (`ID`, `BEACON`, `CQ`, `QST`, `ALL`, `NODES`, `MAIL`, `APRS`, `TEST`, with or without an SSID) are never aimed at one station.
- Unknown keys in this section are not reported. The design is in [frequency matching](../dev/frequency-matching.md).

## `rawCapture`

```json
{ "rawCapture": { "path": "/var/lib/pdn-soundmodem/raw", "chunkMinutes": 15 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `path` | string | `raw` in the state directory (`/var/lib/pdn-soundmodem/raw` under the shipped unit) | Where the chunks are written. |
| `maxBytes` | int64 | `4294967296` (4 GiB) | Byte budget for the directory; the oldest chunks are pruned to fit. |
| `chunkMinutes` | int | `15` | Audio minutes per WAV chunk. |

- Present means on: the unedited receive audio at the DSP rate, continuously. The default budget holds about two days at 12 kHz.
- Refused when the directory cannot be opened: `cannot open the raw-capture directory at <path> ... Set by "rawCapture"."path".`

## `deadFeed`

```json
{ "deadFeed": { "silenceSeconds": 30, "starvationSeconds": 30 } }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `silenceSeconds` | number | by device | Seconds of unbroken digital silence (every sample zero) that declare the feed dead; 0 turns the watch off. |
| `starvationSeconds` | number | by device | Seconds with no samples delivered at all that declare the feed starved; 0 turns the watch off. |

Per-device defaults, used for whichever key is absent:

| Input | `silenceSeconds` | `starvationSeconds` |
|---|---|---|
| `flex:` | 30 | 30 |
| `ubersdr:` | 30 | 30 |
| `openwebrx:` | 30 | 30 |
| ALSA sound card, `pipe:` | 0 (off) | 30 |
| `--wav-loop`, `flex:mock` | 0 (off) | 0 (off) |
| an uplinked station on a monitor | 0 (off) | 30 |

- Either watch firing stops the process with exit 1, so the unit restarts it and rebuilds the input from scratch.
- Refused: a negative value (`"deadFeed"."silenceSeconds" is N. That is how many seconds the watch waits ... use a positive number of seconds, 0 to turn that watch off, or remove it for the device's default.`).
- A Flex that has stood down from a contested slice is silent on purpose and is not restarted by the silence watch.

## `idBeacons`

```json
{ "idBeacons": true }
```

| Key | Type | Default | What it is |
|---|---|---|---|
| `idBeacons` | bool | `true` | Run a 300 baud AFSK AX.25 listener 200 Hz above each PSK modem's carrier, where a NinoTNC sends its station identification. |

- Applies to `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `bpsk1200`, `bpsk1200-multi`, `qpsk600` and `qpsk2400`; `qpsk3600` and every other mode are unaffected. Idents heard are shown on the page, written to the frame log and counted by the survey as decoded; nothing a host sees changes.

## Band placement: `sideband`, `dialFrequency` and `rfFrequency`

```json
{
  "sideband": "usb",
  "dialFrequency": 7049450,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
    { "subChannel": 1, "mode": "bpsk300", "rfFrequency": 7051600 }
  ]
}
```

| Key | Where | What it does |
|---|---|---|
| `sideband` | top level | `"usb"`, `"lsb"` or `"fm"`. Which arithmetic turns an RF frequency into an audio one. |
| `dialFrequency` | top level | Pins the dial in Hz. Absent, the modem chooses one, prints it, and on a headless Flex, a web receiver or a rig with a `rig` section sets it. |
| `rfFrequency` | each `modems[]` entry | The modem's place on the band in absolute Hz. All entries or none. |

The arithmetic, per sideband:

| `sideband` | Audio centre of a modem | Dial when not pinned |
|---|---|---|
| `usb` | `rfFrequency - dial` | Chosen so the modems sit centred in a 300 to 2700 Hz passband, rounded to 50 Hz. |
| `lsb` | `dial - rfFrequency` | The same, mirrored. |
| `fm` | none; `frequency` is a tone on the channel and is left as written | `rfFrequency` is the channel and every modem must give the same one. |

Rules and refusals:

- On USB and LSB the plan writes each modem's audio centre back into `frequency`, so a `frequency` written beside an `rfFrequency` is refused (see [`modems`](#modems)). On FM the page and the plan do no arithmetic and `frequency` stays what it was.
- The passband is the nominal 300 to 2700 Hz of an ordinary SSB rig. A headless Flex, whose filters the modem sets itself, is widened as far as 10000 Hz when the modems need it, and the transmit filter high cut and the slice receive filter follow.
- A chosen dial that leaves a modem outside the passband is refused: `band plan: no dial frequency places every modem inside the 300-2700 Hz passband:` followed by one line per offending modem; when the modems span more than the passband the line reads `these modems span N Hz of RF (... to ...), which is more than the 2400 Hz a single SSB passband can carry`.
- A pinned `dialFrequency` that leaves a modem outside is a warning, not a refusal: `band plan: WARNING - with the dial pinned to D USB, these fall outside the nominal 300-2700 Hz passband. That is only a nominal figure - if your rig passes them, ignore this`.
- A baseband mode (`fsk*`, `c4fsk*`) cannot take an `rfFrequency` on USB or LSB: `band plan: modem N (X) is a baseband mode ... cannot be placed with "rfFrequency"`. On FM it can; `c4fsk9600` on a channel is the ordinary case.
- On FM, modems asking for different channels are refused: `on FM every modem is on the one channel the radio is set to, and these ask for A, B`. A `dialFrequency` that differs from the modems' `rfFrequency` is refused: `On FM those are the same thing said twice - the channel`.
- A `sideband` that is not `usb`, `lsb` or `fm`, at the top level or under `waterfall`, is refused: `"sideband": "X" is not a kind of radio this knows. Use "usb", "lsb" or "fm"`.
- On a headless Flex the slice `mode` states the sideband (`DIGU` and `USB` mean `usb`, `DIGL` and `LSB` mean `lsb`, `FM` and `NFM` mean `fm`); see [`flex`](#flex).
- With no `rfFrequency` anywhere there is no plan: modems sit at their `frequency`, the page's dial comes from `waterfall.dialFrequencyHz` or `dialFrequency`, and a web receiver needs `dialFrequency` to tune at all.
- The plan is printed at start-up as `dial: <MHz> USB` (or `channel: <MHz> FM`) followed by one `modem N <mode> at <MHz> = <Hz> Hz audio` line per modem. Refusals and warnings from the plan are prefixed `band plan:`.

Related: [the guide](../README.md), [command-line reference](command-line.md), [ports and endpoints](ports-and-endpoints.md), [files and directories](files.md).
