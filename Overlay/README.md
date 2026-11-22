# NetSummaryBar

A lightweight, modern real-time network traffic monitor and histogram designed for Windows (compatible with Windows 10 & 11). Inspired by [crener/NetworkSummaryToolbar](https://github.com/crener/NetworkSummaryToolbar), but built as a standalone taskbar-docked widget with zero administrator privileges required.

---

## ✨ Features

- **Histogram + Numerical Rates**:
  - Live rolling histogram showing network activity over time.
  - Crisp numeric speed display for both Download (▼) and Upload (▲).
- **Customized Speed Units**:
  - `KB/s` (Kbyte/s)
  - `MB/s` (Mbyte/s)
  - `Mbps` (Megabits per second)
  - `Auto` (switches between B/s, KB/s, MB/s dynamically)
  - Configurable decimal precision (0 to 3 decimal places).
- **Customized Colors & Transparency**:
  - **Transparent Background**: Shows the native Windows taskbar color, texture, and blur underneath.
  - **Full Clickability**: Right-clicking or dragging anywhere on the transparent window works seamlessly.
  - **Histogram Border Color**: Choose any custom color for the histogram border box and center baseline (or toggle border on/off).
  - Custom color for Download rate & graph (Default: Cyan `#00E5FF`).
  - Custom color for Upload rate & graph (Default: Orange `#FF9100`).
  - Optional custom background color with opacity adjustment (10% to 100%) when not in transparent mode.
- **Spacing / Layout Customization**:
  - Configurable distance/spacing (in pixels) between the text block and the histogram graph.
  - Choose between Text Left / Graph Right or Graph Left / Text Right.
- **Settings Dialog**:
  - **Apply**: Updates the live widget on your taskbar immediately without closing the settings window.
  - **Save**: Applies all settings, saves to disk, and closes the window.
- **Dedicated Application Icon**:
  - Custom high-resolution network activity icon embedded into the executable, taskbar, and system tray.
- **Histogram Visualization Modes**:
  - **Mirror Mode** (*Classic crener style*): Baseline in the middle; upload rises upwards, download drops downwards.
  - **Stacked Mode**: Top half for upload, bottom half for download.
  - **Overlay Mode**: Full-height overlapping graph with alpha blending.
- **Taskbar Docking**:
  - Automatically docks directly adjacent to the Windows taskbar notification tray.
  - Can be unlocked and dragged anywhere on the screen or taskbar.
- **Adapter Selection**:
  - **Auto (default)**: follows the interface that currently carries your internet route, so VPN / tunnel / virtual-switch traffic is never double counted.
  - **All physical adapters** (summed), or pick one specific Wi-Fi / Ethernet adapter.
  - Counters are tracked per adapter: connecting a VPN or re-plugging a cable can no longer produce a fake speed spike.
- **Multi-monitor & DPI**: dock to the taskbar of any monitor; crisp rendering at 125 / 150 / 200 % scaling.
- **Rock-solid z-order**: owned by the taskbar, so Show Desktop and taskbar clicks don't hide it.
- **Fullscreen aware**: the bar hides over fullscreen apps/games and while an auto-hide taskbar is hidden.

---

## 🚀 Quick Start

1. **Run the App**:
   - Double-click `NetSummaryBar.exe`.
   - The bar will appear docked next to your system tray icons on your taskbar.
2. **Right-Click Menu**:
   - Right-click **anywhere** on the bar (or on its icon in the system tray) to access:
     - ⚙ **Settings...**: Open full customization dialog.
     - **Lock Position**: Prevent accidental dragging.
     - **Dock to Taskbar**: Re-align to taskbar tray immediately.
     - **Transparent Background**: Quick toggle for transparent mode.
     - **Speed Units**: Switch instantly between Auto, KB/s, MB/s, and Mbps.
     - **Graph Style**: Switch between Mirror, Stacked, and Overlay.
     - **Exit**: Close the application.

---

## 🛠 Build

Run `build.bat` (uses the .NET Framework C# compiler that ships with Windows; no SDK needed). It embeds `app.ico` and `app.manifest` (DPI awareness).

## 🔧 Technical notes

- **No global mouse hook.** The bar is a per-pixel-alpha layered window. Its empty area is painted at alpha = 1 (invisible, but hit-testable), so right-click, hover and drag work over the whole bar through normal window messages.
- Drawing goes to a persistent DIB section and is pushed only when the picture changes (idle = no repaint).
- The adapter list is cached and refreshed on network-change events; only counters are read each tick.
- Start-with-Windows uses `HKCU\...\Run` (an old Startup `.lnk` is migrated automatically).
- Errors are written to `%APPDATA%\NetSummaryBar\error.log` instead of popping dialogs.
- Settings are written atomically (`settings.json.tmp` -> replace).

## 🖥 Staying put when you click the taskbar / Show Desktop

Show Desktop (and any click on the taskbar) makes Explorer raise the taskbar window. A plain topmost window sitting inside the taskbar area then gets painted over until something puts it back, which showed up as the bar vanishing for a second.

The bar is now an **owned window of the taskbar** (`GWLP_HWNDPARENT`). Windows always keeps an owned window above its owner, so the taskbar can no longer be raised over it. This is the same approach used by [NetSpeedTray](https://github.com/erez-c137/NetSpeedTray), and it is a structural guarantee rather than a timer.

How the details are handled:

- **Owner link:** verified every 2 s with a single compare; z-order is only touched when the link is (re)established, because re-asserting `HWND_TOPMOST` on an owned window would drag the taskbar above open shell menus.
- **Explorer restart / monitor unplug:** Windows destroys a taskbar's owned windows with it. The app is therefore not tied to the bar window: a small controller rebuilds a fresh bar and re-docks it to the new taskbar.
- **Safety net, event driven:** if the taskbar is ever above the bar anyway, it is re-seated *directly above the taskbar* without moving it, taking focus, or covering menus. Triggers are the shell's own reorder/foreground notifications (WinEvent hooks, **not** input hooks), coalesced into one check, plus a 1 s tick. Event-driven re-seats are rate-limited.
- Excluded from Aero Peek (`DWMWA_EXCLUDED_FROM_PEEK`); cannot be minimized; re-shows itself if another process hides it; never treats the desktop as a "fullscreen app".
- Free-floating mode (auto-dock off) has no owner and uses a plain topmost window.

### Diagnostics

Start with `NetSummaryBar.exe --debug` (or create an empty `%APPDATA%\NetSummaryBar\debug.flag`), reproduce, then read `%APPDATA%\NetSummaryBar\error.log`. Millisecond-stamped lines show owner changes, foreground changes, re-seats, external show/hide/cloak events and bar rebuilds.

## Show Desktop hardening
- The fullscreen policy now ignores any foreground window owned by explorer.exe (or no foreground window), so the
  full-screen desktop shown by Show Desktop can never hide the bar.
- Re-seat falls back (walk up the z-order, then HWND_TOPMOST) if Windows refuses the insert, and logs the error.
- Switching to/from the desktop or taskbar is exempt from the re-seat rate cap and gets two bounded follow-up checks (120 ms apart, event-driven, no polling).
- Still flickering? Run `NetSummaryBar.exe --debug`, do Show Desktop once, and send `%APPDATA%\NetSummaryBar\error.log`.

## Tray icon
- Single left click: show / hide the bar (also in the tray menu as "Hide Bar" / "Show Bar"). Not remembered across restarts.
- Double click: open Settings.
