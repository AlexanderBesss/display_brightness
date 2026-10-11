# Agent notes

Windows tray app for external-monitor brightness. WPF UI with WinForms interop for the
tray icon. `net8.0-windows`, `AssemblyName=Brightness`, `RootNamespace=DisplayBrightness`.

## Layout

- `Services/` — native interop and hardware: `NativeTrayIcon`, `TrayBrightnessIcon`,
  `DisplayInterop`/`DisplayService` (DDC/CI), `WmiBrightnessController`, `MonitorEnumerator`,
  `MonitorIdentityReader`, `MsiHidTransport`, `OledCareService`, `StorageService`.
  Interfaces (`IBrightnessController`, `IDisplayService`, ...) sit beside the implementations.
- `ViewModels/` — `MainWindowViewModel`, `MonitorSliderViewModel`, command types.
- `Models/`, `Utilities/` — data types and helpers.
- `Tests/` — separate xunit project (`Tests/DisplayBrightness.Tests.csproj`); the main
  csproj excludes `Tests\**\*.cs`, so tests never compile into the app.

## Build and restart

When the user asks for a build, use the repository's build script — never a bare
`dotnet publish`. A build is not finished until the running app is restarted on the new
binary.

1. **Build:** `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1`
   (equivalently `.\Build.bat`). It takes about **3 s** warm — budget 15 s, do not treat
   it as a long-running job. Exit code `0` = success, `1` = failure; the output ends with
   `Build successful!` or `BUILD FAILED:`.
   A bare `.\Build.ps1` can be refused with "the file is not digitally signed": any file
   rewritten in this workspace inherits the Low integrity label, and PowerShell treats a
   Low-IL script as untrusted. `Build.bat` already passes `-ExecutionPolicy Bypass`.
2. **Restart:** stop the running instance, copy `publish\Brightness.exe` into
   `C:\Users\alexa\AppData\Local\Brightness\`, then start
   `"C:\Users\alexa\AppData\Local\Brightness\Brightness.exe" --startup`. Keep the
   `oled-care-state.json` already in that folder — the running app holds the current
   state; only seed it from `temp\oled-care-state.pre-build-backup.json` when it is
   missing. That folder is the autostart target and the only place the tray icon works —
   never restart from `publish\` (see Gotchas).
3. **Verify:** the new pid has zero top-level windows and
   `%APPDATA%\Brightness\startup-trace.log` contains no `tray icon unavailable:` line.

- Test: `dotnet test Tests\DisplayBrightness.Tests.csproj -c Debug` (156 tests, ~1 s).
  If it fails with "Access to the path ... `obj` ... is denied", or the test host dies
  with `Win32Exception (5)` in `SetParentProcessExitCallback`, the workspace's sandbox
  artifacts are the cause — robocopy the sources to a folder outside the workspace and
  test there; see [docs/tray-icon-integrity-issue.md](docs/tray-icon-integrity-issue.md).

## Runtime state

- `%APPDATA%\Brightness\settings.json` — per-monitor brightness settings.
- `%APPDATA%\Brightness\startup-trace.log`, `startup-error.log` — startup diagnostics.
- `oled-care-state.json` — **next to the exe**, not in `%APPDATA%`.

## Gotchas

- Never launch `publish\Brightness.exe` directly; the workspace folder carries a Low
  integrity label that makes the shell refuse the tray icon. See
  [docs/tray-icon-integrity-issue.md](docs/tray-icon-integrity-issue.md) — it also lists
  the causes already ruled out, so read it before re-investigating a tray-icon failure.
- The tray icon is registered by window handle plus a non-zero `uID`, deliberately without
  `NIF_GUID`. Do not reintroduce a GUID; the same document explains why.
- `Build.ps1` stops any instance whose image lives inside `publish\` before cleaning, so a
  locked output file is usually self-healing. An installed copy elsewhere is left running.
  If the clean step still fails with "Access to the path is denied", find the holder with
  `Get-CimInstance Win32_Process -Filter "Name='Brightness.exe'"`.
- `Build.ps1` wipes `publish\oled-care-state.json`; restore it from
  `temp\oled-care-state.pre-build-backup.json` after building. The "Press any key" pause is
  skipped when input is redirected, so non-interactive runs return promptly.
- `app.manifest` is `asInvoker`: the app never elevates. The README still lists
  administrator privileges as a requirement for brightness control, but DDC/CI control
  works without elevation in practice — don't add elevation on the strength of that line.

## Verifying behaviour

Brightness and display state are testable headlessly through `Services/` and the view
models. For UI/tray checks, use UI Automation against the process id; screenshots are not
available to the agent. A hidden, correctly registered instance has **zero** top-level
windows for its pid and no `tray icon unavailable:` line in `startup-trace.log`.

## Docs

- [docs/tray-icon-integrity-issue.md](docs/tray-icon-integrity-issue.md) — tray icon
  failures, causes ruled out, the GUID trap, and the same sandbox artifacts breaking
  `dotnet build`/`dotnet test` in the workspace.
- [docs/msi-oled-panel-protect-spec.md](docs/msi-oled-panel-protect-spec.md) — MSI OLED
  Care / Panel Protect behaviour.
