# The station page

When you finish this page you can read every part of the station page, which is the browser page the modem serves: a live spectrum and waterfall, the frames it has decoded, the links it can hear, and, on your own page, the sound card's levels and a transmitter test.

![The station page, showing the header controls, two modem chips, the spectrum and ruler, the waterfall tagged with decoded callsigns, and the decoded frames panel](images/waterfall.png)

A station on 20 m running two modems in one passband. The waterfall scrolls downwards and each burst carries the callsign, signal-to-noise figure and frequency offset the modem read off it.

## Opening it

A `waterfall` section in the config file switches the page on. The port defaults to `8107`, and the page listens on the top-level `bind`, which is `127.0.0.1` unless you change it.

```json
{ "waterfall": { "port": 8107 } }
```

The journal says where it is when the modem starts.

```
waterfall: http://127.0.0.1:8107/
```

Open that in a browser on the same machine. Every key is in [`waterfall`](reference/config.md#waterfall); `--waterfall PORT` does the same job from the command line, and `--dial HZ` presets the dial ([station flags](reference/command-line.md#station-flags)).

## The header, left to right

The wordmark carries the version the modem is running, and the first twelve characters of the commit after it. A build that is not a numbered release adds `dev` after the version. Hover it for the full sentence.

Beside the wordmark, a status pill reports the radio behind the station: a FlexRadio's frequency reference, or a web receiver's session state. It turns red when something is wrong and stays hidden on a sound-card station, which has no radio to ask.

When a program on the same machine is listening to the station's audio, such as the mailcast receiver, a second pill names it: "listening: pdn-mailcast-receiver", with the name linking to that program's own page. It appears only while the program is connected, and never on a public page.

Dial is the frequency your radio is tuned to, in MHz, with USB, LSB and FM beside it. Set it and the ruler grows an RF scale above the audio one, so the panel reads in band frequencies. On FM the box is labelled `Channel`, the RF arithmetic does not apply, and the ruler shows the channel once instead. [08-hf.md](08-hf.md) covers band placement.

Span sets how much of the audio passband you see, from 2 kHz up to whatever the sample rate allows.

Listen plays the station's received audio in your browser, with a volume slider beside it. Nothing is received while the station transmits, so a keyup sounds like a gap.

Links opens the links pane, with the number of links currently on it. The `L` key does the same.

The Mixer group sets the sound card's capture and transmit levels in dB, bounded by the card's own range, with a live meter of what the modem is hearing. [04-levels.md](04-levels.md) is the page to work from.

The group appears only on your own page, only on a sound-card station, and only when the modem answers `/api/mixer`. That needs either `api.key` or `waterfall.enableAudioControls`. With a key set and this browser not holding it, the sliders read `key needed` and a Key button appears.

Last TX shows the forward power in watts and the SWR your radio reported. It goes red and reads `Transmitting` while the radio is keyed, then holds the average of that transmission with the time it was taken. Only a FlexRadio reports these; a radio that reports no SWR shows none.

TX test keys the radio and sends a test burst through the normal transmit path. Pick two tones for an SSB linearity check, one free tone, or one of the FM deviation presets, set the seconds, and press Send. It is never on a public page, and a station with no transmitter shows the control disabled with the reason the journal gave at start-up, such as `tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio`. The settings live in [`txTest`](reference/config.md#txtest) and the procedure is in [04-levels.md](04-levels.md).

Mailcast appears on a station with a [`mailcast`](reference/config.md#mailcast) section: the next of GB7RDG's slots, the last slot's tone offset and SNR, the frames heard, bulletins complete, partial and delivered, the BBS's state and, on a retuned rig, where the rig is. Hover it for the detail. It is never on a public page. [14-mailcast.md](14-mailcast.md) has the rest.

Level sets the two dBFS ends of the waterfall's colour scale, floor and top. Auto sets both from what is on screen now. These are per browser and are remembered.

At the right, the connection state reads `live` while the page's socket is up, and says so when it is reconnecting. Under it are the measured frame rate and the width of one spectrum bin, or `transmitting`, or `no audio` when nothing has arrived for a second and a half.

## The modem chips

One chip per modem sits under the header, in sub-channel order: the sub-channel number, the mode, the audio centre frequency, and the RF frequency once you have set the dial. Each chip's colour is the colour that modem's band is drawn in on the spectrum and waterfall. Hover a chip for its audio band in Hz.

A chip also carries a KISS badge, `KISS 8001: 1 host`, saying which port reaches that modem and how many hosts are attached; it reads `KISS 8001, no host` when nothing is. A modem with its own dedicated port or in a polyglot port names that port; one with neither names the shared port. The badge follows clients live, so you can watch your node software attach. [06-connect-your-software.md](06-connect-your-software.md) covers what attaches and how.

The badge is absent until the station has said what it serves, and a public page carries none.

## The spectrum and the waterfall

The spectrum panel draws the live trace with a decaying peak hold behind it and a dB grid every 20 dB. Under it the ruler shows audio frequency in muted text, and RF in amber above that once the dial is set.

Each modem's band is tinted in that modem's colour across both panels, with a solid cap at the top of the band and a dashed line at its centre. Anything landing outside every tint is on a frequency no modem here is listening to.

The waterfall scrolls downwards, newest at the top, coloured between the Level floor and top. Every frame the station decodes is tagged on the burst it came from: a bracket beside the modem's band running the length of the burst, then the callsign (or the mode where no callsign survived), `ID` on an ident, the signal-to-noise figure, and the carrier offset in Hz. The offset is left off on FM, where there is no RF placement to read it against. Your own transmissions are listed but not tagged.

With a `survey` section running, a burst the station could not read gets its own purple bracket and a row in the frames panel.

## Decoded frames

The panel down the right lists what the station has heard and sent, newest first, up to a hundred rows. On connect the modem sends the recent contents of the frame log, so the panel is not empty on a page you have opened after the fact; a backlog row from an earlier day carries the date too. [11-logging-and-metrics.md](11-logging-and-metrics.md) covers the log itself.

A row is two lines, the second of which the page separates with dots.

```
12:44:24  2E0PDN-7 > GB7PDN
1  bpsk300  18.9 dB  -4 Hz  90 B  fec 1
```

The first line is the time, then who sent it to whom, then any badges. A frame whose address field did not hold up says `unattributed` and carries the reason in a note under the figures.

The second line is the sub-channel and mode, then whichever of these the frame carried: the band signal-to-noise figure in dB, ARDOP's own quality as `q N`, the frame's peak audio level in dBFS, the carrier offset in Hz, the length in bytes, `fec N` bytes corrected, a failed CRC, and the IL2P variant. The raw bytes follow as hex, ready to select and copy.

### The badges

| Badge | What it says |
|---|---|
| `TX` | This station sent the frame. It is listed once the audio has gone out, not when it was queued. |
| `ID` | A station identification, heard on the mode that TNC idents in rather than the one it carries data in. |
| `RS ONLY` | Plain IL2P with no trailing CRC, so Reed-Solomon alone stood behind this frame. Hover it to see whether your modem passed it to your node or APRS software. |
| `TOO LOUD` | The card ran out of codes during this frame, or came close enough that a slightly louder station would clip. Turn the capture gain down. |
| `TOO QUIET` | The frame peaked below the level this mode starts losing link margin at. Advice rather than a fault. |
| `SHIFTED` | One of yours, transmitted off the channel centre to land where the station you are answering is listening. |
| `HELD 8.3s busy ch2` | One of yours that waited for the channel before it went out, for long enough to matter, and what took most of the wait: `busy chN` (carrier sense on that sub-channel), `busy` (your radio said so), `our tx` (your own earlier transmissions), `other link`, `backoff`, `turnaround` or `inhibit`. No word where no single cause took half of it. Shown past 0.3 s; the frame log keeps the figure, the split and the cause for every transmission either way. |

Which modes show the dBFS figure and the two level badges is in [04-levels.md](04-levels.md#read-the-frame-badges). Every mode's measurement still reaches the frame log and the monitor.

ARDOP rows list this station's own transmissions as well as what it heard, carry ARDOP's 0 to 100 quality as `q N`, and show a dB figure on a Ping or a PingAck alone. Your own CW idents appear as mode `cw-ident` from your callsign. A POCSAG page you sent is listed as `unattributed` with the `TX` badge and its length. Neither row carries bytes.

## The links pane

Press `L`. The pane opens over the waterfall with one card per pair of stations heard talking on each modem: connections as they are made, used and dropped, with the last hundred lines of each.

UI frames adds cards for unconnected traffic, which is beacons, idents and everything else that never makes a link. Mine narrows the pane to links this station is one end of. The grip at the top resizes the pane, Detach opens it in a window of its own with no waterfall behind it, and Close or `Escape` puts it away.

Press `transcript` on a card to save its feed as a markdown file, oldest line first, with the classic monitor decode beside each line. The file is named for the two stations, the modem and the time.

A line of your own carries a `HELD` tag when that frame waited for the channel before it went out, with the same word for what took most of the wait. `HELD 8.3s busy ch2` is somebody else using the frequency; `HELD 8.3s our tx` is the same delay caused by your own window going out in front of the frame, which is your station working normally. It is worth looking for on a card full of `POLL` and `AGAIN`: the node that queued those frames cannot see the wait, because a KISS write returns as soon as the socket takes it, so it keeps its retry timers running and queues another poll. When the channel finally opens they all go out in one keyup, and the station is deaf for the length of it - so the earlier polls in the run could not have been answered whatever the far end did. A run of `HELD` tags on repeated polls is a channel-access problem at this end, not a link failing at the other.

## A public page

`"waterfall": { "public": true }` serves a page for visitors instead. It shows your `title` and `about` paragraph, credits the receiver the audio comes from, and takes away everything a visitor cannot usefully act on: the dial and sideband, the span, the display levels, the mixer, the KISS host badges, and the transmitter test. Listen and the links pane stay. [09-web-receivers.md](09-web-receivers.md) and [10-public-monitor.md](10-public-monitor.md) cover running one.

`enableAudioControls` is ignored on a public page, and the journal says so.

## The API key

An `api` section with a `key` installs the modem's HTTP API on the same port. The page uses it for one thing: reaching the mixer. Press Key, paste the key, and the sliders come alive. The key is kept in that browser and nowhere else, and the modem never sends it to a page.

`waterfall.enableAudioControls` serves `/api/mixer` with no key at all and nothing else, so the mixer works from any browser that can reach the port.

The same key also serves the configuration, proposal and transmit-test endpoints, which are not page controls. They are in [the API reference](reference/ports-and-endpoints.md#the-api-under-api).

## Reaching it from another machine

Set the top-level `bind` to the address you want, or `"*"` for every interface, and restart. There is no separate bind for the page.

```
waterfall: WARNING - listening beyond loopback. The page has no authentication, and on an operator's page it carries a transmit test: anything that can reach this port can key your transmitter on your licence.
```

The page has no login. Anyone who can reach the port can press Send on the TX test and put a signal on the air under your callsign, and with `enableAudioControls` set they can move your levels too. Put it behind your firewall, a VPN, or a reverse proxy that asks for a password, and read [Every listener](reference/ports-and-endpoints.md#every-listener) before you open anything else.

## If it did not

The page does not load. Check the modem is running and that the journal printed the `waterfall:` line; a station with no `waterfall` section serves nothing. See [12-troubleshooting.md](12-troubleshooting.md).

The page loads but says it is reconnecting. The modem has stopped or the socket cannot stay up. The journal is the place to look.

No spectrum and `no audio` in the corner. The station is not getting audio from the card or the radio. Work through [04-levels.md](04-levels.md) and then [12-troubleshooting.md](12-troubleshooting.md).

The Mixer group is missing. The station has no sound card, as on a FlexRadio or a web receiver, or neither key under [the header](#the-header-left-to-right) is set.

Frames appear on the page but your node software sees nothing. The chip's KISS badge says `no host`. Go to [06-connect-your-software.md](06-connect-your-software.md).

## Related

- [04-levels.md](04-levels.md), for the meter, the badges and the transmitter test in full
- [06-connect-your-software.md](06-connect-your-software.md), for the KISS ports the chips name
- [09-web-receivers.md](09-web-receivers.md) and [10-public-monitor.md](10-public-monitor.md), for public pages
- [11-logging-and-metrics.md](11-logging-and-metrics.md), for the frame log behind the panel
- [`waterfall`](reference/config.md#waterfall), [`api`](reference/config.md#api) and [`txTest`](reference/config.md#txtest) in the configuration reference
- [HTTP on the page port](reference/ports-and-endpoints.md#http-on-the-page-port), for every route and endpoint
