using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public class DisplayService : IDisplayService
{
    private readonly MonitorEnumerator _enumerator = new();
    private readonly IBrightnessController _brightnessController = new BrightnessController();
    private readonly IBrightnessController _wmiController = new WmiBrightnessController();

    public List<MonitorInfo> GetExternalMonitors() =>
        _enumerator.GetExternalMonitors();

    public int? GetBrightness(MonitorInfo monitor)
    {
        return GetController(monitor).GetBrightness(monitor);
    }

    public bool SetBrightness(MonitorInfo monitor, int brightness)
    {
        return GetController(monitor).SetBrightness(monitor, brightness);
    }

    private IBrightnessController GetController(MonitorInfo monitor) =>
        monitor.OutputTechnology ==
            DisplayInterop.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                ? _wmiController
                : _brightnessController;
}
