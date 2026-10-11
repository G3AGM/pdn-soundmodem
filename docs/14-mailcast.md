# Receive GB7RDG's 40 m bulletins into your own BBS

GB7RDG sends packet BBS bulletins on 40 m every daylight hour, and you can drop them into your own LinBPQ or FBB without transmitting anything. The program for that is now [pdn-mailcast-receiver](https://github.com/packet-net/pdn-mailcast/blob/main/docs/receiver.md), version 0.9.0 or later. It runs beside pdn-soundmodem on the same machine and listens through this station's audio, so your radio and sound card stay as they are.

## The built-in receiver is going

pdn-soundmodem's own receiver, the [`mailcast`](reference/config.md#mailcast) section, is deprecated and will be removed in a later release. Until then it keeps working exactly as before, but it gets fixes only, nothing new. A station that still has the section says so once in the journal at start-up, and on the Mailcast panel of its station page:

```
mailcast: WARNING - this built-in mailcast receiver is deprecated and will be removed in a later release
mailcast: its replacement is pdn-mailcast-receiver 0.9.0 or later, which reads this station's audio ("audio": "soundmodem")
mailcast: to move across, copy bbs, sources and "retune": true from this "mailcast" section to the receiver's config (the same names), then remove the section here
mailcast: the guide: https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md
```

The third line lists only the keys your own section sets.

## Moving across

[Moving from pdn-soundmodem's built-in receiver](https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md) has the steps. In short:

1. Install pdn-mailcast-receiver from the packet-net apt repo, on the same machine.
2. In its config, set `"audio": "soundmodem"`, and copy your settings across with the table below. Most keep their names.
3. Remove the `mailcast` section from pdn-soundmodem's config, so only one receiver is listening, and restart both.

Your BBS needs no change: the receiver logs in as the same `Q0CAST`, with the same password. Once it is listening, this station's page names it, with a link to its own status page.

| In pdn-soundmodem's `mailcast` | In the receiver's config |
|---|---|
| `bbs`, with `type`, `host`, `port`, `login`, `password` and `command` | `bbs`, the same keys |
| `sources` | `sources` |
| `dialKHz` | `dialKHz` |
| `hooks`, with `before` and `after`, each `command`, `args` and `timeoutSeconds` | `hooks`, the same keys |
| `"retune": true` | `"retune": true`. pdn-soundmodem still does the tuning, so keep its `rig` section. |
| `stateDirectory` | Not copied. The receiver keeps its own state, and your BBS turns away any bulletin it already has. |
| (nothing) | `"audio": "soundmodem"`, or `"soundmodem:PORT"` if this station's page is not on port 8107 |

Leave out of the receiver's config anything your `mailcast` section left out: the defaults are the same.

## Still on the built-in receiver?

Its full set-up guide is [this page as it was in 0.87.5](https://github.com/packet-net/pdn-soundmodem/blob/v0.87.5/docs/14-mailcast.md), and every key is in [`mailcast`](reference/config.md#mailcast).
