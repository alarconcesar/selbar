using System.Globalization;
using MediaColor = System.Windows.Media.Color;

namespace Selbar.Core.Text;

/// <summary>Valores legibles de un color seleccionado (por ejemplo #0078D4).</summary>
public sealed record ColorInfo(string Hex, string Rgb, string Hsl, string Alpha, MediaColor Color, bool HasAlpha);

/// <summary>Interpreta códigos HEX (#RGB, #RGBA, #RRGGBB, #RRGGBBAA o sin #).</summary>
public static class ColorCode
{
    public static bool TryParse(string? text, out ColorInfo info)
    {
        info = null!;
        string raw = (text ?? "").Trim();
        bool hasHash = raw.StartsWith('#');
        if (hasHash) raw = raw[1..];
        if (raw.Length is not (3 or 4 or 6 or 8)) return false;
        if (!hasHash && (raw.Length is 3 or 4)) return false;
        if (!raw.All(Uri.IsHexDigit)) return false;

        byte r, g, b, a = 255;

        if (raw.Length is 3 or 4)
        {
            r = Hex(raw[0], raw[0]);
            g = Hex(raw[1], raw[1]);
            b = Hex(raw[2], raw[2]);
            if (raw.Length == 4) a = Hex(raw[3], raw[3]);
        }
        else
        {
            r = Hex(raw[0], raw[1]);
            g = Hex(raw[2], raw[3]);
            b = Hex(raw[4], raw[5]);
            if (raw.Length == 8) a = Hex(raw[6], raw[7]);
        }

        var color = MediaColor.FromArgb(a, r, g, b);
        var (h, s, l) = ToHsl(r, g, b);

        string hex = $"#{r:X2}{g:X2}{b:X2}";
        // CSS siempre con punto decimal: con cultura es-ES saldría "rgba(…, 0,5)".
        string alphaCss = Css(a / 255.0);
        string rgb = a == 255
            ? $"rgb({r}, {g}, {b})"
            : $"rgba({r}, {g}, {b}, {alphaCss})";
        string hsl = a == 255
            ? $"hsl({Int(h)}, {Int(s)}%, {Int(l)}%)"
            : $"hsla({Int(h)}, {Int(s)}%, {Int(l)}%, {alphaCss})";
        string alpha = a == 255 ? "100 %" : $"{a / 255.0:P0} ({a})";

        info = new ColorInfo(hex, rgb, hsl, alpha, color, a != 255);
        return true;
    }

    /// <summary>True si el texto es un color HEX (lo usa la acción contextual).</summary>
    public static bool IsHex(string? text)
    {
        string raw = (text ?? "").Trim();
        if (raw.StartsWith('#')) return raw.Length is 4 or 5 or 7 or 9 && raw[1..].All(Uri.IsHexDigit);
        return raw.Length is 6 or 8 && raw.All(Uri.IsHexDigit);
    }

    public static string ToHex(MediaColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Entero para CSS, sin separador decimal de la cultura actual.</summary>
    private static string Int(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Decimal para CSS (alfa 0-1), siempre con punto.</summary>
    private static string Css(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static byte Hex(char hi, char lo) =>
        byte.Parse(new string(new[] { hi, lo }), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static (double H, double S, double L) ToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;
        double l = (max + min) / 2;
        double h = 0, s = 0;

        if (delta > 1e-9)
        {
            s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

            if (Math.Abs(max - rd) < 1e-9) h = ((gd - bd) / delta + (gd < bd ? 6 : 0)) / 6;
            else if (Math.Abs(max - gd) < 1e-9) h = ((bd - rd) / delta + 2) / 6;
            else h = ((rd - gd) / delta + 4) / 6;
        }

        return (h * 360, s * 100, l * 100);
    }
}
