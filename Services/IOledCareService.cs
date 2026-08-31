using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IOledCareService
{
    OledSupportLevel GetSupportLevel(MonitorInfo monitor);

    Task<OledCareStatus> GetStatusAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default);

    Task<PixelRefreshResult> StartPixelRefreshAsync(
        MonitorInfo monitor,
        CancellationToken cancellationToken = default);
}
