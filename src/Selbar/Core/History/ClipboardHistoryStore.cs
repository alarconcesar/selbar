using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Selbar.Core.Platform;

namespace Selbar.Core.History;

/// <summary>Un texto que pasó por el portapapeles vía Selbar.</summary>
public sealed class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    /// <summary>Vista previa de una línea para la lista.</summary>
    public string Preview { get; set; } = "";
}

/// <summary>
/// Últimos 20 textos copiados (local, sin cifrar: es el portapapeles, no secretos).
/// Se guarda en clipboard-history.json junto a los demás datos.
/// </summary>
public static class ClipboardHistoryStore
{
    public const int MaxEntries = 20;
    private const int MaxTextChars = 4000;
    private const int MaxPreviewChars = 120;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static bool _corruptBackedUp;

    public static string FilePath => AppPaths.DataFile("clipboard-history.json");

    /// <summary>Registra un texto (sin duplicar el último). Nunca lanza.</summary>
    public static void Add(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > MaxTextChars) return;

        lock (Gate)
        {
            try
            {
                var all = ReadAllUnlocked(out bool readFailed);
                if (readFailed) return; // bloqueado: no reescribir sobre una lectura fallida
                if (all.Count > 0 && all[0].Text == t)
                {
                    all[0].When = DateTime.Now;
                    WriteAllUnlocked(all);
                    return;
                }
                all.RemoveAll(e => e.Text == t);
                all.Insert(0, new ClipboardEntry
                {
                    Text = t,
                    Preview = PreviewOf(t),
                    When = DateTime.Now
                });
                while (all.Count > MaxEntries) all.RemoveAt(all.Count - 1);
                WriteAllUnlocked(all);
            }
            catch { }
        }
    }

    public static List<ClipboardEntry> All(string? query = null)
    {
        lock (Gate)
        {
            var all = ReadAllUnlocked(out _);
            if (string.IsNullOrWhiteSpace(query)) return all;
            string q = query.Trim();
            return all.Where(e => e.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            try
            {
                var all = ReadAllUnlocked(out bool readFailed);
                if (readFailed) return;
                all.RemoveAll(e => e.Id == id);
                WriteAllUnlocked(all);
            }
            catch { }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }

    private static string PreviewOf(string t)
    {
        string one = t.Replace('\r', ' ').Replace('\n', ' ');
        one = System.Text.RegularExpressions.Regex.Replace(one, @"\s+", " ").Trim();
        if (one.Length <= MaxPreviewChars) return one;
        // No partir un par sustituto (emoji) por la mitad: la lista mostraba un rombo.
        int cut = MaxPreviewChars;
        if (cut > 0 && char.IsHighSurrogate(one[cut - 1])) cut--;
        return cut <= 0 ? "…" : one[..cut] + "…";
    }

    /// <summary>Lee todas las entradas. <paramref name="readFailed"/>: existe pero no se pudo leer.</summary>
    private static List<ClipboardEntry> ReadAllUnlocked(out bool readFailed)
    {
        readFailed = false;
        if (!File.Exists(FilePath)) return new();

        string json;
        try { json = File.ReadAllText(FilePath); }
        catch { readFailed = true; return new(); } // bloqueado o sin permisos: no es corrupción

        var all = new List<ClipboardEntry>();
        int broken = 0;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("El historial del portapapeles no es una lista.");

            // Entrada a entrada y campo a campo: una fecha mal escrita no debe
            // tirar las otras 19 (ni el texto de la propia entrada).
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { broken++; continue; }
                var entry = ReadEntry(item);
                if (entry == null) { broken++; continue; }
                all.Add(entry);
            }
        }
        catch
        {
            BackupCorrupt();
            return new();
        }

        if (broken > 0) BackupCorrupt();

        // Un JSON editado a mano puede traer nulls: un elemento null tumbaba
        // Add (NRE en el predicado) y colaba nulls a la UI. Se sanea al leer
        // (una entrada sin texto no sirve para nada: se descarta).
        all.RemoveAll(e => e is null || string.IsNullOrEmpty(e.Text));
        foreach (var e in all)
        {
            e.Id ??= Guid.NewGuid().ToString("N");
            e.Text ??= "";
            // Entradas de versiones anteriores (o editadas) sin Preview: sin esto
            // la lista mostraba filas en blanco aunque el texto estuviera ahí.
            if (string.IsNullOrEmpty(e.Preview)) e.Preview = PreviewOf(e.Text);
        }
        return all;
    }

    /// <summary>
    /// Convierte un elemento JSON en entrada tolerando campos con el tipo
    /// equivocado: el texto es lo único imprescindible.
    /// </summary>
    private static ClipboardEntry? ReadEntry(JsonElement item)
    {
        string? text = null;
        string id = "";
        string preview = "";
        DateTime when = DateTime.Now;
        foreach (var property in item.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "id":
                    if (property.Value.ValueKind == JsonValueKind.String) id = property.Value.GetString() ?? "";
                    break;
                case "when":
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out var stamp))
                        when = stamp;
                    break;
                case "text":
                    if (property.Value.ValueKind == JsonValueKind.String) text = property.Value.GetString();
                    break;
                case "preview":
                    if (property.Value.ValueKind == JsonValueKind.String) preview = property.Value.GetString() ?? "";
                    break;
            }
        }
        if (string.IsNullOrEmpty(text)) return null;
        return new ClipboardEntry
        {
            Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
            When = when,
            Text = text,
            Preview = preview
        };
    }

    /// <summary>Guarda una copia del archivo ilegible antes de que la próxima escritura lo reemplace.</summary>
    private static void BackupCorrupt()
    {
        if (_corruptBackedUp) return;
        _corruptBackedUp = true;
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, UniqueBackupPath(), false);
        }
        catch { }
    }

    private static string UniqueBackupPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = FilePath + ".corrupto-" + stamp;
        for (int n = 2; File.Exists(path) && n < 1000; n++)
            path = FilePath + ".corrupto-" + stamp + "-" + n;
        return path;
    }

    private static void WriteAllUnlocked(List<ClipboardEntry> all)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Atómico como HistoryStore: un corte a mitad de escritura no trunca el JSON.
            var tmp = FilePath + ".tmp";
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(all, Opts));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                try { fs.Flush(true); } catch { } // best-effort: no todos los FS lo soportan
            }
            File.Move(tmp, FilePath, true);
            // Tras una escritura correcta, una corrupción futura merece su propia copia.
            _corruptBackedUp = false;
        }
        catch { }
    }
}
