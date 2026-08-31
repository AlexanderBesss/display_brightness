namespace DisplayBrightness.Models;

public class MonitorInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public ulong AdapterId { get; set; }
    public uint TargetId { get; set; }
    public uint OutputTechnology { get; set; }
}
