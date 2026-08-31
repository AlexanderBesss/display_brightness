using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class MonitorInfoParserTests
{
    [Theory]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#5&123&0&UID1", "MSI3CD7")]
    [InlineData(@"DISPLAY\GBT2800\5&123", "GBT2800")]
    [InlineData("", "")]
    public void ExtractHardwareId_ReturnsEdidIdentifier(string input, string expected)
    {
        Assert.Equal(expected, MonitorInfoParser.ExtractHardwareId(input));
    }

    [Theory]
    [InlineData(144.0, "144 Hz")]
    [InlineData(60.0, "60 Hz")]
    [InlineData(59.931, "59.93 Hz")]
    [InlineData(59.97, "59.97 Hz")]
    [InlineData(164.99, "164.99 Hz")]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    public void FormatRefreshRate_FormatsActualRate(double rate, string expected)
    {
        Assert.Equal(expected, MonitorInfoParser.FormatRefreshRate(rate));
    }
}
