# Licensing

pdn-soundmodem as a whole is licensed under the **GNU Affero General Public License, version 3 or later** (AGPL-3.0-or-later). The text is in [COPYING](COPYING).

A small number of files carry material derived from someone else's GPL program, so they are not Tom's to relicense. They stay under the **GNU General Public License, version 3 or later** (GPL-3.0-or-later), and one stays GPL-3.0-only. They are all listed below with the reason. The GPLv3 text is in [LICENSES/GPL-3.0.txt](LICENSES/GPL-3.0.txt).

The two licences are designed to be combined. Section 13 of GPLv3 and section 13 of AGPLv3 each allow a GPLv3 work and an AGPLv3 work to be linked into one program and conveyed together. Each part keeps its own licence, and the AGPL's network clause (section 13: users who interact with the program over a network must be offered its source) applies to the combination. pdn-soundmodem serves a web page and an HTTP API, so in practice the program you build or install is used under AGPL terms, network clause included.

## Which licence a file is under

1. A file with an `SPDX-License-Identifier` line is under the licence it names.
2. A file listed on this page is under the licence given here. This covers files that do not carry a header, such as documentation.
3. Every other file is AGPL-3.0-or-later.

A header joining two licences with `AND` marks a file that contains material under both; each licence applies to its own lines.

## Files that stay GPL because they derive from third-party GPL code

[PROVENANCE.md](PROVENANCE.md) has the full history of each component. These are the files in this repository that carry GPL-derived material, meaning code, algorithms, formulas, constants, tables or bit-level behaviour taken from the source named.

| File | Licence | Derived from | What was taken |
|---|---|---|---|
| `src/Packet.SoundModem/Modems/AfskDemodulator.cs` | GPL-3.0-or-later | UZ7HO SoundModem via QtSoundModem (GPLv3+), Dire Wolf (GPL-2.0-or-later) | The demodulator chain, the discriminator formula and the per-mode filter plan from QtSoundModem; the DPLL inertia constant from Dire Wolf |
| `src/Packet.SoundModem/Modems/Afsk1200MultiModem.cs` | GPL-3.0-or-later | UZ7HO SoundModem via QtSoundModem | The offset decoder bank and its default spacing, and the flat/+6/+12 dB per octave emphasis set (`emph_all`) |
| `src/Packet.SoundModem/Modems/BitDpll.cs` | GPL-3.0-or-later | Dire Wolf | The DPLL design and its inertia constant |
| `src/Packet.SoundModem/Modems/PacketDcd.cs` | GPL-3.0-or-later | Dire Wolf 1.6 | The DCD algorithm and its constants |
| `src/Packet.SoundModem/Modems/BpskDemodulator.cs` | GPL-3.0-or-later | QtSoundModem, Dire Wolf | The per-mode band-pass plan from QtSoundModem's tables; the DPLL inertia constant |
| `src/Packet.SoundModem/Modems/QpskDemodulator.cs` | GPL-3.0-or-later | QtSoundModem, Dire Wolf | The per-mode filter plan from QtSoundModem's tables and its whole-samples-per-symbol approach; the DPLL inertia constant |
| `src/Packet.SoundModem/Modems/FskModem.cs` | GPL-3.0-or-later | Dire Wolf (`demod_9600.c`) | The receive chain outline and the interpolation before the clock |
| `src/Packet.SoundModem/Modems/G3ruhScrambler.cs` | GPL-3.0-or-later | Dire Wolf (`gen_tone.c`, `hdlc_rec.c`) | The scrambler expressions bit for bit, and the NRZI and scramble ordering |
| `src/Packet.SoundModem/Modems/C4fskModem.cs` | GPL-3.0-or-later | MMDVM-TNC (GPL-2.0-or-later, G4KLX), Dire Wolf | The wire format: preamble byte, sync word, dibit-to-level map and the Mode 2 layout; the DPLL inertia constant |
| `src/Packet.SoundModem/Fx25/Fx25Codec.cs` | GPL-3.0-or-later | Dire Wolf (`fx25_init.c`, `fx25_send.c`) | The correlation tag table, block formats and the match tolerance |
| `src/Packet.SoundModem/Fx25/Fx25Deframer.cs` | GPL-3.0-or-later | Dire Wolf (`fx25_rec.c`) | The receive-side wire behaviour |
| `src/Packet.SoundModem/UberSdr/PcmBinaryDecoder.cs` | **GPL-3.0-only** | ka9q_ubersdr (madpsy, GPL-3.0) | A direct port of `clients/iq-recorder/pcm_decoder.go`. Upstream ships the GPLv3 text without saying "or later", so this file does not claim it either |

Dire Wolf and MMDVM-TNC are GPL-2.0-or-later and are used here under their "or later" clause as GPL-3.0-or-later.

## Contributions from other authors

Tom Wardill contributed the `pdn-soundmodem@` systemd template unit on 2026-09-14 (PR #477, commits `159bf32` and `60a7175`), originally under GPL-3.0-or-later. Tom Wardill agreed on 2026-10-05, relayed by Tom M0LTE, to relicense that contribution under AGPL-3.0-or-later, so those files (`StateDirectory.cs`, its test, `packaging/pdn-soundmodem@.service` and his lines in the packaging scripts, daemon config code, release workflow and docs) are AGPL-3.0-or-later like the rest of the repository.

Everyone else who has committed to this repository is Tom (as Tom Fanning or M0LTE) or Claude working for him.

## Considered and left AGPL

These files mention a GPL program, or sit in a PROVENANCE row that does, but take no code, constants or tables from it. Moving any of them to the list above costs nothing, because the combination is AGPL either way, so if in doubt, move it.

| File | Why it is AGPL |
|---|---|
| `src/Packet.SoundModem/Hdlc/*` | Written from the AX.25 and HDLC specifications |
| `src/Packet.SoundModem/Modems/EnergyBusyDetector.cs` | Only the idea came from QtSoundModem's busy detector; the design is this project's |
| `src/Packet.SoundModem/Modems/BpskModem.cs`, `QpskModem.cs`, `BpskModulator.cs`, `QpskModulator.cs`, `BpskMultiModem.cs`, `QpskMultiModem.cs`, `Afsk300MultiModem.cs`, `CostasLoop.cs` | Symbol maps from the IL2P specification, a textbook Costas loop, and a decoder bank that follows UZ7HO's idea with this project's own code and measured numbers. The QtSoundModem filter numbers live in the demodulators above |
| `src/Packet.SoundModem/Modems/Afsk1200Modem.cs`, `Afsk1200Il2pModem.cs`, `Afsk300Modem.cs`, `AfskModulator.cs`, `Il2pReceiver.cs` | Name Dire Wolf or QtSoundModem only to compare with them or to match their on-air behaviour; their constants come from the specifications or from the files above |
| `src/Packet.SoundModem/Channel/Cm108Ptt.cs` | The HID report is a hardware convention documented by C-Media (as Hamlib quotes it); no code taken |
| `src/Packet.SoundModem/Kiss/*`, `Channel/SoundModemChannel.cs`, `Modems/IModem.cs` | Written from the KISS specification; several modems on one channel is QtSoundModem's model as an idea only |
| `src/Packet.SoundModem/UberSdr/UberSdrAudioInput.cs`, `UberSdrDevice.cs`, `tools/Packet.SoundModem.UberSdr/UberSdrIqClient.cs` | Independent implementations of the ka9q_ubersdr connection protocol; only `PcmBinaryDecoder.cs` is a port |
| `src/Packet.SoundModem/Modems/FreeDvDatacModem.cs`, `tools/gen-ldpc-tables/gen.py` | The codec2 lineage is LGPL-2.1, not GPL, and lives in the `M0LTE.Ofdm` and `M0LTE.FecLdpc` packages; this glue and the generator script are original |
| `src/Packet.SoundModem.Daemon/ArdopChannelBridge.cs`, `ArdopBusyDetector.cs`, `ArdopReplyWindow.cs` | ardopcf is MIT, which an AGPL work can carry; the ported ARDOP code is in the `M0LTE.Ardop` package |
| `src/Packet.SoundModem.Daemon/Mailcast/*` | The built-in pdn-mailcast receiver. It follows pdn-mailcast's own receiver, which is Tom's, and uses its libraries as packages; nothing in it comes from a third-party GPL program |
| Tests and tools that run against Dire Wolf, QtSoundModem or multimon-ng (`DirewolfCrossValidationTests.cs`, `QtsmInteropTests.cs`, `Pocsag/MultimonNg.cs`, `tools/Packet.SoundModem.QtsmBench`) | Interoperability checks against those programs; no code taken |

## Other licences in the tree

- `src/Packet.SoundModem/OpenWebRx/OpenWebRxProtocol.cs` and `ImaAdpcm.cs` (`AGPL-3.0-or-later`) follow OpenWebRX, which is AGPL-3.0-or-later too: the receiver protocol and the ADPCM sync framing, read from its sources (PROVENANCE.md names the files). The licence is this repository's own, so nothing about the combination changes; the headers are there to say where the shapes came from.
- `samples/ardop/gen-reference-vectors.c` (`AGPL-3.0-or-later AND MIT`) contains three functions copied verbatim from ardopcf (`GenCRC16`, `GenCRC8`, `ComputeTypeParity`), MIT, Copyright (c) 2014-2024 Rick Muething, John Wiseman, Peter LaRue; the file carries ardopcf's MIT notice and those functions stay MIT. It is kept because it is the documented generator of `ardop-reference-vectors.txt`, which `M0LTE.Ardop`'s tests still use.
- Recordings and fixtures under `samples/` and `tests/**/Fixtures/` are data captured from radios or produced by other programs. This page does not relicense them.
- Dependencies keep their own licences. `Packet.Mailcast` and `M0LTE.RaptorQ` (pdn-mailcast's libraries, for the built-in receiver) are AGPL-3.0-only, which combines with this repository's AGPL-3.0-or-later under AGPL-3.0; `Packet.Fbb` (from pdn-fbb) is AGPL-3.0-or-later. `M0LTE.Il2p` and `M0LTE.FmChannel` are GPL-3.0-or-later; `M0LTE.Ardop`, `M0LTE.Dsp`, `M0LTE.Fec`, `M0LTE.FecLdpc`, `M0LTE.Flex`, `M0LTE.Ofdm`, `M0LTE.Pocsag`, `M0LTE.Radio.Audio`, `Packet.Ax25` and `Packet.Core` are AGPL-3.0-or-later; the .NET runtime and the other NuGet dependencies are MIT, Apache-2.0 or BSD.

## Published packages

- The `pdn-soundmodem` NuGet package contains the files above, so its licence expression is `AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only`.
- `pdn-soundmodem-linux` and `pdn-soundmodem-windows` contain none of them and are AGPL-3.0-or-later.
- The `.deb` and the `@packet-net/soundmodem` npm package are AGPL-3.0-or-later as a whole, with the GPL parts named in their `copyright` and `NOTICE` files.

## Rules for new code

- New code is AGPL-3.0-or-later and needs no header.
- Code derived from a GPL source gets that source's licence (GPL-3.0-or-later where the source allows "or later", otherwise what it does allow), an `SPDX-License-Identifier` header, a row on this page and a provenance comment naming the source file and function.
- A contribution from anyone other than Tom stays under the licence it was offered under until its author agrees otherwise; list it here, and record the agreement when it comes.
- Never copy code from this repository into an MIT-licensed package, and never let an MIT-licensed package depend on this one.
