using System.Runtime.InteropServices;

namespace DisplayBrightness.Services;

public static class DisplayInterop
{
    public const uint S_OK = 0;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_VGA = 0;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI = 4;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL = 12;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 16;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL = 18;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    public const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;
    public const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        IntPtr pathInfoArray,
        ref uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        IntPtr callback);

    [DllImport("user32.dll")]
    public static extern uint DisplayConfigGetDeviceInfo(IntPtr deviceInfo);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICEW lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfoW(
        IntPtr hMonitor,
        ref MONITORINFOEX lpmi);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorBrightness(
        IntPtr hMonitor,
        out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness,
        out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMonitorBrightness(
        IntPtr hMonitor,
        uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor,
        byte bVCPCode,
        out uint pvct,
        out uint pdwCurrentValue,
        out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVCPFeature(
        IntPtr hMonitor,
        byte bVCPCode,
        uint dwNewValue);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hDC,
        ref RECT lprc,
        IntPtr lParam);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        [FieldOffset(0)]
        public ulong adapterId;
        [FieldOffset(8)]
        public uint id;
        [FieldOffset(12)]
        public uint modeInfoIdx;
        [FieldOffset(16)]
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        [FieldOffset(0)]
        public ulong adapterId;
        [FieldOffset(8)]
        public uint id;
        [FieldOffset(12)]
        public uint modeInfoIdx;
        [FieldOffset(16)]
        public uint outputTechnology;
        [FieldOffset(20)]
        public uint rotation;
        [FieldOffset(24)]
        public uint scaling;
        [FieldOffset(28)]
        public uint refreshRateNumerator;
        [FieldOffset(32)]
        public uint refreshRateDenominator;
        [FieldOffset(36)]
        public uint scanLineOrdering;
        [FieldOffset(40)]
        public int targetAvailable;
        [FieldOffset(44)]
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        [FieldOffset(0)]
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        [FieldOffset(20)]
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        [FieldOffset(68)]
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        [FieldOffset(0)]
        public uint type;
        [FieldOffset(4)]
        public uint size;
        [FieldOffset(8)]
        public ulong adapterId;
        [FieldOffset(16)]
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct DISPLAYCONFIG_TARGET_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public char[] targetProductDescription;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] targetMonitoredDeviceId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct DISPLAYCONFIG_SOURCE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICEW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public static bool IsExternalTechnology(uint outputTechnology)
    {
        return outputTechnology is
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI or
            DISPLAYCONFIG_OUTPUT_TECHNOLOGY_VGA;
    }

    public static string CharArrayToString(char[]? characters)
    {
        if (characters == null)
            return string.Empty;

        var end = Array.IndexOf(characters, '\0');
        if (end < 0)
            end = characters.Length;

        return new string(characters, 0, end).Trim();
    }

    public static void QueryDisplayConfigPaths(Action<IntPtr, uint> pathProcessor)
    {
        var result = GetDisplayConfigBufferSizes(
            QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
        if (result != S_OK || numPaths == 0)
            return;

        var pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        var pathPtr = Marshal.AllocHGlobal(pathSize * (int)numPaths);
        const int modeSize = 64;
        var modePtr = Marshal.AllocHGlobal(modeSize * (int)Math.Max(numModes, 1));

        try
        {
            result = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref numPaths, pathPtr,
                ref numModes, modePtr, IntPtr.Zero);
            if (result != S_OK)
                return;

            pathProcessor(pathPtr, numPaths);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            Marshal.FreeHGlobal(modePtr);
        }
    }

    // Read target info from raw bytes at known offsets
    public static (ulong adapterId, uint targetId, uint outputTechnology)
        ReadTargetInfoRaw(IntPtr pathPtr, int pathIndex, int pathSize)
    {
        var basePtr = pathPtr + pathIndex * pathSize;
        // Target info begins after the 20-byte DISPLAYCONFIG_PATH_SOURCE_INFO.
        var adapterId = (ulong)Marshal.ReadInt64(basePtr + 20);
        var targetId = (uint)Marshal.ReadInt32(basePtr + 28);
        var outputTechnology = (uint)Marshal.ReadInt32(basePtr + 36);
        return (adapterId, targetId, outputTechnology);
    }
}
