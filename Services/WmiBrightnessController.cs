using System.Management;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public class WmiBrightnessController : IBrightnessController
{
    public int? GetBrightness(MonitorInfo monitor)
    {
        try
        {
            var scope = new ManagementScope(@"\root\wmi");
            scope.Connect();
            return ReadCurrentBrightness(
                scope, GetWmiInstancePrefix(monitor.DevicePath));
        }
        catch
        {
            return null;
        }
    }

    public bool SetBrightness(MonitorInfo monitor, int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        var instancePrefix = GetWmiInstancePrefix(monitor.DevicePath);

        try
        {
            var scope = new ManagementScope(@"\root\wmi");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"));
            using var results = searcher.Get();

            var methods = results.Cast<ManagementObject>().ToList();
            var target = methods.FirstOrDefault(method =>
                InstanceMatches(method, instancePrefix));

            // Some display drivers omit enough of the instance identifier to make
            // an exact match impossible. A single active laptop panel is still an
            // unambiguous target.
            target ??= methods.Count == 1 ? methods[0] : null;
            if (target == null)
                return false;

            using (target)
            using (var input = target.GetMethodParameters("WmiSetBrightness"))
            {
                input["Timeout"] = 0u;
                input["Brightness"] = (byte)brightness;

                // Several laptop drivers return null even when the void WMI method
                // succeeds, so success is confirmed by reading the brightness back.
                using var ignoredResult = target.InvokeMethod(
                    "WmiSetBrightness", input, null);
            }

            var current = ReadCurrentBrightness(scope, instancePrefix);
            if (current.HasValue)
                return current.Value == brightness;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadCurrentBrightness(
        ManagementScope scope, string instancePrefix)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery(
                "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"));
        using var results = searcher.Get();

        var brightnessObjects = results.Cast<ManagementObject>().ToList();
        var target = brightnessObjects.FirstOrDefault(item =>
            InstanceMatches(item, instancePrefix));
        target ??= brightnessObjects.Count == 1 ? brightnessObjects[0] : null;

        if (target == null)
            return null;

        using (target)
        {
            return Convert.ToInt32(target["CurrentBrightness"]);
        }
    }

    private static bool InstanceMatches(
        ManagementBaseObject instance, string expectedPrefix)
    {
        if (string.IsNullOrEmpty(expectedPrefix))
            return false;

        var instanceName = Convert.ToString(instance["InstanceName"])
            ?? string.Empty;
        return instanceName.StartsWith(
            expectedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetWmiInstancePrefix(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return string.Empty;

        var normalized = devicePath
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('#', '\\')
            .TrimEnd('\0');
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3
            ? string.Join('\\', parts.Take(3))
            : normalized;
    }
}
