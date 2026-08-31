using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

internal static class OledValueParser
{
    public static OledPanelInfo? ParsePanelInfo(string value, int? totalUsageHours)
    {
        if (!TryDecodeValue(value, out int panelProtect))
            return null;

        return new OledPanelInfo(panelProtect, totalUsageHours);
    }

    public static bool TryDecodeValue(string value, out int result)
    {
        result = 0;
        if (value.Length is 0 or > 6)
            return false;

        foreach (char character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return int.TryParse(value, out result);
    }
}
