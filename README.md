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
| Audio EQ | Optional. Swaps an [Equalizer APO](https://equalizerapo.com/) preset from the `audio` folder along with the display settings |

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

## Audio EQ (optional)

If [Equalizer APO](https://equalizerapo.com/) is installed, each profile can also carry
an EQ preset, so sound and display switch together when the game starts. Without it the
field is simply disabled and everything else works normally.

Setup takes care of the pieces it can. If Equalizer APO is missing it points you at the
download (its own installer asks which output device to attach to, and needs a reboot).
If a preset uses the LoudMax limiter and that plugin is missing, setup offers to fetch it.

Neither is bundled here, deliberately: Equalizer APO is GPLv2, and LoudMax is freeware
whose author does not permit redistribution. Both are downloaded from their own official
sites, so they come from their authors rather than from this project.

Presets are plain Equalizer APO config files in the `audio` folder next to the
executable. The app copies the selected one into APO's own config folder and adds an
`Include:` line pointing at it. Two details that are easy to get wrong:

- The active file **must** live inside APO's config folder. APO loads a file given by an
  absolute path elsewhere, but does not watch it, so later edits are silently ignored.
- That folder is user-writable by design, so switching presets never needs admin rights.
- APO's Editor rewrites `config.txt` wholesale when it saves, dropping the `Include:`
  line and silently freezing the EQ. The tray watches `config.txt` and puts the line back
  within about a second, via `ReadDirectoryChangesW` rather than any polling. Even so,
  edit preset files directly rather than opening `config.txt` in the Editor.
- Plugin parameters are stored as readable text on the `VSTPlugin:` line, so a preset
  using LoudMax can be shared as-is; the recipient only needs the plugin installed.

The shipped Tarkov presets cut rather than boost, and use no compressor or limiter. The
reasoning: a limiter is usually added to cap what a boost raises, and that combination
raises average loudness, which is what makes long sessions tiring. Turning the loud
things down and then raising the volume lifts quiet cues without that cost. Cutting low
frequencies also uncovers the midrange rather than just quietening things, because low
frequencies mask higher ones far more than the reverse.

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
executable, plus an `audio` folder holding the EQ presets.

Frequency choices in the Tarkov presets follow a published spectrum analysis of the
game's audio by [Stereo Bites](https://www.youtube.com/watch?v=Y-qZJ2g1oK4). The gain
staging deliberately differs, as described above.
