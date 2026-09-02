# MSI OLED Panel Protect (Pixel Refresh) — HID Protocol Specification

Status: verified on MSI MPG 271QRX QD-OLED (`MSI3CD7`) with USB upstream cable.
Last updated: 2026-09-01.

## 1. Purpose

Spec for driving the MSI "Panel Protect" (pixel refresh) routine and reading OLED
status over the monitor's HID control interface. This is what the app's OLED Care
row implements. The app never schedules refreshes and only ever sends the
allow-listed Panel Protect command.

## 2. Device identification

- HID device: `VID_1462` (MSI), `PID_3FA4` — one HID interface.
- Interface reports: input/output 64 bytes, feature 168 bytes.
- Windows path shape: `\\?\hid#vid_1462&pid_3fa4#...#{4d1e55b2-f16f-11cf-88cb-001111000030}`.
- Total panel usage hours are not on the HID interface; read them via DDC/CI
  VCP feature `0xC0`.

## 3. Short-command wire protocol

Commands are ASCII strings written into a 64-byte HID output report.

| Operation | Request frame            | Response frame                     |
| --------- | ------------------------ | ---------------------------------- |
| GET       | `58` + code(5) + `CR`    | `5b` + code + value + `CR`         |
| Scaler event GET | `68` + code(5) + `CR` | `6b` + code + event value + `CR` |
| SET       | `5b` + code(5) + value(3) + `CR` | `5600+` (ack) or `5600-` (reject) |

- Verbs are 2 ASCII chars: `58` = GET, `68` = scaler event GET,
  `5b` = SET/normal reply, `6b` = scaler event reply, `56` = SET reply.
- Feature codes are 5 ASCII chars, e.g. `00;10`.
- The app prefixes the 64-byte report with report ID `0x01` and zero-pads the
  rest. This framing is live-verified on the target device.
- `6b00;30...` is not an unsolicited variation of a normal reply. It is the
  response to the dedicated `6800;30` scaler-event poll used by MSI's helper.
- I/O timeout: 2.5 s.

### 3.1 Feature codes observed

| Code    | Register | Meaning                                  |
| ------- | -------- | ---------------------------------------- |
| `00;00` | 0xB00    | Pixel shift (ours reads `002`)           |
| `00;10` | 0xB10    | Panel protect (ours reads `001`)         |
| `00;11` | 0xB11    | OLED protection related (ours reads `001`)|
| `00;30` | 0xB30    | Panel Protect scaler event (normal GET reads `000`) |
| `00;90` | 0xB90    | Protect notice (`000` Auto, `001` longer interval) |

0xB10/0xB11 are documented as "OLED protection related" flags (factory value 1)
in the 491CQP feature code docs.

## 4. Panel Protect trigger (pixel refresh)

Exact behavior of MSI Gaming Intelligence, confirmed by decompiling
`MonitorControlLibrary.dll` (`Nuvoton.SendPanelProtectCommand`):

- Command: SET `00;10` value `001` → `5b00;10001` + `CR` (11 ASCII bytes).
- **Fire-and-forget**: GI writes the report and never reads a response. The
  firmware withholds the `5600+` ack while the panel-protect routine runs, so
  waiting for the ack times out even though the refresh executes.
- Result: the display shows its own warning (do not look at the screen
  directly); the routine runs for several seconds.

App implementation therefore writes the same command and returns success on a
clean write (no ack wait). See `SetNoAckAsync` in
`Services/MsiHidTransport.cs` and `OledCareService.StartPixelRefreshAsync`.

### 4.1 What GI does NOT do

- No pixel-refresh trigger exists in the decompiled short-command table, in the
  long-command blob (below), in msigd (old generation), or in the 491CQP docs.
- The LongCMD UI page dispatches `CommandType.PanelProtect` to
  `SendPanelProtectCommand`; the ShortTime/LongTime buttons (4949/4950) route
  through `MonitorMicroKeyDetector.exe` — a different UI context, not a second
  HID command.

### 4.2 Panel Protect notification event

`MonitorMicroKeyDetector.exe` continuously polls the monitor with
`6800;30` + `CR`. The reply has the form `6b00;30xxE`, where the final byte
encodes `OLEDCareEventType` as `E - ASCII('0')`. Values above 9 therefore use
ASCII punctuation: for example event 13 is `=` (`0x30 + 13`). Important values:

| Event | Meaning |
| ----- | ------- |
| 0 | No notification at this instant |
| 1 / 13 | Short Panel Protect due (13 offers Later) |
| 2 | Long Panel Protect due |
| 3 / 4 | Forced short / long Panel Protect |
| 5–10, 14–15 | Warning, interrupted, cancelled, or deferred protect |
| 11 / 12 | Manual short / long request from MSI's UI |

MSI polls this channel about once per second and launches `OSDPopupHandler.exe`
when the value is nonzero. A zero is not durable proof that Panel Protect is not
needed: live point-in-time reads remained zero after five additional VCP `0xC0`
usage hours. The app therefore polls while running and latches any actionable
event in the `Notification` entry of `oled-care-state.json`. The indicator
remains visible until a Panel Protect command is successfully started from this
app.

## 5. Long-command blob (feature report 0x11, 257 bytes)

OLED protection settings live at fixed offsets; there is no pixel-refresh
trigger in the blob:

| Offset | Meaning              |
| ------ | -------------------- |
| 113    | Pixel shift          |
| 114    | Protect notice       |
| 115–126| Detection settings   |

## 5.1 Probing results on this unit (271QRX, FW 018, 2026-09-01)

- The 168-byte feature report (ID `0x11`) exists, but GETs of `5800<00`
  (GetAllFunc) and `5800<10` (GetAllBeh) return an all-zero payload for every
  report ID tried (`0x00`–`0x21`), and sending GetAllFunc as a short command
  yields no reply within 12 s. The long-command channel is **not readable**
  on this firmware (X50-generation blobs do not apply here).
- Full short-code sweep (`001xx` + `00;xx` blocks + GI base-table codes):
  78 codes respond, **every value is 3-digit (000–999) or a string** — nothing
  carries a 4-digit counter. Notable reads:

  | Code    | Value                | Meaning                  |
  | ------- | -------------------- | ------------------------ |
  | `00130` | `CD7A014100250`      | Serial number            |
  | `001<0` | `018`                | Firmware version         |
  | `001?0` | `SDC QMC265FF01_D01` | Panel model              |
  | `00150` | `V23`                | Display controller       |
  | `00170` | `104`                | Current refresh rate in Hz, **mod 256** (unit runs a 360 Hz mode, reads 104 = 360 mod 256; stable across samples, GET-only live read, not a counter) |
  | `00100` | `001`                | Power state              |
  | `00;20` | `001`                | Static-screen detect on  |
  | `00;23` | `006`                | SSD reducing level       |
  | `00;50` | `001`                | Multi-logo detect on     |
  | `00;60` | `001`                | Taskbar detect on        |
  | `00;70` | `001`                | Boundary detect on       |

- DDC/CI VCP sweep (33 responding codes): `0xC0` = total usage hours
  (10250, live), `0xC8` = 18 (matches FW), and `0xAC`/`0xAE`/`0xC6`/`0xDF`
  hold static values (54012 / 35960 / 111 / 513) that did not change over
  6 minutes; none equals or scales to a panel-protect counter.
- Gaming Intelligence's helper reads the UART version at startup and then
  polls `6800;30` about once per second. Live reads return `6b00;30000` while
  no notification is being announced.

### 5.2 OSD-only telemetry

The monitor's OSD "OLED Panel Info" shows the panel-protect run count
(observed: `3140`) and time since last run (observed: `0h 10m`). Neither
value is exposed over DDC/CI or USB HID on this model/firmware — exhaustive
GET-only probing of all three channels found no matching register. They are
internal NVRAM values rendered by the OSD only. The only related values the
app can read are total usage hours (VCP `0xC0`) and the OLED protection
settings listed above.

The app therefore keeps a separate, per-monitor history only for Panel Protect
commands that it successfully sends. It records the UTC dispatch time and the
current VCP `0xC0` usage-hours value when available. The app uses wall-clock
time for minute precision while it agrees with the monitor-reported usage delta
within one hour. If the difference exceeds one hour, the monitor delta becomes
authoritative and the UI shows whole panel hours. This avoids counting long
periods while the monitor was off. While history exists, the app rereads only
VCP `0xC0` once per minute so the monitor-authoritative value can advance without
repeating the HID status queries. It is still app-launched history and cannot
detect refreshes started from the OSD or other software.

The app stores this history and the scaler notifications it actually observes in
a single `oled-care-state.json` file next to the application executable: one
entry per monitor holding an optional `History` part (the app-launched panel
protect run) and an optional `Notification` part (the latched monitor request).
The notification part detects monitor requests while the app is running, but
cannot reconstruct a notification missed while the app was closed or confirm
that an OSD/standby-initiated refresh later completed.

## 6. Safety constraints

- Only the allow-listed Panel Protect command (`00;10` = `001`) is sent; no other
  write is issued by the app.
- Avoid dangerous registers `0x960` and `0x40A`.
- User confirmation dialog before each refresh.
- The continuous `6800;30` notification poll is GET-only and never acknowledges,
  cancels, defers, or executes a monitor event.
- No scheduling or inferred monitor history; only successful commands launched
  by this app are recorded. Unsupported monitors get no HID traffic at all.

## 7. Implementation map

| Concern                  | File                                   |
| ------------------------ | -------------------------------------- |
| HID I/O, protocol framing | `Services/MsiHidTransport.cs`          |
| Command/ack parsing      | `Services/MsiHidTransport.cs` (`MsiProtocol`) |
| Status + refresh service | `Services/OledCareService.cs`          |
| Compatibility allow-list | `Services/OledCompatibilityRegistry.cs`|
| Value decoding           | `Services/OledValueParser.cs`          |
| Models                   | `Models/OledCareModels.cs`             |
| UI state                 | `ViewModels/MonitorSliderViewModel.cs` |
| Confirmation dialog      | `Services/UserDialogService.cs`        |
| Tests                    | `Tests/OledCareServiceTests.cs`, `Tests/MsiHidTransportTests.cs` |

## 8. Investigation sources

External repositories:

- [storkme/msi-mpg-271qr-control](https://github.com/storkme/msi-mpg-271qr-control) —
  reverse-engineering notes for the same panel family: decompiled short-command
  table, long-command blob layout, and protocol documentation.
- [hamishmorgan/msi-mpg-491cqp-control](https://github.com/hamishmorgan/msi-mpg-491cqp-control) —
  feature code documentation for a current MSI OLED (0xB10/0xB11 OLED protection
  flags).
- [nicrom/msigd](https://github.com/nicrom/msigd) — old-generation MSI display
  (MSDILed) HID protocol reference.

Local MSI Gaming Intelligence installation (decompiled with ilspycmd):

- `C:\Program Files\GamingIntelligence\MonitorControlLibrary.dll` —
  `Nuvoton.SendPanelProtectCommand` (the Panel Protect byte sequence and
  fire-and-forget write), `CommandType` enum, LongCMD page dispatch.
- `C:\Program Files\GamingIntelligence\OLEDCareLibrary.dll` — dedicated OLED care
  library (PanelProtect strings).
- `C:\Program Files\GamingIntelligence\MSILedDll.dll` — native HID write path
  (`_SetCMD`), used by the .NET layer above.
- `%LOCALAPPDATA%\MSI\GamingIntelligence\Profile\MPG271QRX_QD-OLED\` — GI profile
  JSON with numeric `CmdType` mapping.
