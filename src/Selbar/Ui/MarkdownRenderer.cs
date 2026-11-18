using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Selbar.Ui;

/// <summary>
/// Markdown ligero → FlowDocument con estilo oscuro.
/// Soporta: cabeceras (# y setext), listas (•/1., anidadas y de tareas), citas,
/// código inline y en bloque, tablas con alineación, separadores, **negrita**,
/// *cursiva*, ~~tachado~~, `código` y [enlaces] / &lt;autolinks&gt;.
/// Es best-effort: ante sintaxis rota muestra el texto tal cual sin romper nunca.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Más allá de esto no se analiza: se muestra el texto plano (evita congelar la UI).</summary>
    private const int MaxRenderChars = 400_000;

    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    // Se leen del tema en cada render: el mismo markdown se ve bien en claro y oscuro.
    private static SolidColorBrush Fg => ThemeBrushes.Get("TextPrimary", 0xF3, 0xF3, 0xF3);
    private static SolidColorBrush Gray => ThemeBrushes.Get("TextTertiary", 0x99, 0x99, 0x99);
    private static SolidColorBrush Accent => ThemeBrushes.Get("AccentText", 0x60, 0xCD, 0xFF);
    private static SolidColorBrush CodeFg => ThemeBrushes.Get("Warning", 0xFF, 0xB8, 0x6B);
    private static SolidColorBrush CodeBg => ThemeBrushes.Get("BgWell", 0x1A, 0x1A, 0x1A);
    private static SolidColorBrush CodeText => ThemeBrushes.Get("TextSecondary", 0xD6, 0xD6, 0xD6);
    private static SolidColorBrush CodeBorder => ThemeBrushes.Get("StrokeSubtle", 0x34, 0x34, 0x34);
    private static SolidColorBrush Rule => ThemeBrushes.Get("StrokeSubtle", 0x44, 0x44, 0x44);
    private static SolidColorBrush TableBorder => ThemeBrushes.Get("StrokeSubtle", 0x44, 0x44, 0x44);
    private static SolidColorBrush QuoteBar => ThemeBrushes.Get("AccentFill", 0x60, 0xCD, 0xFF);
    private static SolidColorBrush ErrorFg => ThemeBrushes.Get("Danger", 0xFF, 0x99, 0xA4);
    private static SolidColorBrush HeaderRow => ThemeBrushes.Get("BgBadge", 0x2A, 0x2A, 0x2A);

    private static string Normalize(string? markdown) =>
        (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    public static FlowDocument ToFlowDocument(string markdown, double fontSize = 13.5)
    {
        var doc = new FlowDocument
        {
            FontFamily = TextFont,
            FontSize = fontSize,
            Foreground = Fg,
            PagePadding = new Thickness(2),
            LineHeight = fontSize + 8.5
        };

        var src = Normalize(markdown);
        if (src.Length == 0) return doc;

        if (src.Length > MaxRenderChars)
        {
            doc.Blocks.Add(new Paragraph(new Run(src)));
            return doc;
        }

        try
        {
            Render(doc, src, fontSize);
        }
        catch
        {
            doc.Blocks.Clear();
            doc.Blocks.Add(new Paragraph(new Run(src)));
        }
        return doc;
    }

    // ---------------- Bloques ----------------

    private static void Render(FlowDocument doc, string src, double fontSize)
    {
        var lines = src.Split('\n');
        var para = new List<string>();
        int i = 0;

        void FlushPara()
        {
            if (para.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(p.Inlines, string.Join(" ", para));
            doc.Blocks.Add(p);
            para.Clear();
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var t = raw.Trim();

            if (t.Length == 0) { FlushPara(); i++; continue; }

            // Bloque de código (``` o ~~~)
            if (IsFence(t))
            {
                FlushPara();
                i = ConsumeCode(lines, i, doc, fontSize);
                continue;
            }

            // Cabecera ATX
            if (t[0] == '#' && TryHeading(t, out int level, out var heading))
            {
                FlushPara();
                doc.Blocks.Add(Header(heading, HeadingSize(fontSize, level)));
                i++;
                continue;
            }

            // Cabecera setext (solo con "=", que no es ambiguo)
            if (i + 1 < lines.Length && IsSetextUnderline(lines[i + 1].Trim(), '='))
            {
                FlushPara();
                doc.Blocks.Add(Header(t, fontSize + 7));
                i += 2;
                continue;
            }

            // Separador
            if (IsHr(t))
            {
                FlushPara();
                doc.Blocks.Add(Hr());
                i++;
                continue;
            }

            // Cita
            if (t[0] == '>')
            {
                FlushPara();
                i = ConsumeQuote(lines, i, doc);
                continue;
            }

            // Tabla
            if (IsTableHeader(lines, i))
            {
                FlushPara();
                i = ConsumeTable(lines, i, doc);
                continue;
            }

            // Lista
            if (IsListItem(t))
            {
                FlushPara();
                var list = NewList(IsOrderedMarker(t));
                i = ConsumeList(lines, i, Indent(raw), list, fontSize);
                doc.Blocks.Add(list);
                continue;
            }

            para.Add(t);
            i++;
        }

        FlushPara();
    }

    private static double HeadingSize(double fontSize, int level) => level switch
    {
        1 => fontSize + 7,
        2 => fontSize + 5,
        3 => fontSize + 3,
        4 => fontSize + 2,
        _ => fontSize + 1
    };

    private static bool TryHeading(string t, out int level, out string text)
    {
        level = 0;
        text = "";
        while (level < t.Length && t[level] == '#') level++;
        if (level is < 1 or > 6) return false;
        if (level < t.Length && t[level] != ' ') return false;
        text = t[level..].Trim();
        return text.Length > 0;
    }

    private static bool IsFence(string t) =>
        t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);

    private static bool IsHr(string t)
    {
        if (t.Length < 3) return false;
        var c = t[0];
        if (c is not ('-' or '*' or '_')) return false;
        int count = 0;
        foreach (var ch in t)
        {
            if (ch == c) count++;
            else if (ch != ' ') return false;
        }
        return count >= 3;
    }

    private static bool IsSetextUnderline(string t, char expected)
    {
        if (t.Length < 2) return false;
        foreach (var c in t)
            if (c != expected) return false;
        return true;
    }

    private static int ConsumeCode(string[] lines, int i, FlowDocument doc, double fontSize)
    {
        int start = i;
        var fence = lines[i].Trim();
        var language = fence.TrimStart('`', '~').Trim();
        i++; // salta el fence de apertura
        var code = new List<string>();
        while (i < lines.Length && !IsFence(lines[i].Trim()))
        {
            code.Add(lines[i]);
            i++;
        }
        if (i < lines.Length) i++; // salta el de cierre (si lo hay)

        doc.Blocks.Add(BuildCodeBlock(code, language, fontSize));

        // Si el bloque no se cerró, devolvemos el índice de fin (nunca un bucle infinito).
        return Math.Max(i, start + 1);
    }

    /// <summary>Bloque de código en una tarjeta con etiqueta de lenguaje y botón Copiar.</summary>
    private static Block BuildCodeBlock(List<string> code, string language, double fontSize)
    {
        var source = string.Join("\n", code);

        var body = new System.Windows.Controls.TextBlock
        {
            Text = source,
            FontFamily = MonoFont,
            FontSize = Math.Max(9, fontSize - 1),
            Foreground = CodeText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 10)
        };

        var copy = new System.Windows.Controls.Button
        {
            Content = "Copiar",
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10.5,
            ToolTip = "Copiar este bloque",
            Style = TryStyle("SubtleButton")
        };
        copy.Click += (_, _) =>
        {
            if (!Selbar.Core.Platform.ClipboardService.TrySetText(source)) return;
            copy.Content = "Copiado";
            try
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, _) => { timer.Stop(); copy.Content = "Copiar"; };
                timer.Start();
            }
            catch { }
        };

        var header = new System.Windows.Controls.DockPanel { Margin = new Thickness(12, 6, 8, 4) };
        System.Windows.Controls.DockPanel.SetDock(copy, System.Windows.Controls.Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "código" : language,
            FontSize = 10.5,
            Foreground = Gray,
            VerticalAlignment = VerticalAlignment.Center
        });

        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        var card = new System.Windows.Controls.Border
        {
            Background = CodeBg,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 2, 0, 10),
            Child = stack,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ClipToBounds = true
        };

        return new BlockUIContainer(card);
    }

    private static Style? TryStyle(string key)
    {
        try { return System.Windows.Application.Current?.TryFindResource(key) as Style; }
        catch { return null; }
    }

    private static int ConsumeQuote(string[] lines, int i, FlowDocument doc)
    {
        var quote = new List<string>();
        while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
        {
            var t = lines[i].Trim();
            quote.Add(t.Length > 1 ? t[1..].TrimStart() : "");
            i++;
        }

        string joined = string.Join(" ", quote.Where(q => q.Length > 0)).Trim();
        bool isError = joined.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase);

        var p = new Paragraph
        {
            Foreground = isError ? ErrorFg : Gray,
            BorderBrush = isError ? ErrorFg : QuoteBar,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 8)
        };
        AddInlines(p.Inlines, joined);
        doc.Blocks.Add(p);
        return i;
    }

    // ---------------- Listas ----------------

    private static System.Windows.Documents.List NewList(bool ordered) => new()
    {
        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        Margin = new Thickness(0, 0, 0, 8),
        Padding = new Thickness(0),
        MarkerOffset = 12
    };

    private static bool IsListItem(string t) => IsBulletMarker(t) || IsOrderedMarker(t);

    private static bool IsBulletMarker(string t) =>
        t.Length >= 1 && (t[0] == '-' || t[0] == '*' || t[0] == '+') && (t.Length == 1 || t[1] == ' ');

    private static bool IsOrderedMarker(string t)
    {
        int d = t.IndexOf('.');
        if (d <= 0 || d > 3) return false;
        for (int k = 0; k < d; k++)
            if (!char.IsDigit(t[k])) return false;
        return t.Length == d + 1 || t[d + 1] == ' ';
    }

    /// <summary>Nivel de sangría (2 espacios = 1 nivel; tab = 4 espacios).</summary>
    private static int Indent(string line)
    {
        int spaces = 0;
        foreach (var c in line)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') spaces += 4;
            else break;
        }
        return spaces / 2;
    }

    private static int ConsumeList(string[] lines, int i, int baseIndent,
        System.Windows.Documents.List target, double fontSize)
    {
        ListItem? lastItem = null;

        while (i < lines.Length)
        {
            var raw = lines[i];
            var t = raw.Trim();
            if (t.Length == 0 || !IsListItem(t)) break;

            int indent = Indent(raw);
            if (indent < baseIndent) break;

            if (indent > baseIndent)
            {
                // Lista anidada dentro del último elemento del nivel actual.
                if (lastItem == null) break;
                var nested = NewList(IsOrderedMarker(t));
                i = ConsumeList(lines, i, indent, nested, fontSize);
                lastItem.Blocks.Add(nested);
                continue;
            }

            var item = new ListItem();
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };

            if (TryTaskItem(t, out bool done, out var taskText))
            {
                p.Inlines.Add(new Run(done ? "\u2611 " : "\u2610 ") { FontFamily = SymbolFont });
                AddInlines(p.Inlines, taskText);
            }
            else
            {
                AddInlines(p.Inlines, StripMarker(t));
            }

            item.Blocks.Add(p);
            target.ListItems.Add(item);
            lastItem = item;
            i++;
        }
        return i;
    }

    private static string StripMarker(string t)
    {
        if (IsBulletMarker(t)) return t.Length > 2 ? t[2..].Trim() : "";
        int d = t.IndexOf('.');
        return d >= 0 && t.Length > d + 1 ? t[(d + 1)..].Trim() : t;
    }

    private static bool TryTaskItem(string t, out bool done, out string text)
    {
        done = false;
        text = "";
        if (!IsBulletMarker(t)) return false;
        var body = t.Length > 2 ? t[2..].TrimStart() : "";
        if (body.Length < 3 || body[0] != '[' || body[2] != ']') return false;
        char mark = char.ToLowerInvariant(body[1]);
        if (mark != ' ' && mark != 'x') return false;
        done = mark == 'x';
        text = body.Length > 3 ? body[3..].TrimStart() : "";
        return true;
    }

    // ---------------- Tablas ----------------

    private static bool IsTableHeader(string[] lines, int i)
    {
        if (i + 1 >= lines.Length) return false;
        var t = lines[i].Trim();
        return t.Contains('|') && IsTableSeparator(lines[i + 1].Trim());
    }

    private static bool IsTableSeparator(string t)
    {
        if (t.Length == 0 || !t.Contains('-')) return false;
        foreach (var c in t)
            if (c is not ('-' or ':' or '|' or ' ')) return false;
        return true;
    }

    private static string[] SplitRow(string t) =>
        t.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static int ConsumeTable(string[] lines, int i, FlowDocument doc)
    {
        var header = SplitRow(lines[i].Trim());
        var align = ParseAlignments(lines[i + 1].Trim(), header.Length);
        i += 2;

        var rows = new List<string[]> { header };
        while (i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains('|'))
        {
            rows.Add(SplitRow(lines[i].Trim()));
            i++;
        }

        doc.Blocks.Add(BuildTable(rows, align));
        return i;
    }

    private enum Align { Left, Center, Right }

    private static Align[] ParseAlignments(string separator, int columns)
    {
        var cells = SplitRow(separator);
        var result = new Align[Math.Max(columns, cells.Length)];
        for (int c = 0; c < result.Length; c++)
        {
            var cell = c < cells.Length ? cells[c] : "---";
            bool left = cell.StartsWith(':');
            bool right = cell.EndsWith(':');
            result[c] = left && right ? Align.Center : right ? Align.Right : Align.Left;
        }
        return result;
    }

    private static Table BuildTable(List<string[]> rows, Align[] align)
    {
        var table = new Table
        {
            Margin = new Thickness(0, 0, 0, 10),
            CellSpacing = 0,
            BorderBrush = TableBorder,
            BorderThickness = new Thickness(1, 1, 0, 0)
        };

        int cols = rows.Max(r => r.Length);
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        bool header = true;
        foreach (var row in rows)
        {
            var tr = new TableRow { Background = header ? HeaderRow : null };
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell
                {
                    BorderBrush = TableBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    TextAlignment = (c < align.Length ? align[c] : Align.Left) switch
                    {
                        Align.Center => TextAlignment.Center,
                        Align.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    }
                };
                if (header) p.FontWeight = FontWeights.SemiBold;
                AddInlines(p.Inlines, c < row.Length ? row[c] : "");
                cell.Blocks.Add(p);
                tr.Cells.Add(cell);
            }
            group.Rows.Add(tr);
            header = false;
        }
        return table;
    }

    private static Paragraph Header(string text, double size)
    {
        // El tamaño/peso heredan a los inlines (incluida negrita o código dentro del título).
        var p = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 6),
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg
        };
        AddInlines(p.Inlines, text.Trim());
        return p;
    }

    private static Block Hr() => new BlockUIContainer(new System.Windows.Controls.Border
    {
        Height = 1,
        Background = Rule,
        Margin = new Thickness(0, 6, 0, 14)
    });

    // ---------------- Inlines ----------------

    private static readonly Regex LinkPattern = new(
        @"\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void AddInlines(InlineCollection inlines, string? text, int depth = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (depth > 6) { inlines.Add(new Run(text)); return; }

        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            inlines.Add(new Run(buf.ToString()));
            buf.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buf.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeFg,
                        Background = CodeBg
                    });
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    var bold = new Bold();
                    AddInlines(bold.Inlines, text[(i + 2)..end], depth + 1);
                    inlines.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(strike.Inlines, text[(i + 2)..end], depth + 1);
                    inlines.Add(strike);
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '*')
            {
                int end = FindEmphasisEnd(text, i + 1, '*');
                if (end > i + 1)
                {
                    Flush();
                    var italic = new Italic();
                    AddInlines(italic.Inlines, text[(i + 1)..end], depth + 1);
                    inlines.Add(italic);
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var match = LinkPattern.Match(text, i);
                if (match.Success && match.Index == i)
                {
                    Flush();
                    AddLink(inlines, match.Groups[1].Value, match.Groups[2].Value);
                    i = match.Index + match.Length;
                    continue;
                }
            }
            else if (c == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i + 1)
                {
                    var inner = text[(i + 1)..end];
                    if (Uri.TryCreate(inner, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
                    {
                        Flush();
                        AddLink(inlines, inner, inner);
                        i = end + 1;
                        continue;
                    }
                }
            }

            buf.Append(c);
            i++;
        }

        Flush();
    }

    private static int FindEmphasisEnd(string text, int start, char marker)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] != marker) continue;
            if (i == start) continue;                                  // "*" vacío
            if (char.IsWhiteSpace(text[i - 1])) continue;               // el cierre no va tras espacio
            if (i + 1 < text.Length && text[i + 1] == marker) { i++; continue; } // parte de "**"
            return i;
        }
        return -1;
    }

    private static void AddLink(InlineCollection inlines, string label, string url)
    {
        var link = new Hyperlink(new Run(string.IsNullOrWhiteSpace(label) ? url : label))
        {
            Foreground = Accent,
            TextDecorations = null,
            ToolTip = url
        };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += (_, args) =>
            {
                try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
                args.Handled = true;
            };
        }
        inlines.Add(link);
    }

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '>' or '~' or '|';

    // ---------------- Texto plano ----------------

    /// <summary>Texto sin marcas de Markdown (para "Copiar limpio" y para guardar en .txt).</summary>
    public static string ToPlainText(string? markdown)
    {
        var src = Normalize(markdown);
        if (src.Length == 0) return "";

        var sb = new StringBuilder(src.Length);
        bool inFence = false;

        foreach (var raw in src.Split('\n'))
        {
            var t = raw.Trim();

            if (IsFence(t))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                sb.AppendLine(raw);
                continue;
            }
            if (t.Length == 0)
            {
                sb.AppendLine();
                continue;
            }
            if (IsHr(t)) continue;
            if (t.Contains('|') && IsTableSeparator(t)) continue;

            if (t[0] == '#')
            {
                t = t.TrimStart('#').Trim();
            }
            else if (t.StartsWith('>'))
            {
                t = t.TrimStart('>').Trim();
            }
            else if (IsListItem(t))
            {
                var marker = IsBulletMarker(t) ? "- " : t[..(t.IndexOf('.') + 1)] + " ";
                t = marker + StripInline(StripMarker(t));
            }
            else if (t.StartsWith('|') && t.EndsWith('|'))
            {
                t = string.Join(" | ", SplitRow(t).Select(StripInline));
            }
            else
            {
                t = StripInline(t);
            }

            sb.AppendLine(t);
        }

        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Quita las marcas inline (negritas, código, enlaces...) de una línea.</summary>
    private static string StripInline(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        s = LinkPattern.Replace(s, m =>
        {
            var label = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            return string.IsNullOrWhiteSpace(label) || label == url ? url : $"{label} ({url})";
        });
        s = Regex.Replace(s, @"<((?:https?://|mailto:)[^>]+)>", "$1");
        s = s.Replace("**", "").Replace("~~", "");
        s = Regex.Replace(s, @"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])", "$1");
        s = Regex.Replace(s, "`([^`]*)`", "$1");
        s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!>~|])", "$1");
        return s;
    }
}
