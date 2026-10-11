namespace DisplayBrightness.Models;

/// <summary>
/// What the device namespace says about one monitor: its friendly device string,
/// its instance id, and the adapter (or GDI name) it was found under.
/// </summary>
public sealed record MonitorDeviceDescription(
    string DeviceString,
    string DeviceId,
    string ParentName);
