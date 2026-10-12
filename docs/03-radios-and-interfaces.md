# Radios and interfaces

By the end of this page you will know which `device` string and which `ptt` block your hardware needs.

## Before you start

- pdn-soundmodem installed, from [01-install.md](01-install.md).
- A radio, and whatever carries audio between it and the machine.
- Root on the machine, if you are going to key through a CM108 GPIO pin.

## Pick the audio path

`device` says where receive audio comes from and where transmit audio goes. One string covers both directions; if yours are on two different sound cards, see [Receive and transmit on different cards](#receive-and-transmit-on-different-cards).

| What you have | `device` |
|---|---|
| A USB sound card, or a CM108-class interface | `plughw:CARD=Device,DEV=0` |
| A FlexRadio on the LAN | `flex:10.45.0.76` |
| A public UberSDR web receiver | `ubersdr:m9psy-1.instance.ubersdr.org` |
| A public OpenWebRX or OpenWebRX+ receiver | `openwebrx:http://sdr.example.org:8073/` |
| A radio's IQ output into a stereo sound card (Xiegu G90, SoftRock), receive only | `iqcard:plughw:CARD=Device,DEV=0`; see [hardware/iq-sound-card.md](hardware/iq-sound-card.md) |
| An RTL-SDR dongle behind `rtl_tcp`, or your own OpenWebRX's rtl_tcp-compatible port | `rtltcp:pi.local:1234` |
| An SDRplay radio on a PC running SDRconnect | `sdrconnect:shack-pc.local` |
| Two FIFOs standing in for a card | `pipe:/tmp/in,/tmp/out` |
| Nothing at all | `null` |

`pipe:` lets two modems on one machine talk to each other with no radio between them, and `null` opens ALSA's null device, which neither hears nor transmits. Both are bench tools. Every accepted form is listed under [`device`](reference/config.md#device).

## Wire a sound card or an interface

Write the stable `plughw:CARD=Device,DEV=0` name rather than `plughw:1,0`, because card numbers move when you replug something; [02-first-station.md](02-first-station.md#find-the-sound-card) finds yours.

A CM108-class interface is a USB sound card with a GPIO pin wired to the radio's PTT line, so one USB lead carries audio both ways and the keying. A DRA board, an RB-USB RIM and the board wired to a Tait TM8100 are all this kind of thing. The card is the `device` string; the pin is the `ptt` block below.

At start-up the journal says what was opened and at what rate:

```
audio: plughw:CARD=Device,DEV=0 capture 48000 Hz -> 12000 Hz
```

### Receive and transmit on different cards

If your receive audio comes in on one sound card and your transmit audio goes out through another, keep `device` for one of them and name the other with `captureDevice` (receive) or `playbackDevice` (transmit):

```json
{
  "device": "plughw:CARD=Device,DEV=0",
  "playbackDevice": "plughw:CARD=Device_1,DEV=0"
}
```

The journal then names both:

```
audio: capture plughw:CARD=Device,DEV=0 48000 Hz -> 12000 Hz, playback plughw:CARD=Device_1,DEV=0
```

Each card has its own mixer. The capture gain is set on the receive card and the transmit level on the transmit card, and the station page's sliders follow them. Both cards run at `captureRate`. This works with sound cards only; a Flex, an UberSDR or a pipe carries both directions itself.

Wiring an interface to a radio is a page of its own, one per radio: [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md) for a Tait TM8100 or TM8200 on FM, and [hardware/yaesu-ft450d-cm108.md](hardware/yaesu-ft450d-cm108.md) for a Yaesu FT-450D's rear-panel DATA jack on HF. Setting the card's gains comes next, in [04-levels.md](04-levels.md).

## Choose how the radio is keyed

### A serial RTS or DTR line

```json
{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" } }
```

`line` is `rts` or `dtr`, and `rts` is the default. Use a `/dev/serial/by-id/...` name if you have one, since `ttyUSB` numbers move about. The service user is already in the `dialout` group, so nothing else has to be opened up.

```
ptt: serial /dev/ttyUSB0 (rts)
```

### The GPIO pin on a CM108 interface

```json
{ "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 } }
```

GPIO 3 is what interfaces of this class use, and it is the default, so you can leave `gpio` out. To find which node is yours, search for the C-Media vendor ID `0d8c`:

```sh
grep -il 0d8c /sys/class/hidraw/*/device/uevent
```

The `hidrawN` in the path it prints is your node, so `/sys/class/hidraw/hidraw0/device/uevent` means `device` is `/dev/hidraw0`.

`/dev/hidraw*` is root-only, so the unprivileged service user cannot open it until you add a udev rule. [02-first-station.md](02-first-station.md) has the rule. Add it, replug the interface, then restart the service.

```
ptt: cm108 /dev/hidraw0 (gpio 3)
```

### Through rigctld

```json
{ "rig": {}, "ptt": { "type": "rigctld" } }
```

The radio is keyed over its CAT port with Hamlib's `T 1` and `T 0`, so no PTT lead is needed. It needs the `rig` section, see [Other radios through Hamlib](#other-radios-through-hamlib). The CAT round trip comes out of TXDELAY, so a slow serial link may need a longer TXDELAY from your host.

```
ptt: rigctld 127.0.0.1:4532 (T 1 to key, T 0 to unkey)
```

### VOX

Leave the `ptt` section out and the modem keys nothing. The radio's own VOX trips on the transmit audio instead.

It is a poor way to run data. The radio decides when the carrier comes up, so TXDELAY, the pause between keying and the first data, has to be long enough to cover the VOX attack, and the hang time keeps the transmitter up while the other station is answering. The transmitter test is refused:

```
tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio
```

## FlexRadio

A FlexRadio needs no sound card and no PTT lead. Audio goes over the LAN on DAX, and the radio keys itself, so a `ptt` section beside a `flex:` device is refused at start-up. A Flex station has no sound card, so an `alsa.mixer` section is refused too.

```json
{ "device": "flex:10.45.0.76", "flex": { "antenna": "ANT1", "mode": "DIGU" } }
```

The string is `flex:<radio>[:slice][@station]`. The radio is `discover` to find one by broadcast, a `host` or `host:port`, `serial=...`, `name=...`, or `mock` for a fake radio on this machine. The slice is a letter `A` to `H`.

Headless mode, with no `@station`, is the usual deployment: no SmartSDR anywhere. The modem creates its own slice and tunes it. `frequency`, `antenna` and `mode` configure that slice; `stationName` is what the radio calls this client. With `rfFrequency` on the modems the band plan tunes the slice instead, see [08-hf.md](08-hf.md).

Attach mode, with a trailing `@station`, binds the slice a running SmartSDR already owns, for coexisting with an operator at the radio. SmartSDR configures the slice, so the keys above do nothing.

Either way the journal names the slice, the DAX channel and which mode you got:

```
audio: flex:10.45.0.76 DAX 24000 Hz -> 12000 Hz (slice A, dax 2, headless 14.100000 MHz ANT1 DIGU)
flex: slice 0 health Healthy at bring-up (owned by this client)
```

### DAX channels

A running SmartSDR takes DAX channel 1, so a headless station defaults to channel 2 and the order the two are started in stops mattering. Attach mode keeps channel 1, SmartSDR's own. `daxChannel` sets it on either path.

Two headless instances that both take the default land on channel 2 and displace each other. Give the second one its own `daxChannel`, and set `"receiveOnly": true` so it never writes the radio's global transmit state and never contends for a slice.

### The transmit filter

The transmit filter high cut is a radio-global setting that survives whoever set it last, and it decides how much of your audio reaches the air. A headless station works it out from the modems you configured and says so:

```
flex: setting the transmit filter high cut to 3150 Hz - modem 0 (afsk1200) reaches 2906 Hz
flex: setting the slice receive filter to 300-3150 Hz, to hear everything the modems are placed across
```

Pin it with `transmitFilterHighHz` (500 to 10000 Hz), or set it to `0` to leave whatever the radio already had. Attach mode sets neither filter, leaving both to SmartSDR.

### On the station page

A Flex station has no sound card, so the page has no mixer group. Transmit level is the radio's business: set `txPowerWatts` in the `flex` section, or leave it and the radio's own setting is used and printed.

```
flex: transmit power 100 W, limit 100 W (radio's own setting)
```

After a transmission the page header shows forward power and SWR, see [07-station-page.md](07-station-page.md).

Every key is in [`flex`](reference/config.md#flex), and the four override flags are under [FlexRadio flags](reference/command-line.md#flexradio-flags).

## Other radios through Hamlib

Any radio Hamlib can drive (most HF rigs from the last twenty years) can have its dial set by the modem, be keyed over CAT, and be retuned for a while and put back. The modem talks to Hamlib's `rigctld`, so start that first:

```sh
sudo apt install libhamlib-utils
rigctl -l | grep -i 7300            # find your rig's model number
rigctld -m 3073 -r /dev/ttyUSB0     # 3073 is an IC-7300
```

Then add a `rig` section. On its own it looks for rigctld on this machine at port 4532:

```json
{ "rig": {} }
```

At start-up the journal says what the rig is on:

```
rig: rigctld at 127.0.0.1:4532: 7.049450 MHz USB (2400 Hz passband)
```

With your modems placed by `rfFrequency`, the modem sets the dial and mode from the band plan, as it does on a Flex, instead of asking you to:

```
rig: setting the rig to 7.049450 MHz USB (2400 Hz passband) from the band plan
```

Without `rfFrequency` the dial is left where you put it.

- Many rigs only take audio from their data jack in a data mode. Set `"mode": "PKTUSB"` (or `PKTLSB`) for that.
- The modem asks for a 2400 Hz passband, the window the band plan assumes. If your rig will not set it, the journal says so; `"passbandHz": 0` leaves the rig on its normal width.
- If rigctld is not running, the journal warns and the station carries on without it, trying again in the background. `"required": true` makes the station wait for it instead.
- Start rigctld without `--vfo`; the modem does not speak that form.

`POST /api/rig/tune` retunes the rig for a while and always puts it back, for listening to something outside your passband. While it is retuned the station does not transmit. See [rig tuning windows](reference/ports-and-endpoints.md#rig-tuning-windows). pdn-mailcast-receiver, with `"retune": true`, asks for one through the [receive window](reference/ports-and-endpoints.md#the-receive-window) to hear GB7RDG's bulletins around each slot: [14-mailcast.md](14-mailcast.md).

Every key is in [`rig`](reference/config.md#rig).

## An RTL-SDR over the network

An RTL-SDR dongle on a small box at the antenna can stream its raw IQ over the LAN with `rtl_tcp`, and the station demodulates it on a machine with the CPU to spare. Point `device` at the server:

```json
{
  "device": "rtltcp:pi.local",
  "sideband": "fm",
  "modems": [ { "mode": "afsk1200", "subChannel": 0, "rfFrequency": 144800000 } ]
}
```

On the box with the dongle, start the server listening on the LAN (it listens on 127.0.0.1 unless told otherwise):

```sh
rtl_tcp -a 0.0.0.0 -p 1234
```

- The station tunes the dongle itself: 25 kHz below the band plan's dial, so the dongle's DC spike stays out of the channel, at 1.024 MS/s unless `"rtltcp"."sampleRate"` says otherwise. The tuner's own gain is used unless `"gainDb"` sets one, and `"ppm"` corrects the dongle's crystal.
- The filter and the demodulator are this station's, not the dongle's: USB, LSB or FM, from the band plan's sideband.
- It is receive only, as a web receiver is: no `ptt`, no `rig`, no transmitter test.
- rtl_tcp serves one client at a time. While this station is connected nothing else can use the dongle.
- If the server goes away or goes quiet for five seconds, the station reconnects and marks the time it was gone as a gap in the audio. After five minutes without it, the service restarts.
- Every key is in [`rtltcp`](reference/config.md#rtltcp).

### OpenWebRX's rtl_tcp-compatible port

An OpenWebRX or OpenWebRX+ you run can republish its SDR's raw IQ on an rtl_tcp-compatible port, so the same SDR serves the web page and this station. That port takes no orders: it ignores commands, cannot say which profile it is on, and sends every SDR as 8 bits a sample. So the station is told the profile and never sends anything:

```json
{
  "device": "rtltcp:owrx.local:4950",
  "rtltcp": { "readOnly": true, "centreFrequency": 144900000, "sampleRate": 2400000 }
}
```

- In the SDR device's settings, give it an rtl_tcp-compatible port, and turn on keeping the device running at all times; otherwise the port has nothing to send while nobody has the page open.
- Pin the profile. `centreFrequency` and `sampleRate` have to be what the profile is set to, and the dial has to fall inside its band. Give the device one profile, or make sure nobody switches it.
- If the profile is switched anyway, the station cannot be told. A switch to a different sample rate is caught within about fifteen seconds: the samples are dropped until the rate is right again, and the journal says so. A switch to another band at the same rate cannot be caught for certain; a sudden lasting change in the stream is said in the journal as a warning, and the station goes on decoding whatever the SDR now hears.
- 8 bits a sample is plenty with the SDR's gain set so the noise sits well clear of the bottom: in simulation it costs nothing measurable against float IQ.

## SDRconnect on another PC

If an SDRplay radio is plugged into a PC that runs SDRplay's SDRconnect, the station can listen through SDRconnect over the network. SDRconnect keeps the radio; the station connects to SDRconnect's WebSocket API on port 5454 and tunes it from the band plan. Point `device` at the PC:

```json
{
  "device": "sdrconnect:shack-pc.local",
  "sideband": "usb",
  "modems": [ { "mode": "bpsk300", "subChannel": 0, "rfFrequency": 7048800 } ]
}
```

On the PC with the radio:

- SDRconnect has to be running, with the radio selected and started. SDRconnect supports every RSP except the original RSP1 (the RSP1A and later are fine).
- The station connects to port 5454 on that PC, so the PC's firewall has to let the station's machine in on that port. On Windows, allow SDRconnect through Windows Defender Firewall for private networks when it asks, or add an inbound rule for TCP port 5454.
- Whether SDRconnect accepts connections from other machines out of the box, and where in SDRconnect its WebSocket API is turned on if it is not, has not yet been checked against a real SDRconnect. If the station cannot connect, that is the first place to look.

Pick the stream with `"sdrconnect"."mode"`:

- `"iq"` (the default) takes SDRconnect's raw IQ, and the station's own filter and demodulator do the rest, as on every other SDR. The station asks for 2 MS/s unless told otherwise and takes the IQ to be at the sample rate SDRconnect reports; at 2 MS/s that is about 64 Mbit/s on the network: use wired Ethernet between the two machines.
- `"audio"` takes SDRconnect's own demodulated audio, about 1.5 Mbit/s, which a Wi-Fi link carries easily. The filter and the demodulator are then SDRconnect's. The station sets them (USB, LSB or NFM from the band plan, on the dial) and turns off SDRconnect's AGC, noise reduction, squelch and audio filters, so what arrives is the demodulator's output and nothing else.

What to expect:

- The station tunes the radio: the LO 25 kHz below the dial, so its DC spike stays out of the channel. Leave the frequency alone in the SDRconnect window while the station is listening; a change made there is put back, and the journal says so.
- It is receive only: no `ptt`, no `rig`, no transmitter test.
- At start-up the journal says what SDRconnect reported: its API version, the radio, its sample rate, the antennas and the gain range. Those lines are the ones to send when asking for help.
- If SDRconnect goes away or goes quiet for five seconds, the station reconnects, sets the radio up again and marks the time it was gone as a gap. After five minutes without it, the service restarts.
- Every key is in [`sdrconnect`](reference/config.md#sdrconnect).

## A web receiver

With no antenna at all, point `device` at a public UberSDR web receiver and the station receives over the internet. It is receive only: no `ptt`, no transmitter test, and frames sent to it over KISS are dropped. Every modem needs an `rfFrequency`, or `dialFrequency` must be set, so the receiver knows where to tune. [09-web-receivers.md](09-web-receivers.md) covers the rest.

## Check it worked

Start the service and read the first few lines of the journal.

```sh
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -n 30 --no-pager
```

You want one `audio:` line naming your device, and one `ptt:` line if you configured one. VOX and Flex stations have none. With a `rig` section there is also a `rig:` line naming what the rig is on.

## If it did not

`cannot open the sound device "..."` means the name is wrong, or the card has not enumerated yet. The message itself lists the commands that show what the machine has. The service keeps retrying, so a late USB device clears on its own.

`cannot open the cm108 PTT device "..."` with `Access to the path '/dev/hidraw0' is denied.` under it means the udev rule is missing or has the wrong IDs. Add it, replug, restart.

`--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)` means what it says. Delete the `ptt` section. The same goes for `ubersdr:`, `openwebrx:`, `rtltcp:` and `sdrconnect:`, which have no transmitter.

`cannot listen through "rtltcp:..."` means the rtl_tcp server is not running, is listening only on its own machine (start it with `-a 0.0.0.0`), or another client has it. The service keeps retrying.

`cannot listen through "sdrconnect:..."` means SDRconnect is not running on that PC, the radio is not started in it, or the PC's firewall is keeping the station out of port 5454. The service keeps retrying.

`rig: WARNING - cannot reach rigctld at ...` means rigctld is not running, or is on another port. Start it, and the station picks it up within half a minute.

`cannot reach the FlexRadio "..."` means the radio is off, still booting, or on another network. `ping` the address, or open SmartSDR.

Anything else in the journal is in [12-troubleshooting.md](12-troubleshooting.md).

## Related

- [02-first-station.md](02-first-station.md) for putting this into a working config.
- [04-levels.md](04-levels.md) for receive and transmit levels once the audio path is in place.
- [07-station-page.md](07-station-page.md) for the mixer, the meters and the transmitter test in the browser.
- [08-hf.md](08-hf.md) for SSB, band plans and where the dial goes.
- [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md) for wiring an interface to a TM8100.
- [reference/config.md](reference/config.md) for `device`, `ptt`, `alsa`, `flex`, `rig`, `ubersdr`, `rtltcp` and `sdrconnect` key by key.
