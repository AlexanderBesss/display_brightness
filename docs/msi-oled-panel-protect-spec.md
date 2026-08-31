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
| SET       | `5b` + code(5) + value(3) + `CR` | `5600+` (ack) or `5600-` (reject) |

- Verbs are 2 ASCII chars: `58` = GET, `5b` = SET, `56` = SET reply.
- Feature codes are 5 ASCII chars, e.g. `00;10`.
- The app prefixes the 64-byte report with report ID `0x01` and zero-pads the
  rest. This framing is live-verified on the target device.
- The monitor emits unsolicited 1 Hz pushes (e.g. `6b00;30000`, `5b00110000`);
  responses are matched by the echoed feature code.
- I/O timeout: 2.5 s.

### 3.1 Feature codes observed

| Code    | Register | Meaning                                  |
| ------- | -------- | ---------------------------------------- |
| `00;00` | 0xB00    | Pixel shift (ours reads `002`)           |
| `00;10` | 0xB10    | Panel protect (ours reads `001`)         |
| `00;11` | 0xB11    | OLED protection related (ours reads `001`)|
| `00;30` | 0xB30    | Protection setting (ours reads `000`)    |
| `00;90` | 0xB90    | Protect notice (ours reads `001`)        |

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

## 5. Long-command blob (feature report 0x11, 257 bytes)

OLED protection settings live at fixed offsets; there is no pixel-refresh
trigger in the blob:

| Offset | Meaning              |
| ------ | -------------------- |
| 113    | Pixel shift          |
| 114    | Protect notice       |
| 115–126| Detection settings   |

## 6. Safety constraints

- Only the allow-listed Panel Protect command (`00;10` = `001`) is sent; no other
  write is issued by the app.
- Avoid dangerous registers `0x960` and `0x40A`.
- User confirmation dialog before each refresh.
- No scheduling, no inferred refresh history; unsupported monitors get no HID
  traffic at all.

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
