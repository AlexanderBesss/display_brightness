using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Animation;
using DisplayBrightness.ViewModels;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace DisplayBrightness;

public partial class MainWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    public void ShowNearTray(System.Drawing.Point trayPoint)
    {
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.Opacity = 0;

        Show();
        UpdateLayout();

        var edge = PositionNearTray(trayPoint);
        AnimateFromTray(trayPoint, edge);

        Activate();
        Focus();
    }

    private TrayEdge PositionNearTray(System.Drawing.Point trayPoint)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(trayPoint);
        var workArea = screen.WorkingArea;
        var bounds = screen.Bounds;
        var edge = FindTaskbarEdge(bounds, workArea, trayPoint);

        var windowHandle = new WindowInteropHelper(this).Handle;
        var scale = GetScaleForPoint(trayPoint, windowHandle);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        // Leave enough room that the always-on-top popup never intercepts
        // clicks intended for the taskbar along its edge.
        const int gap = 20;

        int left;
        int top;

        switch (edge)
        {
            case TrayEdge.Top:
                left = trayPoint.X - width + 36;
                top = workArea.Top + gap;
                break;
            case TrayEdge.Left:
                left = workArea.Left + gap;
                top = trayPoint.Y - height + 36;
                break;
            case TrayEdge.Right:
                left = workArea.Right - width - gap;
                top = trayPoint.Y - height + 36;
                break;
            default:
                left = trayPoint.X - width + 36;
                top = workArea.Bottom - height - gap;
                break;
        }

        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        SetWindowPos(windowHandle, IntPtr.Zero, left, top, width, height, SwpNoActivate | SwpNoZOrder);
        return edge;
    }

    private static double GetScaleForPoint(System.Drawing.Point point, IntPtr windowHandle)
    {
        const uint monitorDefaultToNearest = 2;
        var monitor = MonitorFromPoint(point, monitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            return dpiX / 96.0;

        return GetDpiForWindow(windowHandle) / 96.0;
    }

    private void AnimateFromTray(System.Drawing.Point trayPoint, TrayEdge edge)
    {
        var trayInWindow = PointFromScreen(new System.Windows.Point(trayPoint.X, trayPoint.Y));
        WindowChrome.RenderTransformOrigin = new System.Windows.Point(
            Math.Clamp(trayInWindow.X / Math.Max(1, ActualWidth), 0, 1),
            Math.Clamp(trayInWindow.Y / Math.Max(1, ActualHeight), 0, 1));

        var scale = new ScaleTransform(0.92, 0.92);
        var translate = edge switch
        {
            TrayEdge.Top => new TranslateTransform(0, -18),
            TrayEdge.Left => new TranslateTransform(-18, 0),
            TrayEdge.Right => new TranslateTransform(18, 0),
            _ => new TranslateTransform(0, 18)
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        WindowChrome.RenderTransform = transforms;

        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WindowChrome.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.92, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.92, 1, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(translate.X, 0, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(translate.Y, 0, duration) { EasingFunction = easing });
    }

    private static TrayEdge FindTaskbarEdge(
        System.Drawing.Rectangle bounds,
        System.Drawing.Rectangle workArea,
        System.Drawing.Point trayPoint)
    {
        var insets = new[]
        {
            (Edge: TrayEdge.Left, Size: workArea.Left - bounds.Left),
            (Edge: TrayEdge.Top, Size: workArea.Top - bounds.Top),
            (Edge: TrayEdge.Right, Size: bounds.Right - workArea.Right),
            (Edge: TrayEdge.Bottom, Size: bounds.Bottom - workArea.Bottom)
        };
        var taskbar = insets.OrderByDescending(item => item.Size).First();
        if (taskbar.Size > 0)
            return taskbar.Edge;

        var distances = new[]
        {
            (Edge: TrayEdge.Left, Distance: Math.Abs(trayPoint.X - bounds.Left)),
            (Edge: TrayEdge.Top, Distance: Math.Abs(trayPoint.Y - bounds.Top)),
            (Edge: TrayEdge.Right, Distance: Math.Abs(bounds.Right - trayPoint.X)),
            (Edge: TrayEdge.Bottom, Distance: Math.Abs(bounds.Bottom - trayPoint.Y))
        };
        return distances.OrderBy(item => item.Distance).First().Edge;
    }

    private enum TrayEdge
    {
        Left,
        Top,
        Right,
        Bottom
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitorHandle,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr windowHandleInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            e.ButtonState == MouseButtonState.Pressed &&
            !IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            DragMove();
            e.Handled = true;
        }
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        for (var current = element; current != null; current = GetParent(current))
        {
            if (current is WpfButtonBase or
                RangeBase or
                Thumb or
                WpfTextBoxBase)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        return element is Visual or Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && slider.DataContext is MonitorSliderViewModel vm)
        {
            vm.CommitBrightness();
        }
    }
}
