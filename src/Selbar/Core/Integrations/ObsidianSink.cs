using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Selbar.Core.Integrations;

/// <summary>
/// Guarda la selección como nota Markdown dentro de un vault de Obsidian.
/// Todo local: escribe un archivo y no habla con ningún servicio.
/// </summary>
public static class ObsidianSink
{
    public sealed record SaveResult(bool Ok, string Message, string? Path);

    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Valida la configuración antes de intentar escribir.</summary>
    public static string? Validate(string? vault)
    {
        string clean = CleanVault(vault);
        if (clean.Length == 0)
            return "Configura la carpeta de tu vault en Ajustes → Avanzado → Integraciones.";
        if (!Directory.Exists(clean))
            return "La carpeta del vault no existe: " + clean;
        return null;
    }

    /// <summary>
    /// Ruta del vault sin espacios ni comillas de adorno: «Copiar como ruta» del
    /// Explorador pega la ruta entre comillas y antes se rechazaba como si no existiera.
    /// </summary>
    private static string CleanVault(string? vault) =>
        (vault ?? "").Trim().Trim('"').Trim();

    /// <summary>
    /// Escribe la nota y devuelve la ruta final. Si ya existe un archivo con ese
    /// nombre añade " (2)", " (3)"… para no sobrescribir nada del usuario.
    /// </summary>
    public static SaveResult Save(string? vault, string? folder, string text, string? sourceApp, DateTime? now = null)
    {
        string cleanVault = CleanVault(vault);
        string? invalid = Validate(cleanVault);
        if (invalid != null) return new SaveResult(false, invalid, null);

        string body = (text ?? "").Trim();
        if (body.Length == 0) return new SaveResult(false, "No hay texto seleccionado que guardar.", null);

        DateTime stamp = now ?? DateTime.Now;
        try
        {
            // Sin el separador final: con él, la comprobación de que la subcarpeta
            // sigue dentro del vault fallaba y no se guardaba nada.
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cleanVault));
            var (sub, folderOk) = SanitizeFolder(folder);
            if (!folderOk)
                return new SaveResult(false, "La subcarpeta no es válida (¿contiene \"..\"?).", null);
            string dir = sub.Length == 0 ? root : Path.Combine(root, sub);
            // Cinturón y tirantes: aunque SanitizeFolder ya deja la subcarpeta sin
            // separadores ni "..", se comprueba que el destino sigue dentro del vault.
            string fullDir = Path.GetFullPath(dir);
            if (!fullDir.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !fullDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new SaveResult(false, "La subcarpeta queda fuera del vault.", null);
            Directory.CreateDirectory(fullDir);

            string name = SuggestFileName(body, stamp);
            string content = BuildMarkdown(body, sourceApp, stamp);
            string? path = WriteNewFile(fullDir, name, content);
            if (path == null)
                return new SaveResult(false, "Hay demasiadas notas con ese nombre. Cambia el texto o la fecha.", null);

            return new SaveResult(true, "Guardado en Obsidian:\n" + path, path);
        }
        catch (Exception ex)
        {
            return new SaveResult(false, "No se pudo guardar la nota: " + ex.Message, null);
        }
    }

    /// <summary>Frontmatter mínimo (YAML válido) + el texto tal cual.</summary>
    public static string BuildMarkdown(string text, string? sourceApp, DateTime stamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("creado: " + stamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(sourceApp))
            sb.AppendLine("origen: '" + OneLine(sourceApp!).Replace("'", "''") + "'");
        sb.AppendLine("tags: [powertools]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(text.Trim());
        return sb.ToString();
    }

    /// <summary>"2026-10-26 1530 Primeras palabras de la nota.md"</summary>
    public static string SuggestFileName(string text, DateTime stamp)
    {
        string firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Nota";
        string slug = Slug(firstLine);
        if (slug.Length > 48)
        {
            int cut = 48;
            // No partir un par sustituto (emoji) por la mitad: quedaría un nombre inválido.
            if (char.IsHighSurrogate(slug[cut - 1])) cut--;
            slug = cut <= 0 ? "Nota" : slug[..cut].TrimEnd('-', ' ');
        }
        if (slug.Length == 0) slug = "Nota";
        // La fecha va en calendario gregoriano y con dígitos ASCII pase lo que pase:
        // con la cultura del sistema (p. ej. ar-SA) el nombre salía en año hijri.
        return stamp.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + " " + slug + ".md";
    }

    private static string Slug(string line)
    {
        string clean = Regex.Replace(line, @"[\\/:*?""<>|#^\[\]]+", " ");
        // Caracteres de control: Windows no los admite en un nombre y un texto
        // pegado puede traerlos (p. ej. un \u0001 de un PDF copiado).
        clean = Regex.Replace(clean, @"[\u0000-\u001F\u007F]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Replace(' ', '-');
    }

    /// <summary>
    /// Limpia la subcarpeta: sin separadores (una sola carpeta) y sin "..".
    /// Devuelve (nombre, válida). Vacío o solo separadores = la raíz del vault;
    /// si queda fuera del vault, Válida = false para no escribir nada.
    /// </summary>
    private static (string Name, bool Valid) SanitizeFolder(string? folder)
    {
        string f = (folder ?? "").Trim().Trim('/', '\\');
        if (f.Length == 0) return ("", true); // sin subcarpeta (o "/", "\\": la raíz)
        if (f.Contains("..")) return ("", false);
        foreach (char c in InvalidNameChars) f = f.Replace(c, '-');
        string clean = f.Trim('-', ' ');
        return clean.Length == 0 ? ("", false) : (clean, true);
    }

    /// <summary>
    /// Escribe la nota con creación exclusiva: si la ruta ya existe (nota ajena u
    /// otro guardado concurrente) no se trunca, se prueba con " (2)", " (3)"…
    /// Devuelve null si todas las variantes existen (nunca se sobrescribe nada).
    /// </summary>
    private static string? WriteNewFile(string dir, string fileName, string content)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        for (int n = 1; n < 1000; n++)
        {
            string candidate = n == 1
                ? Path.Combine(dir, fileName)
                : Path.Combine(dir, $"{baseName} ({n}){ext}");
            try
            {
                using var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(bytes, 0, bytes.Length);
                // Volcar a disco: una nota del usuario no debe quedarse a medias
                // si el equipo se apaga justo después de guardarla. Best-effort
                // porque un vault en red puede no soportar FlushFileBuffers.
                try { fs.Flush(true); } catch { }
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue; // ocupado: siguiente número
            }
        }
        return null;
    }

    private static string OneLine(string s) => Regex.Replace(s, @"[\r\n]+", " ").Trim();
}
