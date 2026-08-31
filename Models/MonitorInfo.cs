namespace DisplayBrightness.Models;

public sealed class MonitorInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public uint OutputTechnology { get; set; }
}
