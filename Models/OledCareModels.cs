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
    string Message)
{
    public bool CanRunPixelRefresh =>
        SupportLevel != OledSupportLevel.Unsupported &&
        ConnectionState == OledConnectionState.Ready;
}

public sealed record PixelRefreshResult(bool Started, string Message);
