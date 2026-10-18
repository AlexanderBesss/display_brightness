using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DisplayBrightness.Utilities;

/// <summary>
/// Maps an empty string to <see langword="null"/>: WPF pops a tooltip for any non-null
/// content, so an empty bound status string shows a blank tooltip box and only null suppresses it.
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
internal sealed class EmptyStringToNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && string.IsNullOrWhiteSpace(text))
            return null;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
