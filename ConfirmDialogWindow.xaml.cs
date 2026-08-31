using System.Windows;

namespace DisplayBrightness;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        RunButton.IsDefault = true;
        CancelButton.IsCancel = true;
        CancelButton.Focus();
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
