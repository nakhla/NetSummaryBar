# NetSummaryBar
![App Screenshot](https://i.imgur.com/nHpi2rt.png)

A tiny Windows taskbar network monitor: live download / upload speed plus a histogram of recent activity.
Two editions share the same look and settings:

| Edition | Folder | Windows | How it works |
|---|---|---|---|
| **Overlay** (ver. 2.0) | [`Overlay`](Overlay) | 10 / 11 | A borderless per-pixel-alpha window docked next to the tray, kept above the taskbar. No mouse hook. |
| **Deskband** (ver. 2.0) | [`DeskBand`](DeskBand) | 10 (and 7/8) | Hosted by the taskbar itself as a deskband (built on [CSDeskBand](https://github.com/dsafa/CSDeskBand)). **Not supported by the Windows 11 taskbar.** |

## Features
- Download/upload speed text with auto, KB/s, MB/s, Mbps or Kbps units
- Mirror / Stacked / Overlay histogram styles, dynamic or fixed scale
- Transparent or custom background, custom colors, per-adapter or auto adapter selection
- Hover tooltip with speeds, peaks and session totals (optional)
- Overlay: tray icon (click toggles the bar, double click opens settings), fullscreen auto-hide, multi-monitor docking

![App Screenshot](https://i.imgur.com/LxBU0e2.png)

## Build
- Overlay: run `Overlay\build.bat` (uses the .NET Framework compiler that ships with Windows).
- Deskband: C# 6+ is required, run `DeskBand\build.bat` with Visual Studio / Build Tools installed, then `DeskBand\reinstall.bat` as administrator.
- Prebuilt binaries: see the Releases page.

## Author
Magdi Nakhla - https://github.com/nakhla

## License
MIT, see [LICENSE](LICENSE). The deskband includes [CSDeskBand](DeskBand/CSDeskBand.LICENSE.txt) (MIT).
