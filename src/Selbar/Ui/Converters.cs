using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Selbar.Core.Actions;
using Binding = System.Windows.Data.Binding;
using MediaColor = System.Windows.Media.Color;

namespace Selbar.Ui;

/// <summary>
/// Color del distintivo de una acción según lo que hace: se lee de un vistazo
/// si muestra ventana, copia, reemplaza o abre el navegador.
/// Parámetro "bg" = fondo translúcido; cualquier otra cosa = color de texto.
/// </summary>
public sealed class OutputModeBrushConverter : IValueConverter
{
    // Tema oscuro: tonos claros sobre el fondo del distintivo.
    private static readonly MediaColor BlueDark = MediaColor.FromRgb(0x60, 0xCD, 0xFF);
    private static readonly MediaColor GreenDark = MediaColor.FromRgb(0x6C, 0xCB, 0x5F);
    private static readonly MediaColor AmberDark = MediaColor.FromRgb(0xE8, 0xA2, 0x3A);
    private static readonly MediaColor PurpleDark = MediaColor.FromRgb(0xC0, 0x9B, 0xFF);
    private static readonly MediaColor GrayDark = MediaColor.FromRgb(0xA0, 0xA0, 0xA0);

    // Tema claro: los mismos matices, oscurecidos para leerse sobre blanco.
    private static readonly MediaColor BlueLight = MediaColor.FromRgb(0x00, 0x67, 0xC0);
    private static readonly MediaColor GreenLight = MediaColor.FromRgb(0x0F, 0x7B, 0x0F);
    private static readonly MediaColor AmberLight = MediaColor.FromRgb(0x9D, 0x5D, 0x00);
    private static readonly MediaColor PurpleLight = MediaColor.FromRgb(0x6B, 0x3F, 0xA0);
    private static readonly MediaColor GrayLight = MediaColor.FromRgb(0x5D, 0x5D, 0x5D);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value is OutputMode output ? output : OutputMode.Popup;
        bool dark = Theming.IsDark;

        var color = mode switch
        {
            OutputMode.Popup => dark ? BlueDark : BlueLight,
            OutputMode.Copy or OutputMode.DirectCopy => dark ? GreenDark : GreenLight,
            OutputMode.Replace => dark ? AmberDark : AmberLight,
            OutputMode.Browser or OutputMode.OpenUrl => dark ? PurpleDark : PurpleLight,
            _ => dark ? GrayDark : GrayLight
        };

        if (!string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(color);

        return new SolidColorBrush(MediaColor.FromArgb(dark ? (byte)0x26 : (byte)0x1C, color.R, color.G, color.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Invierte un booleano (para estados "vacío"/"lleno").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : false;
}

/// <summary>Visible solo si el texto está vacío (placeholders de cajas de texto).</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = string.IsNullOrEmpty(value as string);
        if (string.Equals(parameter as string, "inverse", StringComparison.OrdinalIgnoreCase)) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible si el valor no es nulo y no está vacío.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string text => text.Trim().Length > 0,
            int number => number > 0,
            _ => true
        };
        if (string.Equals(parameter as string, "inverse", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
