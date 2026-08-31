# Brightness

A lightweight Windows tray application for adjusting external monitor brightness.

## Download

Download `Brightness.exe` from the [latest GitHub release](../../releases/latest).
The published application is self-contained, so the .NET runtime does not need to
be installed separately.

## Features

- Control brightness of external monitors (HDMI, DisplayPort, DVI, VGA, USB-C)
- System tray integration with right-click Exit menu
- Left-click tray icon to show/hide the window
- Persistent brightness settings per monitor
- Optional startup with Windows

## Requirements

- Windows 10/11
- Administrator privileges (required for display brightness control)

## Build

```bash
dotnet build DisplayBrightness.csproj -c Release
```

## Run

```bash
dotnet run --project DisplayBrightness.csproj -c Release
```

## Publish (standalone)

```bash
dotnet publish DisplayBrightness.csproj -c Release -o publish /p:PublishSingleFile=true
```

## Usage

1. Left-click the tray icon to open the brightness control window
2. Adjust sliders to set brightness per monitor
3. Window auto-hides when clicking away
4. Right-click tray icon to exit the application
5. Enable "Start on startup" to launch with Windows
