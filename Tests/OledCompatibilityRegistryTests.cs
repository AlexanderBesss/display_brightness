using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class OledCompatibilityRegistryTests
{
    [Fact]
    public void Find_ReturnsVerifiedProfile_ForMpg271Qrx()
    {
        var monitor = new MonitorInfo
        {
            HardwareId = "MSI3CD7",
            ManufacturerCode = "MSI",
            FriendlyName = "MPG271QX OLED"
        };

        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);

        Assert.NotNull(profile);
        Assert.Equal(OledSupportLevel.Verified, profile.SupportLevel);
        Assert.Equal((ushort)0x1462, profile.HidVendorId);
        Assert.Contains((ushort)0x3FA4, profile.HidProductIds);
        Assert.Equal(OledCompatibilityRegistry.PanelProtectCode, profile.PanelProtectCode);
        Assert.Equal("MSI-HID-ASCII-1", profile.ProtocolVersion);
    }

    [Fact]
    public void Find_ReturnsExperimentalProfile_ForOtherMsiOled()
    {
        var monitor = new MonitorInfo
        {
            HardwareId = "MSIFFFF",
            ManufacturerCode = "MSI",
            FriendlyName = "MSI QD-OLED"
        };

        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);

        Assert.NotNull(profile);
        Assert.Equal(OledSupportLevel.Experimental, profile.SupportLevel);
    }

    [Fact]
    public void Find_ReturnsNull_ForNonMsiDisplay()
    {
        var monitor = new MonitorInfo
        {
            HardwareId = "GBT2800",
            ManufacturerCode = "GBT",
            FriendlyName = "M28U"
        };

        Assert.Null(OledCompatibilityRegistry.Find(monitor));
    }

    [Fact]
    public void PixelRefresh_UsesTheDocumentedPanelProtectRegister()
    {
        var monitor = new MonitorInfo
        {
            HardwareId = "MSI3CD7",
            ManufacturerCode = "MSI",
            FriendlyName = "MPG271QX OLED"
        };

        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);

        Assert.NotNull(profile);
        Assert.Equal("00;10", profile.PanelProtectCode);
        Assert.Equal("001", profile.PixelRefreshValue);
    }
}
