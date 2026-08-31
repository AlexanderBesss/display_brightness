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
    public const uint DISPLAYCONFIG_SET_MONITOR_ATTRIBUTE = 10;

    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    public const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;
    public const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;

    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint IOCTL_VIDEO_SET_DISPLAY_PIN_BRIGHTNESS = 0x001C0314;
    public const uint IOCTL_VIDEO_QUERY_DISPLAY_PIN_BRIGHTNESS = 0x001C0310;

    // SetupAPI
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA_W DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SP_DEVICE_INTERFACE_DETAIL_DATA_W
    {
        public uint cbSize;
        public ushort DevicePath; // First char of the path - struct is variable length
    }

    // For 64-bit, we need a different struct layout
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    public struct SP_DEVICE_INTERFACE_DETAIL_DATA_W_64
    {
        [FieldOffset(0)]
        public uint cbSize;
        [FieldOffset(8)]
        public ushort DevicePath;
    }

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
    public static extern uint DisplayConfigSetDeviceInfo(IntPtr deviceInfo);

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
        ref MONITORINFO lpmi);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    public static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegOpenKeyExW(
        uint hKey,
        string lpSubKey,
        int ulOptions,
        uint samDesired,
        out IntPtr phkResult);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegEnumKeyExW(
        uint hKey,
        uint dwIndex,
        string lpName,
        ref int lpcchName,
        IntPtr lpReserved,
        IntPtr lpClass,
        ref int lpcchClass,
        IntPtr lpftLastWriteTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegQueryValueExW(
        uint hKey,
        string lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        IntPtr lpData,
        ref int lpcbData);

    [DllImport("kernel32.dll")]
    public static extern uint RegCloseKey(uint hKey);

    public const uint HKEY_LOCAL_MACHINE = 0x80000002;
    public const uint KEY_READ = 0x20019;

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

    // Windows 10 21H2+ version with 128-bit adapter IDs
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2_PATH_SOURCE_INFO
    {
        public ulong adapterId;
        public ulong adapterId2;
        public uint id;
        public uint statusFlags;
        public int coordinateX;
        public int coordinateY;
        public uint pixelAspectRatioX;
        public uint pixelAspectRatioY;
        public uint forcedPixelAspectRatioX;
        public uint forcedPixelAspectRatioY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2_PATH_TARGET_INFO
    {
        public ulong adapterId;
        public ulong adapterId2;
        public uint id;
        public uint outputTechnology;
        public uint outputId;
        public uint targetMode;
        public uint targetPrimary;
        public IntPtr targetSpecificId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2_PATH_INFO
    {
        public DISPLAYCONFIG_2_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_2_PATH_TARGET_INFO targetInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SET_MONITOR_ATTRIBUTE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint monitorFrequency;
        public uint monitorAttribute;
        public uint monitorNewAttribute;
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
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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
        return outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNEL
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI
            || outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_VGA;
    }

    public static string CharArrayToString(char[] arr)
    {
        if (arr == null) return string.Empty;
        var end = Array.FindIndex(arr, c => c == '\0');
        if (end < 0) end = arr.Length;
        return new string(arr, 0, end).Trim();
    }

    public static bool? QueryDisplayConfigPaths(Func<IntPtr, uint, bool> pathProcessor)
    {
        var result = GetDisplayConfigBufferSizes(
            QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
        if (result != S_OK || numPaths == 0)
            return null;

        int pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        var pathPtr = Marshal.AllocHGlobal(pathSize * (int)numPaths);
        const int modeSize = 64;
        var modePtr = Marshal.AllocHGlobal(modeSize * (int)Math.Max(numModes, 1));

        try
        {
            result = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref numPaths, pathPtr,
                ref numModes, modePtr, IntPtr.Zero);
            if (result != S_OK)
                return null;

            return pathProcessor(pathPtr, numPaths);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            Marshal.FreeHGlobal(modePtr);
        }
    }

    public static bool? QueryDisplayConfigPaths2(Func<IntPtr, uint, bool> pathProcessor)
    {
        var result = GetDisplayConfigBufferSizes(
            QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
        if (result != S_OK || numPaths == 0)
            return null;

        int pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        var pathPtr = Marshal.AllocHGlobal(pathSize * (int)numPaths);
        const int modeSize = 64;
        var modePtr = Marshal.AllocHGlobal(modeSize * (int)Math.Max(numModes, 1));

        try
        {
            result = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref numPaths, pathPtr,
                ref numModes, modePtr, IntPtr.Zero);
            if (result != S_OK)
                return null;

            return pathProcessor(pathPtr, numPaths);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            Marshal.FreeHGlobal(modePtr);
        }
    }

    // Read target info from raw bytes at known offsets
    public static (ulong adapterId, uint targetId, uint outputTechnology) ReadTargetInfoRaw(IntPtr pathPtr, int pathIndex, int pathSize)
    {
        var basePtr = pathPtr + pathIndex * pathSize;
        // Target info begins after the 20-byte DISPLAYCONFIG_PATH_SOURCE_INFO.
        ulong adapterId = (ulong)Marshal.ReadInt64(basePtr + 20);
        uint targetId = (uint)Marshal.ReadInt32(basePtr + 28);
        uint outputTechnology = (uint)Marshal.ReadInt32(basePtr + 36);
        return (adapterId, targetId, outputTechnology);
    }
}
