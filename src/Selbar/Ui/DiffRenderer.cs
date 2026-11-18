using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace Selbar.Ui;

/// <summary>
/// Diff palabra a palabra entre el texto original y el resultado (LCS).
/// Sirve para que "Corregir" sea confiable: se ve exactamente qué cambia.
/// </summary>
public static class DiffRenderer
{
    public enum DiffKind { Same, Added, Removed }

    public sealed class DiffSegment
    {
        public string Text = "";
        public DiffKind Kind;
    }

    /// <summary>Más tokens que esto se muestra sin resaltado (evita matrices enormes).</summary>
    private const int MaxTokens = 1500;

    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static SolidColorBrush Fg => ThemeBrushes.Get("TextPrimary", 0xD8, 0xD8, 0xD8);
    private static SolidColorBrush AddedFg => ThemeBrushes.Get("Success", 0x8F, 0xE3, 0x9A);
    private static SolidColorBrush AddedBg => ThemeBrushes.Tint("Success", 0x2E, 0x1E, 0x3A, 0x22);
    private static SolidColorBrush RemovedFg => ThemeBrushes.Get("Danger", 0xFF, 0x9A, 0xA2);
    private static SolidColorBrush RemovedBg => ThemeBrushes.Tint("Danger", 0x2E, 0x3A, 0x1E, 0x22);
    private static SolidColorBrush Muted => ThemeBrushes.Get("TextTertiary", 0x9A, 0x9A, 0x9A);

    /// <summary>Calcula los segmentos del diff (palabras y espacios).</summary>
    public static List<DiffSegment> Compute(string? before, string? after)
    {
        var a = Tokenize(before ?? "");
        var b = Tokenize(after ?? "");
        var result = new List<DiffSegment>();

        if (a.Count == 0)
        {
            foreach (var token in b) Add(result, token, DiffKind.Added);
            return result;
        }
        if (b.Count == 0)
        {
            foreach (var token in a) Add(result, token, DiffKind.Removed);
            return result;
        }

        if (a.Count > MaxTokens || b.Count > MaxTokens)
        {
            result.Add(new DiffSegment { Text = before ?? "", Kind = DiffKind.Removed });
            result.Add(new DiffSegment { Text = "\n", Kind = DiffKind.Same });
            result.Add(new DiffSegment { Text = after ?? "", Kind = DiffKind.Added });
            return result;
        }

        int n = a.Count, m = b.Count;
        var lcs = new short[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? (short)(lcs[i + 1, j + 1] + 1)
                    : (short)Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                Add(result, a[x], DiffKind.Same);
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                Add(result, a[x], DiffKind.Removed);
                x++;
            }
            else
            {
                Add(result, b[y], DiffKind.Added);
                y++;
            }
        }
        while (x < n) Add(result, a[x++], DiffKind.Removed);
        while (y < m) Add(result, b[y++], DiffKind.Added);

        return result;
    }

    /// <summary>Documento sin formato markdown, con el original tachado y lo nuevo resaltado.</summary>
    public static FlowDocument ToFlowDocument(string? before, string? after, double fontSize = 13.5)
    {
        var doc = new FlowDocument
        {
            FontFamily = TextFont,
            FontSize = fontSize,
            Foreground = Fg,
            PagePadding = new Thickness(2),
            LineHeight = fontSize + 8
        };

        var legend = new Paragraph { Margin = new Thickness(0, 0, 0, 10), FontSize = fontSize - 2.5 };
        legend.Inlines.Add(new Run("− original   ") { Foreground = RemovedFg });
        legend.Inlines.Add(new Run("+ resultado") { Foreground = AddedFg });
        doc.Blocks.Add(legend);

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var segment in Compute(before, after))
        {
            var run = new Run(segment.Text);
            switch (segment.Kind)
            {
                case DiffKind.Added:
                    run.Foreground = AddedFg;
                    run.Background = AddedBg;
                    break;
                case DiffKind.Removed:
                    run.Foreground = RemovedFg;
                    run.Background = RemovedBg;
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
                default:
                    run.Foreground = Fg;
                    break;
            }
            paragraph.Inlines.Add(run);
        }

        if (paragraph.Inlines.Count == 0)
            paragraph.Inlines.Add(new Run("(sin cambios)") { Foreground = Muted });

        doc.Blocks.Add(paragraph);
        return doc;
    }

    private static void Add(List<DiffSegment> segments, string text, DiffKind kind)
    {
        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1].Text += text;
            return;
        }
        segments.Add(new DiffSegment { Text = text, Kind = kind });
    }

    /// <summary>Palabras y espacios por separado (para poder resaltar sin perder el espaciado).</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var buffer = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (buffer.Length > 0) { tokens.Add(buffer.ToString()); buffer.Clear(); }
                tokens.Add(ch.ToString());
            }
            else
            {
                buffer.Append(ch);
            }
        }
        if (buffer.Length > 0) tokens.Add(buffer.ToString());
        return tokens;
    }
}
