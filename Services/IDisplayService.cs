using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IDisplayService
{
    List<MonitorInfo> GetExternalMonitors();
    int? GetBrightness(MonitorInfo monitor);
    bool SetBrightness(MonitorInfo monitor, int brightness);
}
