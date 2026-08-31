namespace DisplayBrightness.Models;

public sealed class MonitorInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string ManufacturerCode { get; set; } = string.Empty;
    public ushort EdidProductCode { get; set; }
    public uint OutputTechnology { get; set; }
    public double RefreshRateHz { get; set; }
}
