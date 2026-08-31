using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DisplayBrightness.Services;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness;

public partial class App : System.Windows.Application
{
    private const int BrightnessStep = 2;

    private NativeTrayIcon? _trayIcon;
    private System.Windows.Controls.ContextMenu? _trayMenu;
    private MainWindow? _mainWindow;
    private bool _isExiting;
    private int _pendingWheelSteps;
    private int _isWheelUpdateQueued;

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

        var viewModel = (MainWindowViewModel)_mainWindow.DataContext;
        _trayIcon = new NativeTrayIcon(
            TrayBrightnessIcon.Create(viewModel.AverageBrightness),
            GetTrayText(viewModel.PrimaryBrightness));

        _trayIcon.MouseWheel += detents =>
            QueueBrightnessAdjustment(viewModel, detents);
        _trayIcon.IsWheelEnabled = viewModel.CanAdjustBrightness;

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.AverageBrightness))
                UpdateTrayIcon(
                    viewModel.AverageBrightness,
                    viewModel.PrimaryBrightness);
            else if (args.PropertyName == nameof(MainWindowViewModel.CanAdjustBrightness) &&
                     _trayIcon != null)
                _trayIcon.IsWheelEnabled = viewModel.CanAdjustBrightness;
        };

        _trayMenu = CreateTrayMenu();

        _mainWindow.Deactivated += (_, _) =>
        {
            if (_trayIcon?.IsPointOverIcon(System.Windows.Forms.Cursor.Position) != true)
                _mainWindow.Hide();
        };

        _trayIcon.LeftClick += () =>
        {
            if (_trayMenu != null)
                _trayMenu.IsOpen = false;

            ToggleMainWindow();
        };
        _trayIcon.RightClick += ShowTrayMenu;

        _mainWindow.Closing += (_, e) =>
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                _mainWindow!.Hide();
            }
        };

        _mainWindow.Closed += (_, _) =>
        {
            if (_isExiting)
                Shutdown();
        };

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowNearTray(GetInitialTrayPoint());
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow == null)
            return;

        if (_mainWindow.IsVisible)
            _mainWindow.Hide();
        else
            _mainWindow.ShowNearTray(System.Windows.Forms.Cursor.Position);
    }

    private static System.Drawing.Point GetInitialTrayPoint()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var rect))
        {
            var horizontal = rect.Right - rect.Left >= rect.Bottom - rect.Top;
            return horizontal
                ? new System.Drawing.Point(rect.Right - 24, rect.Top + (rect.Bottom - rect.Top) / 2)
                : new System.Drawing.Point(rect.Left + (rect.Right - rect.Left) / 2, rect.Bottom - 24);
        }

        var workArea = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? System.Windows.Forms.SystemInformation.WorkingArea;
        return new System.Drawing.Point(workArea.Right - 24, workArea.Bottom - 1);
    }

    private void UpdateTrayIcon(int? averageBrightness, int? primaryBrightness)
    {
        if (_trayIcon == null)
            return;

        _trayIcon.Update(
            TrayBrightnessIcon.Create(averageBrightness),
            GetTrayText(primaryBrightness));
    }

    private void QueueBrightnessAdjustment(
        MainWindowViewModel viewModel,
        int wheelDetents)
    {
        Interlocked.Add(ref _pendingWheelSteps, wheelDetents);
        if (Interlocked.CompareExchange(ref _isWheelUpdateQueued, 1, 0) != 0)
            return;

        Dispatcher.InvokeAsync(
            () => ApplyPendingBrightnessAdjustment(viewModel),
            DispatcherPriority.Background);
    }

    private void ApplyPendingBrightnessAdjustment(MainWindowViewModel viewModel)
    {
        int wheelSteps = Interlocked.Exchange(ref _pendingWheelSteps, 0);
        Interlocked.Exchange(ref _isWheelUpdateQueued, 0);

        if (wheelSteps != 0)
            viewModel.AdjustPrimaryBrightness(wheelSteps * BrightnessStep);

        if (Volatile.Read(ref _pendingWheelSteps) != 0)
            QueueBrightnessAdjustment(viewModel, 0);
    }

    private static string GetTrayText(int? brightness) => brightness.HasValue
        ? $"Main monitor brightness - {brightness.Value}%"
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
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();
        base.OnExit(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);
}
