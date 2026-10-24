# OP-B

A native Windows companion app for the Teenage Engineering OP-Z, modelled on the official
Android/iOS app: same layout, colours and screens, built for a touchscreen laptop.
Unofficial; not affiliated with Teenage Engineering.

## Run

Plug the OP-Z in with USB-C and start `publish\OP-B.exe` (self-contained; no .NET install needed).
It finds the OP-Z MIDI port by itself and reconnects if you unplug it.

- **F11** fullscreen, **M** or the rectangle at the bottom right opens the menu.
- Tap the battery to open **Devices**; tap the play/stop icon to start/stop the sequencer.
- Close other apps that use the OP-Z MIDI port (DAWs, the TE web tools) first.
- After quitting, the OP-Z keeps the old session for about 5 seconds, so a quick relaunch
  connects a few seconds later.

## Screens

| Page | What it does |
| --- | --- |
| Screen | Follows the OP-Z like the official app: the track screen for the active track (sound, envelope, LFO/arp, sends, pan, level, note length/style, quantize, portamento, 16-step sequencer with playhead, locks and components); **Mixer**, **Tempo**, **Project** while those buttons are held; **Tape**, **Perform** (live punch-ins), **Master** (filter curve, transposition, routing), **Module** (expansion module and input), **Lights** (fixture preview, patterns and animations) and **Motion** (color preview, slot keys) for those tracks; the step-component wiring view while shift + a step is held. |
| Touch editing | Tap any parameter (red frame) and drag on the touch pad, or use the mouse wheel. Mixer faders work with several fingers. Tap a step dot to add/remove a trig. |
| Configurator | Assign plugs to the 10 slots per track, commit/revert. Needs OP-Z firmware 1.2.45+. |
| Photomatic | 10 rolls × 24 image/video slots, played from track 16, with the colour dials and white-key effects. Media lives in `Documents\OP-B\photomatic`. |
| Motion | Live visuals driven by every track. Built-in visual paks (the official Unity videopaks can't run outside the official app). |
| MIDI setup | Per-track enable/channel, all 256 outgoing CC assignments, the 8 global MIDI flags. |
| Devices | Connection, serial, firmware, battery, module, reconnect. |
| Video out | Tap either side of the black bar on Photomatic/Motion: the visual opens fullscreen on a second display, or over the window if there is none. |

## Firmware

Everything except the configurator works on the firmware this was tested with (1.2.17).
The configurator uses the OP-Z file service, which was added in later firmware; update from
teenage.engineering/downloads/op-z to use it. Sound names ("SEBA KICKS") also come from that
service; until then the track screen shows `PLUG <id>`.

## How it works

The official app talks to the OP-Z with manufacturer SysEx over USB MIDI ("StateSync"). OP-B
implements it from public reverse-engineering notes:

- `src/OPB.Core` – WinMM MIDI transport, 7-bit packing, zlib, the StateSync session
  (heartbeat, state mirroring, writes), pattern bank / global / MIDI-config models, file service.
- `src/OPB.App` – WPF app; every screen draws into a 1600×812 design space scaled to the window.
- `tools/OPB.Probe` – console diagnostics: `OPB.Probe 6` prints the live state,
  `--write-test` runs reversible write checks with readback.
- `tests/` – protocol and model tests with fixtures captured from a real OP-Z.

Writes verified on hardware with readback: sound parameters ($0E), track select ($03), tempo
($0C), step count (CC 60), pattern bank upload ($09/$0A with ACKs, exact restore), live punch-ins from the Perform screen
(perform channel notes 101–112 for drums, 113–124 for synths, captured from the device).
File service (firmware 1.2.45+): pulling plugs.json and the slot map, and configurator commits
(write + readback in one session; the OP-Z needs ~6 s between file sessions) are verified. Not yet
verified on hardware: mixer mutes, MIDI-config
commits, live camera/video in Photomatic, and some value labels (note length, swing,
metronome sound) whose byte mapping comes from community docs.

Develop with the .NET 8 SDK:

```
dotnet build
dotnet test tests\OPB.Core.Tests
dotnet publish src\OPB.App -c Release -o publish
OP-B.exe --offline tests\fixtures [--ui mixer|tempo|project|shift] [--track N]   # no hardware
```

## Credits

Protocol knowledge from [kmorrill/op-z-sysex](https://github.com/kmorrill/op-z-sysex) (MIT),
[MarkRdgOx/opzdoc](https://github.com/MarkRdgOx/opzdoc),
[leddy231/libopz](https://github.com/leddy231/libopz) and
[lrk/z-po-project](https://github.com/lrk/z-po-project); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). OP-Z is a trademark of Teenage Engineering.
