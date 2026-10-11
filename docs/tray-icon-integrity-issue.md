# Tray icon refused: "Failed to create tray icon (Win32 error 5)"

An incident note. Keep this when changing anything about tray registration.

## Symptom

The app starts but no icon appears in the notification area, or an older build shows a
`Failed to start Brightness: Failed to create tray icon.` dialog. The same executable
works when it is copied to a different folder. A secondary symptom is the app silently
failing to write `settings.json` or its logs under `%APPDATA%`.

## Cause

The workspace root `F:\Projects\display_brightness` carries an explicit

```
Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)
Gaming\CodexSandboxUsers:(OI)(CI)(M)
```

ACE pair — an agent-sandbox artifact. Both inherit into `publish\Brightness.exe`. An image
with a Low integrity mandatory label runs at Low integrity, and a Low-IL process cannot
send messages to the Medium-IL shell. `Shell_NotifyIconW(NIM_ADD)` therefore returns FALSE
with `ERROR_ACCESS_DENIED` (Win32 5). The same label explains the `%APPDATA%` writes: a
Low-IL process cannot write up into a Medium-IL folder.

## How to confirm it in two minutes

1. `icacls publish\Brightness.exe` — look for `Mandatory Label\Low Mandatory Level`.
   A copy that works elsewhere has no such ACE.
2. Copy the exe to `C:\Users\alexa\AppData\Local\Brightness\` and run it. If the icon
   appears, the folder label is the cause and the code is innocent — stop digging in
   `Services/NativeTrayIcon.cs`.
3. `%APPDATA%\Brightness\startup-trace.log` records each startup. A
   `tray icon unavailable:` line means the shell refused the icon; its absence means the
   icon registered.

## Dead ends already ruled out

Verified as *not* the cause, so do not re-investigate them:

- The icon handle. `Bitmap.GetHicon()` -> `Icon.FromHandle()` -> `Clone()` ->
  `DestroyIcon(original)` is correct; `Clone()` yields an independent handle and the add
  succeeds with it even after the original is destroyed.
- The `NOTIFYICONDATAW` layout. `cbSize` = 976 (the Vista+ layout with the trailing
  `hIcon`) is right.
- The window kind. A `NativeWindow.CreateHandle(CreateParams { Caption = ... })` window,
  a visible window, and a WinForms `Form` all register fine.
- The callback message, the tooltip text, `NIF_SHOWTIP`, and message pumping.
- The icon identity: a fresh GUID, `SystemIcons.Application.Handle`, and a window-scoped
  id all fail identically from the labeled folder.
- The process being unopenable by the shell: `OpenProcess` with `PROCESS_DUP_HANDLE` and
  full access succeeds on the failing process, so handle duplication is not the problem.
- Integrity-level probing from an external process: `OpenProcessToken` on another user's
  process returns a pseudo-token and reports nonsense. Measure nothing with that method.

## Fix

Run the installed copy, never the build output:

```
C:\Users\alexa\AppData\Local\Brightness\Brightness.exe --startup
```

That path is what `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` points at.
Clearing the label at the source: `icacls F:\Projects\display_brightness /setintegritylevel Medium`
— done 2026-10-11 from a **non-elevated** shell; the earlier "needs elevation" note was
wrong. Label-setting checks `WRITE_OWNER`, which the root's owner (alexa, FullControl)
has. Child objects created while the Low ACE was in force keep an orphaned inherited
Low label and cannot be relabeled — their DACL grants only Modify, no `WRITE_OWNER`.
Delete and recreate them instead: `Build.ps1`/`Build.bat` via `git checkout`, `publish\`
via a rebuild. Verified after the fix: `publish\Brightness.exe --startup` registers the
tray icon, and workspace `dotnet test` passes 156/156. The agent sandbox may re-apply
the label; if symptoms return, check `icacls F:\Projects\display_brightness` for
`Mandatory Label\Low` before re-investigating.

## Same artifact, other symptoms: build and test failures (2026-10)

The same sandbox residue also breaks plain `dotnet build` / `dotnet test` in the
workspace. Two distinct problems, both Codex-sandbox artifacts:

- `obj\` and `bin\` are owned by `Gaming\CodexSandboxOffline` — a leftover artifact.
  Their DACL still grants Modify to `NT AUTHORITY\Authenticated Users`, so an ordinary
  user shell builds there fine; but the agent harness's sandbox runs with a restricted
  token that lacks those group SIDs, and a sandboxed `dotnet build` fails during
  restore with `Access to the path 'obj\<guid>.tmp' is denied`. The same restricted
  token also blocked writes into ordinary folders like `temp\`. The same build from a
  non-sandboxed, still non-elevated shell succeeds. (`Tests\obj`, `Tests\bin` are
  normal.)
- `dotnet test` compiles fine but aborts before running a single test: the test host
  dies with `System.ComponentModel.Win32Exception (5): Access is denied.` inside
  `SetParentProcessExitCallback` (`Process.EnableRaisingEvents`). The Low mandatory
  label inherited into the test output folder — check with
  `icacls <output>\testhost.exe`, which shows `Mandatory Label\Low Mandatory Level:(I)(NW)` —
  makes the test host run at Low integrity, and a Low-IL process cannot open a handle
  to its Medium-IL `dotnet.exe` parent. Same integrity rule as the tray icon, new
  victim.

Dead ends, so do not re-investigate:

- It is not WOW64: a 32-bit shell opens the same parent handle fine.
- It is not the NuGet-cached test host: `%USERPROFILE%\.nuget\packages\microsoft.testplatform.testhost\17.13.0\build\netcoreapp3.1\x86\testhost.x86.exe`
  has no label. The labeled copy is the one in the project's output folder.
- `--settings` with `TargetProcessArchitecture=x64` is ignored; the run still uses
  `testhost.x86`.
- Redirecting intermediates with `-p:BaseIntermediateOutputPath=...` backfires: once
  `obj\` is no longer the intermediate path, its stale `obj\Release\**\*.g.cs` files
  are globbed back into the compile ("already defines a member" errors), and MSBuild
  splits command-line property values on `;`, so a multi-pattern `DefaultItemExcludes`
  cannot be passed as one argument.

What works: build and test from a copy outside the labeled tree (fallback; no longer
needed since the 2026-10-11 label clear above — a freshly created `testhost.exe` then
defaults to Medium and workspace `dotnet test` passes again).

```
robocopy F:\Projects\display_brightness C:\Users\alexa\review-src /E ^
  /XD obj bin publish temp review-build .git .vs /XF *.user
dotnet test C:\Users\alexa\review-src\Tests\DisplayBrightness.Tests.csproj -c Debug
```

156/156 tests pass that way (~1 s). Delete the copy afterwards.

Cleanup: `obj\` and `bin\` are regenerable build artifacts and safe to delete; the
Low label at the root needs elevation to clear
(`icacls F:\Projects\display_brightness /setintegritylevel Medium`) — with the same
caveat as the Fix section above: the agent sandbox may re-apply the artifacts.

## Related trap: GUID-based registration

Registering the icon with `NIF_GUID` makes the identity outlive the process. If an
instance dies without removing the icon, the shell keeps refusing that GUID for every
later instance, and claiming a replacement GUID makes Windows treat the icon as brand new
and park it in the notification-area overflow — one registry key per GUID under
`HKCU\Control Panel\NotifyIconSettings`. The app therefore registers by window handle plus
a non-zero `uID` and never sets `NIF_GUID`.
