using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IMonitorEnumerator
{
    List<MonitorInfo> Enumerate();
    List<MonitorInfo> EnumerateSafe();
    List<MonitorInfo> GetExternalMonitors();
}
