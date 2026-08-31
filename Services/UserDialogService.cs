using System.Windows;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public sealed class UserDialogService : IUserDialogService
{
    public bool ConfirmPixelRefresh(MonitorInfo monitor, OledSupportLevel supportLevel)
    {
        string message =
            $"Run pixel refresh on {monitor.FriendlyName}?\n\n" +
            "The display shows a warning during the refresh. Do not look at the " +
            "screen directly and do not unplug the monitor or disconnect its " +
            "power while the refresh is running.";

        if (supportLevel == OledSupportLevel.Experimental)
        {
            message += "\n\nThis monitor uses experimental MSI OLED support. " +
                "Only the known Panel Protect command will be sent.";
        }

        var dialog = new ConfirmDialogWindow("Run pixel refresh", message)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }
}
