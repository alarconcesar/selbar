using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace Selbar.Ui;

/// <summary>
/// Utilidades de color (HSL, luminancia WCAG y mezclas) para adaptar el color de
/// acento de Windows sin perder contraste ni legibilidad.
/// </summary>
public static class ColorMath
{
    /// <summary>Convierte a HSL (H en grados 0-360, S y L en 0-1).</summary>
    public static (double H, double S, double L) ToHsl(MediaColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double l = (max + min) / 2;

        if (delta < 1e-9) return (0, 0, l);

        double s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-9) h = ((g - b) / delta + (g < b ? 6 : 0)) / 6;
        else if (Math.Abs(max - g) < 1e-9) h = ((b - r) / delta + 2) / 6;
        else h = ((r - g) / delta + 4) / 6;

        return (h * 360.0, s, l);
    }

    /// <summary>Construye un color desde HSL (H en grados, S y L en 0-1).</summary>
    public static MediaColor FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 1e-9)
        {
            byte gray = ToByte(l * 255);
            return MediaColor.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return MediaColor.FromRgb(
            ToByte(HueToRgb(p, q, h + 1.0 / 3) * 255),
            ToByte(HueToRgb(p, q, h) * 255),
            ToByte(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    /// <summary>Luminancia relativa WCAG (0 = negro, 1 = blanco).</summary>
    public static double Luminance(MediaColor color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>Fuerza la luminosidad HSL dentro de [min, max] (el resto del color no se toca).</summary>
    public static MediaColor ClampLightness(MediaColor color, double min, double max)
    {
        var (h, s, l) = ToHsl(color);
        double target = Math.Clamp(l, min, max);
        if (Math.Abs(target - l) < 1e-6) return color;
        var result = FromHsl(h, s, target);
        return MediaColor.FromArgb(color.A, result.R, result.G, result.B);
    }

    /// <summary>Aclara un color acercándolo al blanco.</summary>
    public static MediaColor Lighten(MediaColor color, double amount)
        => Mix(color, Colors.White, Math.Clamp(amount, 0, 1));

    /// <summary>Oscurece un color acercándolo al negro.</summary>
    public static MediaColor Darken(MediaColor color, double amount)
        => Mix(color, Colors.Black, Math.Clamp(amount, 0, 1));

    /// <summary>Mezcla lineal entre dos colores (t = 0 devuelve <paramref name="from"/>).</summary>
    public static MediaColor Mix(MediaColor from, MediaColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return MediaColor.FromArgb(
            from.A,
            ToByte(from.R + (to.R - from.R) * t),
            ToByte(from.G + (to.G - from.G) * t),
            ToByte(from.B + (to.B - from.B) * t));
    }

    /// <summary>Mismo color con otra opacidad.</summary>
    public static MediaColor WithAlpha(MediaColor color, byte alpha)
        => MediaColor.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Negro o blanco, el que más contraste dé sobre el fondo dado.</summary>
    public static MediaColor ContrastText(MediaColor background)
        => Luminance(background) > 0.45 ? MediaColor.FromRgb(0x10, 0x10, 0x10) : Colors.White;

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
