using System.Net;
using System.Text.RegularExpressions;

namespace Selbar.Core.Web;

/// <summary>
/// Convierte HTML en texto legible (sin dependencias). Se usa para resumir un
/// enlace: quita scripts, estilos y etiquetas, conserva los saltos de párrafo
/// y decodifica entidades.
/// </summary>
public static class HtmlText
{
    public const int DefaultMaxChars = 24000;

    /// <summary>Un título más largo que esto no cabe en ninguna ventana.</summary>
    public const int MaxTitleChars = 160;

    /// <summary>Tope para cada regex: un HTML patológico no debe colgar la barra.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(3);

    public static string ExtractTitle(string? html)
    {
        try
        {
            var match = Regex.Match(html ?? "", @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            if (!match.Success) return "";

            // Decodificar primero: &amp; y &nbsp; no deben quedar como espacios raros.
            string title = WebUtility.HtmlDecode(match.Groups[1].Value);
            title = Regex.Replace(title, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
            // Un <title> de 5 MB rompería la barra de título de la ventana.
            return Truncate(title, MaxTitleChars);
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    /// <summary>Corta sin partir un par suplente (medio emoji deja UTF-16 inválido).</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = max;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut].TrimEnd() + "…";
    }

    public static string ToPlain(string? html, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        try
        {
            return ToPlainCore(html, maxChars);
        }
        catch (RegexMatchTimeoutException)
        {
            // Plan B: tira las etiquetas sin matices sobre un prefijo.
            string slice = html.Length > maxChars * 2 ? html[..(maxChars * 2)] : html;
            var sb = new System.Text.StringBuilder(slice.Length);
            bool inTag = false;
            foreach (char c in slice)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; sb.Append(' '); continue; }
                if (!inTag) sb.Append(c);
            }
            string s = WebUtility.HtmlDecode(sb.ToString());
            s = s.Trim();
            return Truncate(s, maxChars);
        }
    }

    private static string ToPlainCore(string html, int maxChars)
    {
        // Etiquetas con cierre y, si el HTML viene roto y no lo tienen, hasta el
        // final: un <script> sin cerrar dejaba el código JS como si fuera texto.
        string s = Regex.Replace(html, @"<(script|style|noscript|svg)\b[^>]*>.*?(?:</\1\s*>|$)", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        s = Regex.Replace(s, @"<head[^>]*>.*?</head\s*>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline, RegexTimeout);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase, RegexTimeout);
        s = Regex.Replace(s, @"</(p|div|li|h[1-6]|tr|section|article|blockquote)>", "\n", RegexOptions.IgnoreCase, RegexTimeout);
        s = Regex.Replace(s, @"<[^>]+>", " ", RegexOptions.None, RegexTimeout);
        s = WebUtility.HtmlDecode(s);
        // U+00A0 (&amp;nbsp;) y los espacios finos no son "espacio" para la regex
        // de abajo: quedaban en el texto y el modelo los veía como caracteres raros.
        s = Regex.Replace(s, @"[ \t\f\v\u00A0\u2007\u202F]+", " ", RegexOptions.None, RegexTimeout);
        s = Regex.Replace(s, @"\n\s*\n\s*", "\n\n", RegexOptions.None, RegexTimeout);
        s = s.Trim();

        return Truncate(s, maxChars);
    }
}
