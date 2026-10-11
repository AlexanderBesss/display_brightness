using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DisplayBrightness.Services;

internal interface IMsiHidTransport
{
    Task<HidOperationResult> GetAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        CancellationToken cancellationToken);

    Task<HidOperationResult> GetScalerEventAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        CancellationToken cancellationToken);

    Task<HidOperationResult> SetAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        string value,
        CancellationToken cancellationToken);

    Task<HidOperationResult> SetNoAckAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        string value,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends MSI protocol commands to the USB interface of one specific display.
/// Every call first identifies the candidate devices by their chassis serial so
/// a command can never reach a different monitor that shares the VID/PID.
/// </summary>
internal sealed class MsiHidTransport : IMsiHidTransport
{
    internal sealed record Command(
        string Frame,
        bool IsSet,
        bool WaitForAck,
        string FeatureCode,
        string ReplyVerb);

    private const byte ReportId = 0x01;
    private const int MinimumReportLength = 64;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly SemaphoreSlim DeviceLock = new(1, 1);

    private readonly Func<ushort, ushort[], List<string>> _enumerate;
    private readonly Func<string, Command, CancellationToken, Task<HidOperationResult>> _execute;

    internal MsiHidTransport(
        Func<ushort, ushort[], List<string>>? enumerate = null,
        Func<string, Command, CancellationToken, Task<HidOperationResult>>? execute = null)
    {
        _enumerate = enumerate ?? MsiHidNative.EnumerateDevicePaths;
        _execute = execute ?? ((path, command, token) => ExecuteForPathAsync(
            path, command, token));
    }

    public Task<HidOperationResult> GetAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            monitorSerialNumber,
            new Command(
                MsiProtocol.GetCommand(featureCode),
                IsSet: false,
                WaitForAck: false,
                featureCode,
                MsiProtocol.ReplyVerb),
            cancellationToken);

    public Task<HidOperationResult> GetScalerEventAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            monitorSerialNumber,
            new Command(
                MsiProtocol.GetScalerEventCommand(featureCode),
                IsSet: false,
                WaitForAck: false,
                featureCode,
                MsiProtocol.ScalerEventReplyVerb),
            cancellationToken);

    public Task<HidOperationResult> SetAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        string value,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            monitorSerialNumber,
            new Command(
                MsiProtocol.SetCommand(featureCode, value),
                IsSet: true,
                WaitForAck: true,
                featureCode,
                MsiProtocol.ReplyVerb),
            cancellationToken);

    public Task<HidOperationResult> SetNoAckAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        string featureCode,
        string value,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            vendorId,
            productIds,
            monitorSerialNumber,
            new Command(
                MsiProtocol.SetCommand(featureCode, value),
                IsSet: true,
                WaitForAck: false,
                featureCode,
                MsiProtocol.ReplyVerb),
            cancellationToken);

    private Task<HidOperationResult> ExecuteAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        Command command,
        CancellationToken cancellationToken) => Task.Run(
            () => ExecuteCoreAsync(
                vendorId,
                productIds,
                monitorSerialNumber,
                command,
                cancellationToken),
            cancellationToken);

    private async Task<HidOperationResult> ExecuteCoreAsync(
        ushort vendorId,
        ushort[] productIds,
        string monitorSerialNumber,
        Command command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasUsableSerial(monitorSerialNumber))
            return HidOperationResult.Fail(
                "Cannot identify this display's serial number. OLED Care is unavailable.");

        await DeviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> paths = _enumerate(vendorId, productIds);
            if (paths.Count == 0)
                return HidOperationResult.NotConnected();

            IdentityProbeOutcome probe = await SelectDevicesBySerialAsync(
                paths,
                monitorSerialNumber.Trim(),
                cancellationToken).ConfigureAwait(false);
            if (probe.Matches.Count > 1)
                return HidOperationResult.Fail(
                    "Multiple USB interfaces have this display's serial number. OLED Care is unavailable.");
            if (probe.Matches.Count == 0)
                return probe.LastFailure ?? HidOperationResult.NotConnected();

            cancellationToken.ThrowIfCancellationRequested();
            return await _execute(probe.Matches[0], command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeviceLock.Release();
        }
    }

    private sealed record IdentityProbeOutcome(
        List<string> Matches,
        HidOperationResult? LastFailure);

    /// <summary>
    /// Returns the candidate interfaces whose chassis serial matches, plus the
    /// last probe failure so the caller can report why nothing matched.
    /// Identification is GET-only: no status or write command may reach a device
    /// until its serial matches the selected display.
    /// </summary>
    private async Task<IdentityProbeOutcome> SelectDevicesBySerialAsync(
        List<string> paths,
        string serial,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        HidOperationResult? lastFailure = null;
        bool identityProbeSucceeded = false;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HidOperationResult result = await _execute(
                path,
                IdentityProbeCommand,
                cancellationToken).ConfigureAwait(false);
            if (result.State != HidOperationState.Success)
            {
                lastFailure = result;
                continue;
            }

            identityProbeSucceeded = true;
            if (string.Equals(serial, result.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                matches.Add(path);
        }

        if (matches.Count == 0 && paths.Count == 1 && identityProbeSucceeded)
        {
            // Some monitors expose an EDID factory serial through WMI but a
            // different chassis serial through the MSI HID register. With exactly
            // one responsive VID/PID candidate there is no device ambiguity, so
            // use that candidate. Never apply this fallback when multiple
            // interfaces are present.
            matches.Add(paths[0]);
            lastFailure = null;
        }

        return new IdentityProbeOutcome(matches, lastFailure);
    }

    private static readonly Command IdentityProbeCommand = new(
        MsiProtocol.GetCommand(MsiProtocol.SerialNumberFeatureCode),
        IsSet: false,
        WaitForAck: false,
        MsiProtocol.SerialNumberFeatureCode,
        MsiProtocol.ReplyVerb);

    /// <summary>
    /// A blank or all-zero EDID serial means the display never reported one, so
    /// there is no way to prove which interface belongs to it.
    /// </summary>
    private static bool HasUsableSerial(string monitorSerialNumber)
    {
        string serial = monitorSerialNumber.Trim();
        return serial.Length > 0 && !serial.All(character => character == '0');
    }

    private static async Task<HidOperationResult> ExecuteForPathAsync(
        string path,
        Command command,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = MsiHidNative.OpenDevice(path);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return IsDeviceBusyError(error)
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
            command.Frame,
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

            if (command.IsSet && !command.WaitForAck)
                return HidOperationResult.Ok();

            string? payload = await ReadMatchingPayloadAsync(
                stream,
                capabilities.InputReportLength,
                command,
                timeoutSource.Token).ConfigureAwait(false);
            if (payload == null)
                return HidOperationResult.TimedOut();

            if (payload == MsiProtocol.AckFailure)
                return HidOperationResult.Fail(
                    "The monitor rejected the command.");

            if (command.IsSet)
                return HidOperationResult.Ok();

            if (!MsiProtocol.TryParseReply(
                    payload,
                    command.ReplyVerb,
                    command.FeatureCode,
                    out string value))
            {
                return HidOperationResult.Fail(
                    $"Unexpected reply from the monitor: {payload}.");
            }

            return HidOperationResult.Ok(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HidOperationResult.TimedOut();
        }
        catch (IOException ex)
        {
            int error = ex.HResult & 0xFFFF;
            return IsDeviceBusyError(error)
                ? HidOperationResult.Busy()
                : HidOperationResult.Fail(ex.Message);
        }
    }

    private static bool IsDeviceBusyError(int error) =>
        error is MsiHidNative.ErrorAccessDenied or MsiHidNative.ErrorSharingViolation;

    private static async Task<string?> ReadMatchingPayloadAsync(
        Stream stream,
        int inputReportLength,
        Command command,
        CancellationToken cancellationToken)
    {
        string expectedReply = command.IsSet
            ? MsiProtocol.AckSuccess
            : command.ReplyVerb + command.FeatureCode;

        byte[] report = new byte[inputReportLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int count = await stream.ReadAsync(report, cancellationToken)
                .ConfigureAwait(false);
            if (count <= 1)
                continue;

            string payload = ExtractPayload(report, count);
            if (command.IsSet)
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
