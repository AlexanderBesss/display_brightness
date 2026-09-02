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

        int? totalUsageHours = await GetTotalUsageHoursAsync(
            monitor,
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

        HidOperationResult refreshResult = await _transport.GetAsync(
            profile.HidVendorId,
            profile.HidProductIds,
            profile.RefreshRateCode,
            cancellationToken).ConfigureAwait(false);

        int? refreshRateHz = refreshResult.State == HidOperationState.Success
            ? ResolveRefreshRate(refreshResult.Value, monitor.RefreshRateHz)
            : null;

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
            message,
            refreshRateHz);
    }

    public async Task<int?> GetTotalUsageHoursAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default)
    {
        if (OledCompatibilityRegistry.Find(monitor) == null)
            return null;

        return await Task.Run(
            () => _usageHoursReader(monitor),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OledPanelProtectEvent?> GetPanelProtectEventAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default)
    {
        OledMonitorProfile? profile = OledCompatibilityRegistry.Find(monitor);
        if (profile == null)
            return null;

        HidOperationResult result = await _transport.GetScalerEventAsync(
            profile.HidVendorId,
            profile.HidProductIds,
            OledCompatibilityRegistry.PanelProtectEventCode,
            cancellationToken).ConfigureAwait(false);
        if (result.State != HidOperationState.Success ||
            !OledValueParser.TryParsePanelProtectEvent(
                result.Value,
                out OledPanelProtectEventType eventType))
        {
            return null;
        }

        return new OledPanelProtectEvent(
            eventType,
            DescribePanelProtectEvent(eventType));
    }

    // The 00170 register reports the refresh rate mod 256, so a 360 Hz mode
    // reads 104. Pick the candidate (raw + 256k) closest to the OS-reported
    // rate so >255 Hz modes resolve correctly.
    internal static int? ResolveRefreshRate(string? rawValue, double osHz)
    {
        if (!int.TryParse(rawValue, out int value) || value < 0)
            return null;

        if (osHz <= 0)
            return value;

        int resolved = value;
        double bestDistance = Math.Abs(value - osHz);
        for (int k = 1; k <= 3; k++)
        {
            int candidate = value + 256 * k;
            double distance = Math.Abs(candidate - osHz);
            if (distance < bestDistance)
            {
                resolved = candidate;
                bestDistance = distance;
            }
        }

        return resolved;
    }

    internal static string DescribePanelProtectEvent(
        OledPanelProtectEventType eventType) => eventType switch
    {
        OledPanelProtectEventType.None => "No Panel Protect notification",
        OledPanelProtectEventType.ShortTime => "Panel Protect is due",
        OledPanelProtectEventType.LongTime => "Long Panel Protect is due",
        OledPanelProtectEventType.ForcedShortTime => "Panel Protect is required",
        OledPanelProtectEventType.ForcedLongTime => "Long Panel Protect is required",
        OledPanelProtectEventType.ManualShortTimeWarning => "Panel Protect was not completed",
        OledPanelProtectEventType.ManualLongTimeWarning => "Long Panel Protect was not completed",
        OledPanelProtectEventType.AutoShortTimePowerButtonCancel => "Automatic Panel Protect was interrupted",
        OledPanelProtectEventType.AutoLongTimePowerButtonCancel => "Automatic long Panel Protect was interrupted",
        OledPanelProtectEventType.ForcedShortTimePowerButtonCancel => "Required Panel Protect was interrupted",
        OledPanelProtectEventType.ForcedLongTimePowerButtonCancel => "Required long Panel Protect was interrupted",
        OledPanelProtectEventType.ManualShortTimeFromUi => "Manual Panel Protect requested",
        OledPanelProtectEventType.ManualLongTimeFromUi => "Manual long Panel Protect requested",
        OledPanelProtectEventType.ShortTimeWithLater => "Panel Protect is due",
        OledPanelProtectEventType.AutoShortTimePowerButtonCancelWithLater => "Automatic Panel Protect was interrupted",
        OledPanelProtectEventType.PanelProtectCancelWithoutLater => "Panel Protect was cancelled",
        _ => "Panel Protect needs attention"
    };

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
