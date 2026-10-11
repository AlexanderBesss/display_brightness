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
