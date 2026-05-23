# DesktopPerfWidget

DesktopPerfWidget is a small Windows on ARM desktop performance widget. It shows CPU, memory, physical disk activity, network send/receive speed, GPU, and NPU status in a compact desktop-layer overlay, with an optional separate clock window.

The project is intentionally simple: one C# WinForms source file plus PowerShell install/build scripts. It targets an ARM64 .NET Framework executable so Windows Task Manager reports it as ARM64 instead of x64 or AnyCPU.

## Features

- Bottom-right desktop widget with CPU, memory, disk, network, GPU, and NPU panels.
- Per-core CPU activity bars, current/base CPU frequency, memory manufacturer/speed, and readable hardware names.
- GPU/NPU usage plus memory usage when Windows exposes matching performance counters.
- Network upload/download graphs, connected Wi-Fi SSID when available, and disconnected state rendering.
- Memory uses WMI physical-memory data for manufacturer and configured speed, plus live usage/capacity.
- Disk read/write activity plus whole-disk capacity usage.
- Warning backgrounds above 80% load and warning triangle after sustained critical load.
- Notification-area icon with settings and exit actions.
- Drag-and-drop metric layout editor for the six visible panel slots.
- Optional clock window with 24-hour mode, calendar text, and battery/charging display.
- Power-saving mode using Windows EcoQoS power throttling when available and lower process priority.
- Stable visible desktop mode by default, plus an experimental WorkerW desktop-parent mode.
- Current-user autostart install/uninstall scripts.
- Runtime, error, and install logs capped to 10 MB.

## Requirements

- Windows on ARM.
- .NET Framework runtime with Windows Forms support.
- Visual Studio Build Tools 2022 or newer when building from source.
- Roslyn `csc.exe` with `/platform:arm64` support.

The build script searches these compiler locations:

```text
C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
C:\Program Files (x86)\Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
```

## Quick Start

Build the executable:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1
```

Install current-user autostart and start the widget:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

If you start it from Explorer, use `Install.cmd` so the command window stays open and shows the result.

Uninstall autostart and stop the widget:

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
```

`Uninstall.cmd` does the same thing and keeps the command window open.

## Command Line

`DesktopPerfWidget.exe` starts the widget in stable visible desktop mode.

```text
--stop              Signal the running widget instance to exit.
--install           Add current-user autostart, stop the old instance, and start the widget.
--uninstall         Remove current-user autostart and stop the widget.
--no-start          Use with --install to register autostart without launching immediately.
--desktop-parent    Start in experimental Explorer WorkerW desktop-parent mode.
--workerw           Alias for --desktop-parent.
--test              Print one performance sample to the console and write it to the log.
```

The install script accepts matching options:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -NoStart
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -DesktopParent
```

The autostart registry value is:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopPerfWidgetArm64
```

## Settings

- The app creates a notification-area icon. If Windows hides it, open the tray overflow with the small arrow.
- Right-click the tray icon to open a small menu with `设置...` and `退出`. You can also right-click the widget and choose `设置...`.
- Settings support width, height, bottom-left pixel position, background transparency, secondary transparency away from the desktop, visibility mode, autostart, power-saving mode, and the separate clock window.
- The six metric panels are arranged with a drag-and-drop layout editor: drag a metric into slots 1-6 to show it in that order, or click the small `x` on a slot to hide it. Slots are shown as 3 rows by 2 columns, matching the widget's actual panel positions.
- Settings include an alert-test toggle that forces every visible panel into the full warning state for visual checks.
- Power-saving mode is enabled by default and applies process-level Windows EcoQoS power throttling with low process priority when supported.
- Width, height, and position values are physical screen pixels. The settings UI labels them as window position X/Y and time position X/Y.
- Width, height, and position can be adjusted with both numeric inputs and sliders.
- Secondary transparency is applied to the full performance and clock windows only when the foreground window is not the desktop or taskbar.
- In `一直可见` and `仅全屏不可见` modes, the widget lets mouse clicks pass through to windows behind it. `仅桌面可见` keeps normal widget right-click behavior.
- The clock window uses Windows system time, updates to seconds, and has separate 24-hour mode, optional calendar display, optional battery power/charging rate display, transparency, size, and position settings.
- Calendar mode left-aligns the time and right-aligns the date plus full weekday on the right.
- Power display uses pale red for battery power and pale green for charging when Windows exposes the battery rate.
- `重置` restores the current default size and bottom-right position.
- Changes preview immediately. Closing the settings window or pressing `Cancel` restores the previous saved state. Press `保存` to persist settings.

Settings are saved here:

```text
%LOCALAPPDATA%\DesktopPerfWidget\settings.ini
```

## Logs

Runtime log:

```text
%LOCALAPPDATA%\DesktopPerfWidget\DesktopPerfWidget.log
```

Error-only log:

```text
%LOCALAPPDATA%\DesktopPerfWidget\error.log
```

Install/uninstall log:

```text
%LOCALAPPDATA%\DesktopPerfWidget\install.log
```

The local log folder is capped at 10 MB for `.log` files. Old log files can be archived in the program directory before a fresh run.

## Panels

- CPU uses the live processor name, shows current/base frequency, and overlays per-core bars on top of the existing usage curve. A bar's portion above 80% turns yellow; above 95% the whole active bar turns red.
- Long hardware names in panel titles are split after the detected vendor/brand prefix so model names remain readable in narrow panels.
- Memory shows `MEM` before its usage percentage.
- Memory, GPU, and NPU graphs add a red warning background above 80% usage. Disk uses read/write activity only for this warning.
- The red warning layer fades up to 70% opacity at 100%.
- The transparent yellow-outline warning triangle appears only after the relevant load stays at or above 98% for 3 seconds.
- GPU uses two graph lines: GPU usage and GPU memory usage. The text shows GPU model, GPU usage, and VRAM usage.
- NPU uses two graph lines: NPU usage and NPU memory usage. The text shows NPU model, NPU usage, and memory usage.
- Network uses two graph lines: blue `UP` and red `DL`. The text shows the connected Wi-Fi SSID when available, upload rate, and download rate. When disconnected, the graph dims to 50% transparency, shows a red cross, and displays `网络已断开`.
- Disk uses two graph lines: physical-disk read/write activity and whole-disk capacity usage. The text shows the physical disk model, `R/W` activity, and used/total capacity.

## Troubleshooting

If the widget process is running but nothing is visible, run `Start-Visible-Test.cmd`. It starts the same stable visible mode as the default executable.

`Start-WorkerW-Test.cmd` starts the experimental Explorer WorkerW desktop-parent mode. On some Windows builds this layer is an invisible or tiny worker window, so it is not the default.

If hardware panels show zeros or generic names, run:

```powershell
.\DesktopPerfWidget.exe --test
```

Then inspect `DesktopPerfWidget.log` and `error.log`. Availability of GPU, NPU, Wi-Fi SSID, and battery rate data depends on Windows exposing the relevant PDH, WMI, WLAN, and battery information on the current machine.

## Project Layout

```text
DesktopPerfWidget.cs       Main WinForms app, sampler, renderer, settings UI, and native interop.
Build-Arm64.ps1           ARM64 build script.
Install.ps1 / Install.cmd Current-user autostart install wrapper.
Uninstall.ps1 / .cmd      Current-user autostart removal wrapper.
Start-Visible-Test.cmd    Restarts in stable visible mode.
Start-WorkerW-Test.cmd    Restarts in experimental WorkerW mode.
docs/TECHNICAL.md         Architecture and maintenance notes.
LICENSE                   MIT license.
```

## Development

The source currently lives in a single C# file to keep the tool easy to copy, build, and audit. For deeper implementation notes, see [docs/TECHNICAL.md](docs/TECHNICAL.md).

Recommended validation before publishing changes:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1
.\DesktopPerfWidget.exe --test
```

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
