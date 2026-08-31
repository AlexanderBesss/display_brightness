using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public static class MonitorInfoParser
{
    public static string ExtractModelFromDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;

        var upper = deviceId.ToUpper();
        foreach (var prefix in new[] { "MON", "ANC" })
        {
            var idx = upper.IndexOf(prefix);
            if (idx < 0) continue;
            var end = upper.IndexOf('\\', idx + prefix.Length);
            if (end > 0)
                return deviceId.Substring(idx + prefix.Length, end - idx - prefix.Length);
        }

        return string.Empty;
    }

    public static string NormalizeDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;
        return deviceId.Replace("\0", "").Trim();
    }

    public static bool IsExternalMonitorByDeviceId(string deviceId)
    {
        var lower = deviceId.ToLower();
        return !lower.Contains("ldu") &&
               !lower.Contains("microsoft basic display") &&
               !lower.StartsWith("screen\\");
    }

    public static MonitorInfo CreateMonitorFromTargetName(
        DisplayInterop.DISPLAYCONFIG_TARGET_NAME targetName,
        ulong adapterId, uint targetId, string fallbackKey, uint outputTech)
    {
        var friendlyName = DisplayInterop.CharArrayToString(
            targetName.targetProductDescription);
        var deviceId = DisplayInterop.CharArrayToString(
            targetName.targetMonitoredDeviceId);
        var model = ExtractModelFromDeviceId(deviceId);

        if (string.IsNullOrWhiteSpace(friendlyName))
            friendlyName = model;
        if (string.IsNullOrWhiteSpace(friendlyName))
            friendlyName = deviceId;
        if (string.IsNullOrWhiteSpace(friendlyName))
            friendlyName = $"Monitor ({fallbackKey})";
        if (string.IsNullOrWhiteSpace(deviceId))
            deviceId = fallbackKey;

        return new MonitorInfo
        {
            DevicePath = deviceId,
            FriendlyName = friendlyName,
            ModelName = model,
            AdapterId = adapterId,
            TargetId = targetId,
            OutputTechnology = outputTech
        };
    }

    public static MonitorInfo CreateMonitorFromEnumDevices(
        string subName, string subString, string subId,
        string deviceId, string deviceString,
        ulong adapterId, uint targetId)
    {
        var name = subString;
        var model = ExtractModelFromDeviceId(subId);
        if (string.IsNullOrWhiteSpace(name))
            name = model;
        if (string.IsNullOrWhiteSpace(name))
            name = deviceString;
        if (string.IsNullOrWhiteSpace(name))
            name = $"Monitor ({deviceId})";

        var path = subId;
        if (string.IsNullOrWhiteSpace(path))
            path = deviceId;

        return new MonitorInfo
        {
            DevicePath = path,
            FriendlyName = name,
            ModelName = model,
            AdapterId = adapterId,
            TargetId = targetId
        };
    }

    public static MonitorInfo CreateMonitorFromEnumMonitors(
        string monitorDeviceName,
        (string deviceString, string deviceId, string parentName) deviceInfo,
        ulong adapterId, uint targetId)
    {
        var (deviceString, deviceId, parentName) = deviceInfo;
        string friendlyName = string.Empty;
        string model = string.Empty;

        if (!string.IsNullOrWhiteSpace(deviceString))
        {
            friendlyName = deviceString == parentName
                ? monitorDeviceName
                : deviceString;
            model = ExtractModelFromDeviceId(deviceId);
        }

        return new MonitorInfo
        {
            DevicePath = monitorDeviceName,
            DisplayName = monitorDeviceName,
            FriendlyName = friendlyName,
            ModelName = model,
            AdapterId = adapterId,
            TargetId = targetId
        };
    }
}
