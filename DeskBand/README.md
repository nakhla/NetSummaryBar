# Net Summary Bar - DeskBand edition

Same histogram + speed text, same settings and look as the floating NetSummaryBar, but hosted by the taskbar
itself as a deskband (the technique used by crener/NetworkSummaryToolbar, built on CSDeskBand, MIT).
Explorer lays it out inside the taskbar, so there is no overlay window: no z-order fights, no Show Desktop or
taskbar-click flicker, no fullscreen logic, no drag handling.

## IMPORTANT: Windows version
Deskbands only exist on **Windows 7 / 8 / 10** (and Windows 11 only if the classic taskbar is restored with a
third-party tool). The **Windows 11 taskbar does not load deskbands**. On Windows 11 use the floating
NetSummaryBar_fixed build instead.

## Install (recommended: clean reinstall)
1. Extract this folder anywhere.
2. Double-click `reinstall.bat` (it asks for administrator rights). It closes Explorer, removes every old
   registration, copies the DLL into a fresh `C:\Program Files\NetSummaryBar\<date_time>` folder, registers it
   there and restarts Explorer. Use it for first installs, updates and repairs; it never fights a locked DLL.
3. Right-click the taskbar > Toolbars > tick **Net Summary Bar**. (The "Class not registered" message printed
   during registration is harmless; it only means Windows could not tick the toolbar automatically.)
4. Unlock the taskbar to drag the band; right-click it for "Net Summary Bar settings...".

`install.bat` / `uninstall.bat` are the simple alternatives. Uninstall: untick the toolbar, run `uninstall.bat`,
restart Explorer, delete the folders under `C:\Program Files\NetSummaryBar`.

## Notes
- Settings are shared with the floating app (`%APPDATA%\NetSummaryBar\settings.json`). Position/docking/lock options in
  the dialog do not apply to the deskband; "Bar width/height" sets the band's minimum size.
- "Start with Windows" is hidden: Explorer loads the band by itself.
- The band lives inside explorer.exe. Errors are logged to `%APPDATA%\NetSummaryBar\error.log`; the code catches
  everything in its timer and paint paths so it cannot take the taskbar down.
- Transparent background uses a per-pixel-alpha child window, so the taskbar acrylic shows through. If Windows
  refuses it the band falls back to classic transparent painting (log: `layered mode refused`).
- DLL was compiled with Mono's mcs (IL for .NET Framework 4.x) because no Windows toolchain was available;
  `build.bat` rebuilds it with Roslyn. I could not run it on Windows here, so first test on a non-critical machine.
- CSDeskBand: Copyright (c) 2017 Brandon Chong, MIT (see CSDeskBand.LICENSE.txt).
