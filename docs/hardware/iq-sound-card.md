# IQ through a sound card: Xiegu G90, SoftRock and the like

Some radios put out their receiver's baseband IQ as two audio signals, I and Q, meant for a stereo sound card: the Xiegu G90 for its panadapter, and every SoftRock-style receiver. pdn-soundmodem can take that IQ from the card's left and right inputs, pick your channel out of it, demodulate it itself, and decode. It is receive only.

What you get over an ordinary audio lead: the station's own filter and demodulator rather than the radio's, and the whole band the card carries (24 kHz either side of the centre at 48 kHz) to place the channel in.

## Wiring

**Not yet confirmed on a real radio.** The G90's IQ output, its connector, pinout and level are in the G90 user manual; check them there, and against the radio, before you build a lead. The headphone socket is not the IQ path: it sums to mono.

What the card needs to see, for any IQ source:

- I on the left input and Q on the right. If they are the other way round, the station hears everything mirrored about the centre (a USB signal as LSB, and on the wrong side); set `"swapIq": true` rather than rebuilding the lead.
- A stereo line input that is really stereo. Many USB dongles have a mono microphone input only, which is no use here. `arecord -L` lists the cards; the card must open with two channels at the rate you set.
- Both channels at the same gain, with AGC and any "mic boost" off. The station does not touch the card's mixer on this device; set it once with `alsamixer`.

## Configuration

```json
{
  "device": "iqcard:plughw:CARD=Device,DEV=0",
  "sideband": "usb",
  "dialFrequency": 10147300,
  "iqCard": {
    "centreFrequency": 10141300,
    "sampleRate": 48000
  },
  "modems": [ { "subChannel": 0, "mode": "bpsk300", "frequency": 1500 } ]
}
```

- `device` is `iqcard:` and the ALSA name of the card the IQ comes in on.
- The **channel** listens where the band plan says: give the modems an `rfFrequency`, or set `dialFrequency`, as on any other receiver that tunes itself ([08-hf.md](../08-hf.md)). `sideband` picks USB, LSB or FM.
- The **centre** is what RF frequency the IQ is centred on: what the radio's dial is set to, for a radio whose IQ is centred on its dial. Set `centreFrequency`, or add a `"rig"` section and leave it out: the centre is then read off the rig's dial over rigctld (plus `centreOffsetHz` for a radio whose IQ is offset from its dial), and followed when the rig moves.
- There is no AGC. IQ from a quiet band arrives well below the card's full scale, and some decoders hear a weak burst better at a normal level (1200 AFSK by about 2 dB in simulation), so if the station page's level meter reads low, raise `gain` until it reads where [04-levels.md](../04-levels.md) says.
- Keep the centre off the channel. A sound card has a DC offset and hum at the centre of its IQ, and the station says so at start-up if the centre falls inside the channel's audio. In the example the dial is 6 kHz above the centre, so the channel's audio, 150 to 3450 Hz above the dial, is well clear.

Every key is in [reference/config.md](../reference/config.md#iqcard).

## Balance

A sound card's two inputs are never quite matched, and any gain or phase difference between them leaves an image of every signal on the far side of the centre. A 0.5 dB and 3 degree mismatch leaves images only about 30 dB down, so a strong station on the mirror of your channel can land on top of it.

About a minute after start-up the journal says what the station measures:

```
iq: the card's balance over 60 s: Q is +0.42 dB against I and +1.30 degrees off quadrature, an image 33 dB down uncorrected. Measured on whatever the band holds, so a guide; a tone's image is the test.
```

Put those figures in `gainImbalanceDb` and `phaseImbalanceDegrees`, restart, and the next line says what is left. The measurement assumes the band is noise and unrelated signals, so treat it as a starting point. To check it properly, put a steady carrier on the mirror of your channel (as far below the centre as the channel's audio is above it) and watch how loud it comes through in the channel on the station page: without the correction it is about 30 dB down, and with a good one it is gone into the noise.

## What it cannot do yet

- **Transmit.** The station only receives on an `iqcard:` device. A G90 that receives on IQ and transmits through its audio path needs the receive and transmit sides split, which is not built yet.
- **Windows.** The device is ALSA only.
- **24-bit cards.** The card is read at 16 bits.

## If it did not work

- `cannot open the sound device`: the name is wrong, the card is in use, or it will not open two channels at that rate. Try `arecord -D plughw:CARD=Device,DEV=0 -c 2 -r 48000 -f S16_LE -d 5 test.wav` and play the file back.
- Nothing decodes, signals you expect are missing from the station page and others turn up that belong the far side of the centre: I and Q are the wrong way round. Set `"swapIq": true`.
- `needs to know what RF frequency the card's IQ is centred on`: set `centreFrequency`, or add a `rig` section.
- `cannot hear the dial`: the dial is further from the centre than the card carries. Move the centre (the radio's dial) nearer the channel.
