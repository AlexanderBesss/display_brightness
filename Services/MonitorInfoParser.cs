using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public static class MonitorInfoParser
{
    private static readonly string[] ModelIdPrefixes = ["MON", "ANC"];

    public static string ExtractHardwareId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;

        string normalized = deviceId.Replace('#', '\\');
        foreach (string part in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 7 &&
                part[..3].All(char.IsLetter) &&
                part[3..].All(Uri.IsHexDigit))
            {
                return part.ToUpperInvariant();
            }
        }

        return string.Empty;
    }

    public static string ExtractModelFromDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;

        foreach (var prefix in ModelIdPrefixes)
        {
            var idx = deviceId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var end = deviceId.IndexOf('\\', idx + prefix.Length);
            if (end > 0)
                return deviceId[(idx + prefix.Length)..end];
        }

        return string.Empty;
    }

    public static string FormatRefreshRate(double refreshRateHz)
    {
        if (refreshRateHz <= 0)
            return string.Empty;

        return $"{refreshRateHz.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} Hz";
    }

    public static bool IsExternalMonitorByDeviceId(string deviceId)
    {
        return !deviceId.Contains("ldu", StringComparison.OrdinalIgnoreCase) &&
               !deviceId.Contains("microsoft basic display", StringComparison.OrdinalIgnoreCase) &&
               !deviceId.StartsWith("screen\\", StringComparison.OrdinalIgnoreCase);
    }

    public static MonitorInfo CreateMonitorFromTargetName(
        DisplayInterop.DISPLAYCONFIG_TARGET_NAME targetName,
        string fallbackKey,
        uint outputTechnology)
    {
        var productDescription = DisplayInterop.CharArrayToString(
            targetName.targetProductDescription);
        var deviceId = DisplayInterop.CharArrayToString(
            targetName.targetMonitoredDeviceId);
        var hardwareId = ExtractHardwareId(deviceId);
        var model = FirstNonEmpty(ExtractModelFromDeviceId(deviceId), hardwareId);

        return new MonitorInfo
        {
            DevicePath = FirstNonEmpty(deviceId, fallbackKey),
            FriendlyName = FirstNonEmpty(
                productDescription,
                model,
                deviceId,
                $"Monitor ({fallbackKey})"),
            ModelName = model,
            HardwareId = hardwareId,
            ManufacturerCode = hardwareId.Length >= 3 ? hardwareId[..3] : string.Empty,
            EdidProductCode = targetName.edidProductCodeId,
            OutputTechnology = outputTechnology
        };
    }

    public static MonitorInfo CreateMonitorFromEnumDevices(
        string subString,
        string subId,
        string deviceId,
        string deviceString)
    {
        var hardwareId = ExtractHardwareId(subId);
        var model = FirstNonEmpty(ExtractModelFromDeviceId(subId), hardwareId);

        return new MonitorInfo
        {
            DevicePath = FirstNonEmpty(subId, deviceId),
            FriendlyName = FirstNonEmpty(
                subString,
                model,
                deviceString,
                $"Monitor ({deviceId})"),
            ModelName = model,
            HardwareId = hardwareId,
            ManufacturerCode = hardwareId.Length >= 3 ? hardwareId[..3] : string.Empty
        };
    }

    public static MonitorInfo CreateMonitorFromEnumMonitors(
        string monitorDeviceName,
        (string deviceString, string deviceId, string parentName) deviceInfo)
    {
        var (deviceString, deviceId, parentName) = deviceInfo;
        var hardwareId = ExtractHardwareId(deviceId);
        var friendlyName = string.Equals(
            deviceString, parentName, StringComparison.OrdinalIgnoreCase)
                ? monitorDeviceName
                : FirstNonEmpty(deviceString, monitorDeviceName);

        return new MonitorInfo
        {
            DevicePath = FirstNonEmpty(deviceId, monitorDeviceName),
            DisplayName = monitorDeviceName,
            FriendlyName = friendlyName,
            ModelName = FirstNonEmpty(ExtractModelFromDeviceId(deviceId), hardwareId),
            HardwareId = hardwareId,
            ManufacturerCode = hardwareId.Length >= 3 ? hardwareId[..3] : string.Empty
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
    }
}
