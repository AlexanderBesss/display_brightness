using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IBrightnessController
{
    int? GetBrightness(MonitorInfo monitor);
    bool SetBrightness(MonitorInfo monitor, int brightness);
}
