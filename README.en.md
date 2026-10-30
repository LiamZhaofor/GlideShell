# GlideShell

[简体中文](README.md) · **English**

> Touchpad gestures on Windows that truly follow your fingers: the screen moves exactly as far as your fingers do. Stop and it stops, reverse and it rewinds, and nothing is decided until you lift.

**Website and user guide** (Chinese): <https://liamzhaofor.github.io/GlideShell/> · [Download the latest release](https://github.com/LiamZhaofor/GlideShell/releases/latest)

> The app's interface (tray menu, settings, notifications) is currently in Simplified Chinese only.

Most native Windows multi-finger gestures work as "recognize a gesture → play a fixed animation". GlideShell reads raw contacts from the precision touchpad (PTP) directly and rebuilds multitasking on its own composition layer, modeled on macOS Mission Control:

- **Three-finger swipe up · Window overview**: every window flies from where it is into the overview layout, with progress driven entirely by finger travel. Pause and the picture holds halfway; swipe back and it rewinds.
- **Three-finger swipe down · App windows view**: tiles only the current app's windows, with minimized ones in a separate row at the bottom, while other windows fade out in place. It follows your fingers the whole way, and a swipe up dismisses it (like macOS App Exposé). In the overview, clicking the title under a window switches to that app's view, and clicking again goes back to the overview.
- **Four-finger swipe down · Show desktop**: windows slide off the screen edges with your fingers to reveal the desktop; a four-finger swipe up pulls them back. When the desktop isn't showing, a four-finger swipe up opens the overview (as on macOS).
- **Desktop strip in the overview**: the top of the overview lists all virtual desktops. Hover over it and it expands into live thumbnails; click another desktop to switch right away and zoom from its thumbnail into that desktop. The "+" at the end adds a desktop and the "×" next to a name removes one. Drag a window onto a desktop to move it there, or onto "+" to create a new desktop and move it.
- **Four-finger horizontal swipe inside the overview**: without leaving the overview, the window area slides sideways with your fingers to the neighboring desktop's page, with its windows already laid out.
- **Three-finger horizontal swipe · Switch windows**: a strip of windows pans with your fingers and, on release, snaps to the nearest window and brings it to the front. Alt+Tab uses the same switcher.
- **Four-finger horizontal swipe · Switch virtual desktops**: the neighboring desktop slides in with your fingers, with no dead zone at the start. The first and last desktops rubber-band, there is no flicker when the switch finishes, and window shadows match the system's.
- **macOS-style physics**: on release, distance and velocity decide whether to complete or cancel, and the release velocity feeds straight into a spring animation. A new gesture can catch any animation midway.
- **Rounded cards**: as windows fly into the overview, their corners morph from the window's own radius into macOS-style rounded corners (maximized windows too), and the radius shrinks proportionally with the window, just like Mission Control. The switcher matches, and the radius is adjustable.
- **Text and decorations in the composition layer**: each window shows its app icon and title underneath. The hover frame, close button and desktop names move and fade frame by frame with the picture, and never vanish and pop back during paging, reflow or dragging. The three-finger switcher and the overview share one style.
- **Details that match the system**: thumbnails in the overview and during desktop switches carry window shadows calibrated against the real thing (verified identical at 125%, 150% and 175%), and when the overlay is removed the picture hands off seamlessly to the real desktop.
- **Multiple monitors**: each screen lays out on its own. A window spanning two screens is drawn on both, so nothing is missing while it expands or collapses.
- **Mouse gestures that follow too**: hold a mouse side button (Back by default; Forward or Middle also work) and drag. Left/right switches desktops, up opens the overview, down opens the app windows view, all tracking the pointer like the touchpad does (the Mac Mouse Fix approach). A plain click is still Back.
- **Plenty of ways in**: Ctrl+Win+←/→ also uses the finger-following desktop-switch animation, and each of the four screen corners can be a hot corner (overview, app windows, show desktop).
- **Customizable**: gesture mapping, sensitivity, excluded apps, three-finger drag, and grouping the overview by app.
  - Three-finger tap acts as a middle click. GlideShell recognizes it itself; while enabled, the system's three-finger tap is paused so you never get two clicks. You can also turn it off and hand it back to Windows.
- **Lightweight and reversible**:
  - No drivers: a native program of about 5.6 MB. Private memory is about 16 MB right after start, and about 20–30 MB after using the overview and settings.
  - On exit, pause or crash, it restores the system gestures to your original settings automatically.

## Requirements

- Windows 11 (developed and tested on 24H2 / build 26300)
- A precision touchpad (Settings → Bluetooth & devices → Touchpad says "Your PC has a precision touchpad")

## Install

Download `GlideShell-2.0.0-win-x64.zip` from [Releases](https://github.com/LiamZhaofor/GlideShell/releases), extract it, and double-click **安装.cmd** ("Install").

- The app is installed to `%LOCALAPPDATA%\Programs\GlideShell`, with a Start menu entry and startup at sign-in. No administrator rights are needed.
- To uninstall, double-click **卸载.cmd** ("Uninstall"). It exits the app first, confirms the system gestures have been restored, and then removes the app. Settings and logs are kept; to delete them too, run `uninstall.ps1 -Purge`.
- The app is not code-signed, so Windows may warn that it "can't verify the publisher" the first time it runs.

Upgrading:
- When a new version is out, the tray shows a notification. Click "升级" (Upgrade) and GlideShell downloads it, verifies the SHA-256, installs it, and restarts, keeping your settings. `GlideShell.exe --upgrade` does the same.
- Reinstalling by hand also works: a running GlideShell exits cleanly before it is replaced.

## Usage

Once running, GlideShell sits in the notification area of the taskbar. It only takes over the system gestures it implements; every other gesture is still handled by Windows.

| Gesture | Default | With "three-finger drag" on |
|---|---|---|
| Three-finger up / down | Up: overview; down: app windows view (expands with your fingers, collapses when reversed) | Drag |
| Three-finger left / right | Switch windows | Drag |
| Four-finger up / down | Down: show desktop; up: bring windows back (opens the overview if the desktop isn't showing) | Overview |
| Four-finger left / right | Switch virtual desktops | Switch virtual desktops |
| Three-finger tap | Middle click | Middle click |

**In the overview**
- **Opening and closing**
  - Click a window, or swipe three fingers down or press Enter, to switch to the selected window. Esc closes the overview.
  - Ctrl+Alt+O also opens the overview.
  - Clicking the taskbar (tray, Start menu, taskbar buttons) closes the overview automatically so whatever pops up is visible.
- **App windows view**
  - When the overview is closed, a three-finger swipe down opens the app windows view. It tiles only the current app's windows (minimized ones in a row at the bottom) and fades the rest out.
  - A three-finger swipe up closes it, and clicking a window switches to it. This view has no desktop strip.
  - Clicking the title row under a window (app icon + title) toggles between the overview and that app's windows view without closing either.
- **Desktop strip and paging**
  - With two or more virtual desktops, the desktop strip sits at the top. Hover to expand it into thumbnails; click another desktop to switch to it, or click the current one to close the overview.
  - A four-finger left/right swipe keeps you in the overview while the window area slides to the neighboring desktop's page, as in Mission Control. Release past halfway to switch, otherwise it springs back. If you close the overview after paging, windows fly back to their real positions on the new desktop.
  - The "+" at the end of the strip adds a desktop at the end without switching to it.
  - Hovering a desktop shows an "×" to the right of its name. Click it to remove that desktop; its windows merge into the desktop on its left. If you remove the current desktop, the overview zooms into the desktop it merged into and then closes.
- **Window actions**
  - Hovering a window shows a close button in its top-left corner. Click it to close the window, and the others reflow to fill the gap.
  - If the app pops up a dialog such as "Save changes?", the overview closes and switches to it.
  - Press and drag a window: it follows the pointer, and near the top the desktop strip expands and the window shrinks.
  - Drop it on another desktop to move it there (it flies into that thumbnail and the rest reflow). Drop it on "+" to create a desktop and move it there. Drop it anywhere else and it flies back.
- **Selection**: three-finger left/right swipes, the arrow keys, or Tab (Shift+Tab for reverse) move the selection.

**Other ways in**
- **Mouse button gestures**: hold the Back button and drag. In settings you can change this to Forward or Middle, or turn it off.
  - Left/right switches desktops. With the overview open it pages instead, and the desktop follows the pointer.
  - Up opens the overview; down opens the app windows view or closes the overview.
  - A single click is still Back.
- **Alt+Tab**: replaces the system switcher with GlideShell's.
  - Hold Alt and press Tab to select the next window (Shift+Tab for the previous one; ←/→ also work). Release Alt and the selected window flies back into place and comes to the front. Esc cancels.
  - A quick tap switches straight to the previous window without showing the switcher.
  - While holding Alt, hover a card to select it, click a card to switch to it, or click empty space to cancel. Delete closes the selected window.
  - Windows handles it instead while GlideShell is paused, an excluded app is in front, or GlideShell is paused for full screen. You can turn this off in settings.
- **Ctrl+Win+←/→**: switches to the neighboring desktop with GlideShell's animation, and repeated presses chain. With the overview open it pages inside the overview instead. You can turn this off in settings and hand it back to Windows.
- **Hot corners**: in settings, assign an action to each of the four screen corners (window overview, app windows, show desktop). Resting the pointer in a corner for about 0.1 s triggers it. All are off by default.

**Tray menu**
- Open overview; Pause (switches straight back to native Windows gestures); Settings…
- Toggles for three-finger drag and four-finger desktop switching; sensitivity high / standard / low.
- Apps that don't use gestures; pause in full screen; start at sign-in; open the log folder.

**Settings window** (tray menu → Settings…, or launch GlideShell again)
- Looks like Windows 11 Settings: Mica background, left navigation, rounded cards, following the system light/dark theme and accent color. Changes apply immediately; there is nothing to save.
- Home: a diagram of what each gesture does now, plus start at sign-in and pause in full screen.
- Touchpad gestures: who handles each gesture, three-finger drag, three-finger tap as middle click, and sensitivity (high / standard / low; "fine-tune" adjusts travel and drag speed per gesture).
- Overview & desktops: app windows view, group by app, dimmed background, window corner radius, Alt+Tab, Ctrl+Win+←/→.
- Mouse & hot corners: the hold-and-drag button and its sensitivity; set hot corners on the four corners of a screen diagram.
- Excluded apps; About (automatic update check, config file, logs, reset to defaults).
- Fully keyboard-operable: Tab to move, Space/Enter to activate, ←/→ to adjust sliders.

**Advanced**
- **Config file**: `%APPDATA%\GlideShell\config.json`. Comments are allowed and changes take effect when you save. Less common options such as spring parameters live here too; see [docs/CONFIG.md](docs/CONFIG.md) (Chinese) for every option.
- **Log**: `%LOCALAPPDATA%\GlideShell\logs\glideshell.log`.
- **Update check and one-click upgrade**:
  - **Check**: by default, GlideShell asks GitHub once a day for the latest stable release. It only fetches the version number and sends nothing else. When there is a new version, the tray notifies you once.
  - **Upgrade**: click the notification, the tray menu item "升级到 x.y.z…" (Upgrade to x.y.z…), or "升级" (Upgrade) under About in settings. GlideShell then downloads the release package from GitHub, checks its size and SHA-256 (the digest GitHub reports), runs the package's install script to install in place, and restarts. It never upgrades on its own.
  - **UIAccess builds**: a UIAccess build installed from source into Program Files does not use one-click upgrade, because the release package is the standard-privilege build.
  - **Turning it off**: you can turn off the automatic check under About in settings (after which GlideShell never goes online) or check manually.
- **Diagnostics**:
  - The "诊断" (Diagnostics) page in settings lists compatibility self-checks one by one: touchpad and report mode, whether the system gesture takeover is in effect, the virtual desktop interface, shared thumbnails, UIAccess, keyboard/mouse hooks, late frames in the last render, and the update check.
  - "生成诊断报告" (Create diagnostic report) writes the version, system, displays, self-check results, config and the last 300 log lines to a text file, copies it to the clipboard, and opens it in Notepad. It stays on your machine, ready to paste into an issue. The tray menu and `GlideShell.exe --diagnostics` can create it too.
  - After an abnormal exit, GlideShell mentions it once on the next start.
- **Emergency restore**: if the system gestures somehow weren't restored, run `GlideShell.exe --restore-native`. `GlideShell.exe --quit` asks a running instance to exit cleanly.

## Performance (measured on the dev machine)

Test machine:
- Radeon 780M; built-in screen 2560×1600 at 150% scaling and 165 Hz.
- External screen 2560×1440 at 125% and 200 Hz.

| Item | Value |
|---|---|
| Program size | About 5.6 MB (NativeAOT) |
| Resident memory | Private memory about 16 MB right after start, about 20–30 MB after using the overview and settings (Direct2D / DirectWrite and the app icon cache) |
| Frame interval for overview / desktop switch / window switch | P50 6.1 ms / P95 6.2 ms (keeps up with 165 Hz) |
| Late frames (one frame missed) | 0 / 7387 frames (200 Hz, each of the four simulated gestures 5 times; also 0 at 60 Hz and on a mixed 60 Hz + 200 Hz dual-monitor setup, see ROADMAP 9.2) |
| Input to screen | About 1 frame (sampled late against the compositor clock) |
| Warm-up when reopening the overview | About 3–6 ms |

## How it works

```
Input thread    Raw Input reads raw PTP contacts (allocation-free parsing, coordinates in millimeters, timing from the hardware ScanTime)
    ↓
Pure logic      gesture recognition (decided only on lift) → gesture mapping and exclusions → overview / window switcher / desktop switch controllers → physics (velocity, landing point, springs)
    ↓
Render thread   samples at the display refresh rate and drives DirectComposition + live DWM thumbnails
```

- The overlay only displays; it never takes focus. The actual foreground or virtual-desktop switch happens underneath it, and the overlay is removed at the end for a seamless hand-off.
- To take over system gestures, GlideShell writes the registry and tells Explorer to refresh its settings cache. Before taking over it writes a record to disk, and a watchdog process uses that record to restore the settings if the main process exits unexpectedly.
- Two undocumented interfaces are used: the virtual desktop COM interface and shared thumbnails (dwmapi#147). Both are self-checked at startup, with automatic fallbacks if a check fails: the four-finger gestures go back to the system, or GlideShell uses regular thumbnails.

Design notes, measurements from each stage, and the roadmap are in **[docs/ROADMAP.md](docs/ROADMAP.md)** (Chinese).

## Known limitations

- **Hardware and displays**
  - Fully verified on one machine only (an ASUS precision touchpad in parallel report mode). Hybrid report mode, where each report carries only some of the fingers and which is common on other brands, is covered by unit tests only. If you hit problems on another touchpad, please attach a diagnostic report from Settings → 诊断 (Diagnostics).
  - Multiple monitors were verified with real gestures on a 150% + 125% (165 Hz + 200 Hz) dual-monitor setup; other combinations haven't been tested yet.
  - Window shadows were measured to match the system at 125% / 150% / 175%; 100% hasn't been measured yet.
  - Windows 10 isn't supported yet.
- **Standard-privilege release build**
  - The release package is the standard-privilege build, so the overlay can't cover system UI such as the Start menu. If you need that, build the UIAccess version from source (see below).
  - It can't minimize apps running as administrator.
  - While an app running as administrator is in the foreground, it receives no key presses (Alt+Tab goes to Windows), and its middle clicks and drags can't reach that app.
- **Behavior and cost**
  - Removing the current desktop, or the desktop the overview was opened from, in the desktop strip closes the overview (it doesn't re-layout in place).
  - Show desktop really minimizes the windows. Windows pulled back are restored to where they were before being minimized.
  - With mouse button gestures on, moving the mouse costs some CPU. The low-level mouse hook makes Windows switch to GlideShell once per mouse event, so the cost scales with the polling rate: about 1% of one core at 1000 Hz and about 8% at 8000 Hz. If you don't use them, choose "不用" (None) in settings.
  - The app windows view groups windows by app (Store apps by app identity), so all windows from the same program count together.
  - When switching desktops, Windows itself shows a desktop-name hint at the bottom of the screen. Explorer draws it inside existing UI, so it can't be suppressed.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build GlideShell.slnx
dotnet test GlideShell.slnx

pwsh build\publish.ps1                 # self-contained single file → artifacts\publish\win-x64\GlideShell.exe
pwsh build\publish.ps1 -Aot            # NativeAOT (needs Visual Studio "Desktop development with C++")
pwsh build\package.ps1 -Aot            # release package → artifacts\GlideShell-<version>-win-x64.zip
pwsh build\install.ps1                 # install on this machine (publishes first if there is no build output)
```

**UIAccess build**: the overlay can cover system UI such as the Start menu, and foreground switching is more reliable. It uses a self-signed certificate that is only valid on this computer.

```powershell
pwsh build\publish.ps1 -Aot -UIAccess  # the first run shows a UAC prompt to trust the local certificate
pwsh build\install.ps1 -UIAccess       # installs to %ProgramFiles%\GlideShell; only the copy step needs admin rights
```

To demo with synthetic gestures without touching the touchpad:

```powershell
dotnet run --project src\GlideShell -- --simulate overview   # also switch / desktop-right / desktop-left, etc.
```

### Diagnostic tool `tools/PtpProbe`

```powershell
dotnet run --project tools\PtpProbe                         # touchpad report descriptor (contact slots, physical size, etc.)
dotnet run --project tools\PtpProbe -- live                 # print gesture recognition results live
dotnet run --project tools\PtpProbe -- record               # record input traces by scenario
dotnet run --project tools\PtpProbe -- calibrate <trace dir> # suggest recognition parameters from traces and verify by replay
dotnet run --project tools\PtpProbe -- windows              # which windows the overview would include
dotnet run --project tools\PtpProbe -- native               # system gestures: registry values vs. Explorer's effective values
dotnet run --project tools\PtpProbe -- desktops             # virtual desktop interface self-check
dotnet run --project tools\PtpProbe -- thumbnails           # thumbnail backend self-check
dotnet run --project tools\PtpProbe -- thumbnails --shadow  # compare the drawn shadow with the real system shadow point by point
```

### Layout

```
src/
  GlideShell.Core/       pure logic: gesture recognition, mapping, physics, layout, interaction controllers (no Windows API calls, replay-testable)
  GlideShell.Platform/   Windows implementation: Raw Input, DWM, DirectComposition, virtual desktops, system gesture takeover
  GlideShell/            the resident app: tray, settings window, thread orchestration, config
tests/GlideShell.Tests/  unit tests, real-device trace replay tests, architecture guard tests
tools/PtpProbe/          diagnostic probe
build/                   publish, package, install and certificate scripts
docs/                    roadmap, config reference, technical validation notes
legacy/                  the old MissionFlow prototype (read-only reference, tag missionflow-final)
```

## Acknowledgements

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows) (MIT): the PTP parsing approach comes from its TouchpadHelper
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad) (MIT): the upstream approach for reading touchpads via Raw Input
- During research we also looked at GestureSign, TouchpadGestures_Advanced, windows-expose-clone, GNOME Shell's workspace.js and others

## License

[MIT](LICENSE) © LiamZhaofor. Bundled third-party MIT code keeps its original copyright notices.
