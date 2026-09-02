namespace DisplayBrightness.Models;

public enum OledSupportLevel
{
    Unsupported,
    Experimental,
    Verified
}

public enum OledConnectionState
{
    Unsupported,
    UsbNotConnected,
    Busy,
    Ready,
    Error
}

public sealed record OledPanelInfo(
    int? PanelProtect,
    int? TotalUsageHours);

public sealed record OledCareStatus(
    OledSupportLevel SupportLevel,
    OledConnectionState ConnectionState,
    OledPanelInfo? PanelInfo,
    string Message,
    int? RefreshRateHz = null)
{
    public bool CanRunPixelRefresh =>
        SupportLevel != OledSupportLevel.Unsupported &&
        ConnectionState == OledConnectionState.Ready;
}

public sealed record PixelRefreshResult(bool Started, string Message);

public sealed record OledPanelProtectHistory(
    DateTimeOffset LastStartedAtUtc,
    int? TotalUsageHoursAtStart);

public enum OledPanelProtectEventType
{
    None = 0,
    ShortTime = 1,
    LongTime = 2,
    ForcedShortTime = 3,
    ForcedLongTime = 4,
    ManualShortTimeWarning = 5,
    ManualLongTimeWarning = 6,
    AutoShortTimePowerButtonCancel = 7,
    AutoLongTimePowerButtonCancel = 8,
    ForcedShortTimePowerButtonCancel = 9,
    ForcedLongTimePowerButtonCancel = 10,
    ManualShortTimeFromUi = 11,
    ManualLongTimeFromUi = 12,
    ShortTimeWithLater = 13,
    AutoShortTimePowerButtonCancelWithLater = 14,
    PanelProtectCancelWithoutLater = 15
}

public sealed record OledPanelProtectEvent(
    OledPanelProtectEventType Type,
    string Message)
{
    public bool RequiresAttention =>
        Type != OledPanelProtectEventType.None &&
        Type is not OledPanelProtectEventType.ManualShortTimeFromUi and
            not OledPanelProtectEventType.ManualLongTimeFromUi;
}

public sealed record OledPanelProtectNotification(
    OledPanelProtectEventType Type,
    DateTimeOffset FirstObservedAtUtc,
    int? TotalUsageHoursAtObservation);
