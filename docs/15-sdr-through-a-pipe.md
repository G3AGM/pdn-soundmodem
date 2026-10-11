# An SDR through a pipe

You will finish with a station that decodes packet off your own SDR (an RTL-SDR dongle, an SDRplay RSP, an Airspy, a HackRF or anything else SoapySDR can open) with nothing linked into the modem but a pipe.

The modem does not talk to the SDR itself. A small program that does (`rtl_sdr`, or `rx_sdr` from rx_tools) writes the raw IQ to its standard output, and the modem reads it, filters out the channel and demodulates it with its own receive filter. The `iqpipe:` device starts that program, restarts it if it dies, and tells it where to tune.

It receives and never transmits, like a web receiver.

**Not yet proven on air.** Every part of this has been tested against recorded IQ and real child processes, but no station has yet decoded real traffic through an RTL-SDR or an SDRplay this way. Treat the first one as a trial, and tell us how it went.

## Before you start

- pdn-soundmodem installed, with the config file at `/etc/pdn-soundmodem/soundmodem.json`. See [01-install.md](01-install.md).
- The SDR's own program installed and working: run it once by hand, as below, and see bytes come out before handing it to the modem.
  - `rtl_sdr` is in Debian's `rtl-sdr` package.
  - `rx_sdr` is from [rx_tools](https://github.com/rxseger/rx_tools), built against SoapySDR, with the SoapySDR module for your device (`soapysdr-module-rtlsdr`, `soapysdr-module-airspy`, `soapysdr-module-hackrf`; for an SDRplay, SDRplay's API service and the SoapySDRPlay3 module, which are SDRplay's own software under their licence).
- The `pdn-soundmodem` user allowed to open the device: on Debian that usually means the `plugdev` group for a USB SDR.
- The frequencies you want to listen on, in Hz.

## Point the modem at the program

Set [`device`](reference/config.md#device) to `iqpipe:` and say what the program writes in an [`iqpipe`](reference/config.md#iqpipe) section:

```json
"device": "iqpipe:",
"iqpipe": {
  "command": "rtl_sdr -f {frequency} -s {rate} -g 30 -",
  "format": "cu8",
  "rate": 1024000
},
"sideband": "fm",
"modems": [ { "subChannel": 0, "mode": "afsk1200", "rfFrequency": 144800000 } ]
```

The modem runs the command with `/bin/sh -c`, so write it exactly as you would at a shell prompt. `{frequency}` and `{rate}` are filled in each time it starts: the rate is your `rate`, and the frequency is the dial less an eighth of the rate (at most 25 kHz), so the channel sits off the spike most SDRs have at their centre. The `-` at the end is what makes these programs write to standard output; leave it off and they write a file instead.

A pipe says nothing about itself, so `format` and `rate` have to be what the command was told. Get them wrong and the journal says so after 30 seconds:

```
iqpipe: the feed delivered 1228800 S/s over the last 30 s, not the 1024000 S/s configured (+20.0 %); faster than it can be: the rate in the configuration is likely not the rate the program was told, and every frequency heard is off by the same factor
```

As for a web receiver, the modem tunes from your band plan. Give every modem an [`rfFrequency`](reference/config.md#band-placement-sideband-dialfrequency-and-rffrequency), or set `dialFrequency`. Any `sideband` works: `usb`, `lsb`, or `fm`, where the channel filter is as wide as the widest mode on it.

## Command lines for the common devices

These are starting points, not tested settings. Gains especially are a matter for your antenna and your band. Each one leaves `{frequency}` and `{rate}` for the modem to fill in.

| Device | `command` | `format` | `rate` |
|---|---|---|---|
| RTL-SDR, no SoapySDR | `rtl_sdr -f {frequency} -s {rate} -g 30 -` | `cu8` | `1024000` |
| RTL-SDR through SoapySDR | `rx_sdr -d driver=rtlsdr -f {frequency} -s {rate} -g 30 -F CU8 -` | `cu8` | `1024000` |
| SDRplay RSP | `rx_sdr -d driver=sdrplay -f {frequency} -s {rate} -F CF32 -` | `cf32` | `96000` |
| Airspy R2 or Mini | `rx_sdr -d driver=airspy -f {frequency} -s {rate} -F CS16 -` | `cs16` | `2500000` (R2) or `3000000` (Mini) |
| Airspy HF+ | `rx_sdr -d driver=airspyhf -f {frequency} -s {rate} -F CF32 -` | `cf32` | `192000` |
| HackRF | `rx_sdr -d driver=hackrf -f {frequency} -s {rate} -F CS8 -` | `cs8` | `2000000` |

- An RTL-SDR takes rates from 225001 to 300000 and from 900001 to 3200000 samples a second. 1024000 is plenty for a 25 kHz channel, and costs less CPU than the 2.4 MS/s often quoted.
- The SDRplay line is the one the project's own on-air bench uses with an RSP1, at 96000. To pin its gain rather than leave it to the RSP's AGC, add `-g AGC=false,IFGR=20,RFGR=0`, which needs an `rx_sdr` that passes those settings through.
- Lower rates cost less CPU. Choose the lowest your device offers that is comfortably wider than the channel.
- `rx_sdr -d` takes any SoapySDR device string. `SoapySDRUtil --find` lists what is plugged in, with the `driver=` and `serial=` to put there when there is more than one.

## Or read a FIFO, or standard input

If something else already runs the SDR, have it write into a FIFO and point the modem at that instead. The command is not used, so say where the IQ is centred yourself:

```json
"device": "iqpipe:/run/pdn-soundmodem/iq",
"iqpipe": { "format": "cu8", "rate": 1024000, "centreFrequency": 144775000 }
```

The modem creates the FIFO if it is not there. Each program that opens it and writes is a run; when it closes the FIFO, the modem waits for the next writer. If none comes within 30 seconds, the [dead-feed watch](reference/config.md#deadfeed) restarts the station, as for any input that stops.

`"device": "iqpipe:-"` reads standard input, for a pipeline run by hand: `rtl_sdr -f 144775000 -s 1024000 - | pdn-soundmodem --config ...`. Standard input is read once; when it ends, the station stops.

Neither can be retuned by the modem, so the dial has to lie inside the IQ: within about half the rate of `centreFrequency`, less the channel's own width. If it does not, start-up stops and says so.

## Start it and read the journal

```
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -f
```

You should see the program's own first lines, prefixed with its name, and the audio line:

```
iqpipe: rtl_sdr: Found 1 device(s):
...
audio: iqpipe rtl_sdr -f {frequency} -s {rate} -g 30 -: cu8 at 1024000 S/s centred on 144.775000 MHz -> FM at 144.800000 MHz (+25000 Hz), audio at 12000 Hz (RECEIVE ONLY)
```

The first 20 lines the program writes to its standard error each time it starts go into the journal, then one line every ten seconds. If it exits, the journal says how, with its last words, and it is started again:

```
iqpipe: rtl_sdr exited with code 1; its last words: usb_claim_interface error -6; restarting in 1.0 s
```

The wait doubles, up to 16 seconds, each time it dies within ten seconds of starting. After five of those in a row the station stops with exit 1, and systemd starts it afresh. A program that stays up but stops writing is caught by the [dead-feed watch](reference/config.md#deadfeed) after 30 seconds, the same way.

## What the modem can and cannot see

A pipe carries no sample counter, so the modem cannot know what the SDR program dropped. It measures what it can against the clock: the time between one run and the next, and a stall that leaves the feed behind the clock, are each marked in the audio as silence of that length, so what follows is heard at the right time and nothing is spliced. Both are told to the channel as lost audio, as a sound card's overruns are, so the [channel audio stream](reference/ports-and-endpoints.md#the-channel-audio-stream) marks them as gaps too. A program that drops samples without stalling shows up only in the 30-second rate check, as a feed that is short.

As with a web receiver, [`ptt`](reference/config.md#ptt), [`rig`](reference/config.md#rig), [`alsa.mixer`](reference/config.md#alsa) and a modem's [`identify`](reference/config.md#modemsidentify) are refused. Unlike one, the SDR is yours, so [`publish`](reference/config.md#publish) is allowed.
