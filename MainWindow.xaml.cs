using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

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
            if (current is System.Windows.Controls.Primitives.ButtonBase or
                RangeBase or
                Thumb or
                System.Windows.Controls.Primitives.TextBoxBase)
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Slider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Slider slider && slider.DataContext is MonitorSliderViewModel vm)
        {
            vm.CommitBrightness();
        }
    }


}
