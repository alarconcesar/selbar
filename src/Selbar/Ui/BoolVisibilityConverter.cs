using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Selbar.Ui;

/// <summary>Convierte bool → Visibility (parámetro "inverse" para lo contrario).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (string.Equals(parameter as string, "inverse", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
