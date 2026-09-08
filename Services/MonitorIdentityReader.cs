using System.Management;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

internal static class MonitorIdentityReader
{
    public static void PopulateSerialNumbers(List<MonitorInfo> monitors)
    {
        var candidates = monitors.Where(m => OledCompatibilityRegistry.Find(m) != null).ToList();
        if (candidates.Count == 0)
            return;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT * FROM WmiMonitorID WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    string instance = Convert.ToString(item["InstanceName"]) ?? string.Empty;
                    if (item["SerialNumberID"] is not ushort[] serial)
                        continue;
                    foreach (var monitor in candidates)
                    {
                        if (InstanceMatches(monitor.DevicePath, instance))
                            monitor.SerialNumber = new string(serial.TakeWhile(c => c != 0)
                                .Select(c => (char)c).ToArray()).Trim();
                    }
                }
            }
        }
        catch
        {
            // Missing identity disables HID control; never guess a USB target.
        }
        foreach (var duplicate in candidates.Where(m => m.SerialNumber.Length > 0)
            .GroupBy(m => m.SerialNumber, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var monitor in duplicate)
                monitor.SerialNumber = string.Empty;
        }
    }

    internal static bool InstanceMatches(string devicePath, string instance)
    {
        string prefix = WmiBrightnessController.GetWmiInstancePrefix(devicePath);
        return prefix.StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(prefix, instance, StringComparison.OrdinalIgnoreCase) ||
             (instance.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) &&
              uint.TryParse(instance[(prefix.Length + 1)..], out _)));
    }
}
