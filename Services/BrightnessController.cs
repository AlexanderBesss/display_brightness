using System.Runtime.InteropServices;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

/// <summary>
/// Controls external monitors through the Windows physical-monitor APIs.
/// Those APIs carry MCCS/DDC/CI commands over the display connection; DDC/CI
/// monitors are not generic HID devices and cannot be controlled by writing an
/// arbitrary HID output report.
/// </summary>
public class BrightnessController : IBrightnessController
{
    private const byte BrightnessVcpCode = 0x10;

    public int? GetBrightness(MonitorInfo monitor)
    {
        int? brightness = null;
        VisitPhysicalMonitors(
            monitor,
            physicalMonitor =>
            {
                brightness = GetPhysicalMonitorBrightness(physicalMonitor);
                return brightness.HasValue;
            },
            stopAfterSuccess: true);

        return brightness;
    }

    public bool SetBrightness(MonitorInfo monitor, int brightness)
    {
        var clampedBrightness = Math.Clamp(brightness, 0, 100);
        return VisitPhysicalMonitors(
            monitor,
            physicalMonitor => SetPhysicalMonitorBrightness(
                physicalMonitor, clampedBrightness),
            stopAfterSuccess: false);
    }

    private static bool VisitPhysicalMonitors(
        MonitorInfo monitor,
        Func<DisplayInterop.PHYSICAL_MONITOR, bool> visitor,
        bool stopAfterSuccess)
    {
        var anySuccess = false;

        foreach (var logicalMonitor in FindLogicalMonitors(monitor))
        {
            if (!DisplayInterop.GetNumberOfPhysicalMonitorsFromHMONITOR(
                    logicalMonitor.Handle, out var count) || count == 0)
            {
                continue;
            }

            var physicalMonitors = new DisplayInterop.PHYSICAL_MONITOR[count];
            if (!DisplayInterop.GetPhysicalMonitorsFromHMONITOR(
                    logicalMonitor.Handle, count, physicalMonitors))
            {
                continue;
            }

            try
            {
                foreach (var physicalMonitor in physicalMonitors)
                {
                    if (!visitor(physicalMonitor))
                        continue;

                    anySuccess = true;
                    if (stopAfterSuccess)
                        return true;
                }
            }
            finally
            {
                DisplayInterop.DestroyPhysicalMonitors(count, physicalMonitors);
            }
        }

        return anySuccess;
    }

    private static int? GetPhysicalMonitorBrightness(
        DisplayInterop.PHYSICAL_MONITOR monitor)
    {
        if (DisplayInterop.GetMonitorBrightness(
                monitor.hPhysicalMonitor, out var minimum,
                out var current, out var maximum) && maximum > minimum)
        {
            return ScaleToPercentage(current, minimum, maximum);
        }

        if (DisplayInterop.GetVCPFeatureAndVCPFeatureReply(
                monitor.hPhysicalMonitor, BrightnessVcpCode,
                out _, out var currentVcpValue, out var maximumVcpValue) &&
            maximumVcpValue > 0)
        {
            return ScaleToPercentage(currentVcpValue, 0, maximumVcpValue);
        }

        return null;
    }

    private static bool SetPhysicalMonitorBrightness(
        DisplayInterop.PHYSICAL_MONITOR monitor,
        int percentage)
    {
        if (DisplayInterop.GetMonitorBrightness(
                monitor.hPhysicalMonitor, out var minimum, out _, out var maximum) &&
            maximum >= minimum)
        {
            var value = ScalePercentage(percentage, minimum, maximum);
            if (DisplayInterop.SetMonitorBrightness(monitor.hPhysicalMonitor, value))
                return true;
        }

        uint maximumVcpValue = 100;
        if (DisplayInterop.GetVCPFeatureAndVCPFeatureReply(
                monitor.hPhysicalMonitor, BrightnessVcpCode,
                out _, out _, out var reportedMaximum) && reportedMaximum > 0)
        {
            maximumVcpValue = reportedMaximum;
        }

        var vcpValue = ScalePercentage(percentage, 0, maximumVcpValue);
        if (DisplayInterop.SetVCPFeature(
                monitor.hPhysicalMonitor, BrightnessVcpCode, vcpValue))
            return true;

        return false;
    }

    internal static uint ScalePercentage(int percentage, uint minimum, uint maximum)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        if (maximum <= minimum)
            return minimum;

        var range = maximum - minimum;
        return minimum + (uint)Math.Round(
            range * (percentage / 100d), MidpointRounding.AwayFromZero);
    }

    internal static int ScaleToPercentage(uint value, uint minimum, uint maximum)
    {
        if (maximum <= minimum)
            return 0;

        var clamped = Math.Clamp(value, minimum, maximum);
        return (int)Math.Round(
            (clamped - minimum) * 100d / (maximum - minimum),
            MidpointRounding.AwayFromZero);
    }

    private static List<LogicalMonitor> FindLogicalMonitors(MonitorInfo monitor)
    {
        var all = new List<LogicalMonitor>();
        var exactMatches = new List<LogicalMonitor>();

        bool EnumCallback(IntPtr hMonitor, IntPtr hdcMonitor,
            ref DisplayInterop.RECT monitorRect, IntPtr data)
        {
            var info = new DisplayInterop.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<DisplayInterop.MONITORINFOEX>()
            };

            if (!DisplayInterop.GetMonitorInfoW(hMonitor, ref info))
                return true;

            var deviceName = info.szDevice?.TrimEnd('\0') ?? string.Empty;
            var item = new LogicalMonitor(hMonitor, deviceName);
            all.Add(item);

            if (string.Equals(deviceName, monitor.DisplayName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deviceName, monitor.DevicePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                exactMatches.Add(item);
            }

            return true;
        }

        var callback = new DisplayInterop.MonitorEnumProc(EnumCallback);
        DisplayInterop.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (exactMatches.Count > 0)
            return exactMatches;

        // DisplayConfig-based monitor records carry a monitor device ID rather
        // than a GDI display name. Match that ID to each active display adapter.
        foreach (var item in all)
        {
            if (LogicalDisplayContainsMonitor(item.DeviceName, monitor.DevicePath))
                exactMatches.Add(item);
        }

        return exactMatches;
    }

    private static bool LogicalDisplayContainsMonitor(
        string logicalDisplayName, string requestedDeviceId)
    {
        if (string.IsNullOrWhiteSpace(requestedDeviceId))
            return false;

        const uint EddiGetDeviceInterfaceName = 0x00000001;
        var requested = NormalizeMonitorId(requestedDeviceId);

        for (uint index = 0; index < 16; index++)
        {
            var device = new DisplayInterop.DISPLAY_DEVICEW
            {
                cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>()
            };

            if (!DisplayInterop.EnumDisplayDevicesW(
                    logicalDisplayName, index, ref device,
                    EddiGetDeviceInterfaceName))
            {
                break;
            }

            var candidate = NormalizeMonitorId(device.DeviceID);
            if (candidate.Length > 0 &&
                (string.Equals(candidate, requested,
                     StringComparison.OrdinalIgnoreCase) ||
                 candidate.Contains(requested,
                     StringComparison.OrdinalIgnoreCase) ||
                 requested.Contains(candidate,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeMonitorId(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('#', '\\')
            .TrimEnd('\0')
            .Trim();
    }

    private sealed record LogicalMonitor(IntPtr Handle, string DeviceName);
}
