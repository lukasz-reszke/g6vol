# g6vol for Windows

Make the Windows volume slider control the **optical (S/PDIF) output** of a Sound BlasterX G6.

Same problem as on [macOS](../macos/README.md): the G6 exposes a hardware volume control, so Windows moves the G6's own
attenuator instead of scaling the audio, and that attenuator only affects the analog outputs. The
optical output gets the untouched digital stream, so speakers connected over optical always play at
full level.

Windows has no muting process tap, but every app's audio passes through its session volume (the
per-app sliders in the volume mixer), which Windows applies in software before the audio reaches the
device. g6vol mirrors the G6's volume (in dB) and mute into the session volume of every app playing to
the G6 — when the volume changes and as soon as an app starts playing.

The slider, volume keys and the G6's knob keep driving its hardware volume; g6vol just mirrors it. At
100% the signal is unchanged. Other output devices are not touched.

Per-app levels keep working: an app set to half in the volume mixer plays at half of the master
volume. An app can't go above the master (its slider snaps back).

## Requirements

- Windows 10/11
- Nothing else: it builds with the C# compiler that ships with Windows (.NET Framework 4.x)

## Install

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This copies `g6vol.exe` to `%LOCALAPPDATA%\Programs\g6vol`, starts it, and registers it to start at
logon (`HKCU\...\CurrentVersion\Run`). No admin rights needed. It runs in the background, without a
window or tray icon.

Log: `%LOCALAPPDATA%\g6vol\g6vol.log`

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## Other devices

The device is matched by name (default `Sound BlasterX G6`, matched anywhere in the endpoint name, e.g.
`Speakers (2- Sound BlasterX G6)`). Set `G6VOL_DEVICE` to target another device with the same problem:

```powershell
setx G6VOL_DEVICE "My USB DAC"
```

Then run `install.ps1` again (or log off and on).

## Notes

- The volume mixer shows the scaled values: with the master at a gain of 0.5, apps show 50.
- If headphones are plugged into the G6, volume is applied twice (hardware + digital), so the slider
  feels steeper there.
- Exclusive-mode audio (some games, players in WASAPI exclusive/ASIO mode, bitstreamed Dolby/DTS)
  bypasses session volume and plays at full level on optical.
- Only the overall volume is mirrored, not the L/R balance.
- Windows remembers each app's volume, including the scaled one. g6vol keeps track of what it scaled
  (`%LOCALAPPDATA%\g6vol\sessions.tsv`) so it isn't mistaken for the app's own level later. Stopping
  g6vol gives running apps their own level back; apps that aren't running at uninstall stay quieter
  until reset in Settings → System → Sound → Volume mixer → Reset (`uninstall.ps1` lists them).
- If g6vol crashes, apps stay at their last volume (no jump to full level), but stop following the
  slider until the next logon.
- `g6vol.exe --debug` logs per-app changes; `g6vol.exe --stop` stops the running instance.
