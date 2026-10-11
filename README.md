# Brightness

A lightweight Windows tray application for adjusting external monitor brightness.

## Download

Download `Brightness.exe` from the [latest GitHub release](../../releases/latest).
The published application is a small, framework-dependent executable and requires
the .NET 8 Desktop Runtime (x64).

## Features

- Control brightness of external monitors (HDMI, DisplayPort, DVI, VGA, USB-C)
- System tray integration with right-click Exit menu
- Left-click tray icon to show/hide the window
- Scroll over the tray icon to adjust the primary controllable display by 2% per
  wheel notch (or the first controllable display if the primary is unavailable)
- Background brightness changes and display discovery keep the tray responsive
- Automatic recovery after display/USB changes and sleep, including while hidden
- Persistent brightness settings per monitor
- Standalone MSI OLED Care status, user-initiated Panel Protect support, and
  app-tracked time since the last successful Panel Protect launch
- Continuous MSI scaler-event monitoring with a persisted Panel Protect-needed
  indicator in the monitor list
- Optional startup with Windows

## Requirements

- Windows 10/11
- .NET 8 Desktop Runtime (x64)
- Administrator privileges (required for display brightness control)
- The monitor's USB upstream cable for MSI OLED Care controls

MSI OLED Care currently has a verified compatibility profile for the MPG 271QRX
QD-OLED (`MSI3CD7`). Other detected MSI OLED models are clearly marked
Experimental and use only the allow-listed Panel Protect command. The app does
not schedule refreshes or infer refresh history; when the monitor does not return
OLED Panel Info, it displays `Status unavailable`. A captured Panel Protect
notification is latched locally until Panel Protect is started from this app.

OLED Care matches the display's EDID serial number to the USB monitor's serial
number before reading status or sending Panel Protect. Missing, duplicate, or
unmatched identities disable OLED control with an explanation; the app never
chooses a USB monitor by model alone. A display that is still connected but
returns an unreadable brightness keeps its last known value and controls through
one failed read, and is disabled only when a second consecutive read fails. A
failed write on a known controllable display gets one retry and leaves the
controls available for another adjustment.
Precision touchpad scrolling preserves fractional wheel movement, and rapid
changes are combined and paced. Display changes trigger a debounced refresh;
periodic background checks recover displays even while the popup stays hidden,
and opening the popup re-reads a display that currently looks unusable.

## Build

```bash
dotnet build DisplayBrightness.csproj -c Release
dotnet test Tests/DisplayBrightness.Tests.csproj -c Release
```

## Run

```bash
dotnet run --project DisplayBrightness.csproj -c Release
```

## Publish (small single file)

```bash
dotnet publish DisplayBrightness.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=false -o publish
```

## Usage

1. Left-click the tray icon to open the brightness control window
2. Scroll over the tray icon to adjust the primary display brightness
3. Adjust sliders to set brightness per monitor
4. For a supported MSI OLED, use the OLED Care row to refresh status or run a
   confirmed pixel refresh
5. Window auto-hides when clicking away
6. Right-click tray icon to exit the application
7. Enable "Start on startup" to launch with Windows
