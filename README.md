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
- Scroll over the tray icon to adjust the primary display by 2% per wheel notch
- Persistent brightness settings per monitor
- Optional startup with Windows

## Requirements

- Windows 10/11
- .NET 8 Desktop Runtime (x64)
- Administrator privileges (required for display brightness control)

## Build

```bash
dotnet build DisplayBrightness.csproj -c Release
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
4. Window auto-hides when clicking away
5. Right-click tray icon to exit the application
6. Enable "Start on startup" to launch with Windows
