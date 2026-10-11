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
Clearing the label at the source needs an elevated prompt
(`icacls F:\Projects\display_brightness /setintegritylevel Medium`); a non-elevated shell
gets "Access is denied" because the mandatory label lives in the SACL, and the agent
sandbox may re-apply it anyway.

## Related trap: GUID-based registration

Registering the icon with `NIF_GUID` makes the identity outlive the process. If an
instance dies without removing the icon, the shell keeps refusing that GUID for every
later instance, and claiming a replacement GUID makes Windows treat the icon as brand new
and park it in the notification-area overflow — one registry key per GUID under
`HKCU\Control Panel\NotifyIconSettings`. The app therefore registers by window handle plus
a non-zero `uID` and never sets `NIF_GUID`.
