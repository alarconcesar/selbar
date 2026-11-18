using System.Text.RegularExpressions;

namespace Selbar.Core.Text;

/// <summary>
/// Correo electrónico: detectar, extraer y armar el mailto:.
/// Todo local y sin red. Solo sintaxis, no valida que el buzón exista.
/// </summary>
public static class EmailTools
{
    private static readonly Regex Pure = new(
        @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>¿El texto es (o contiene solo) un correo, con o sin nombre/mailto?</summary>
    public static bool IsEmail(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 320) return false;
        if (t.Contains('\n')) return false;

        // La puntuación de la frase ("juan@x.com,") no forma parte de la dirección.
        t = t.TrimEnd(')', ']', '}', ',', ';', ':', '.');
        if (t.Length == 0) return false;

        string? email = ExtractEmail(t);
        if (email == null) return false;
        // Sin basura fuera del correo: solo se permite nombre delante ("Nombre <mail>").
        int close = t.LastIndexOf('>');
        if (close >= 0 && t[(close + 1)..].Trim().Length > 0) return false;
        return true;
    }

    /// <summary>
    /// Extrae el correo de "juan@x.com", "Juan &lt;juan@x.com&gt;" o "mailto:juan@x.com".
    /// Devuelve null si no hay un correo válido.
    /// </summary>
    public static string? ExtractEmail(string? text)
    {
        string t = (text ?? "").Trim().Trim('"', '\'');
        if (t.Length == 0 || t.Length > 320 || t.Contains('\n')) return null;

        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            t = t["mailto:".Length..].Trim().Split('?')[0];

        // "Nombre <correo>": quedarse con lo que hay entre ángulos.
        int open = t.LastIndexOf('<');
        int close = t.LastIndexOf('>');
        if (open >= 0 && close > open)
            t = t[(open + 1)..close].Trim();

        t = t.Trim();
        // Quita paréntesis/corchetes que envuelven la dirección y la puntuación
        // de la frase; nunca el punto inicial (lo rechaza ValidShape).
        t = t.TrimStart('(', '[', '{').TrimEnd(')', ']', '}', ',', ';', ':', '.');
        if (t.Contains(' ')) return null;

        try
        {
            if (Pure.IsMatch(t) && ValidShape(t)) return t.ToLowerInvariant();
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    /// <summary>
    /// Forma válida: sin puntos consecutivos ni en bordes, dominios sin
    /// hífens en bordes ni puntos consecutivos.
    /// </summary>
    private static bool ValidShape(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        string local = email[..at], domain = email[(at + 1)..];
        if (local.Length > 64 || domain.Length > 253) return false;
        if (local.StartsWith('.') || local.EndsWith('.') || local.Contains("..")) return false;
        if (domain.StartsWith('.') || domain.StartsWith('-') || domain.EndsWith('.') || domain.EndsWith('-')) return false;
        if (domain.Contains("..")) return false;
        foreach (string part in domain.Split('.'))
            if (part.Length is 0 or > 63 || part.StartsWith('-') || part.EndsWith('-')) return false;
        return true;
    }

    /// <summary>
    /// Arma el enlace mailto: para redactar. Si le pasan "Nombre &lt;mail&gt;"
    /// o "mailto:…", primero extrae el correo; sin correo válido devuelve "".
    /// </summary>
    public static string ToMailto(string? text)
    {
        string? email = ExtractEmail(text);
        return email == null ? "" : "mailto:" + email;
    }
}
