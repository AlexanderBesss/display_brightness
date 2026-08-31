using System.Runtime.InteropServices;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public class MonitorEnumerator
{
    private const uint MaxDisplayDevices = 16;

    public List<MonitorInfo> GetExternalMonitors()
    {
        var monitors = Enumerate();

        if (monitors.Count == 0)
            return monitors;

        var external = monitors.Where(monitor =>
            MonitorInfoParser.IsExternalMonitorByDeviceId(monitor.DevicePath) ||
            DisplayInterop.IsExternalTechnology(monitor.OutputTechnology)).ToList();

        return external.Count > 0 ? external : monitors;
    }

    private static List<MonitorInfo> Enumerate()
    {
        if (TryEnumerate(TryGetMonitorsFromDisplayConfig, out var monitors))
            return monitors;

        if (TryEnumerate(GetMonitorsViaEnumDisplayMonitors, out monitors))
            return monitors;

        return TryEnumerate(GetMonitorsViaEnumDisplayDevices, out monitors)
            ? monitors
            : new List<MonitorInfo>();
    }

    private static bool TryEnumerate(
        Func<List<MonitorInfo>> enumerate,
        out List<MonitorInfo> monitors)
    {
        try
        {
            monitors = enumerate();
            return monitors.Count > 0;
        }
        catch
        {
            monitors = new List<MonitorInfo>();
            return false;
        }
    }

    private static List<MonitorInfo> GetMonitorsViaEnumDisplayMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceMap = BuildDeviceNameMap();

        bool EnumCallback(IntPtr hMonitor, IntPtr hDC,
            ref DisplayInterop.RECT lprc, IntPtr lParam)
        {
            try
            {
                var miex = new DisplayInterop.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<DisplayInterop.MONITORINFOEX>()
                };
                if (!DisplayInterop.GetMonitorInfoW(hMonitor, ref miex))
                    return true;

                var key = $"{lprc.Left},{lprc.Top},{lprc.Right},{lprc.Bottom}";
                if (!seen.Add(key))
                    return true;

                var monitorDeviceName = miex.szDevice.TrimEnd('\0');
                deviceMap.TryGetValue(monitorDeviceName, out var info);
                monitors.Add(MonitorInfoParser.CreateMonitorFromEnumMonitors(
                    monitorDeviceName, info));

                return true;
            }
            catch
            {
                return true;
            }
        }

        var callback = new DisplayInterop.MonitorEnumProc(EnumCallback);
        DisplayInterop.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return monitors;
    }

    private static Dictionary<string, (string deviceString, string deviceId, string deviceName)>
        BuildDeviceNameMap()
    {
        var map = new Dictionary<string, (string, string, string)>(
            StringComparer.OrdinalIgnoreCase);

        // First, try the standard approach: enumerate GPUs, then their monitors
        for (uint i = 0; i < MaxDisplayDevices; i++)
        {
            var dd = CreateDisplayDevice();

            if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                break;

            var gpuName = dd.DeviceName.TrimEnd('\0');
            // Try to enumerate monitors attached to this device
            for (uint j = 0; j < MaxDisplayDevices; j++)
            {
                var dd2 = CreateDisplayDevice();

                if (!DisplayInterop.EnumDisplayDevicesW(gpuName, j, ref dd2, 0))
                    break;

                var subName = dd2.DeviceName.TrimEnd('\0');
                var subString = dd2.DeviceString.TrimEnd('\0');
                var subId = dd2.DeviceID.TrimEnd('\0');
                var subFlags = dd2.StateFlags;
                var subActive = (subFlags & DisplayInterop.DISPLAY_DEVICE_ACTIVE) != 0;
                var subMirroring = (subFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
                if (!subActive)
                    continue;
                if (subMirroring)
                    continue;

                map[subName] = (subString, subId, gpuName);
            }
        }

        // If no monitors found at second level, try treating first-level devices as monitors
        if (map.Count == 0)
        {
            for (uint i = 0; i < MaxDisplayDevices; i++)
            {
                var dd = CreateDisplayDevice();

                if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                    break;

                var devName = dd.DeviceName.TrimEnd('\0');
                var devString = dd.DeviceString.TrimEnd('\0');
                var devId = dd.DeviceID.TrimEnd('\0');
                var devFlags = dd.StateFlags;
                var isAttached = (devFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                if (!isAttached)
                    continue;

                // Try to get monitor info by enumerating children with EDDI_GET_DEVICE_INTERFACE_NAME
                var monitorId = devId;
                var monitorString = devString;

                // Check if this has child devices that are actual monitors
                for (uint j = 0; j < MaxDisplayDevices; j++)
                {
                    var dd2 = CreateDisplayDevice();

                    if (!DisplayInterop.EnumDisplayDevicesW(devName, j, ref dd2, 0x40)) // EDDI_GET_DEVICE_INTERFACE_NAME
                        continue;

                    var childString = dd2.DeviceString.TrimEnd('\0');
                    var childId = dd2.DeviceID.TrimEnd('\0');
                    if (!string.IsNullOrEmpty(childId))
                    {
                        monitorId = childId;
                        if (!string.IsNullOrEmpty(childString))
                            monitorString = childString;
                    }
                }

                map[devName] = (monitorString, monitorId, devName);
            }
        }

        return map;
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
                    var (adapterId, targetId, outputTech) = DisplayInterop.ReadTargetInfoRaw(
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
                        monitors.Add(monitor);
                    }
                }
                catch
                {
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
        var gpuDevices = new List<DisplayInterop.DISPLAY_DEVICEW>();

        for (uint i = 0; i < MaxDisplayDevices; i++)
        {
            var dd = CreateDisplayDevice();

            if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                break;

            if ((dd.StateFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;
            if ((dd.StateFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                continue;

            gpuDevices.Add(dd);
        }

        foreach (var gpu in gpuDevices)
        {
            var deviceName = gpu.DeviceName.TrimEnd('\0');
            var deviceString = gpu.DeviceString.TrimEnd('\0');
            var deviceId = gpu.DeviceID.TrimEnd('\0');

            for (uint j = 0; j < MaxDisplayDevices; j++)
            {
                var dd2 = CreateDisplayDevice();

                if (!DisplayInterop.EnumDisplayDevicesW(deviceName, j, ref dd2, 0))
                    break;

                var subString = dd2.DeviceString.TrimEnd('\0');
                var subId = dd2.DeviceID.TrimEnd('\0');

                if ((dd2.StateFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                    continue;
                if ((dd2.StateFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;

                var monitor = MonitorInfoParser.CreateMonitorFromEnumDevices(
                    subString, subId, deviceId, deviceString);
                monitors.Add(monitor);
            }
        }

        return monitors;
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
