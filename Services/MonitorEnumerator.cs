using System.Runtime.InteropServices;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public class MonitorEnumerator : IMonitorEnumerator
{
    public List<MonitorInfo> Enumerate()
    {
        try
        {
            var monitors = TryGetMonitorsFromDisplayConfig();
            if (monitors.Count > 0) return monitors;
        }
        catch
        {
        }

        try
        {
            var monitors = GetMonitorsViaEnumDisplayMonitors();
            if (monitors.Count > 0) return monitors;
        }
        catch
        {
        }

        try
        {
            return GetMonitorsViaEnumDisplayDevices();
        }
        catch
        {
            return new List<MonitorInfo>();
        }
    }

    public List<MonitorInfo> EnumerateSafe()
    {
        try
        {
            return Enumerate();
        }
        catch
        {
            return new List<MonitorInfo>();
        }
    }

    public List<MonitorInfo> GetExternalMonitors()
    {
        var monitors = EnumerateSafe();

        if (monitors.Count > 0)
        {
            var external = monitors.Where(m =>
                MonitorInfoParser.IsExternalMonitorByDeviceId(m.DevicePath) ||
                DisplayInterop.IsExternalTechnology(m.OutputTechnology)).ToList();
            if (external.Count > 0)
                return external;
        }

        return monitors;
    }

    private (ulong adapterId, uint targetId) TryGetAdapterTargetForMonitor(string monitorDeviceName)
    {
        (ulong adapterId, uint targetId) result = (0ul, 0u);
        try
        {
            DisplayInterop.QueryDisplayConfigPaths((pathPtr, numPaths) =>
            {
                try
                {
                    for (uint i = 0; i < numPaths; i++)
                    {
                        try
                        {
                            var path = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_PATH_INFO>(
                                pathPtr + (int)i * Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_PATH_INFO>());

                            var ptr = Marshal.AllocHGlobal(
                                Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_TARGET_NAME>());
                            try
                            {
                                var targetName = CreateTargetNameStruct(
                                    path.targetInfo.adapterId, path.targetInfo.id);

                                Marshal.StructureToPtr(targetName, ptr, false);
                                DisplayInterop.DisplayConfigGetDeviceInfo(ptr);
                                targetName = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_TARGET_NAME>(ptr);

                                var deviceId = DisplayInterop.CharArrayToString(targetName.targetMonitoredDeviceId);
                                var monitorName = DisplayInterop.CharArrayToString(
                                    targetName.targetProductDescription);

                                if (deviceId == monitorDeviceName ||
                                    monitorName == monitorDeviceName ||
                                    (deviceId.Length > 0 && deviceId.Contains(monitorDeviceName,
                                        StringComparison.OrdinalIgnoreCase)))
                                {
                                    result = (path.targetInfo.adapterId, path.targetInfo.id);
                                    return true;
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(ptr);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
                return true;
            });
        }
        catch
        {
        }

        return result;
    }

    private List<MonitorInfo> GetMonitorsViaEnumDisplayMonitors()
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
                if (seen.Contains(key))
                    return true;
                seen.Add(key);

                var monitorDeviceName = miex.szDevice.TrimEnd('\0');
                if (deviceMap.TryGetValue(monitorDeviceName, out var info))
                {
                    var monitor = MonitorInfoParser.CreateMonitorFromEnumMonitors(
                        monitorDeviceName, info, 0ul, 0u);
                    monitors.Add(monitor);
                }
                else
                {
                    var emptyInfo = (string.Empty, string.Empty, string.Empty);
                    var monitor = MonitorInfoParser.CreateMonitorFromEnumMonitors(
                        monitorDeviceName, emptyInfo, 0ul, 0u);
                    monitors.Add(monitor);
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        var proc = new DisplayInterop.MonitorEnumProc(EnumCallback);
        try
        {
            DisplayInterop.EnumDisplayMonitors(
                IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        }
        catch
        {
        }

        return monitors;
    }

    private Dictionary<string, (string deviceString, string deviceId, string deviceName)>
        BuildDeviceNameMap()
    {
        var map = new Dictionary<string, (string, string, string)>();

        // First, try the standard approach: enumerate GPUs, then their monitors
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DisplayInterop.DISPLAY_DEVICEW();
            dd.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

            if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                continue;

            var gpuName = dd.DeviceName.TrimEnd('\0');
            var gpuString = dd.DeviceString.TrimEnd('\0');
            var gpuId = dd.DeviceID.TrimEnd('\0');
            var gpuFlags = dd.StateFlags;
            var isMirroring = (gpuFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
            // Try to enumerate monitors attached to this device
            for (uint j = 0; j < 16; j++)
            {
                var dd2 = new DisplayInterop.DISPLAY_DEVICEW();
                dd2.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

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
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DisplayInterop.DISPLAY_DEVICEW();
                dd.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

                if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                    continue;

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
                for (uint j = 0; j < 16; j++)
                {
                    var dd2 = new DisplayInterop.DISPLAY_DEVICEW();
                    dd2.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

                    if (!DisplayInterop.EnumDisplayDevicesW(devName, j, ref dd2, 0x40)) // EDDI_GET_DEVICE_INTERFACE_NAME
                        continue;

                    var childName = dd2.DeviceName.TrimEnd('\0');
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

    private List<MonitorInfo> TryGetMonitorsFromDisplayConfig()
    {
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<string>();

        DisplayInterop.QueryDisplayConfigPaths((pathPtr, numPaths) =>
        {
            try
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
                        if (seen.Contains(key))
                            continue;
                        seen.Add(key);

                        var monitor = GetMonitorFromTarget(
                            adapterId, targetId,
                            key, outputTech);
                        if (monitor != null)
                        {
                            monitor.DisplayName = GetSourceDisplayName(
                                pathPtr, (int)i);
                            monitor.OutputTechnology = outputTech;
                            monitors.Add(monitor);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return true;
        });

        return monitors;
    }

    private List<MonitorInfo> TryGetMonitorsFromDisplayConfig2()
    {
        var monitors = new List<MonitorInfo>();
        var seen = new HashSet<string>();

        DisplayInterop.QueryDisplayConfigPaths2((pathPtr, numPaths) =>
        {
            try
            {
                for (uint i = 0; i < numPaths; i++)
                {
                    try
                    {
                        var path = Marshal.PtrToStructure<DisplayInterop.DISPLAYCONFIG_2_PATH_INFO>(
                            pathPtr + (int)i * Marshal.SizeOf<DisplayInterop.DISPLAYCONFIG_2_PATH_INFO>());

                        if (path.targetInfo.id == 0)
                            continue;

                        if (path.targetInfo.outputTechnology > 11)
                        {
                            continue;
                        }

                        var key = $"{path.targetInfo.adapterId}:{path.targetInfo.id}";
                        if (seen.Contains(key))
                            continue;
                        seen.Add(key);

                        var monitor = GetMonitorFromTarget(
                            path.targetInfo.adapterId, path.targetInfo.id,
                            key, path.targetInfo.outputTechnology);
                        if (monitor != null)
                        {
                            monitor.OutputTechnology = path.targetInfo.outputTechnology;
                            monitors.Add(monitor);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return true;
        });

        return monitors;
    }

    private MonitorInfo? GetMonitorFromTarget(
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
                targetName, adapterId, targetId, fallbackKey, outputTech);
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

    private List<MonitorInfo> GetMonitorsViaEnumDisplayDevices()
    {
        var monitors = new List<MonitorInfo>();
        var gpuDevices = new List<DisplayInterop.DISPLAY_DEVICEW>();

        for (uint i = 0; i < 16; i++)
        {
            var dd = new DisplayInterop.DISPLAY_DEVICEW();
            dd.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

            if (!DisplayInterop.EnumDisplayDevicesW(null, i, ref dd, 0))
                continue;

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

            for (uint j = 0; j < 16; j++)
            {
                var dd2 = new DisplayInterop.DISPLAY_DEVICEW();
                dd2.cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>();

                if (!DisplayInterop.EnumDisplayDevicesW(deviceName, j, ref dd2, 0))
                    break;

                var subString = dd2.DeviceString.TrimEnd('\0');
                var subId = dd2.DeviceID.TrimEnd('\0');

                if ((dd2.StateFlags & DisplayInterop.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                    continue;
                if ((dd2.StateFlags & DisplayInterop.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;

                var subName = dd2.DeviceName.TrimEnd('\0');
                var displayConfigIds = TryGetAdapterTargetForMonitor(subName);

                var monitor = MonitorInfoParser.CreateMonitorFromEnumDevices(
                    subName, subString, subId, deviceId, deviceString,
                    displayConfigIds.Item1, displayConfigIds.Item2);
                monitors.Add(monitor);
            }
        }

        return monitors;
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
