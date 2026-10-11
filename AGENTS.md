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
   The Low integrity label that once made PowerShell refuse rewritten scripts with
   "the file is not digitally signed" was cleared 2026-10-11; if that message returns,
   the label was re-applied (see Gotchas). `Build.bat` passes `-ExecutionPolicy Bypass`.
2. **Restart:** stop the running instance, copy `publish\Brightness.exe` into
   `C:\Users\alexa\AppData\Local\Brightness\`, then start
   `"C:\Users\alexa\AppData\Local\Brightness\Brightness.exe" --startup`. Keep the
   `oled-care-state.json` already in that folder — the running app holds the current
   state; only seed it from `temp\oled-care-state.pre-build-backup.json` when it is
   missing. That folder is the autostart target and the canonical place to run from;
   `publish\` is runnable since the 2026-10-11 label clear, but keep restarting the
   installed copy (see Gotchas).
3. **Verify:** the new pid has zero top-level windows and
   `%APPDATA%\Brightness\startup-trace.log` contains no `tray icon unavailable:` line.

- Test: `dotnet test Tests\DisplayBrightness.Tests.csproj -c Debug` (171 tests, ~1 s) —
  works in the workspace since the 2026-10-11 label clear. If it fails with "Access to
  the path ... `obj` ... is denied" (sandboxed shell with a restricted token) or the
  test host dies with `Win32Exception (5)` in `SetParentProcessExitCallback` (Low label
  re-applied), see [docs/tray-icon-integrity-issue.md](docs/tray-icon-integrity-issue.md)
  for the diagnosis and the robocopy-outside-the-tree fallback.

## Runtime state

- `%APPDATA%\Brightness\settings.json` — per-monitor brightness settings.
- `%APPDATA%\Brightness\startup-trace.log`, `startup-error.log` — startup diagnostics.
- `oled-care-state.json` — **next to the exe**, not in `%APPDATA%`.
- `brightness.log` (+ `brightness.log.old` after rotation) — application diagnostics
  (`Services/AppLog.cs`), **next to the exe**: session banners, startup args, display
  connect/retire, brightness write failures, OLED operations, unhandled exceptions.
  One event per line (stack traces are flattened); rotates at 1 MiB; writes never
  throw and open the file with `FileShare.ReadWrite`, so a second running copy — the
  app has no single-instance guard — or a log viewer that permits writes cannot
  silence it. Polling failures log once per streak through `Utilities/FailureStreak.cs`.

## Gotchas

- The workspace once carried a Low integrity label that made the shell refuse the tray
  icon for anything run from it; cleared 2026-10-11 (`icacls F:\Projects\display_brightness
  /setintegritylevel Medium` on the root, then delete-and-recreate of the labeled
  artifacts). If a `tray icon unavailable:` line reappears, check
  `icacls F:\Projects\display_brightness` for `Mandatory Label\Low` before
  re-investigating — the sandbox can re-apply it. See
  [docs/tray-icon-integrity-issue.md](docs/tray-icon-integrity-issue.md) — it also lists
  the causes already ruled out.
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
  failures, the Low-integrity-label artifact (cleared 2026-10-11), causes ruled out,
  the GUID trap, and the same artifacts breaking `dotnet build`/`dotnet test`.
- [docs/msi-oled-panel-protect-spec.md](docs/msi-oled-panel-protect-spec.md) — MSI OLED
  Care / Panel Protect behaviour.
