using System.Text.RegularExpressions;

namespace Selbar.Core.Text;

/// <summary>
/// Rutas de archivo de Windows: detectar sintaxis y normalizar
/// (comillas, barras, %VARIABLES% y ~). No toca disco: la existencia
/// se comprueba donde se ejecuta la acción.
/// </summary>
public static class FilePathTools
{
    /// <summary>¿Tiene pinta de ruta? (C:\…, \\equipo\…, %VAR%\…, ~/…).</summary>
    public static bool IsFilePath(string? text)
    {
        string t = Unquote((text ?? "").Trim());
        if (t.Length is < 3 or > 500 || t.Contains('\n')) return false;

        // No confundir con URLs ni correos.
        string lower = t.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://") ||
            lower.StartsWith("ftp://") || lower.StartsWith("mailto:") ||
            lower.StartsWith("www.") || (t.Contains('@') && t.Contains('.') && !t.Contains('\\') && !t.Contains('/')))
            return false;

        if (Regex.IsMatch(t, @"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)) return true;
        if (t.StartsWith(@"\\") && t.Length > 4 && !t.StartsWith(@"\\\"))
        {
            // UNC: \\equipo\recurso (al menos dos componentes).
            string rest = t[2..];
            int slash = rest.IndexOf('\\');
            return slash > 0 && slash < rest.Length - 1;
        }
        if (Regex.IsMatch(t, @"^%[A-Za-z_][A-Za-z0-9_]*%([\\/]|$)", RegexOptions.CultureInvariant)) return true;
        if ((t.StartsWith("~/") || t.StartsWith(@"~\")) && t.Length >= 3) return true;
        return false;
    }

    /// <summary>
    /// Normaliza para mostrar y abrir: quita comillas, unifica barras en rutas
    /// con unidad/UNC, expande %VAR% y ~. Nunca lanza.
    /// </summary>
    public static string Normalize(string? text)
    {
        string t = Unquote((text ?? "").Trim());
        if (t.Length == 0) return "";

        try { t = Environment.ExpandEnvironmentVariables(t); } catch { }

        if (t.StartsWith("~") && (t.Length == 1 || t[1] is '/' or '\\'))
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) t = home + t[1..];
            }
            catch { }
        }

        if (Regex.IsMatch(t, @"^[A-Za-z]:[/\\]", RegexOptions.CultureInvariant) || t.StartsWith(@"\\"))
            t = t.Replace('/', '\\');

        return t.Trim();
    }

    private static readonly (char Open, char Close)[] QuotePairs =
    {
        ('"', '"'), ('\'', '\''), ('“', '”'), ('‘', '’'), ('«', '»')
    };

    private static string Unquote(string t)
    {
        t = t.Trim();
        foreach (var (open, close) in QuotePairs)
            if (t.Length >= 2 && t[0] == open && t[^1] == close)
                return t[1..^1].Trim();
        return t;
    }
}
