using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DisplayBrightness.Services;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Controls.ContextMenu? _trayMenu;
    private Window? _mainWindow;
    private bool _isExiting = false;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            StartApp();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to start Brightness:\n\n{ex.Message}",
                "Brightness — Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void StartApp()
    {
        _mainWindow = new MainWindow();
        _mainWindow.Show();

        var viewModel = (MainWindowViewModel)_mainWindow.DataContext;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayBrightnessIcon.Create(viewModel.AverageBrightness),
            Text = GetTrayText(viewModel.AverageBrightness),
            Visible = true
        };

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.AverageBrightness))
                UpdateTrayIcon(viewModel.AverageBrightness);
        };

        _trayMenu = CreateTrayMenu();

        _trayIcon.MouseClick += (s, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (_trayMenu != null)
                    _trayMenu.IsOpen = false;

                if (_mainWindow!.IsVisible)
                {
                    _mainWindow.Hide();
                }
                else
                {
                    _mainWindow.Show();
                    _mainWindow.Activate();
                    _mainWindow.Focus();
                }
            }
            else if (args.Button == System.Windows.Forms.MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };

        _mainWindow.Closing += (s, e) =>
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                _mainWindow!.Hide();
            }
        };

        _mainWindow.Closed += (s, e) =>
        {
            if (_isExiting)
                Shutdown();
        };
    }

    private void UpdateTrayIcon(int? brightness)
    {
        if (_trayIcon == null)
            return;

        var previousIcon = _trayIcon.Icon;
        _trayIcon.Icon = TrayBrightnessIcon.Create(brightness);
        _trayIcon.Text = GetTrayText(brightness);
        previousIcon?.Dispose();
    }

    private static string GetTrayText(int? brightness) => brightness.HasValue
        ? $"Brightness - {brightness.Value}% average"
        : "Brightness - no controllable displays";

    private System.Windows.Controls.ContextMenu CreateTrayMenu()
    {
        var exitIcon = new TextBlock
        {
            Text = "\uE7E8",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 126, 135)),
            TextAlignment = TextAlignment.Center
        };

        var exitItem = new System.Windows.Controls.MenuItem
        {
            Header = "Exit Brightness",
            Icon = exitIcon,
            Style = (Style)FindResource("TrayMenuItemStyle")
        };
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Style = (Style)FindResource("TrayContextMenuStyle")
        };
        menu.Items.Add(exitItem);
        return menu;
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu == null)
            return;

        _trayMenu.IsOpen = false;
        _trayMenu.Placement = PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
    }

    private void ExitApplication()
    {
        if (_trayMenu != null)
            _trayMenu.IsOpen = false;

        _isExiting = true;
        DisposeTrayIcon();
        _mainWindow?.Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;

        var icon = _trayIcon.Icon;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        icon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
    }
}
