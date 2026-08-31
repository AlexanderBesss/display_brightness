using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public sealed class OledCareService : IOledCareService
{
    private readonly IMsiHidTransport _transport;
    private readonly Func<MonitorInfo, int?> _usageHoursReader;

    public OledCareService()
        : this(
            new MsiHidTransport(),
            monitor => new BrightnessController().GetVcpFeatureValue(monitor, 0xC0) is uint value
                ? checked((int)value)
                : null)
    {
    }

    internal OledCareService(
        IMsiHidTransport transport,
        Func<MonitorInfo, int?> usageHoursReader)
    {
        _transport = transport;
        _usageHoursReader = usageHoursReader;
    }

    public OledSupportLevel GetSupportLevel(MonitorInfo monitor) =>
        OledCompatibilityRegistry.Find(monitor)?.SupportLevel ?? OledSupportLevel.Unsupported;

    public async Task<OledCareStatus> GetStatusAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default)
    {
        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);
        if (profile == null)
        {
            return new OledCareStatus(
                OledSupportLevel.Unsupported,
                OledConnectionState.Unsupported,
                null,
                "OLED Care is not supported for this display.");
        }

        int? totalUsageHours = await Task.Run(
            () => _usageHoursReader(monitor),
            cancellationToken).ConfigureAwait(false);

        HidOperationResult result = await _transport.GetAsync(
            profile.HidVendorId,
            profile.HidProductIds,
            profile.PanelProtectCode,
            cancellationToken).ConfigureAwait(false);

        OledConnectionState connectionState = MapState(result.State);
        if (result.State != HidOperationState.Success)
        {
            return new OledCareStatus(
                profile.SupportLevel,
                connectionState,
                null,
                result.Message);
        }

        OledPanelInfo? panelInfo = OledValueParser.ParsePanelInfo(
            result.Value,
            totalUsageHours);

        string message = panelInfo != null
            ? "OLED panel protect status read from the monitor."
            : totalUsageHours.HasValue
                ? $"Status unavailable · {totalUsageHours:N0} total panel hours"
                : "Status unavailable for this firmware.";

        return new OledCareStatus(
            profile.SupportLevel,
            OledConnectionState.Ready,
            panelInfo,
            message);
    }

    public async Task<PixelRefreshResult> StartPixelRefreshAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default)
    {
        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);
        if (profile == null)
            return new PixelRefreshResult(false, "OLED Care is not supported for this display.");

        // Fire and forget, matching MSI Gaming Intelligence: the firmware
        // withholds the ack while the panel protect routine runs.
        HidOperationResult result = await _transport.SetNoAckAsync(
            profile.HidVendorId,
            profile.HidProductIds,
            profile.PanelProtectCode,
            profile.PixelRefreshValue,
            cancellationToken).ConfigureAwait(false);

        return result.State == HidOperationState.Success
            ? new PixelRefreshResult(
                true,
                "Panel protect command sent. A warning is shown on the display; do not look at it directly.")
            : new PixelRefreshResult(false, result.Message);
    }

    private static OledConnectionState MapState(HidOperationState state) => state switch
    {
        HidOperationState.NotConnected => OledConnectionState.UsbNotConnected,
        HidOperationState.Busy => OledConnectionState.Busy,
        HidOperationState.Success => OledConnectionState.Ready,
        _ => OledConnectionState.Error
    };
}
