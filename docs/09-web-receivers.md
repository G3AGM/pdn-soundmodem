# Web receivers

You will finish with a station that decodes packet off somebody else's antenna, on a machine with no radio, no sound card and no PTT lead.

pdn-soundmodem can take its audio from a public UberSDR web receiver instead of a sound card. UberSDR is receiver software that streams a live radio to a browser, with many public instances. The modem asks one of them for raw IQ, the untouched signal before any demodulation, and demodulates it here. Everything after that is the same as a radio station: the same modes, the same station page, the same frame log, the same KISS ports for your node or APRS software.

The one difference is that it receives and never transmits.

Most of this page is about UberSDR. An OpenWebRX or OpenWebRX+ receiver works too, with some differences, which [Listen through an OpenWebRX receiver](#listen-through-an-openwebrx-receiver) covers.

## Before you start

- pdn-soundmodem installed, with the config file at `/etc/pdn-soundmodem/soundmodem.json`. See [01-install.md](01-install.md).
- The address of a public UberSDR instance, such as `m9psy-1.instance.ubersdr.org`.
- The frequencies you want to listen on, in Hz. HF packet lives around 7.050 MHz in the UK; [08-hf.md](08-hf.md) covers band plans.
- Outbound HTTPS from the machine. No sound card, no interface and no PTT line.

## Point the modem at a receiver

Set [`device`](reference/config.md#device) to `ubersdr:` followed by the instance.

```json
"device": "ubersdr:m9psy-1.instance.ubersdr.org"
```

Write the instance however you have it. A bare host, a `host:port`, or the whole URL out of the browser's address bar (`ubersdr:https://m9psy-1.instance.ubersdr.org/`) all name the same receiver. HTTPS on port 443 is assumed unless the string says otherwise.

A radio has a dial you can read off. A web receiver has none, so the modem tunes it from your band plan. Give every modem an [`rfFrequency`](reference/config.md#band-placement-sideband-dialfrequency-and-rffrequency) in Hz, or set a top-level `dialFrequency` to pin the dial yourself. With neither, start-up stops with this:

```
the UberSDR instance at m9psy-1.instance.ubersdr.org has to be told where to listen. Give every modem an "rfFrequency" and the dial is worked out from them, or set "dialFrequency" to pin it - unlike a radio there is no dial already set to read off.
```

A web receiver is an SSB receiver, so `"sideband": "fm"` is refused. Point `device` at a sound card fed by an FM radio instead.

## Start it and read the journal

```
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -f
```

Among the start-up lines you should see the dial the modem chose, the audio line, and the receiver naming itself:

```
dial: 7.048800 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 1500 Hz audio
kiss: this station receives only - frames arriving on these ports are refused, not transmitted. Everything the modems hear is still delivered.
audio: m9psy-1.instance.ubersdr.org iq48 IQ at 7.048800 MHz -> USB 150-3450 Hz audio at 12000 Hz (RECEIVE ONLY)
ubersdr: M9PSY-1, RX888 with 40m Full Wave Loop (GPSDO), Dalgety Bay, Scotland, UK, reference offset 0 Hz
ubersdr: session limit 10800 s - the stream is picked up again each time the receiver ends one
```

The KISS ports are still served and everything the modems hear still reaches your node or APRS software. Frames sent the other way come back as `tx[N] DROPPED` with the reason. The transmitter test on the station page is unavailable and says so.

Four things are refused on this device. [`ptt`](reference/config.md#ptt) and a modem's [`identify`](reference/config.md#modemsidentify) need a transmitter. [`alsa.mixer`](reference/config.md#alsa) needs a sound card. [`publish`](reference/config.md#publish) is refused because a public receiver is on the monitor site in its own right. An `ardop` modem loads and hears the channel, with a start-up warning that no ARQ session can complete.

## Shape what you hear

The [`ubersdr`](reference/config.md#ubersdr) section sets the stream's parameters. All of it is optional.

```json
"ubersdr": { "mode": "iq48", "gain": 1.0 }
```

`mode` is the receiver's IQ mode. `iq48` is 48 kHz of complex baseband and is what every public instance offers; use `iq96` only where an instance allows it. `password` is for a protected instance.

`ssbLowHz` and `ssbHighHz` are the receive filter, in Hz above the dial. The defaults of 150 and 3450 clear the whole 300 to 2700 Hz band a plan can place modems in, with room either side. Narrow them to emulate a tighter rig.

`gain` scales the demodulated audio. The decoders hardly care about level, so it is there for the waterfall: raise it above 1.0 if a quiet instance makes the picture hard to read. `startupGuardMs` discards the first second after each connect, which is the level ramp at the head of an instance's stream.

## Connect only while somebody is watching

A station feeding a node wants the receiver all day. A page for visitors has nothing to show while nobody is looking, and the receiver's listener slot is somebody else's to lend. Set `onDemand`:

```json
"ubersdr": { "onDemand": true, "lingerSeconds": 60 }
```

The first browser to open the station page opens the session, every further browser shares it, and the last one to leave starts the `lingerSeconds` clock. A page refresh or a tab switch then costs the receiver nothing. `0` closes the session at once.

`onDemand` needs a [`waterfall`](reference/config.md#waterfall) section, since the page's viewers are what asks for the receiver. The audio line says what it is doing:

```
audio: m9psy-1.instance.ubersdr.org iq48 IQ at 7.048800 MHz -> USB 150-3450 Hz audio at 12000 Hz (RECEIVE ONLY, on demand: connected while the waterfall has a viewer, held 60 s after the last leaves)
```

Every later `ubersdr:` line carries the viewer count, so `ubersdr: live, 2 viewers: ...` and `ubersdr: lingering, 0 viewers: ...` tell you who a reconnect is for.

## Put the page in front of visitors

Set `public` on the [`waterfall`](reference/config.md#waterfall) section to dress the station page for people who did not build it.

```json
"waterfall": {
  "port": 8099,
  "public": true,
  "title": "40 m packet monitor",
  "about": "The 7050-7052 kHz packet window, receive only."
}
```

`title` goes in the tab and the top bar. `about` is one paragraph under it. An on-demand station also credits the web receiver it listens through and links to it.

A public page hides the KISS host badges, the dial and display controls, the stats and the links pane's Mine filter, and it never carries the mixer or the transmitter test. The waterfall, the links pane, the decoded frames and Listen all stay. [07-station-page.md](07-station-page.md) goes through every control.

Serving the page beyond the machine needs a top-level [`bind`](reference/config.md#kissport-and-bind) of `"*"`. There is no `bind` inside `waterfall`. Anything that can reach the port can watch the station, and watching is all it can do.

## Keep the frames

A receive-only station is a listening post, so turn on the [`frameLog`](reference/config.md#framelog) and let it accumulate.

```json
"frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
```

Every frame heard is written to SQLite with its placement and decode quality, and the page's frames and links panels open on what is already there. [11-logging-and-metrics.md](11-logging-and-metrics.md) has the queries and the metrics endpoints.

## A complete config

A 40 m listening post on three modes, on demand, with a public page:

```json
{
  "device": "ubersdr:m9psy-1.instance.ubersdr.org",
  "bind": "*",
  "kissPort": 8105,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300, "port": 8101 },
    { "subChannel": 1, "mode": "bpsk300",       "rfFrequency": 7051600, "port": 8102 },
    { "subChannel": 2, "mode": "qpsk600",       "rfFrequency": 7052200, "port": 8103 }
  ],
  "ubersdr": { "onDemand": true, "lingerSeconds": 60 },
  "waterfall": {
    "port": 8099,
    "public": true,
    "title": "40 m packet monitor",
    "about": "The 7050-7052 kHz packet window, receive only."
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
}
```

## Check it worked

Open `http://<machine>:8099/` and look at the top of the page. It should name the receiver and link to it, and the waterfall should be moving within a few seconds of the page opening.

```
journalctl -u pdn-soundmodem -o cat | grep '^ubersdr:'
```

A working station shows the receiver description and the session limit. A decode looks the same as it would off a radio:

```
rx[0] afsk300-il2pc M0LTE>GB7IOW-1 15 bytes  crc ok  fec 0  -5 Hz
```

## If it did not

`cannot stream IQ from "ubersdr:..."` with `cannot reach the UberSDR instance` means the address is wrong or the instance is down. The message includes a `curl` line to check it with. The service keeps retrying.

`refusing this address for now` at start-up, or `is refusing us for now` on a later try, means the daily listening allowance for your address is spent, or the instance is rate-limiting you. Nothing in the config fixes that. The station stays up and starts hearing audio when the receiver lets it back in.

`has been unreachable for 5 minutes` stops an always-on station with exit 1, and systemd restarts it. With `onDemand` a receiver that goes away keeps the station running. The page stays up, says what is wrong, and keeps trying while anybody is waiting.

The waterfall moves but nothing decodes. Check the modem is on the right `rfFrequency` and the right mode for what is on air; see [05-modes.md](05-modes.md) and the checklist in [12-troubleshooting.md](12-troubleshooting.md).

## Listen through an OpenWebRX receiver

OpenWebRX is the other common web receiver software, and OpenWebRX+ is a fork of it; both work. Set `device` to `openwebrx:` followed by the address you would open in a browser:

```json
"device": "openwebrx:http://sdr.example.org:8073/"
```

Give the whole URL, path included, if the receiver lives under one (`openwebrx:https://example.org/owrx/`). A bare host or `host:port` is taken as plain HTTP on port 8073, where OpenWebRX installs.

The band plan tunes it exactly as it tunes an UberSDR, so every modem needs an `rfFrequency` or the file needs a `dialFrequency`.

### What is different

An OpenWebRX receiver sends demodulated audio, not IQ. Its demodulator, its passband filter and its AGC are in the path, and none of them is ours.

- **The sideband picks the demodulator.** `usb` and `lsb` use the receiver's SSB demodulator. `"sideband": "fm"` uses its narrow FM demodulator, which an UberSDR cannot do, so a VHF FM packet channel can be heard this way.
- **The audio is 12 kHz.** That suits the 12 kHz and 48 kHz mode families. Anything faster than 1200 baud through FM audio, such as 9600 GFSK, is not realistic.
- **The audio may be compressed.** Many receivers send 4-bit ADPCM. That is the receiver operator's setting and a listener cannot turn it off. The start-up line says which you are getting. What it costs each mode is under [What ADPCM costs](#what-adpcm-costs).
- **The receiver is on one band at a time.** Its operator, or another listener, chooses a profile, and that profile sets the band the receiver covers. If that band does not reach your dial, start-up stops and lists the profiles on offer:

```
cannot listen through "openwebrx:http://sdr.example.org:8073/"
  sdr.example.org:8073 is on 445.506250 to 446.506250 MHz (profile "PMR 446"), which does not reach the dial 7.074000 MHz. Choose the profile that does with "openwebrx": { "profile": "..." }; it offers "HF 40 Metri" (rtlsdr|5849...), "PMR 446" (rtlsdr|2a7b...).
```

Name the one you want in the [`openwebrx`](reference/config.md#openwebrx) section:

```json
"openwebrx": { "profile": "HF 40 Metri" }
```

Choosing a profile moves the receiver for everyone listening to it, so the station asks once each time it connects, and never fights another listener. If somebody moves the receiver away later, the journal says so and the station waits for it to come back. Some receivers lock their profiles, and some say "Ask before tuning" on their page. Ask before you name a profile on a receiver like that.

### The rest of the section

```json
"openwebrx": { "profile": "HF 40 Metri", "ssbLowHz": 150, "ssbHighHz": 3450, "gain": 1.0 }
```

`ssbLowHz` and `ssbHighHz` are the SSB passband the receiver is asked for, in Hz from the dial; the defaults clear the whole 300 to 2700 Hz band a plan can place modems in. `fmHalfWidthHz` is half the FM channel filter's width, 5000 by default. `gain` and `startupGuardMs` are as for `ubersdr`.

`onDemand` is UberSDR only for now; an OpenWebRX station connects at start-up and stays connected.

### Read the journal

```
audio: sdr.example.org:8073 USB at 7.048800 MHz, passband 150 to 3450 Hz, ADPCM audio at 12000 Hz (RECEIVE ONLY)
openwebrx: openwebrx v1.2.2, Example SDR, Somewhere, on 5.900000 to 8.300000 MHz (profile "HF 40 Metri")
openwebrx: the receiver compresses its audio to 4-bit ADPCM, a setting of its own that a listener cannot change
```

- `is refusing us for now (Too many clients)` means every listener slot is taken; OpenWebRX+ also says `Client address banned` to an address that reconnected too often. The station stays up and asks again, waiting longer each time, up to 15 minutes.
- `the receiver is on X to Y MHz, which does not reach Z` mid-run means somebody moved it. Listening resumes when it comes back.
- `has been unreachable for 5 minutes` stops the station with exit 1, as on UberSDR, and systemd restarts it.

### What ADPCM costs

Measured in the simulator, not yet on air: each mode's ladder run twice, once as it is and once through the same 12 kHz ADPCM round trip a receiver puts its audio through (`sm-ota sim --adpcm`), 100 bursts a rung. The figure is where half the frames come through.

| Mode | Channel | 50% point, plain | 50% point, ADPCM | Cost |
|---|---|---|---|---|
| `bpsk300` | AWGN | -5.6 dB | -5.5 dB | about 0.1 dB |
| `afsk300-il2pc` | AWGN | -2.0 dB | -1.8 dB | about 0.2 dB |
| `qpsk600` | AWGN | -1.7 dB | -1.6 dB | about 0.1 dB |
| `bpsk1200` | AWGN | +0.4 dB | +0.5 dB | none measurable |
| `qpsk2400` | AWGN | +3.9 dB | +4.3 dB | about 0.4 dB |
| `afsk1200` | FM, data port | +10.0 dB | +9.9 dB | none measurable |
| `qpsk3600` | FM, data port | +7.3 dB | +7.4 dB | about 0.1 dB |

The fading channels (`bpsk300` on Moderate, `qpsk2400` on Good) showed no difference beyond the spread of 100 bursts. The cost hardly moves with level: coding at -6, -20, -40 and -50 dBFS gave the same results within that spread. So ADPCM is not what limits the faster modes. Their limits are the receiver's passband, its AGC and its demodulator, which the simulator does not model and only an on-air test will show. 9600 baud and faster cannot pass through 12 kHz audio at all.

## How it works

On UberSDR the modem takes IQ rather than the instance's demodulated audio, so the receive filter is set here. On OpenWebRX the receiver's own demodulator, filter and AGC are in the path. `captureRate` does not apply to either; the stream brings its own clock.

Public instances cap a session, three hours on the ones measured, and report the cap at start-up. A closed stream is ordinary, and the modem picks it up again, losing about a second each time to the start-of-stream ramp.

These are somebody else's receivers. One long session is kinder to the receiver than repeated reconnects. If you will leave a station running for weeks, tell the instance's operator.

## Related

- [10-public-monitor.md](10-public-monitor.md) puts many web receivers behind one site with a picker, which is the [`monitor`](reference/config.md#monitor) section rather than this one.
- [08-hf.md](08-hf.md) for band plans, sidebands and several modes sharing one passband.
- [reference/config.md](reference/config.md#ubersdr) for every `ubersdr`, [`openwebrx`](reference/config.md#openwebrx) and `waterfall` key with its default.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md#http-on-the-page-port) for what the page port serves.
- [13-decode-a-recording.md](13-decode-a-recording.md) if you would rather work from a recording than a live receiver.
