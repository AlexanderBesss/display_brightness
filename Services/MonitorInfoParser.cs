using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public static class MonitorInfoParser
{
    private static readonly string[] ModelIdPrefixes = ["MON", "ANC"];

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
        var model = ExtractModelFromDeviceId(deviceId);

        return new MonitorInfo
        {
            DevicePath = FirstNonEmpty(deviceId, fallbackKey),
            FriendlyName = FirstNonEmpty(
                productDescription,
                model,
                deviceId,
                $"Monitor ({fallbackKey})"),
            ModelName = model,
            OutputTechnology = outputTechnology
        };
    }

    public static MonitorInfo CreateMonitorFromEnumDevices(
        string subString,
        string subId,
        string deviceId,
        string deviceString)
    {
        var model = ExtractModelFromDeviceId(subId);

        return new MonitorInfo
        {
            DevicePath = FirstNonEmpty(subId, deviceId),
            FriendlyName = FirstNonEmpty(
                subString,
                model,
                deviceString,
                $"Monitor ({deviceId})"),
            ModelName = model
        };
    }

    public static MonitorInfo CreateMonitorFromEnumMonitors(
        string monitorDeviceName,
        (string deviceString, string deviceId, string parentName) deviceInfo)
    {
        var (deviceString, deviceId, parentName) = deviceInfo;
        var friendlyName = string.Equals(
            deviceString, parentName, StringComparison.OrdinalIgnoreCase)
                ? monitorDeviceName
                : FirstNonEmpty(deviceString, monitorDeviceName);

        return new MonitorInfo
        {
            DevicePath = monitorDeviceName,
            DisplayName = monitorDeviceName,
            FriendlyName = friendlyName,
            ModelName = ExtractModelFromDeviceId(deviceId)
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
    }
}
