# Files and directories

What the Debian package installs, what pdn-soundmodem reads and writes on disk, and what an upgrade, a remove or a purge leaves behind. The package is built by [`packaging/build-deb.sh`](../../packaging/build-deb.sh) and the service runs under [`packaging/pdn-soundmodem.service`](../../packaging/pdn-soundmodem.service); every path on this page is as the shipped package lays it out. A second modem on the same machine runs as a template instance, [`pdn-soundmodem@NAME`](#more-than-one-modem), from its own config file and its own state directory.

## What the package installs

| Path | What it is |
|---|---|
| `/usr/lib/pdn-soundmodem/pdn-soundmodem` | The modem itself, a self-contained single-file build. No .NET runtime is installed or needed on the machine |
| `/usr/lib/pdn-soundmodem/*.so` | Native libraries published beside the binary (`libSystem.IO.Ports.Native.so` for serial PTT, `libe_sqlite3.so` for the frame log). The modem finds them relative to its own real path, so the binary lives here and `/usr/bin` holds a symlink |
| `/usr/bin/pdn-soundmodem` | Symlink to the binary above |
| `/usr/lib/systemd/system/pdn-soundmodem.service` | The systemd unit. Enabled and started on install, restarted on upgrade |
| `/usr/lib/systemd/system/pdn-soundmodem@.service` | The template unit. `pdn-soundmodem@NAME` runs `/etc/pdn-soundmodem/NAME.json` with `/var/lib/pdn-soundmodem/NAME/` as its state directory. Never enabled by the package |
| `/etc/systemd/system/multi-user.target.wants/pdn-soundmodem.service` | The enable symlink, created by postinst on first install. Not shipped in the package |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | The example config, a copy of [`soundmodem.example.json`](../../soundmodem.example.json) from the repository. The file postinst seeds from |
| `/etc/pdn-soundmodem/` | Shipped as an empty directory, so it outlives a remove and purge has to delete it by hand |
| `/etc/pdn-soundmodem/soundmodem.json` | The station's config. Seeded by postinst from the example if absent, owned by root, mode 0644. Not a dpkg-owned file |
| `/var/lib/pdn-soundmodem/` | The state directory. Created by systemd from `StateDirectory=` at each start, owned by the service user, mode 0750. A template instance's is `NAME/` inside it |
| `/usr/share/doc/pdn-soundmodem/copyright`, `changelog.Debian.gz` | Licence and changelog |

The seeded config names `default` as the sound card and a CM108 interface on `/dev/hidraw0`, so the first start after a fresh install fails until the file is edited; `systemctl status pdn-soundmodem` says why. The package depends on `libc6`, `libgcc-s1`, `libstdc++6`, `libasound2` (or `libasound2t64`) and `adduser`, section `hamradio`.

## What the modem reads

| Path | When |
|---|---|
| `/etc/pdn-soundmodem/soundmodem.json` | At every start, from `--config` in the unit's `ExecStart`; that flag is the only way the file is found (see [the file](config.md#the-file)) |
| `/var/lib/pdn-soundmodem/pending-config.json` | At start, if present: a one-run config left by `POST /api/config` without `?persist=true` (see [the API](ports-and-endpoints.md#the-api-under-api)). It is loaded in place of the config file and deleted as soon as it has been read, before the station is built, whether or not it parsed. The journal then says the station is running a one-run configuration |
| `/var/lib/pdn-soundmodem/rig-restore-HOST-PORT.json` | At start, with a [`rig`](config.md#rig) section: where the rig goes back to after a tuning window the station was stopped in the middle of, one file per `rigctld` (`rig-restore-127.0.0.1-4532.json`). The rig is put back before anything is sent, and the file is deleted once it has been. One that names a different `rigctld` is left alone with a journal line |
| `/var/lib/pdn-soundmodem/mailcast/` | At start, with a [`mailcast`](config.md#mailcast) section: the receiver's pieces and rebuilt bulletins (`store/`) and what the BBS said about each (`deliveries.jsonl`), so partial bulletins add up across restarts and nothing waiting for the BBS is lost. With `mailcast.hooks`, `hooks.json` is there while an "after" command is owed. An outbox file that cannot be read is moved to `store/quarantine/`, which nothing prunes; a bulletin the BBS refused is removed, since the receiver keeps no archive of answered bulletins. `mailcast.stateDirectory` moves it |
| `/var/lib/pdn-soundmodem/mixer-state.json` | At start, on a sound card that has a mixer: the capture gain and playback level set from the station page or `/api/mixer` in an earlier run. A key set in `alsa.mixer` wins over it (see [`alsa`](config.md#alsa)). Ignored with a journal line if it was written for a different `device` or does not parse |
| `modemPlugins[].path` | At start, before the modem list is planned: each named assembly and the `.deps.json` beside it. A plugin that fails to load is reported and skipped; a modem entry naming one of its modes then fails start-up as an unknown mode. See [modem plugins](../dev/modem-plugins.md) |
| `--wav FILE`, `--wav-loop FILE` | A recording in place of live audio |
| `/usr/share/pdn-soundmodem/soundmodem.example.json` | Only to decide whether a configuration-error message can offer a `cp` of the example |

The state directory is wherever systemd's `$STATE_DIRECTORY` points, which is `/var/lib/pdn-soundmodem` under the shipped unit and `/var/lib/pdn-soundmodem/NAME` under `pdn-soundmodem@NAME`. The default `frameLog.path`, `survey.path` and `rawCapture.path` follow it too, so two instances never share a frame log. Run from a terminal without it, `pending-config.json`, `mixer-state.json` and the `rig-restore-*.json` files sit beside the config file and the three defaults are the `/var/lib/pdn-soundmodem` paths below. `alsa.mixer.stateFile` moves the mixer state file anywhere.

Device nodes the modem opens: `/dev/snd/*` for the sound card, `/dev/ttyUSB*` or `/dev/ttyS*` for serial PTT, `/dev/hidraw*` for CM108 PTT (opened for writing). A `pipe:IN,OUT` device creates the two FIFOs if they do not exist.

## What the modem writes and when

| Path | Written when | Size cap |
|---|---|---|
| `/var/lib/pdn-soundmodem/mixer-state.json` | A mixer change from the station page or `POST /api/mixer`. Persisted unless the request says `?persist=false`; the page never sends that. Written to `.mixer-state.json.<pid>.tmp` in the same directory, then renamed over the file. Holds `device`, `writtenAt`, `captureGainDb` and `playbackDb`, and only the controls that have been set | One small JSON file |
| `/var/lib/pdn-soundmodem/rig-restore-HOST-PORT.json` | A rig tuning window opening: `rigctld`, `dialHz`, `mode` and `passbandHz` of where the rig was. Written to the same name plus `.tmp`, flushed to the disk, then renamed over the file, before the rig is moved; deleted once the rig is confirmed back | One small JSON file |
| `/var/lib/pdn-soundmodem/pending-config.json` | `POST /api/config` without `?persist=true`. The body is written verbatim, the process exits 1 and systemd restarts it on the new document. Deleted at that start | One config document |
| `/etc/pdn-soundmodem/soundmodem.json` | `POST /api/config?persist=true` only. The body is written verbatim. Nothing else in the modem writes the config file | One config document |
| `/var/lib/pdn-soundmodem/frames.db` (`frameLog.path`) | Every frame received or sent, and one row per transmitter test, when `frameLog` is set. SQLite in WAL mode, so `frames.db-wal` (and SQLite's `frames.db-shm`) sit beside it while the modem runs; the WAL is folded into the file at a clean shutdown. The directory is created if missing. Writes that fail are dropped and counted | None; grows until you prune it |
| `<frameLog.path>/frames-<slug>.db` | A monitor site: one log per receiver inside the directory `frameLog.path` names. A monitor refuses a `frameLog.path` that names a file or ends in `.db` | None |
| `/var/lib/pdn-soundmodem/survey/` (`survey.path`) | One `<yyyyMMdd-HHmmss>-<centre>hz-<verdict>.wav` and a `.json` sidecar per kept burst, when `survey` is set. Oldest deleted first, by name, when the directory is over budget. Served back at `/survey/<file>` on the page port (see [Routes](ports-and-endpoints.md#routes)) | `survey.maxBytes`, 512 MiB by default; at most `survey.maxPerHour` (30) captures an hour |
| `/var/lib/pdn-soundmodem/raw/` (`rawCapture.path`) | `raw-<yyyyMMddTHHmmssZ>.wav` chunks of `rawCapture.chunkMinutes` (15) of receive audio each, when `rawCapture` is set. Oldest deleted first when over budget; the chunk being written is never deleted | `rawCapture.maxBytes`, 4 GiB by default |
| `$TMPDIR/pdn-soundmodem-proposed-<id>.json`, `/tmp` under the unit | Every `POST /api/config`, to validate the proposed document through the start-up parser. Deleted when the request is answered | Transient |
| The FIFOs named in `pipe:IN,OUT` | At start, if absent. Mode 0666 before the umask | |

The mixer state file is the only file written without a config section asking for it. `--mixer-show`, `--uplink-token` and `--version` write nothing. `--two-tone` and `--tone` build the station as configured, so a config with `frameLog` gets one row per test, and one with `survey` or `rawCapture` has the directory created and, for `rawCapture`, a chunk written while the test runs. The journal is stdout and stderr under systemd; the modem keeps no log file of its own.

## Ownership and permissions

| Item | Value |
|---|---|
| Service user | `pdn-soundmodem`, a system user with no home directory, created by postinst with `adduser --system --no-create-home --group` |
| Supplementary groups | `audio` for `/dev/snd/*`; `dialout` for serial PTT on `/dev/ttyUSB*` and `/dev/ttyS*` |
| `/var/lib/pdn-soundmodem` | `StateDirectory=pdn-soundmodem`, `StateDirectoryMode=0750`, owned by the service user; created by systemd at every start. The template has `StateDirectory=pdn-soundmodem/%i` |
| `/etc/pdn-soundmodem/soundmodem.json` | root, 0644, readable by the service. `ReadWritePaths=/etc/pdn-soundmodem` lifts `ProtectSystem=full` for that one directory so a `?persist=true` write is not blocked by the unit |
| Hardening | `NoNewPrivileges=true`, `ProtectHome=true`, `ProtectSystem=full` |
| Realtime priority | `LimitRTPRIO=10` permits realtime scheduling up to priority 10; nothing in the modem asks for it, so the line has no effect |
| Start condition | `ConditionPathExists=/etc/pdn-soundmodem/soundmodem.json`: the unit does not start at all without the config file. The template's is `/etc/pdn-soundmodem/%i.json` |
| Stopping | `KillMode=mixed` and `TimeoutStopSec=400`: `systemctl stop` signals the modem alone, not a [mailcast hook](config.md#mailcasthooks) it is running, and waits for it to put a retuned rig back and run an owed "after" (up to 300 s). The limit applies with or without hooks; without them the modem ends a stop that has wedged itself at 90 s, exit 1, as systemd's default did before |
| Exit codes | `Restart=on-failure` with `RestartPreventExitStatus=2`: exit 2 (the configuration is wrong) is not retried, so the journal carries one explanation; any other non-zero exit, or a crash, restarts after `RestartSec=5`; exit 0 stays stopped. The config API's restart exits 1. The codes are listed in the [command-line reference](command-line.md#exit-codes) |

The seeded config file is owned by root, and `ReadWritePaths` only removes the read-only mount. A `?persist=true` write from the service therefore fails with a permission error until the file is made writable by the service user, for example `chown pdn-soundmodem /etc/pdn-soundmodem/soundmodem.json`. Neither the package nor the unit does this. The 500 the modem answers with blames `ProtectSystem=full` and suggests adding `ReadWritePaths=/etc/pdn-soundmodem`, which the shipped unit already has; the file's ownership is the cause.

`/dev/hidraw*` is root-only by default and CM108 PTT opens it for writing, so the service user needs a udev rule granting the `audio` group access to the interface's node, in `/etc/udev/rules.d/99-pdn-soundmodem-cm108.rules`, with the interface's vendor and product IDs (`0d8c:013c` is a common C-Media interface):

```
KERNEL=="hidraw*", ATTRS{idVendor}=="0d8c", ATTRS{idProduct}=="013c", MODE="0660", GROUP="audio"
```

The rule takes effect once udev has reloaded its rules and the interface has been replugged. `cat /sys/class/hidraw/*/device/uevent` maps each `hidraw` node to its USB IDs (`HID_ID=0003:00000D8C:00000012` for the C-Media interface on the bench); the number moves with what else is plugged in. Serial PTT needs no rule because the unit already joins `dialout`. [02-first-station.md](../02-first-station.md#choose-the-ptt-line) walks through adding the rule, and the [Tait TM8100 page](../hardware/tait-tm8100-cm108.md) covers one CM108 interface in detail.

## Upgrade, remove and purge

| Action | Removed | Kept |
|---|---|---|
| Upgrade (`apt upgrade`, or `apt install` of a newer version) | The old binary, shims, units, example config and doc files, each replaced by the new one | `/etc/pdn-soundmodem/soundmodem.json` (postinst seeds only when the file is absent), every `NAME.json`, `/var/lib/pdn-soundmodem` and everything in it, the service user, the unit's enablement and each instance's. The unit is restarted if it is enabled or running, and so is every running instance |
| Remove (`apt remove`) | Binary, shims, symlink, both units, example config, doc directory. The unit and every running instance are stopped by prerm; the unit is masked by postrm | `/etc/pdn-soundmodem/soundmodem.json`, every `NAME.json` and their directory, `/var/lib/pdn-soundmodem` and everything in it, the service user, the instances' enable symlinks |
| Purge (`apt purge`) | Everything remove removes, plus `/etc/pdn-soundmodem/soundmodem.json`, `/etc/pdn-soundmodem` if then empty, the unit's enable symlinks and mask, every instance's enable symlink, and the `pdn-soundmodem` user and its group (the group stays if another user has been added to it) | Every `NAME.json` an operator wrote, and `/var/lib/pdn-soundmodem` and everything in it: the frame logs, survey captures, raw captures and mixer state files of the unit and of every instance |

No maintainer script touches `/var/lib/pdn-soundmodem`; delete it by hand after a purge if you want it gone. A reinstall of the same version keeps the unit enabled. Postinst does not re-enable a unit an operator has disabled; it records the symlinks so purge can clean them up. Instances are found by name: postinst and prerm ask systemd which `pdn-soundmodem@*.service` units are running, and postrm removes whatever `pdn-soundmodem@*.service` symlinks are in `/etc/systemd/system/*.wants/`.

## More than one modem

`pdn-soundmodem@NAME.service` is the plain unit with the two paths parameterised: `ExecStart` reads `/etc/pdn-soundmodem/NAME.json` and `StateDirectory=pdn-soundmodem/NAME` gives it `/var/lib/pdn-soundmodem/NAME/`, created and owned like the parent. User, groups, hardening, `ReadWritePaths`, the restart policy and the exit-2 rule are the same. `systemctl enable --now pdn-soundmodem@NAME` is the whole of starting one; the package never enables an instance, and the plain unit can be disabled on a machine that runs everything by name.

Each config file must claim its own ports. `kissPort`, `waterfall.port`, `paging.port`, `ardop.port` and a modem entry's own `port` are all bound by the process that reads them; the second process to ask for a port fails to start with the bind error in its journal. The sound device and the PTT line must differ for the same reason. Two instances that both enable `frameLog` without a `path` write two files, `/var/lib/pdn-soundmodem/NAME/frames.db` each, because the default follows `$STATE_DIRECTORY`; a `path` set explicitly is used as written, so two files that name the same path share it, which SQLite tolerates and nothing here wants.

## Copying and backing up

| What | How |
|---|---|
| The station's configuration | `/etc/pdn-soundmodem/soundmodem.json`, plus `/var/lib/pdn-soundmodem/mixer-state.json` if a level was set from the page rather than the file |
| The frame log while the modem runs | Copy `frames.db`, `frames.db-wal` and `frames.db-shm` together, or read it in place with `sqlite3`. A copy of `frames.db` alone taken mid-run is missing everything still in the WAL |
| The frame log after a clean stop | `frames.db` alone; the WAL has been folded in |
| Survey and raw captures | Plain WAV and JSON files; copy the directory |
| The journal | `journalctl -u pdn-soundmodem`; the modem writes no log file |

## The station page assets

The station page and the monitor picker are two HTML files embedded in the binary as resources (`waterfall.html` and `monitor.html`) and served by the modem's own HTTP listener on the page port. Nothing is installed for them: there is no web root, no directory to serve and no page state on the machine. Display settings (dial, sideband, span, levels, volume) are kept per browser. The `web/` folder in the repository is the separate WebAssembly package and is not part of the `.deb`.

Related: [the guide](../README.md), [configuration reference](config.md), [command-line reference](command-line.md), [ports and endpoints](ports-and-endpoints.md).
