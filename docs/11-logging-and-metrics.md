# Logging and metrics

When you finish, your station will keep a searchable record of every frame it hears and sends, capture the signals it could not read, and publish what it hears to Prometheus or InfluxDB with a ready-made Grafana dashboard.

Each part below is independent. Turn on the ones you want.

## Before you start

A working station, from [02-first-station.md](02-first-station.md).

`sqlite3` to read the frame log: `sudo apt install sqlite3`.

A `waterfall` section, if you want metrics. The two metrics endpoints ride the station page's HTTP listener, so there has to be one. See [07-station-page.md](07-station-page.md).

Disk for whatever you turn on. A frame log is a few hundred bytes a frame, survey captures stop at 512 MB, raw capture is about 2 GB a day at 12 kHz.

## Keep a frame log

Add a [`frameLog`](reference/config.md#framelog) section and restart:

```json
"frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
```

The path is the default, so `"frameLog": {}` gets you the same file. Under the `pdn-soundmodem@NAME` template unit the default becomes `/var/lib/pdn-soundmodem/NAME/frames.db`, so two instances never share one log.

The journal names the file at start-up:

```
frame log: /var/lib/pdn-soundmodem/frames.db
```

One row goes into the `frames` table per frame heard and per frame sent.

| Column | What it holds |
|---|---|
| `heard_at` | When it arrived, UTC, ISO 8601. On a `tx` row, when it went out |
| `direction` | `rx` for a frame the station heard, `tx` for one it sent |
| `sub_channel`, `mode`, `mode_name` | Which modem carried it, as `0`, `bpsk300-il2pc`, `BPSK300 IL2Pc`. The modem describes itself here, framing and all, so one you configured as `bpsk300` reads as `bpsk300-il2pc` |
| `source`, `destination` | AX.25 callsigns, null where none could be read |
| `length`, `payload` | Its size in bytes, and the frame itself as a blob |
| `crc_valid` | 1 where the frame's CRC checked, 0 where it did not, null where there was none to check |
| `plain_il2p` | 1 for a frame read as plain IL2P with no CRC behind it, which is what the page badges `RS ONLY` |
| `trailer_near_bits`, `monitor_only` | For a plain IL2P frame, how many of the 32 trailer bits disagreed with the payload (0 to 4), and whether the station showed you a frame while withholding it from your node or APRS software |
| `corrected`, `erased_bytes`, `chased_bits` | What the forward error correction had to do to read it |
| `snr_db` | Strength of the burst over the rolling noise floor, in dB |
| `peak_dbfs`, `clipped`, `level`, `peak_shown` | How loud the frame's own audio was, whether the card ran out of codes during it, the verdict its modem reached (`loud`, `quiet` or `ok`) and whether that figure is worth showing |
| `offset_hz` | How far off centre the sender was, where the modem could measure it |
| `audio_hz`, `rf_hz` | Where that modem sits in the audio, and on the band once you have set `rfFrequency` |
| `tx_trim_hz` | On a `tx` row, how far the burst was shifted to suit the station it was addressed to |
| `quality`, `ardop_sn_db` | ARDOP's own 0 to 100 constellation quality and its 3 kHz-referenced SNR, null on every other mode |
| `held_ms` | On a `tx` row, how long the frame waited between being handed to the channel and reaching the air, in milliseconds |
| `held_cause` | Which one thing took at least half of that wait: `busy`, `ourtx`, `slot`, `turnaround`, `inhibit`, `queue`, or `mixed` where none did |
| `held_busy_ms`, `held_ourtx_ms`, `held_slot_ms`, `held_turnaround_ms`, `held_inhibit_ms`, `held_queue_ms` | The same wait split by cause, in milliseconds: carrier sense, this station's own transmissions, the p-persistence roll, the turnaround hold, a transmit inhibit, and waiting behind another of this station's links |
| `held_busy_ch`, `held_busy_by` | Which sub-channel asserted carrier sense for most of the wait, and all of the ones that did: `2`, `0,2`, `radio` where the station's control cable answered for the whole station, or `radio,0` where both did |

`held_ms` is written for every transmission this station makes, including the ones that went out immediately, so that "how much of my traffic waits, and for how long" is a query rather than a guess. It is null on a received frame, where the wait belonged to the other station's channel access, and null on a transmission relayed from somewhere else. It covers the whole wait: carrier sense, the p-persistence roll, the turnaround hold, any transmit inhibit, and time spent behind this station's own earlier frames in the same keyup. The figure is worth having because the host cannot measure it at all: a KISS write returns as soon as the socket takes the frame, so a node whose UA sat behind a busy channel for four minutes sees exactly what one that sent it instantly sees, and runs its retry timers accordingly.

**The `held_*` columns say where that wait went, and they add up to `held_ms`.** The total on its own cannot tell you the one thing you need from it. A frame held 8 s because somebody else was using the frequency and a frame held 8 s because it was the third frame of a MAXFRAME=3 window, queued behind two of your own bursts, are the same number and opposite situations: the first is a frequency you cannot work on, the second is your station working normally. So the channel writes down which it was, and the journal line names it: `held 8.3s (8.1s channel busy on ch0)` against `held 8.3s (6.8s behind our own transmissions)`.

A frame queued behind another of yours inherits whatever was holding the one in front of it, so a window of three that waited out a minute of carrier sense reports a minute of carrier sense on all three rather than blaming your own queue for two of them: the channel was occupied and none of them was going anywhere. What is filed as your own traffic is the time the transmitter actually spent keyed up, which is `held_ourtx_ms`.

Where your station's airtime goes is then one query:

```sql
SELECT held_cause, COUNT(*), SUM(held_ms) / 1000 AS seconds_waiting
FROM frames WHERE direction = 'tx'
GROUP BY held_cause ORDER BY seconds_waiting DESC;
```

and which sub-channel is holding you up is the same query over `held_busy_ch`. All of these are null on a received frame, on a relayed transmission, and on rows written by a build from before they existed.

A `tx` row leaves the receive measurements null, because nothing measured your own transmission, and old rows carry null in columns that did not exist when they were written. Everything that keys the radio is a row: a KISS frame, an ARDOP burst, a transmitter test, a Morse ident and a POCSAG page. The last three have no callsigns, so `source` and `destination` are null, `payload` holds a sentence saying what went out, and `mode` reads `tx-test`, `cw-ident` or `pocsag` and the paging baud.

## Query the log

The file is a SQLite database in write-ahead mode, which means you can read it while the modem is running and writing to it:

```
sqlite3 /var/lib/pdn-soundmodem/frames.db
```

Who you have heard in the last day, busiest first:

```sql
SELECT source, COUNT(*) AS frames, MAX(datetime(heard_at)) AS last_heard
FROM frames WHERE direction = 'rx' AND datetime(heard_at) > datetime('now', '-1 day')
GROUP BY source ORDER BY frames DESC;
```

How well each station gets in, per mode, since one callsign on two modes is two different paths:

```sql
SELECT source, mode, COUNT(*) AS frames, ROUND(AVG(snr_db), 1) AS mean_snr
FROM frames WHERE direction = 'rx' AND snr_db IS NOT NULL
GROUP BY source, mode ORDER BY frames DESC;
```

What you put on the air today, leaving out tests and idents:

```sql
SELECT datetime(heard_at), mode, source, destination, length
FROM frames WHERE direction = 'tx' AND mode NOT IN ('tx-test', 'cw-ident')
AND date(heard_at) = date('now') ORDER BY id DESC;
```

Whether your receive level suits the traffic, which is the same verdict as the page's `TOO LOUD` and `TOO QUIET` badges:

```sql
SELECT level, COUNT(*) FROM frames
WHERE direction = 'rx' AND level IS NOT NULL GROUP BY level;
```

Mostly `loud` or `quiet` means a gain to change; see [04-levels.md](04-levels.md). To copy the log off a running station, take `frames.db`, `frames.db-wal` and `frames.db-shm` together; after a clean stop, `frames.db` alone holds everything ([copying and backing up](reference/files.md#copying-and-backing-up)).

## Capture what you could not read

A survey watches the whole passband for packet-shaped bursts and writes out the ones no modem read. Add a [`survey`](reference/config.md#survey) section:

```json
"survey": { "path": "/var/lib/pdn-soundmodem/survey" }
```

The journal prints `survey: /var/lib/pdn-soundmodem/survey` at start-up. Each capture is a WAV and a JSON sidecar of the same name, stamped with the time, the measured centre and the verdict:

```
20260804-151909-862hz-unclaimed.wav
20260804-151909-862hz-unclaimed.json
```

| Verdict | What it means |
|---|---|
| `unclaimed` | Packet-shaped, and outside every modem's band. Nobody was listening there |
| `missed` | Inside a modem's band, and nothing decoded. Your receiver could not read it |
| `unattributed` | Something decoded, and carried no readable AX.25 addresses |

Normal traffic your modems read is dropped, and so is anything too wide, too narrow or too long to be a packet, and nothing is captured while you transmit. By default a survey keeps 512 MB of captures, 30 an hour, 120 seconds before the same part of the spectrum is captured again, bursts up to 20 seconds and no weaker than 6 dB over the noise floor. The oldest captures are deleted to make room. All of those are keys you can change.

With a station page, each capture is bracketed on the waterfall where and when it happened and listed in the frames panel with `audio` and `details` links. Those files are served at `/survey/<file>` on the page port, which is a reason to keep that port off the open internet.

The sidecar records the centre, edges, width, duration and SNR, the RF frequency where the dial is known, and the modems you were running at the time, so a capture read months later still says what its verdict meant. Point the decoder at the WAV to find out what it was: [13-decode-a-recording.md](13-decode-a-recording.md).

### Let the station propose modems

Add `"propose": true` and each capture is read back with every mode that could have carried it. Once enough separate captures agree, the station says what it would take to read the traffic:

```
propose: add afsk300 at 7.050570 MHz - 34 frame(s) in 34 capture(s), PD4R-12, 19 dB, 2026-08-06 to 2026-08-24
```

A proposal either adds a modem where nobody was listening, or changes the framing of one that already covers the frequency and cannot read what is there. Three captures of evidence are needed by default, each carrying a frame whose own check sequence verified.

With an [`api`](reference/config.md#api) key set, `GET /api/proposals` returns each proposal with a `config` you can POST to `/api/config`; see [the API](reference/ports-and-endpoints.md#the-api-under-api).

## Record the whole channel

Raw capture keeps the receive audio unedited, for re-decoding a whole run offline later. Add a [`rawCapture`](reference/config.md#rawcapture) section:

```json
"rawCapture": { "path": "/var/lib/pdn-soundmodem/raw", "chunkMinutes": 15 }
```

You get 15-minute mono WAV chunks named `raw-20260804T151909Z.wav` at the DSP rate, pruned oldest first at 4 GB. That is about two days at 12 kHz. Recording pauses while you transmit, and a chunk stays a readable WAV after a power cut.

## Publish metrics

Add a [`metrics`](reference/config.md#metrics) section beside your `waterfall`:

```json
"waterfall": { "port": 8107 },
"metrics": { "enabled": true }
```

The journal says where they are:

```
metrics: http://127.0.0.1:8107/metrics (prometheus) and http://127.0.0.1:8107/metrics/frames (one point per frame, influx line protocol). No authentication.
```

`/metrics` is Prometheus text: totals and sums per station and mode, for rates and dashboards. `/metrics/frames` is InfluxDB line protocol, one point per frame heard in the last 300 seconds, for plotting individual frames. Both are unauthenticated, so put them behind a reverse proxy or a VPN if the port is reachable from outside.

```
curl -s http://localhost:8107/metrics | grep pdn_station_frames_total
pdn_station_frames_total{station="M0LTE",mode="afsk1200"} 7
```

A frame counts towards a station only when its own check sequence verified, the station passed it to your node or APRS software, and its source address parsed. Everything else adds one to `pdn_frames_uncounted_total`, so a big number there beside a short station list is a receiver at its limit.

SSIDs fold into the callsign, since `GB7IOW-1` and `GB7IOW-9` are one transmitter. Modes stay apart. Every series is in the [metrics reference](reference/ports-and-endpoints.md#metrics).

`/metrics` also says whether the station is keeping up. `pdn_receive_realtime_ratio` is the share of real time that reached the modems over the last minute, and `pdn_receive_behind` is 1 while it is under 95%. A station that cannot keep up loses audio before any modem hears it, which looks from outside exactly like a quiet band, so it says so in the journal too:

```
receive: BEHIND real time - 40% of the audio reached the modems over the last 60 s; the other 60% is lost or queued behind (frames missed or late, the waterfall slow, Listen chopped). The receive loop was busy 91% of that time, about 2.3 s of work per second of audio: it is the bottleneck. ...
```

When the loop is the bottleneck, the modems on the channel want more CPU than the station is getting. When it was mostly waiting, the audio went missing on the way in: the network, the radio or the device.

Scrape `/metrics` with Prometheus, or anything that reads Prometheus text. Under `scrape_configs` in `prometheus.yml`:

```yaml
  - job_name: pdn-soundmodem
    static_configs:
      - targets: ['station.example:8107']
```

The per-frame feed needs a collector that pulls line protocol. With Telegraf:

```toml
[[inputs.http]]
  urls = ["http://station.example:8107/metrics/frames"]
  interval = "10s"
  data_format = "influx"
```

Collect faster than `frameWindowSeconds` (300 by default) or you lose frames. Collecting twice inside the window is harmless, since reading consumes nothing and a repeated point replaces itself.

## Import the Grafana dashboard

The dashboard is at [reference/grafana/pdn-soundmodem.json](reference/grafana/pdn-soundmodem.json). In Grafana, go to Dashboards, New, Import and upload the file. Then pick your datasources from the Prometheus and InfluxDB pickers at the top of the dashboard, beside the station picker.

## Check it worked

```
sqlite3 /var/lib/pdn-soundmodem/frames.db "SELECT COUNT(*) FROM frames;"
curl -s http://localhost:8107/metrics | grep pdn_stations
ls /var/lib/pdn-soundmodem/survey | tail
```

A rising count, a `pdn_stations` figure of 1 or more once a frame has been heard, and captures appearing on a busy band.

## If it did not

`metrics: WARNING - nothing to serve them on` means there is no `waterfall` section. The figures are being collected and nothing is serving them; add a `waterfall` with a port.

The modem exits with `cannot open the frame log at ...` when the service user cannot write to the directory. Give the directory to the service user, or drop the section. `survey` and `rawCapture` fail the same way, naming their directory.

A `frames.db` that looks 4 KB while the station runs is not empty. The rows are in `frames.db-wal` beside it, and every SQLite reader sees them. A clean stop folds them into the file.

No captures at all usually means the band is quiet, or everything on it is being decoded. On a busy band, read the survey counter in the frames panel header, which shows captures, skipped and megabytes: `maxPerHour` may be turning most of them away.

Empty Grafana panels with a live `/metrics` are usually the wrong datasource on the import, or a scrape that is not running. Check Prometheus has the target up.

More symptoms are in [12-troubleshooting.md](12-troubleshooting.md).

## Related

- [07-station-page.md](07-station-page.md) shows the same frames and captures live.
- [04-levels.md](04-levels.md) acts on the `level` column.
- [09-web-receivers.md](09-web-receivers.md) pairs a receive-only station with a frame log.
- [13-decode-a-recording.md](13-decode-a-recording.md) decodes a capture.
- [reference/config.md](reference/config.md) has every key in `frameLog`, `survey`, `metrics` and `rawCapture`.
- [reference/ports-and-endpoints.md](reference/ports-and-endpoints.md) has the endpoints and every metric series.
- [reference/files.md](reference/files.md#what-the-modem-writes-and-when) has what lands on disk and what caps it.
