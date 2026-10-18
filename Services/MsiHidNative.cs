using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DisplayBrightness.Services;

/// <summary>
/// Raw HID plumbing: interface enumeration by VID/PID, opening a device, report sizes.
/// No MSI protocol knowledge lives here.
/// </summary>
internal static class MsiHidNative
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    public static List<string> EnumerateDevicePaths(ushort vendorId, ushort[] productIds) =>
        EnumerateInterfacePaths(vendorId, productIds, requireProduct: true);

    public static List<string> EnumerateVendorPaths(ushort vendorId) =>
        EnumerateInterfacePaths(vendorId, [], requireProduct: false);

    private static List<string> EnumerateInterfacePaths(
        ushort vendorId,
        ushort[] productIds,
        bool requireProduct)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = SetupDiGetClassDevsW(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
            return [];

        var paths = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    break;
                }

                if (!TryGetInterfacePath(deviceInfoSet, ref interfaceData, out string path))
                    continue;

                string vendorToken = $"vid_{vendorId:x4}";
                if (!path.Contains(vendorToken, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (requireProduct &&
                    !productIds.Any(productId =>
                        path.Contains($"pid_{productId:x4}", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                paths.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return paths;
    }

    /// <summary>SetupDi is called twice: the first call learns the detail-buffer size.</summary>
    private static bool TryGetInterfacePath(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        out string path)
    {
        path = string.Empty;
        SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);
        if (requiredSize == 0)
            return false;

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // The first field is a DWORD whose size depends on the pointer width.
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(
                    deviceInfoSet,
                    ref interfaceData,
                    detailBuffer,
                    requiredSize,
                    out _,
                    IntPtr.Zero))
            {
                return false;
            }

            path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(path);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    public static SafeFileHandle OpenDevice(string path) =>
        CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

    public static bool TryGetReportCapabilities(
        SafeFileHandle handle,
        out HidReportCapabilities? capabilities)
    {
        capabilities = null;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData))
            return false;

        try
        {
            int result = HidP_GetCaps(preparsedData, out HidpCaps caps);
            if (result < 0)
                return false;

            capabilities = new HidReportCapabilities(
                caps.UsagePage,
                caps.Usage,
                caps.InputReportByteLength,
                caps.OutputReportByteLength,
                caps.FeatureReportByteLength);
            return true;
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(
        SafeFileHandle hidDeviceObject,
        out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
