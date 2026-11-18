using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Selbar.Core.Platform;
using Selbar.Core.Text;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaColor = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace Selbar.Ui;

/// <summary>
/// Vista previa de un color HEX con sus representaciones. La primaria copia HEX8
/// cuando la entrada tiene alfa; el HEX sin alfa queda como alternativa explícita.
/// Las cadenas CSS se formatean con cultura invariante.
/// </summary>
public partial class ColorPreviewWindow : FluentWindow
{
    private ColorInfo? _info;
    private bool _showAlpha;
    private string _hex6 = "";
    private string _hex8 = "";
    private string _rgb = "";
    private string _hsl = "";

    public ColorPreviewWindow(string text)
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowKeyDown;

        if (!ColorCode.TryParse(text, out var info))
        {
            ShowInvalid(text);
            return;
        }

        _info = info;
        bool explicitAlpha = HasExplicitAlpha(text);
        _showAlpha = info.HasAlpha || explicitAlpha;
        _hex6 = info.Hex.ToUpperInvariant();
        _hex8 = $"#{info.Color.R:X2}{info.Color.G:X2}{info.Color.B:X2}{info.Color.A:X2}";
        _rgb = FormatRgb(info.Color, _showAlpha);
        _hsl = FormatHsl(info.Color, _showAlpha);

        SwatchColor.Background = new SolidColorBrush(info.Color);
        SwatchText.Text = _showAlpha ? _hex8 : _hex6;
        HexLabel.Text = _showAlpha ? "HEX8" : "HEX";
        HexText.Text = _showAlpha ? _hex8 : _hex6;
        RgbText.Text = _rgb;
        HslText.Text = _hsl;
        AlphaText.Text = info.Alpha;
        AlphaRow.Visibility = _showAlpha ? Visibility.Visible : Visibility.Collapsed;
        CopyHexBtn.Content = _showAlpha ? "Copiar HEX8" : "Copiar HEX";
        MoreBtn.Visibility = _showAlpha ? Visibility.Visible : Visibility.Collapsed;
        SetStatus("");
    }

    /// <summary>
    /// ¿La entrada traía un canal alfa explícito? #RGBA y #RRGGBBAA (o su forma
    /// sin #) lo llevan aunque el valor sea FF; ese FF no debe desaparecer al copiar.
    /// </summary>
    private static bool HasExplicitAlpha(string? text)
    {
        string raw = (text ?? "").Trim();
        if (raw.StartsWith('#')) raw = raw[1..];
        return raw.Length is 4 or 8;
    }

    private void ShowInvalid(string? text)
    {
        _info = null;
        _showAlpha = false;
        SwatchColor.Background = Brushes.Transparent;
        SwatchText.Text = "Color no válido";
        HexLabel.Text = "HEX";
        HexText.Text = text ?? "";
        RgbText.Text = HslText.Text = AlphaText.Text = "—";
        AlphaRow.Visibility = Visibility.Collapsed;
        CopyHexBtn.Content = "Copiar HEX";
        CopyHexBtn.IsEnabled = false;
        RgbCopyBtn.IsEnabled = false;
        HslCopyBtn.IsEnabled = false;
        MoreBtn.Visibility = Visibility.Collapsed;
        SetStatus("Color no válido. Formatos admitidos: #RGB, #RGBA, #RRGGBB o #RRGGBBAA.", error: true);
    }

    private void OnCopyHex(object s, RoutedEventArgs e)
    {
        if (_info == null) return;
        Copy(_showAlpha ? _hex8 : _hex6, _showAlpha ? "HEX8" : "HEX");
    }

    private void OnCopyHexNoAlpha(object s, RoutedEventArgs e)
    {
        if (_info == null) return;
        Copy(_hex6, "HEX sin alfa");
    }

    private void OnCopyValue(object sender, RoutedEventArgs e)
    {
        if (_info == null || sender is not Button { Tag: string kind }) return;
        string value = kind switch
        {
            "rgb" => _rgb,
            "hsl" => _hsl,
            _ => _hex6
        };
        string label = kind switch
        {
            "rgb" => _showAlpha ? "RGBA" : "RGB",
            "hsl" => _showAlpha ? "HSLA" : "HSL",
            _ => "HEX"
        };
        Copy(value, label);
    }

    private void Copy(string value, string label)
    {
        bool copied = ClipboardService.TrySetText(value);
        SetStatus(
            copied ? $"{label} copiado." : "No se pudo copiar: el portapapeles está ocupado.",
            error: !copied);
    }

    private void OnMore(object s, RoutedEventArgs e)
    {
        try
        {
            if (MoreBtn.ContextMenu is not { } menu) return;
            menu.PlacementTarget = MoreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch { }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Enter activa la primaria solo cuando el foco no está en otro botón.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button && CopyHexBtn.IsEnabled)
        {
            OnCopyHex(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }

    private static string FormatRgb(MediaColor color, bool withAlpha)
    {
        if (!withAlpha)
            return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", color.R, color.G, color.B);
        return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.##})",
            color.R, color.G, color.B, color.A / 255.0);
    }

    private static string FormatHsl(MediaColor color, bool withAlpha)
    {
        var (h, s, l) = ToHsl(color.R, color.G, color.B);
        if (!withAlpha)
            return string.Format(CultureInfo.InvariantCulture, "hsl({0:0}, {1:0}%, {2:0}%)", h, s, l);
        return string.Format(CultureInfo.InvariantCulture, "hsla({0:0}, {1:0}%, {2:0}%, {3:0.##})",
            h, s, l, color.A / 255.0);
    }

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
