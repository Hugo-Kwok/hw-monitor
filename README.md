# Better HW Monitor

A HWiNFO-style sensor monitor that can put **several sensors on one graph**
(for example GPU Temperature + GPU Hot Spot), with a second Y-axis for a second unit.

Sensor readings come from [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK (to build) / .NET 8 Desktop Runtime (to run)
- **PawnIO** driver for CPU and motherboard sensors (temperatures, voltages, fans).
  LibreHardwareMonitor 0.9.6 uses PawnIO instead of the old WinRing0 driver that Windows Defender flags.
  Install it from https://github.com/namazso/PawnIO.Setup/releases.
  GPU sensors (NVIDIA / AMD) work without it.
- The app asks for administrator rights on start (needed for most sensors).

## Build

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The exe is `publish\BetterHwMonitor.exe`.

## Use

- **Left:** every sensor, grouped by device, with Current / Min / Max. Type in *Filter* to search
  (e.g. `temp`, `GPU`, `fan`).
- **Right:** your graphs. Click a graph to select it (blue border).
  - Double-click a sensor to add it to the selected graph.
  - Or select several sensors (Ctrl/Shift-click) and drag them onto any graph.
  - Drop onto the dashed box at the bottom to make a new graph.
  - Right-click the sensor list for "Add to selected graph" / "Add to a new graph".
- Each graph holds any number of curves with up to **two units**: the first unit uses the left axis,
  the second the right axis (e.g. °C on the left, MHz on the right).
- Hover a graph to see every curve's value at that moment.
- Click a graph title to rename it. ✕ in the legend removes a curve.
- The legend sits on the right of each graph. Drag the divider between a chart and its legend to change the
  legend width; all graphs follow (double-click the divider to reset). Click a curve's name to rename it (for this app only); ↺ or the
  right-click menu changes it back to the original sensor name.
- **Y axis...** in a graph's header sets a manual min/max for the left and right axis (empty = automatic).
- ▲ / ▼ in a graph's header move it up or down.
- Click a curve's color swatch in the legend to pick another color (presets or a hex code like `#FF8800`).
- **Your graphs are saved automatically** (graphs, curves, colors, axes, time window, update rate and
  window position) to `%AppData%\BetterHwMonitor\layout.json` and restored on the next start.
- **Closing the window keeps the app running in the system tray**, so monitoring and graph history continue.
  Click the tray icon (or start the exe again) to bring the window back. Right-click the tray icon and choose
  **Exit** to quit for real. Graph history lives in memory, so it starts fresh after Exit or a reboot.
- **Dark mode:** the *Dark mode / Light mode* button in the toolbar switches the theme (title bar included).
  The choice is saved in `%AppData%\BetterHwMonitor\theme.txt`; on first start the app follows the Windows app theme.
- *Time window* sets how much history the graphs show; up to 3 days is kept.
- Every reading is recorded to `%AppData%\BetterHwMonitor\history` (one file per hour, files older than 3 days
  are deleted), so after a restart the graphs still show earlier data. The time the app was closed shows as a gap.
- Click a category header in the sensor list (e.g. "Embedded Controller") to collapse or expand it.
- "◀ Hide" hides the whole sensor list so the graphs get the full width; "Sensors ▶" brings it back.
  Both are remembered between runs.
