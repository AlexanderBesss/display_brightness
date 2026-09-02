using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DisplayBrightness.Services;

internal enum HidOperationState
{
    Success,
    NotConnected,
    Busy,
    TimedOut,
    Failed
}

internal sealed record HidOperationResult(
    HidOperationState State,
    string Value,
    string Message)
{
    public static HidOperationResult Ok(string value = "") =>
        new(HidOperationState.Success, value, string.Empty);

    public static HidOperationResult Fail(string message) =>
        new(HidOperationState.Failed, string.Empty, message);

    public static HidOperationResult NotConnected() =>
        new(
            HidOperationState.NotConnected,
            string.Empty,
            "Connect the monitor's USB upstream cable to use OLED Care.");

    public static HidOperationResult Busy() =>
        new(
            HidOperationState.Busy,
            string.Empty,
            "MSI Gaming Intelligence is using the monitor. Close it and try again.");

    public static HidOperationResult TimedOut() =>
        new(
            HidOperationState.TimedOut,
            string.Empty,
            "The MSI OLED control interface timed out.");
}

internal sealed record HidReportCapabilities(
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength);

internal interface IMsiHidTransport
{
    Task<HidOperationResult> GetAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        CancellationToken cancellationToken);

    Task<HidOperationResult> GetScalerEventAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        CancellationToken cancellationToken);

    Task<HidOperationResult> SetAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        string value,
        CancellationToken cancellationToken);

    Task<HidOperationResult> SetNoAckAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        string value,
        CancellationToken cancellationToken);
}

internal static class MsiProtocol
{
    public const string GetVerb = "58";
    public const string ReplyVerb = "5b";
    public const string ScalerEventGetVerb = "68";
    public const string ScalerEventReplyVerb = "6b";
    public const string AckSuccess = "5600+";
    public const string AckFailure = "5600-";
    public const int FeatureCodeLength = 5;

    public static string GetCommand(string featureCode) =>
        GetVerb + featureCode + "\r";

    public static string GetScalerEventCommand(string featureCode) =>
        ScalerEventGetVerb + featureCode + "\r";

    public static string SetCommand(string featureCode, string value) =>
        ReplyVerb + featureCode + value + "\r";

    public static bool TryParseReply(
        string payload,
        string featureCode,
        out string value) =>
        TryParseReply(payload, ReplyVerb, featureCode, out value);

    public static bool TryParseScalerEventReply(
        string payload,
        string featureCode,
        out string value) =>
        TryParseReply(payload, ScalerEventReplyVerb, featureCode, out value);

    private static bool TryParseReply(
        string payload,
        string replyVerb,
        string featureCode,
        out string value)
    {
        value = string.Empty;
        string expected = replyVerb + featureCode;
        if (!payload.StartsWith(expected, StringComparison.Ordinal))
            return false;

        value = payload[expected.Length..];
        return true;
    }
}

internal sealed class MsiHidTransport : IMsiHidTransport
{
    private const byte ReportId = 0x01;
    private const int MinimumReportLength = 64;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly SemaphoreSlim DeviceLock = new(1, 1);

    public Task<HidOperationResult> GetAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            MsiProtocol.GetCommand(featureCode),
            isSet: false,
            waitForAck: false,
            featureCode,
            MsiProtocol.ReplyVerb,
            cancellationToken);

    public Task<HidOperationResult> GetScalerEventAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            MsiProtocol.GetScalerEventCommand(featureCode),
            isSet: false,
            waitForAck: false,
            featureCode,
            MsiProtocol.ScalerEventReplyVerb,
            cancellationToken);

    public Task<HidOperationResult> SetAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        string value,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            MsiProtocol.SetCommand(featureCode, value),
            isSet: true,
            waitForAck: true,
            featureCode,
            MsiProtocol.ReplyVerb,
            cancellationToken);

    public Task<HidOperationResult> SetNoAckAsync(
        ushort vendorId,
        ushort[] productIds,
        string featureCode,
        string value,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            MsiProtocol.SetCommand(featureCode, value),
            isSet: true,
            waitForAck: false,
            featureCode,
            MsiProtocol.ReplyVerb,
            cancellationToken);

    private static async Task<HidOperationResult> ExecuteAsync(
        ushort vendorId,
        ushort[] productIds,
        string command,
        bool isSet,
        bool waitForAck,
        string featureCode,
        string replyVerb,
        CancellationToken cancellationToken)
    {
        await DeviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> paths = MsiHidNative.EnumerateDevicePaths(
                vendorId,
                productIds);
            if (paths.Count == 0)
                return HidOperationResult.NotConnected();

            HidOperationResult? lastFailure = null;
            bool foundBusyDevice = false;
            foreach (string path in paths)
            {
                HidOperationResult result = await ExecuteForPathAsync(
                    path,
                    command,
                    isSet,
                    waitForAck,
                    featureCode,
                    replyVerb,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (result.State == HidOperationState.Busy)
                {
                    foundBusyDevice = true;
                    continue;
                }

                if (result.State == HidOperationState.Success)
                    return result;
                lastFailure = result;
            }

            return foundBusyDevice
                ? HidOperationResult.Busy()
                : lastFailure ?? HidOperationResult.Fail(
                    "The MSI OLED control interface did not respond.");
        }
        finally
        {
            DeviceLock.Release();
        }
    }

    private static async Task<HidOperationResult> ExecuteForPathAsync(
        string path,
        string command,
        bool isSet,
        bool waitForAck,
        string featureCode,
        string replyVerb,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = MsiHidNative.OpenDevice(path);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return error is MsiHidNative.ErrorAccessDenied or MsiHidNative.ErrorSharingViolation
                ? HidOperationResult.Busy()
                : HidOperationResult.Fail(new Win32Exception(error).Message);
        }

        if (!MsiHidNative.TryGetReportCapabilities(
                handle,
                out HidReportCapabilities? capabilities) ||
            capabilities == null ||
            capabilities.InputReportLength < MinimumReportLength ||
            capabilities.OutputReportLength < MinimumReportLength)
        {
            return HidOperationResult.Fail(
                "The MSI HID report layout is not supported.");
        }

        byte[]? outputReport = BuildOutputReport(
            command,
            capabilities.OutputReportLength,
            ReportId);
        if (outputReport == null)
        {
            return HidOperationResult.Fail(
                "The MSI command is larger than the HID output report.");
        }

        try
        {
            using var stream = new FileStream(
                handle,
                FileAccess.ReadWrite,
                bufferSize: capabilities.OutputReportLength,
                isAsync: true);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(IoTimeout);

            await stream.WriteAsync(outputReport, timeoutSource.Token)
                .ConfigureAwait(false);
            await stream.FlushAsync(timeoutSource.Token).ConfigureAwait(false);

            if (isSet && !waitForAck)
                return HidOperationResult.Ok();

            string? payload = await ReadMatchingPayloadAsync(
                stream,
                capabilities.InputReportLength,
                isSet,
                featureCode,
                replyVerb,
                timeoutSource.Token).ConfigureAwait(false);
            if (payload == null)
                return HidOperationResult.TimedOut();

            if (payload == MsiProtocol.AckFailure)
                return HidOperationResult.Fail(
                    "The monitor rejected the command.");

            if (isSet)
                return HidOperationResult.Ok();

            string value;
            bool parsed = replyVerb == MsiProtocol.ScalerEventReplyVerb
                ? MsiProtocol.TryParseScalerEventReply(
                    payload,
                    featureCode,
                    out value)
                : MsiProtocol.TryParseReply(
                    payload,
                    featureCode,
                    out value);
            if (!parsed)
                return HidOperationResult.Fail(
                    $"Unexpected reply from the monitor: {payload}.");

            return HidOperationResult.Ok(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HidOperationResult.TimedOut();
        }
        catch (IOException ex)
        {
            int error = ex.HResult & 0xFFFF;
            return error is MsiHidNative.ErrorAccessDenied or MsiHidNative.ErrorSharingViolation
                ? HidOperationResult.Busy()
                : HidOperationResult.Fail(ex.Message);
        }
    }

    private static async Task<string?> ReadMatchingPayloadAsync(
        Stream stream,
        int inputReportLength,
        bool isSet,
        string featureCode,
        string replyVerb,
        CancellationToken cancellationToken)
    {
        string expectedReply = isSet
            ? MsiProtocol.AckSuccess
            : replyVerb + featureCode;

        byte[] report = new byte[inputReportLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int count = await stream.ReadAsync(report, cancellationToken)
                .ConfigureAwait(false);
            if (count <= 1)
                continue;

            string payload = ExtractPayload(report, count);
            if (isSet)
            {
                if (payload is MsiProtocol.AckSuccess or MsiProtocol.AckFailure)
                    return payload;
                continue;
            }

            if (payload.StartsWith(expectedReply, StringComparison.Ordinal))
                return payload;
        }

        return null;
    }

    private static string ExtractPayload(byte[] report, int count)
    {
        int end = count;
        for (int i = 1; i < count; i++)
        {
            if (report[i] is 0 or (byte)'\r')
            {
                end = i;
                break;
            }
        }

        return Encoding.ASCII.GetString(report, 1, end - 1);
    }

    internal static byte[]? BuildOutputReport(
        string command,
        int outputReportLength,
        byte reportId)
    {
        if (outputReportLength < MinimumReportLength)
            return null;

        byte[] commandBytes = Encoding.ASCII.GetBytes(command);
        if (commandBytes.Length > outputReportLength - 1)
            return null;

        byte[] report = new byte[outputReportLength];
        report[0] = reportId;
        commandBytes.CopyTo(report, 1);
        return report;
    }
}

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

                SetupDiGetDeviceInterfaceDetailW(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out uint requiredSize,
                    IntPtr.Zero);
                if (requiredSize == 0)
                    continue;

                IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(
                            deviceInfoSet,
                            ref interfaceData,
                            detailBuffer,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                    if (string.IsNullOrWhiteSpace(path))
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
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return paths;
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
