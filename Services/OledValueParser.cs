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

    public static bool TryParsePanelProtectEvent(
        string value,
        out OledPanelProtectEventType eventType)
    {
        eventType = OledPanelProtectEventType.None;
        if (value.Length != 3 || value[0] != '0' || value[1] != '0')
            return false;

        int eventCode = value[2] - '0';
        if (!Enum.IsDefined(typeof(OledPanelProtectEventType), eventCode))
            return false;

        eventType = (OledPanelProtectEventType)eventCode;
        return true;
    }
}
