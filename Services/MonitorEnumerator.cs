using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DisplayBrightness.Models;
using DisplayBrightness.Utilities;

namespace DisplayBrightness.Services;

public class MonitorEnumerator
{
    private const uint MaxDisplayDevices = 16;

    // EnumDisplayDevices flag: report each child's DeviceID as its device
    // interface path instead of a raw instance id.
    private const uint EddGetDeviceInterfaceName = 1;

    // A monitor that fails to parse vanishes from the UI with no other trace, so
    // the first sighting of each broken identity is logged once per process.
    // Enumeration runs on a timer, so logging every sighting would bury the log.
    private const int MaxLoggedParseFailures = 32;

    private static readonly ConcurrentDictionary<string, FailureStreak> StrategyFailures = new();
    private static readonly HashSet<string> LoggedParseFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoggedParseFailuresGate = new();

    public List<MonitorInfo> GetExternalMonitors()
    {
        var monitors = Enumerate();
        MonitorIdentityReader.PopulateSerialNumbers(monitors);

        if (monitors.Count == 0)
            return monitors;

        var external = monitors.Where(monitor =>
            MonitorInfoParser.IsExternalMonitorByDeviceId(monitor.DevicePath) ||
            DisplayInterop.IsExternalTechnology(monitor.OutputTechnology)).ToList();

        return external.Count > 0 ? external : monitors;
    }

    private static List<MonitorInfo> Enumerate()
    {
        if (TryEnumerate("DisplayConfig", TryGetMonitorsFromDisplayConfig, out var monitors))
            return monitors;

        if (TryEnumerate("EnumDisplayMonitors", GetMonitorsViaEnumDisplayMonitors, out monitors))
            return monitors;

        return TryEnumerate("EnumDisplayDevices", GetMonitorsViaEnumDisplayDevices, out monitors)
            ? monitors
            : new List<MonitorInfo>();
    }

    private static bool TryEnumerate(
        string strategy,
        Func<List<MonitorInfo>> enumerate,
        out List<MonitorInfo> monitors)
    {
        FailureStreak failures = StrategyFailures.GetOrAdd(
            strategy,
            static _ => new FailureStreak());

        try
        {
            monitors = enumerate();
            failures.Cleared();
            return monitors.Count > 0;
        }
        catch (Exception ex)
        {
            // The fallback chain retries on every refresh cycle: report the first
            // failure of a streak, not one line per cycle.
            failures.Report(
                $"monitor enumeration strategy {strategy} failed: {ex.GetType().Name}: {ex.Message}");
            monitors = new List<MonitorInfo>();
            return false;
        }
    }

    /// <summary>
    /// Logs one line per broken monitor identity per process, so a driver that
    /// consistently fails to parse cannot write a line per enumeration cycle.
    /// </summary>
    private static void ReportParseFailure(string site, string identity)
    {
        lock (LoggedParseFailuresGate)
        {
            if (LoggedParseFailures.Count >= MaxLoggedParseFailures ||
                !LoggedParseFailures.Add($"{site}|{identity}"))
            {
                return;
            }
        }

        AppLog.Warn($"monitor enumeration dropped a monitor at {site}: {identity}");
    }

    private static List<MonitorInfo> GetMonitorsViaEnumDisplayMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceMap = BuildDeviceNameMap();

        bool EnumCallback(IntPtr hMonitor, IntPtr hDC,
            ref DisplayInterop.RECT lprc, IntPtr lParam)
        {
            string key = $"{lprc.Left},{lprc.Top},{lprc.Right},{lprc.Bottom}";
            string monitorDeviceName = string.Empty;
            try
            {
                var miex = new DisplayInterop.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<DisplayInterop.MONITORINFOEX>()
                };
                if (!DisplayInterop.GetMonitorInfoW(hMonitor, ref miex))
                    return true;

                if (!seen.Add(key))
                    return true;

                monitorDeviceName = miex.szDevice.TrimEnd('\0');
                var info = deviceMap.TryGetValue(
                    monitorDeviceName,
                    out MonitorDeviceDescription? description)
                    ? description
                    : new MonitorDeviceDescription(
                        string.Empty,
                        string.Empty,
                        monitorDeviceName);
                var monitor = MonitorInfoParser.CreateMonitorFromEnumMonitors(
                    monitorDeviceName, info);
                monitor.RefreshRateHz = DisplayInterop.GetDeviceRefreshRateHz(
                    monitorDeviceName);
                monitors.Add(monitor);

                return true;
            }
            catch (Exception ex)
            {
                ReportParseFailure(
                    $"EnumDisplayMonitors [{key} {monitorDeviceName}]",
                    $"{ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }

        var callback = new DisplayInterop.MonitorEnumProc(EnumCallback);
        DisplayInterop.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return monitors;
    }

    /// <summary>
    /// Maps each GDI device name (<c>\\.\DISPLAYn</c>) to the monitor identity
    /// behind it, so an EnumDisplayMonitors hit can be matched to its instance id.
    /// </summary>
    private static Dictionary<string, MonitorDeviceDescription> BuildDeviceNameMap()
    {
        var map = new Dictionary<string, MonitorDeviceDescription>(
            StringComparer.OrdinalIgnoreCase);

        // The standard layout: enumerate GPUs, then the monitors under each GPU.
        AddMonitorsAttachedToAdapters(map);

        // Some drivers expose the monitors as first-level devices instead.
        if (map.Count == 0)
            AddFirstLevelMonitors(map);

        return map;
    }

    private static void AddMonitorsAttachedToAdapters(
        Dictionary<string, MonitorDeviceDescription> map)
    {
        for (uint adapterIndex = 0;
             adapterIndex < MaxDisplayDevices &&
             TryGetDisplayDevice(null, adapterIndex, 0, out var adapter);
             adapterIndex++)
        {
            string adapterName = adapter.DeviceName.TrimEnd('\0');
            for (uint monitorIndex = 0;
                 monitorIndex < MaxDisplayDevices &&
                 TryGetDisplayDevice(adapterName, monitorIndex, EddGetDeviceInterfaceName, out var monitorDevice);
                 monitorIndex++)
            {
                if (!IsActiveMonitorDevice(monitorDevice))
                    continue;

                string monitorName = monitorDevice.DeviceName.TrimEnd('\0');
                var description = new MonitorDeviceDescription(
                    monitorDevice.DeviceString.TrimEnd('\0'),
                    monitorDevice.DeviceID.TrimEnd('\0'),
                    adapterName);
                map[monitorName] = description;
                map.TryAdd(adapterName, description);
            }
        }
    }

    private static void AddFirstLevelMonitors(
        Dictionary<string, MonitorDeviceDescription> map)
    {
        for (uint deviceIndex = 0;
             deviceIndex < MaxDisplayDevices &&
             TryGetDisplayDevice(null, deviceIndex, 0, out var device);
             deviceIndex++)
        {
            if ((device.StateFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;

            string deviceName = device.DeviceName.TrimEnd('\0');
            string monitorId = device.DeviceID.TrimEnd('\0');
            string monitorString = device.DeviceString.TrimEnd('\0');

            // Prefer the child device's identity when the adapter exposes one.
            foreach (DisplayInterop.DISPLAY_DEVICEW child in
                EnumerateAttachedMonitorDevices(deviceName))
            {
                string childId = child.DeviceID.TrimEnd('\0');
                if (childId.Length == 0)
                    continue;

                monitorId = childId;
                string childString = child.DeviceString.TrimEnd('\0');
                if (childString.Length > 0)
                    monitorString = childString;
            }

            map[deviceName] = new MonitorDeviceDescription(
                monitorString,
                monitorId,
                deviceName);
        }
    }

    private static IEnumerable<DisplayInterop.DISPLAY_DEVICEW> EnumerateAttachedMonitorDevices(
        string adapterName)
    {
        for (uint index = 0;
             index < MaxDisplayDevices &&
             TryGetDisplayDevice(adapterName, index, EddGetDeviceInterfaceName, out var device);
             index++)
        {
            yield return device;
        }
    }

    private static List<MonitorInfo> TryGetMonitorsFromDisplayConfig()
    {
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<string>();

        DisplayInterop.QueryDisplayConfigPaths((pathPtr, numPaths) =>
        {
            for (uint i = 0; i < numPaths; i++)
            {
                try
                {
                    var (adapterId, targetId, outputTech,
                        refreshNumerator, refreshDenominator) =
                        DisplayInterop.ReadTargetInfoRaw(
                            pathPtr, (int)i,
                            Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_PATH_INFO>());

                    // Keep every currently supported connector type, including
                    // USB-C/indirect wired displays and internal panels.
                    if (outputTech >
                            DisplayInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL &&
                        outputTech !=
                            DisplayInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL)
                    {
                        continue;
                    }

                    var key = $"{adapterId}:{targetId}";
                    if (!seen.Add(key))
                        continue;

                    var monitor = GetMonitorFromTarget(
                        adapterId, targetId,
                        key, outputTech);
                    if (monitor != null)
                    {
                        monitor.DisplayName = GetSourceDisplayName(
                            pathPtr, (int)i);
                        monitor.RefreshRateHz = refreshDenominator > 0
                            ? (double)refreshNumerator / refreshDenominator
                            : 0;
                        monitors.Add(monitor);
                    }
                }
                catch (Exception ex)
                {
                    ReportParseFailure(
                        $"DisplayConfig [path {i}]",
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        return monitors;
    }

    private static MonitorInfo? GetMonitorFromTarget(
        ulong adapterId, uint targetId, string fallbackKey, uint outputTech)
    {
        var ptr = Marshal.AllocHGlobal(
            Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_TARGET_NAME>());

        try
        {
            var targetName = CreateTargetNameStruct(adapterId, targetId);

            Marshal.StructureToPtr(targetName, ptr, false);
            var result = DisplayInterop.DisplayConfigGetDeviceInfo(ptr);

            if (result != DisplayInterop.S_OK)
                return null;

            targetName = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_TARGET_NAME>(ptr);
            return MonitorInfoParser.CreateMonitorFromTargetName(
                targetName, fallbackKey, outputTech);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string GetSourceDisplayName(IntPtr pathPtr, int pathIndex)
    {
        var pathSize = Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_PATH_INFO>();
        var path = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_PATH_INFO>(
            pathPtr + pathIndex * pathSize);
        var sourceName = new DisplayInterop.DISPLAYCONFIG_SOURCE_NAME
        {
            header = new DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_SOURCE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            },
            viewGdiDeviceName = string.Empty
        };

        var pointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_SOURCE_NAME>());
        try
        {
            Marshal.StructureToPtr(sourceName, pointer, false);
            if (DisplayInterop.DisplayConfigGetDeviceInfo(pointer) != DisplayInterop.S_OK)
                return string.Empty;

            sourceName = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_SOURCE_NAME>(
                pointer);
            return sourceName.viewGdiDeviceName?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static List<MonitorInfo> GetMonitorsViaEnumDisplayDevices()
    {
        var monitors = new List<MonitorInfo>();
        var adapters = new List<DisplayInterop.DISPLAY_DEVICEW>();

        for (uint index = 0;
             index < MaxDisplayDevices && TryGetDisplayDevice(null, index, 0, out var adapter);
             index++)
        {
            if (IsDesktopDevice(adapter))
                adapters.Add(adapter);
        }

        foreach (DisplayInterop.DISPLAY_DEVICEW adapter in adapters)
        {
            var adapterName = adapter.DeviceName.TrimEnd('\0');
            var adapterString = adapter.DeviceString.TrimEnd('\0');
            var adapterId = adapter.DeviceID.TrimEnd('\0');

            foreach (DisplayInterop.DISPLAY_DEVICEW monitorDevice in
                EnumerateAttachedMonitorDevices(adapterName))
            {
                if (!IsDesktopDevice(monitorDevice))
                    continue;

                var monitor = MonitorInfoParser.CreateMonitorFromEnumDevices(
                    monitorDevice.DeviceString.TrimEnd('\0'),
                    monitorDevice.DeviceID.TrimEnd('\0'),
                    adapterId,
                    adapterString);
                monitor.DisplayName = adapterName;
                monitor.RefreshRateHz = DisplayInterop.GetDeviceRefreshRateHz(
                    adapterName);
                monitors.Add(monitor);
            }
        }

        return monitors;
    }

    private static bool IsActiveMonitorDevice(DisplayInterop.DISPLAY_DEVICEW device) =>
        (device.StateFlags & DisplayInterop.DISPLAY_DEVICE_ACTIVE) != 0 &&
        (device.StateFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) == 0;

    private static bool IsDesktopDevice(DisplayInterop.DISPLAY_DEVICEW device) =>
        (device.StateFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 &&
        (device.StateFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) == 0;

    private static bool TryGetDisplayDevice(
        string? parentName,
        uint index,
        uint flags,
        out DisplayInterop.DISPLAY_DEVICEW device)
    {
        device = CreateDisplayDevice();
        return DisplayInterop.EnumDisplayDevicesW(parentName, index, ref device, flags);
    }

    private static DisplayInterop.DISPLAY_DEVICEW CreateDisplayDevice()
    {
        return new DisplayInterop.DISPLAY_DEVICEW
        {
            cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>()
        };
    }

    internal static DisplayInterop.DISPLAYCONFIG_TARGET_NAME CreateTargetNameStruct(
        ulong adapterId, uint id)
    {
        return new DisplayInterop.DISPLAYCONFIG_TARGET_NAME
        {
            header = new DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayInterop.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_TARGET_NAME>(),
                adapterId = adapterId,
                id = id
            },
            targetMonitoredDeviceId = new char[128],
            targetProductDescription = new char[64]
        };
    }
}
