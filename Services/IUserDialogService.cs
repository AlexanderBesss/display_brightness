using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IUserDialogService
{
    bool ConfirmPixelRefresh(MonitorInfo monitor, OledSupportLevel supportLevel);
}
