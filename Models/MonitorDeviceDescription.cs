namespace DisplayBrightness.Models;

/// <summary>What the device namespace reports for one monitor: device string, instance id,
/// and the adapter (or GDI name) it was found under.</summary>
public sealed record MonitorDeviceDescription(
    string DeviceString,
    string DeviceId,
    string ParentName);
