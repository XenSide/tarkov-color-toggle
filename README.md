# Tarkov Color Toggle

Automatically applies an ICC display calibration profile while Escape From Tarkov is
running, and reverts to your normal display settings the moment it closes.

No polling — it uses a permanent WMI event subscription that reacts instantly to
`EscapeFromTarkov.exe` starting or stopping.

## How it works

- Applies `profile.icc` (the file sitting next to the exe) directly to your primary
  monitor's GPU gamma ramp when Tarkov starts, and resets to a linear/default ramp
  when it closes. This is the same low-level mechanism (`SetDeviceGammaRamp`) that
  color-calibration tools use, so it works even in exclusive fullscreen.
- Compiled as a GUI-subsystem executable, so it never flashes a console window when
  it runs in the background.
- `-Install` registers two on-demand Scheduled Tasks (`TarkovColorOn` /
  `TarkovColorOff`) plus a permanent WMI event filter watching for
  `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` on `EscapeFromTarkov.exe`.
  When the game starts or stops, WMI triggers the matching task, which runs this
  same exe with `-On` or `-Off`.

Source is included (`TarkovColorToggle.cs`) — since this installs a background
automation with a UAC prompt, you should be able to see exactly what it does before
running it. It's plain C#, compiles with the `csc.exe` that ships with every Windows
install (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), no external
dependencies.

## Usage

1. Put `TarkovColorToggle.exe` and your own `profile.icc` in the same folder.
2. Double-click the exe. It'll ask to install (triggers one UAC prompt).
3. Play. Colors switch automatically when Tarkov opens/closes.

To remove everything: double-click the exe again — it detects the existing
install and offers to uninstall.

Uninstalling can also be done directly:

```
TarkovColorToggle.exe -Uninstall
```

## Notes

- Targets your **primary** monitor.
- Looks for `profile.icc` next to the exe; if that exact name isn't found, it falls
  back to the first `*.icc` file in the folder.
- Logs every toggle to `toggle.log` next to the exe, for troubleshooting.
- The very first trigger after installing can take up to ~20 seconds (WMI's event
  provider warming up); after that it responds in under a second.
