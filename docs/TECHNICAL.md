# DesktopPerfWidget Technical Notes

## Project Summary

DesktopPerfWidget was developed into a compact Windows on ARM desktop performance overlay. The current version focuses on reliability first: a stable visible desktop window is the default, WorkerW desktop parenting remains available only as an experimental mode, and the app includes settings, logging, autostart, a tray menu, alert rendering, and a separate clock window.

The project is distributed as a small source package. The core application is `DesktopPerfWidget.cs`; build and lifecycle tasks are handled by PowerShell and CMD wrappers.

## Runtime Architecture

`Program` is the entry point. It handles command-line lifecycle operations before starting the WinForms message loop:

- `--stop` signals the named stop event.
- `--install` writes the current-user Run registry value, stops an old instance, and optionally starts a new one.
- `--uninstall` removes the Run registry value and stops the app.
- `--test` opens the sampler, waits for a real PDH interval, prints one sample, and logs it.
- default startup creates a single-instance mutex, loads settings, applies power-saving mode, initializes `PdhSampler`, and runs `WidgetForm`.

Single-instance behavior uses:

```text
Local\DesktopPerfWidgetArm64
Local\DesktopPerfWidgetArm64Stop
```

Autostart is stored in:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DesktopPerfWidgetArm64
```

## Main Components

`WidgetForm` owns the main overlay window. It creates the tray icon, settings menu, timer, metric history buffers, alert state, current settings, and the optional `ClockForm`.

`ClockForm` renders the separate clock layer. It reads clock-related settings from `WidgetSettings`, updates on a short timer, and can show calendar and battery/charging information when Windows exposes the data.

`SettingsForm` is the WinForms settings UI. Changes preview immediately through `WidgetForm.ApplyRuntimeSettings`. Saving writes `settings.ini`; canceling or closing without save restores the last saved state.

`WidgetSettings` loads, normalizes, clones, and saves all persistent user settings. It also owns the canonical metric ids and default panel order.

`PdhSampler` is the hardware data layer. It opens one PDH query, adds the best available counters, samples once per UI timer tick, and returns a `PerfSnapshot`.

`NativeMethods` and `PdhNative` contain the Windows interop surface for layered-window rendering, DPI awareness, process power throttling, console attachment, PDH, WLAN SSID, WorkerW discovery, and related Win32 calls.

## Sampling Model

The widget samples once per second. The first PDH collection primes the query; later samples read formatted counter values.

CPU:

- Uses `\Processor Information(_Total)\% Processor Utility` when available.
- Falls back to `\Processor(_Total)\% Processor Time`.
- Reads per-core counters from wildcard paths and sorts them by processor/core instance.
- Reads actual frequency when Windows exposes it, with WMI current/max clock as fallback.

Memory:

- Uses `GlobalMemoryStatusEx`.
- Reports physical memory used, total, and percentage.

Disk:

- Selects a physical disk counter matching the system drive when possible.
- Falls back through first physical disk, `_Total`, and logical `C:` disk time.
- Uses WMI `Win32_DiskDrive` for model and size.
- Uses fixed drive roots for capacity/free-space calculations.

Network:

- Uses PDH `Network Interface(*)\Bytes Sent/sec` and `Bytes Received/sec`.
- Filters loopback, ISATAP, and Teredo adapters.
- Uses `NetworkInterface` to decide connected/disconnected state.
- Uses WLAN APIs to show Wi-Fi SSID when available.

GPU:

- Uses `GPU Engine(*)\Utilization Percentage`.
- Uses `GPU Adapter Memory(*)\Dedicated Usage` and `Shared Usage`.
- Uses WMI `Win32_VideoController` for model and nominal memory.

NPU:

- Tries native `NPU Engine(*)` and `NPU Adapter Memory(*)` counters first.
- Falls back to NPU-looking GPU Engine paths when Windows exposes NPU activity through GPU counter categories.
- Detects NPU devices from `Win32_PnPEntity` by class and common NPU keywords.

## Rendering Model

The widget is a borderless WinForms tool window that renders a transparent layered surface. The default mode avoids WorkerW parenting because some Windows builds create invisible or tiny WorkerW hosts. Experimental desktop parenting is still available with `--desktop-parent`.

The panel layout is a 3-row by 2-column grid. `MetricOrder` defines slot order while individual `Show*` flags decide visibility. History buffers are capped to the visible graph length.

Alert rendering is deliberately staged:

- Warning background starts above 80%.
- The red overlay grows toward 70% opacity at 100%.
- Warning triangle requires at least 98% load for 3 seconds.
- The alert test option forces visible panels into the warning state for UI validation.

## Settings File

Settings are stored as a simple key/value file:

```text
%LOCALAPPDATA%\DesktopPerfWidget\settings.ini
```

Current saved version:

```text
Version=2
```

Important keys:

```text
Width
Height
LeftX
BottomY
BackgroundTransparencyPercent
ClockWidth
ClockHeight
ClockLeftX
ClockBottomY
ClockTransparencyPercent
ClockUse24Hour
ClockCalendarEnabled
ClockPowerEnabled
VisibilityMode
StartupEnabled
ShowCpu
ShowMemory
ShowDisk
ShowNetwork
ShowGpu
ShowNpu
AlertTestEnabled
PowerSavingEnabled
MetricOrder
```

Values are normalized on load and save. Unknown or invalid values are ignored where possible so older or hand-edited files do not prevent startup.

## Visibility Modes

The code supports three user-facing visibility behaviors:

- Desktop-only mode keeps normal widget right-click behavior.
- Always-visible mode lets mouse clicks pass through to windows behind the widget.
- Hide-when-fullscreen mode also lets clicks pass through and hides the widget while a fullscreen foreground window is detected.

The separate clock window follows the same fullscreen hiding behavior.

## Build

`Build-Arm64.ps1` compiles the single source file with Roslyn:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1
```

It passes:

```text
/target:winexe
/platform:arm64
/optimize+
```

Referenced assemblies:

```text
System.dll
System.Drawing.dll
System.Management.dll
System.Windows.Forms.dll
```

The default output is:

```text
DesktopPerfWidget.exe
```

Use `-OutputPath` to build elsewhere:

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\out\DesktopPerfWidget.exe
```

## Install and Uninstall

`Install.ps1` builds the executable if missing, writes current-user autostart, stops any running instance, and starts the widget unless `-NoStart` is passed.

`Uninstall.ps1` removes the current-user autostart value and stops the widget unless `-KeepRunning` is passed.

The `.cmd` wrappers exist for Explorer launches, keeping a visible command window open for the user.

## Logging

Logs live in:

```text
%LOCALAPPDATA%\DesktopPerfWidget
```

Files:

```text
DesktopPerfWidget.log
error.log
install.log
```

The install scripts and application cap `.log` files in that folder at about 10 MB. Runtime logs include startup arguments, architecture details, PDH counter initialization, periodic samples, settings changes, and exception traces.

## Validation Checklist

Before sharing a build:

1. Run `powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1`.
2. Run `.\DesktopPerfWidget.exe --test` and confirm it prints a complete sample.
3. Start the widget normally and confirm the tray icon appears.
4. Open settings from the tray and verify save/cancel behavior.
5. Toggle alert test and confirm visible warning rendering.
6. Restart with `Start-Visible-Test.cmd` if the window is not visible.
7. Test `Install.ps1 -NoStart` and `Uninstall.ps1 -KeepRunning` when changing lifecycle behavior.

## Known Limits

- Windows performance counter availability varies by device, driver, and OS build.
- NPU counters are not consistently exposed across Windows on ARM systems.
- Battery power/charge rate display depends on WMI battery data.
- WorkerW desktop-parent mode is intentionally experimental.
- The codebase is currently a single large source file, which keeps distribution simple but makes targeted unit testing harder.

## Suggested Future Improvements

- Split `DesktopPerfWidget.cs` into focused files once the public behavior stabilizes.
- Add a small project file for IDE builds while keeping the current script build path.
- Add automated smoke tests around settings parsing and metric-order normalization.
- Publish signed release binaries through GitHub Releases.
- Add screenshots or a short demo GIF to the README after GitHub publication.
