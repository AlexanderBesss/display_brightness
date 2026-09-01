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
    private const int BrightnessStep = 2;
    private const int WheelDelta = 120;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private System.Drawing.Point? _anchorScreenPoint;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        SizeChanged += MainWindow_SizeChanged;
    }

    public void ShowInBottomRight(System.Drawing.Point screenPoint)
    {
        _anchorScreenPoint = screenPoint;
        WindowChrome.BeginAnimation(OpacityProperty, null);
        WindowChrome.Opacity = 0;

        Show();
        UpdateLayout();

        PositionInBottomRight(screenPoint);
        AnimateFromBottomRight();

        Activate();
        Focus();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible && _anchorScreenPoint is { } screenPoint)
            PositionInBottomRight(screenPoint);
    }

    private void PositionInBottomRight(System.Drawing.Point screenPoint)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(screenPoint);
        var workArea = screen.WorkingArea;

        var windowHandle = new WindowInteropHelper(this).Handle;
        var scale = GetScaleForPoint(screenPoint, windowHandle);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        // Keep the popup and its shadow clear of the work-area edges.
        const int gap = 20;

        int left = Math.Max(workArea.Left, workArea.Right - width - gap);
        int top = Math.Max(workArea.Top, workArea.Bottom - height - gap);

        SetWindowPos(windowHandle, IntPtr.Zero, left, top, width, height, SwpNoActivate | SwpNoZOrder);
    }

    private static double GetScaleForPoint(System.Drawing.Point point, IntPtr windowHandle)
    {
        const uint monitorDefaultToNearest = 2;
        var monitor = MonitorFromPoint(point, monitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            return dpiX / 96.0;

        return GetDpiForWindow(windowHandle) / 96.0;
    }

    private void AnimateFromBottomRight()
    {
        WindowChrome.RenderTransformOrigin = new System.Windows.Point(1, 1);

        var scale = new ScaleTransform(0.92, 0.92);
        var translate = new TranslateTransform(0, 18);
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

    private void MonitorCard_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Border monitorCard ||
            monitorCard.DataContext is not MonitorSliderViewModel viewModel ||
            e.Delta == 0)
        {
            return;
        }

        int wheelSteps = e.Delta / WheelDelta;
        if (wheelSteps == 0)
            wheelSteps = Math.Sign(e.Delta);

        viewModel.AdjustBrightness(wheelSteps * BrightnessStep);
        e.Handled = true;
    }
}
