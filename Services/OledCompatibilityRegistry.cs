using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

internal sealed record OledMonitorProfile(
    string HardwareId,
    ushort HidVendorId,
    ushort[] HidProductIds,
    OledSupportLevel SupportLevel,
    string ProtocolVersion,
    string PixelShiftCode,
    string PanelProtectCode,
    string ProtectNoticeCode,
    string PixelRefreshValue);

internal static class OledCompatibilityRegistry
{
    internal const ushort MsiVendorId = 0x1462;

    // MSI short-command feature codes (5 ASCII chars, nibble encoding where
    // hex A-F map to ':'-'?'; ';' is nibble B). See the "OLED Protection"
    // block of the MSI monitor HID protocol:
    //   0xB00 pixel shift, 0xB10/0xB11 panel protect, 0xB90 protect notice.
    internal const string PixelShiftCode = "00;00";
    internal const string PanelProtectCode = "00;10";
    internal const string ProtectNoticeCode = "00;90";

    private static readonly OledMonitorProfile[] Profiles =
    [
        new(
            "MSI3CD7",
            MsiVendorId,
            [0x3FA4, 0x3FA3],
            OledSupportLevel.Verified,
            "MSI-HID-ASCII-1",
            PixelShiftCode,
            PanelProtectCode,
            ProtectNoticeCode,
            "001")
    ];

    public static OledMonitorProfile? Find(MonitorInfo monitor)
    {
        OledMonitorProfile? exact = Profiles.FirstOrDefault(profile =>
            string.Equals(
                profile.HardwareId,
                monitor.HardwareId,
                StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        bool isMsi = string.Equals(
            monitor.ManufacturerCode,
            "MSI",
            StringComparison.OrdinalIgnoreCase) ||
            monitor.HardwareId.StartsWith("MSI", StringComparison.OrdinalIgnoreCase);
        bool isOled = monitor.FriendlyName.Contains("OLED", StringComparison.OrdinalIgnoreCase) ||
                      monitor.ModelName.Contains("OLED", StringComparison.OrdinalIgnoreCase);
        if (!isMsi || !isOled)
            return null;

        return new OledMonitorProfile(
            monitor.HardwareId,
            MsiVendorId,
            [0x3FA4, 0x3FA3],
            OledSupportLevel.Experimental,
            "MSI-HID-ASCII-1",
            PixelShiftCode,
            PanelProtectCode,
            ProtectNoticeCode,
            "001");
    }
}
