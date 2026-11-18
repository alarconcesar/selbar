using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Selbar.Ui;

/// <summary>
/// Pinceles del tema para contenido generado en código (markdown, diff, iconos).
/// Se leen del diccionario en el momento de dibujar, así el tema claro y el
/// oscuro se ven igual de bien sin duplicar paletas por renderizador.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>Pincel del tema o el color de reserva si aún no hay aplicación.</summary>
    public static SolidColorBrush Get(string key, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    /// <summary>Mismo color del tema con transparencia (fondos de resaltado).</summary>
    public static SolidColorBrush Tint(string key, byte alpha, byte r, byte g, byte b)
    {
        var color = Get(key, r, g, b).Color;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
