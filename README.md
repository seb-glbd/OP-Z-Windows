# OP-B

A native Windows companion app for the Teenage Engineering OP-Z, modelled on the official
Android/iOS app: same layout, colours and screens, built for a touchscreen laptop.
Unofficial; not affiliated with Teenage Engineering.

## Requirements

- Windows 10 or 11 (x64). A touchscreen is optional; mouse and wheel work everywhere.
- An OP-Z connected with USB-C, or over Bluetooth like the mobile apps (needs a Bluetooth LE
  adapter, which most laptops have). Neither needs a driver.
- Tested with OP-Z firmware 1.2.17 and 1.2.45. The configurator needs 1.2.45 or newer (see [Firmware](#firmware)).

## Download

Get `OP-B.exe` from the [latest release](https://github.com/seb-glbd/OP-Z-Windows/releases/latest).
It's a single self-contained file: no installer and no .NET install needed. Plug in the OP-Z, or
connect it over [Bluetooth](#bluetooth), and run it.

- **Versions:** 0.2.0 adds Bluetooth. 0.1.0 is USB only.

- **Windows SmartScreen:** the exe isn't code-signed, so on first launch Windows may show
  "Windows protected your PC". Click **More info → Run anyway**.
- **Checksum:** each release includes `OP-B.exe.sha256`. To check your download, run
  `Get-FileHash OP-B.exe -Algorithm SHA256` in PowerShell and compare the two values.

## Build and run

To build it yourself instead, install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then:

```
dotnet publish src\OPB.App -c Release -o publish
```

This produces `publish\OP-B.exe`, a single self-contained file: copy it anywhere and run it, no
.NET install needed on that machine. OP-B finds the OP-Z's MIDI port by itself and reconnects
if you unplug it.

- Close other apps that use the OP-Z MIDI port (DAWs, TE's web tools) first.
- **F11** toggles fullscreen. **M** or the rectangle at the bottom right opens the menu.
  **Esc** closes the menu, returns to the main screen, or leaves fullscreen.
- Tap the battery to open or close **Devices**; tap the play/stop icon to start or stop the sequencer.
- After quitting, the OP-Z holds the old connection for about 5 seconds, so a quick relaunch
  connects a few seconds later.

### Bluetooth

1. Open **Devices** (tap the battery) and press the button on the back of the OP-Z. Its light
   blinks blue and it shows up under **Bluetooth**.
2. Tap it. OP-B connects, pairing with Windows first if the OP-Z asks for it, and the light turns green.

OP-B remembers that OP-Z and reconnects to it by itself whenever no USB cable is plugged in.
USB wins when both are there. If you plug in a cable while on Bluetooth, tap **Reconnect**
to switch. **Forget Bluetooth** stops using it.

If the OP-Z was paired with Windows and later with another device (such as a phone running the
official app), it forgets the Windows pairing and refuses to connect. OP-B says so on the Devices
page: remove the OP-Z in Windows Settings > Bluetooth & devices, press the button on its back and
tap it again.

## Screens

The main **Screen** page follows the OP-Z like the official app does:

| When | Screen |
| --- | --- |
| Drum or synth track (1–8) | Sound, envelope, LFO (arp settings on the arp track), FX sends, pan, level, note length/style, quantize, portamento, and the 16-step sequencer with playhead, parameter locks and step components. |
| FX 1 / FX 2 | The same layout with the parts an effect doesn't have hatched out. |
| Tape | Spinning reels and waveform, following the tape speed. |
| Master | Chorus, drive and the HP/LP filter with its response curve; the transposition the master notes play; which tracks follow it (tap to toggle); note style and playback speed. |
| Perform | Two octaves of the 12 punch-in effects, drums on the left and synths on the right. Touch to play them live (several fingers, slide between keys). |
| Module | The 16 values with their outgoing CC numbers, the connected expansion module (oplab, line, rumble), the input selection and microphone state. |
| Lights | A 16-fixture preview, simulated from color, alt color, speed, intensity and pattern (the OP-Z doesn't report real fixture colors); tap the 10 patterns, hold the 14 animations; knobs 5–8. |
| Motion | Hue/saturation/brightness/contrast and crossfade/zoom/double/woozy with a color preview, the 24 slot keys, shortcuts to Photomatic and Motion. |
| Mixer / Tempo / Project button held | Mixer (multi-touch faders, mutes, group levels), metronome with tempo/swing/metronome settings, or the project's patterns and chains. |
| Shift + a step held | The step-component and parameter-lock wiring view for that step. |

Touch editing: tap a parameter (red frame), then drag on the hatched touch pad on the right or
use the mouse wheel. Tap a step dot to add or remove a trig.

Other pages, from the menu:

| Page | What it does |
| --- | --- |
| Configurator | Assign plugs (sample packs, synth engines, effects) to the 10 slots of each track; commit or revert. Needs firmware 1.2.45+. The OP-Z takes about 6 seconds between file transfers, so expect a short wait on commit. |
| Photomatic | 10 rolls × 24 image/video slots, played from track 16, with the colour dials and white-key effects. Media lives in `Documents\OP-B\photomatic`. |
| Motion | Live visuals driven by every track, from built-in visual paks (the official Unity videopaks only run in the official app). |
| MIDI setup | Per-track enable and channel, all 256 outgoing CC assignments, the 8 global MIDI flags. |
| Devices | MIDI ports, OP-Zs nearby over Bluetooth, connection, serial, firmware, battery, expansion module, reconnect. |
| Video out | Tap either side of the black bar on Photomatic or Motion: the visual opens fullscreen on a second display, or over the window if there is none. |

## Firmware

Everything except the configurator works on firmware 1.2.17. The configurator uses the OP-Z's
file service, which needs firmware 1.2.45 or newer; update from teenage.engineering/downloads/op-z.

Sound names on the track screen ("SEBA KICKS", "ORGAN", "DELAY") come from the OP-Z's plug
catalog, which the configurator reads and caches in `%APPDATA%\OP-B`. Open the configurator once
after connecting; until then the track screen shows `PLUG <id>`.

## Hardware status

Verified on an OP-Z with readback:

- Sound parameters ($0E), track select ($03), tempo ($0C), step count (CC 60).
- Pattern edits: full bank upload ($09/$0A with per-packet ACKs), restored byte for byte.
- Live punch-ins from the Perform screen: notes 101–112 (drums) and 113–124 (synths) on the
  perform track's channel, captured from the device.
- File service (1.2.45+): reading the plug catalog and slot map, and configurator commits
  (upload and readback in one session).
- Bluetooth: pairing and connecting from the Devices page, and the StateSync session over BLE MIDI.

Not yet verified on hardware:

- Whether the file service (configurator) works over Bluetooth at a usable speed.
- Mixer mutes and MIDI setup saves.
- Photomatic with a live camera or video files; video out on a second display.
- Master routing toggles and the Lights pattern/animation triggers.
- Some value labels whose byte mapping comes from community docs: note length, swing,
  metronome sound.

## How it works

The official app talks to the OP-Z with manufacturer SysEx over MIDI ("StateSync"), over USB or
Bluetooth LE MIDI. OP-B implements it from public reverse-engineering notes:

- `src/OPB.Core`: WinMM MIDI transport, a BLE-MIDI transport straight to the OP-Z's GATT
  characteristic (Windows Bluetooth LE APIs, no MIDI driver), 7-bit packing, zlib, the StateSync
  session (heartbeat, state mirroring, writes), pattern bank / global / MIDI-config models, file service.
- `src/OPB.App`: WPF app; every screen draws into a 1600×812 design space scaled to the window.
- `tools/OPB.Probe`: console diagnostics. `OPB.Probe 6` prints the live state for 6 seconds;
  `--short` logs incoming MIDI notes and CCs, `--dump <dir>` saves the pattern bank, global and
  MIDI config, `--write-test` runs reversible write checks with readback, and `--file-test` checks
  file-service timing without changing anything. Add `--ble` to connect over Bluetooth instead
  (to the first OP-Z found advertising, or `--ble <address>`); allow ~15 seconds, e.g. `OPB.Probe 15 --ble`.
  `--ble-scan` lists paired LE devices and nearby advertisements; `--ble-connect <address>` makes
  one GATT connection attempt with timings. Neither writes anything.
- `tests/`: protocol and model tests with fixtures captured from a real OP-Z.

## Develop

```
dotnet build
dotnet test tests\OPB.Core.Tests
```

Run the app without an OP-Z, from captured fixtures:

```
src\OPB.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\OP-B.exe --offline tests\fixtures --track 4
```

Offline options: `--track N` (0–15), `--page N` (0–3), `--ui mixer|tempo|project|shift`,
`--step N` (with `shift`), `--playing`. Add `--snapshot out.png <Page> 1600x900` to render a page
to a PNG and exit.

## Credits

Protocol knowledge from [kmorrill/op-z-sysex](https://github.com/kmorrill/op-z-sysex) (MIT),
[MarkRdgOx/opzdoc](https://github.com/MarkRdgOx/opzdoc),
[leddy231/libopz](https://github.com/leddy231/libopz) and
[lrk/z-po-project](https://github.com/lrk/z-po-project); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
OP-Z is a trademark of Teenage Engineering.
