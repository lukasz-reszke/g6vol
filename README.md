# g6vol

Make the macOS volume slider control the **optical (S/PDIF) output** of a Sound BlasterX G6.

The G6 exposes a hardware volume control to macOS, so the slider and volume keys work — but that
control only affects the G6's analog outputs. The optical output gets the untouched digital stream,
so speakers connected over optical always play at full level.

g6vol fixes this by applying the same volume digitally before the audio reaches the G6:

1. A Core Audio process tap captures (and mutes) everything other apps send to the G6.
2. A private aggregate device (G6 + tap) re-plays that audio to the G6, scaled by the G6's own
   volume (in dB), mute and L/R balance.

The slider keeps driving the G6's hardware volume; g6vol just mirrors it. At 100% the signal is
unchanged. Other output devices are not touched.

## Requirements

- macOS 14.2+ (process taps)
- Xcode Command Line Tools (`xcode-select --install`)

## Install

```sh
./build.sh
./install.sh
```

This copies `g6vol.app` to `~/Applications` and registers a LaunchAgent (`local.g6vol`) that starts
it at login and restarts it if it crashes. On first run macOS asks for **System Audio Recording**
permission — allow it. Rebuilding changes the ad-hoc signature, so macOS will ask again.

Log: `~/Library/Logs/g6vol.log`

## Uninstall

```sh
./uninstall.sh
```

## Other devices

The device is matched by name (default `Sound BlasterX G6`). Set `G6VOL_DEVICE` to target another
device with the same problem, e.g. in the LaunchAgent plist:

```xml
<key>EnvironmentVariables</key>
<dict>
	<key>G6VOL_DEVICE</key>
	<string>My USB DAC</string>
</dict>
```

## Notes

- If headphones are plugged into the G6, volume is applied twice (hardware + digital), so the
  slider feels steeper there.
- macOS may show the system-audio-recording indicator while g6vol runs.
- If g6vol crashes, audio plays at full level until launchd restarts it (up to ~10 s).
- `g6vol --debug` logs per-buffer peak levels once a second.
