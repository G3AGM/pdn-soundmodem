# The pdn-soundmodem guide

pdn-soundmodem is a software TNC for Linux. It turns a sound card, a FlexRadio or a public web receiver into a packet modem, speaks KISS over TCP to your node or APRS software, runs ARDOP for Winlink and POCSAG paging, and shows the band in a browser. This guide is for the operator setting one up. Read it in order the first time, or pick a task from the list below. Every page says what to do and what you should see; the keys, flags, ports and files behind it are in the reference pages at the end.

## Reading order

1. [01-install.md](01-install.md): install the package on Debian, Ubuntu or Raspberry Pi OS, and see what it put where.
2. [02-first-station.md](02-first-station.md): one AFSK 1200 modem decoding APRS off air, on the station page, with your software attached.
3. [03-radios-and-interfaces.md](03-radios-and-interfaces.md): which `device` string and `ptt` block your sound card, interface, FlexRadio or web receiver needs.
4. [04-levels.md](04-levels.md): set the receive gain from the level meter and the transmit audio with a test tone.
5. [05-modes.md](05-modes.md): every mode, what it talks to, and how well proven it is.
6. [06-connect-your-software.md](06-connect-your-software.md): KISS over TCP, LinBPQ, APRS clients, ARDOP for Pat and Winlink Express, and paging.
7. [07-station-page.md](07-station-page.md): every control, panel and badge on the browser page.
8. [08-hf.md](08-hf.md): place modems on an HF band by RF frequency and let the modem tell you the dial.
9. [09-web-receivers.md](09-web-receivers.md): decode packet off a public web receiver with no radio at all.
10. [10-public-monitor.md](10-public-monitor.md): put your station on a monitor site, or run one.
11. [11-logging-and-metrics.md](11-logging-and-metrics.md): the frame log, survey captures, raw capture, Prometheus and Grafana.
12. [12-troubleshooting.md](12-troubleshooting.md): read the journal, the start-up refusals, and what to do when nothing decodes.
13. [13-decode-a-recording.md](13-decode-a-recording.md): get the frames out of a WAV file with the source-tree tools.
14. [14-mailcast.md](14-mailcast.md): receive GB7RDG's 40 m bulletins into your own LinBPQ or FBB with pdn-mailcast-receiver, and move across from the deprecated built-in receiver.
15. [15-sdr-through-a-pipe.md](15-sdr-through-a-pipe.md): receive through your own RTL-SDR, SDRplay, Airspy or HackRF, with `rtl_sdr` or `rx_sdr` writing IQ into the modem.

The two hardware pages are what to build to wire a CM108 interface to a radio: [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md) for a Tait TM8100 on FM, and [hardware/yaesu-ft450d-cm108.md](hardware/yaesu-ft450d-cm108.md) for a Yaesu FT-450D's DATA jack on HF.

## I want to

- Try it with no radio to hand: [02-first-station.md](02-first-station.md#no-radio-to-hand).
- Key the radio through a CM108 interface: [03-radios-and-interfaces.md](03-radios-and-interfaces.md#the-gpio-pin-on-a-cm108-interface).
- Use a FlexRadio: [03-radios-and-interfaces.md](03-radios-and-interfaces.md#flexradio).
- Set my receive level: [04-levels.md](04-levels.md#set-the-rx-gain).
- Set my TX level: [04-levels.md](04-levels.md#send-a-transmit-test).
- Choose a mode: [05-modes.md](05-modes.md#which-family-you-want).
- Set TXDELAY and the channel timing: [06-connect-your-software.md](06-connect-your-software.md#channel-access-is-your-softwares-to-set).
- Connect LinBPQ: [06-connect-your-software.md](06-connect-your-software.md#linbpq).
- Connect APRS software: [06-connect-your-software.md](06-connect-your-software.md#aprs-software).
- Run Winlink: [06-connect-your-software.md](06-connect-your-software.md#ardop-for-pat-and-winlink-express).
- Send pages to POCSAG pagers: [06-connect-your-software.md](06-connect-your-software.md#pocsag-paging).
- Open the page and the KISS port to my network: [06-connect-your-software.md](06-connect-your-software.md#who-can-reach-these-ports).
- Put several modes in one HF passband: [08-hf.md](08-hf.md#place-your-modems-by-rf-frequency).
- Put a modem on an FM channel: [08-hf.md](08-hf.md#on-an-fm-radio).
- Listen without a radio: [09-web-receivers.md](09-web-receivers.md).
- Listen through my own SDR: [15-sdr-through-a-pipe.md](15-sdr-through-a-pipe.md).
- Publish my station: [10-public-monitor.md](10-public-monitor.md#add-the-publish-block).
- Run a monitor site: [10-public-monitor.md](10-public-monitor.md#run-a-monitor-site).
- Keep a log of every frame: [11-logging-and-metrics.md](11-logging-and-metrics.md#keep-a-frame-log).
- See my station in Grafana: [11-logging-and-metrics.md](11-logging-and-metrics.md#import-the-grafana-dashboard).
- Read the journal: [12-troubleshooting.md](12-troubleshooting.md#read-the-journal-first).
- Find out why nothing decodes: [12-troubleshooting.md](12-troubleshooting.md#nothing-decodes).
- Fix a service that will not start: [12-troubleshooting.md](12-troubleshooting.md#the-service-will-not-start).
- Decode a recording: [13-decode-a-recording.md](13-decode-a-recording.md).
- Receive GB7RDG's 40 m bulletins into my own BBS: [14-mailcast.md](14-mailcast.md).
- Move from the built-in mailcast receiver to pdn-mailcast-receiver: [14-mailcast.md](14-mailcast.md#moving-across).
- Wire a Tait TM8100: [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md).
- Wire a Yaesu FT-450D: [hardware/yaesu-ft450d-cm108.md](hardware/yaesu-ft450d-cm108.md).
- Receive a Xiegu G90's or a SoftRock's IQ through a sound card: [hardware/iq-sound-card.md](hardware/iq-sound-card.md).
- Run two modems on one machine: [01-install.md](01-install.md#more-than-one-modem-on-one-machine).
- Upgrade or remove it: [01-install.md](01-install.md#upgrading).

## Reference

- [reference/config.md](reference/config.md) has every configuration key, with its type, default and what the modem refuses.
- [reference/command-line.md](reference/command-line.md) has every flag and the exit codes.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md) has every listener, the KISS command set, the ARDOP and paging protocols, the HTTP API and the metrics.
- [reference/files.md](reference/files.md) has every path the package installs, reads and writes.

Developers, including anyone writing a modem plugin or building from source, start at [dev/README.md](dev/README.md).
