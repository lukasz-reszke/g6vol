# g6vol

Make the system volume control the **optical (S/PDIF) output** of a Sound BlasterX G6.

The G6 exposes a hardware volume control, so the slider and volume keys work — but that control only
affects the G6's analog outputs. The optical output gets the untouched digital stream, so speakers
connected over optical always play at full level.

g6vol mirrors the G6's volume (in dB) and mute onto the audio digitally, before it reaches the G6. The
slider, volume keys and the G6's knob keep driving its hardware volume; at 100% the signal is
unchanged. Other output devices are not touched.

| Platform | How | |
|---|---|---|
| macOS 14.2+ | A Core Audio process tap captures the audio sent to the G6 and re-plays it scaled | [macos/](macos/README.md) |
| Windows 10/11 | Scales the per-app volume (session volume) of every app playing to the G6 | [windows/](windows/README.md) |

Both match the device by name and can target another device with the same problem via the
`G6VOL_DEVICE` environment variable.
