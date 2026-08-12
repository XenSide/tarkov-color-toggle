# Display Profile Switcher

Switches your monitor's colour settings automatically when a game launches, and puts
them back when it closes. Global hotkeys let you switch profiles by hand at any time.

Originally built for Escape From Tarkov, but any application can be watched.

No polling and no dependencies: it reacts to Windows process events, and everything is
done with APIs that ship with Windows.

## What a profile can change

| Setting | How it works |
| --- | --- |
| Gamma, contrast, brightness | Written into the GPU's gamma ramp, so it applies even in exclusive fullscreen |
| Saturation | Desktop-wide colour matrix via the Magnification API |
| ICC base curve | Optional. The `vcgt` calibration curve of an `.icc` file is used as the starting point, then the sliders adjust it |

Saturation genuinely cannot be done with a gamma ramp: a gamma ramp is a per-channel 1D
lookup table, and saturation requires mixing channels. That is why it goes through a
separate mechanism, and why it needs the tray app to be running (the effect only lives
as long as the process that set it). Everything else keeps working without it.

## Usage

1. Put `TarkovColorToggle.exe` in a folder of its own, together with any `.icc`
   files you want to use as a base.
2. Run it. It asks once for administrator rights to register the watcher, then a tray
   icon appears.
3. Click the tray icon to switch profiles, or open **Settings** to create them.

In Settings you can create profiles, assign a global hotkey to each one, and list the
applications that should trigger them automatically.

To remove everything, run:

```
TarkovColorToggle.exe -Uninstall
```

## How it works

- A permanent WMI event subscription watches `Win32_ProcessStartTrace` and
  `Win32_ProcessStopTrace` for the executables you configured. These are kernel-pushed
  events - nothing is polled.
- When one fires, it runs a scheduled task which re-checks what is actually running and
  applies the first matching rule, or reverts to default if nothing matches. Because it
  re-checks rather than trusting the event, overlapping applications resolve correctly.
- The tray app owns the global hotkeys and the saturation effect, and runs unelevated so
  the (limited) scheduled task can talk to it.

Two details worth knowing, both of which cost real debugging time to find:

- `Win32_ProcessStopTrace` truncates `ProcessName` to 14 characters, so stop events are
  matched on a prefix rather than compared exactly.
- Windows silently ignores gamma ramps it considers too extreme - `SetDeviceGammaRamp`
  returns success but nothing changes. The installer sets `GdiIcmGammaRange` to widen the
  permitted range, and restores the previous value on uninstall.

## Limitations

- Gamma, contrast and brightness apply to the **primary monitor** only. Saturation is
  desktop-wide by design and cannot be limited to one screen.
- **HDR**: `SetDeviceGammaRamp` is documented as undefined behaviour in HDR mode, and
  Windows ignores the `vcgt` curve of an ICC profile there too. This tool is for SDR.
- Changing the watched application list needs administrator rights, because the WMI
  subscription lives outside the app. If you decline the prompt, the list is rolled back
  so what the UI shows and what the system watches never disagree.

## Building

Plain C#, no external dependencies. Compiles with the `csc.exe` that ships with Windows:

```
build.cmd
```

## Files

The app keeps `profiles.json` (your profiles and rules), `state.txt` (the active profile)
and `toggle.log` (a history of what was applied, for troubleshooting) next to the
executable.
