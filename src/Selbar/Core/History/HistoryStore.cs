using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Core.Platform;

namespace Selbar.Core.History;

/// <summary>Resultado de IA guardado en el historial.</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    /// <summary>Id de la acción que lo generó (para "Repetir").</summary>
    public string ActionId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Model { get; set; } = "";
    public string InputPreview { get; set; } = "";
    /// <summary>Texto original completo (recortado), para volver a ejecutar la acción.</summary>
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    /// <summary>True si los campos de texto están cifrados con DPAPI.</summary>
    public bool Encrypted { get; set; }

    /// <summary>Fecha legible ("hace 5 min") para la lista; no se guarda.</summary>
    [JsonIgnore]
    public string WhenRelative
    {
        get
        {
            var delta = DateTime.Now - When;
            if (delta < TimeSpan.Zero) return "ahora";
            if (delta.TotalSeconds < 60) return "ahora";
            if (delta.TotalMinutes < 60) return $"hace {(int)delta.TotalMinutes} min";
            if (delta.TotalHours < 24) return $"hace {(int)delta.TotalHours} h";
            if (delta.TotalDays < 7) return $"hace {(int)delta.TotalDays} d";
            return When.ToString("d/M/yyyy");
        }
    }

    /// <summary>Número de palabras del resultado (para el detalle).</summary>
    [JsonIgnore]
    public int WordCount => (Output ?? "")
        .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>¿Se puede volver a ejecutar la acción con este texto?</summary>
    [JsonIgnore]
    public bool CanRepeat => !string.IsNullOrWhiteSpace(Input) && !string.IsNullOrWhiteSpace(Action);

    public override string ToString() => $"{Action} · {When:g}";
}

/// <summary>
/// Historial de resultados de IA en history.json. Respeta la configuración de
/// privacidad: se puede desactivar, cifrar con DPAPI y limitar el número de
/// entradas. Todo se escribe de forma atómica.
/// </summary>
public static class HistoryStore
{
    public const int MaxOutputChars = 60_000;
    public const int MaxInputChars = 20_000;
    public const int MaxPreviewChars = 160;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static HistoryConfig _config = new();
    private static bool _corruptBackedUp;

    /// <summary>Aplica la configuración de privacidad (al arrancar y al guardar ajustes).</summary>
    public static void Configure(HistoryConfig? config) => _config = config ?? new HistoryConfig();

    public static HistoryConfig Config => _config;

    public static string FilePath => AppPaths.DataFile("history.json");

    /// <summary>Todas las entradas (más recientes primero), opcionalmente filtradas.</summary>
    public static List<HistoryEntry> All(string? query = null)
    {
        lock (Gate)
        {
            var all = ReadAllUnlocked(out _);
            return Filter(all, query);
        }
    }

    public static void Add(string actionId, string action, string model, string input, string output)
    {
        if (!_config.Enabled) return;

        lock (Gate)
        {
            try
            {
                var all = ReadAllUnlocked(out bool readFailed);
                if (readFailed)
                {
                    // Un bloqueo momentáneo (antivirus, OneDrive, copia de
                    // seguridad) se leía como «no hay historial» y la escritura
                    // borraba todo lo anterior. Mejor perder esta entrada que
                    // el historial completo.
                    Logger.Warn("historial: no se pudo leer el archivo (¿bloqueado?); no se guarda para no borrar lo existente");
                    return;
                }
                var flatInput = (input ?? "").Trim();
                all.Insert(0, new HistoryEntry
                {
                    ActionId = Truncate(actionId ?? "", 80),
                    Action = Truncate(action ?? "", 80),
                    Model = Truncate(model ?? "", 80),
                    Input = Truncate(flatInput, MaxInputChars),
                    InputPreview = Truncate(Flatten(flatInput), MaxPreviewChars),
                    Output = Truncate(output ?? "", MaxOutputChars)
                });

                while (all.Count > Math.Max(1, _config.MaxEntries)) all.RemoveAt(all.Count - 1);
                WriteAllUnlocked(all);
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "historial: no se pudo guardar la entrada");
            }
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            try
            {
                var all = ReadAllUnlocked(out bool readFailed);
                if (readFailed) return; // no reescribir a ciegas sobre el archivo bueno
                all.RemoveAll(e => e.Id == id);
                WriteAllUnlocked(all);
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "historial: no se pudo borrar la entrada");
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { }
        }
    }

    /// <summary>Reescribe todas las entradas aplicando la configuración actual (cifrar o descifrar).</summary>
    public static void RewriteAll()
    {
        lock (Gate)
        {
            try
            {
                var all = ReadAllUnlocked(out bool readFailed);
                if (readFailed) return; // no cifrar/descifrar sobre una lectura fallida
                WriteAllUnlocked(all);
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "historial: no se pudo reescribir");
            }
        }
    }

    /// <summary>Borra el historial si el usuario lo pidió para el cierre de la app.</summary>
    public static void ClearIfConfigured()
    {
        if (_config.ClearOnExit) Clear();
    }

    private static List<HistoryEntry> Filter(List<HistoryEntry> all, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return all;
        var q = query.Trim();
        return all.Where(e =>
            (e.Action ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (e.Model ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (e.InputPreview ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (e.Output ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Lee todas las entradas. <paramref name="readFailed"/> indica que el archivo
    /// existe pero no se pudo leer (bloqueado, sin permisos): quien vaya a escribir
    /// debe abortar, porque una lista vacía sobrescribiría el historial bueno.
    /// </summary>
    private static List<HistoryEntry> ReadAllUnlocked(out bool readFailed)
    {
        readFailed = false;
        if (!File.Exists(FilePath)) return new();

        string json;
        try { json = File.ReadAllText(FilePath); }
        catch { readFailed = true; return new(); } // bloqueado o sin permisos: no es corrupción, no tocar el archivo

        var all = new List<HistoryEntry>();
        int broken = 0;
        int badFields = 0;
        _corruptBackedUp = false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("El historial no es una lista.");

            // Entrada a entrada y campo a campo: un solo campo mal escrito (una
            // fecha a mano, un número donde va texto) hacía fallar el
            // Deserialize de TODO el archivo y el historial entero desaparecía
            // en la siguiente escritura. Ahora se conserva lo que sí se puede leer.
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { broken++; continue; } // nulls y basura
                var entry = ReadEntry(item, ref badFields);
                if (entry is null) { broken++; continue; }
                all.Add(entry);
            }
        }
        catch
        {
            BackupCorrupt();
            return new();
        }

        if (broken > 0 || badFields > 0)
        {
            // Se han descartado/corregido datos: guardar copia antes de que la
            // próxima escritura los borre del disco (y avisar solo la primera vez).
            bool first = !_corruptBackedUp;
            BackupCorrupt();
            if (first)
            {
                if (broken > 0) Logger.Warn($"historial: {broken} entrada(s) ilegible(s) descartada(s)");
                if (badFields > 0) Logger.Warn($"historial: {badFields} campo(s) con tipo inválido se ignoraron");
            }
        }

        // Un JSON editado a mano puede traer nulls: un solo null tumbaba el
        // foreach de abajo y dejaba el historial vacío (la próxima escritura
        // lo borraba del disco). Se sanea al leer.
        all.RemoveAll(e => e is null);
        foreach (var entry in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
            entry.ActionId ??= "";
            entry.Action ??= "";
            entry.Model ??= "";
            entry.InputPreview ??= "";
            entry.Input ??= "";
            entry.Output ??= "";
            Unprotect(entry);
            // Entradas antiguas o escritas a mano sin vista previa: se genera del
            // texto para que la lista no muestre una fila en blanco.
            if (!entry.Encrypted && string.IsNullOrEmpty(entry.InputPreview) && !string.IsNullOrEmpty(entry.Input))
                entry.InputPreview = Truncate(Flatten(entry.Input), MaxPreviewChars);
        }
        return all;
    }

    /// <summary>
    /// Convierte un elemento JSON en entrada sin que un campo con el tipo
    /// equivocado se lleve por delante el texto de la entrada entera.
    /// </summary>
    private static HistoryEntry? ReadEntry(JsonElement item, ref int badFields)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject()) fields[property.Name] = property.Value;

        var entry = new HistoryEntry();
        if (fields.TryGetValue(nameof(HistoryEntry.Id), out var id))
        {
            if (id.ValueKind == JsonValueKind.String) entry.Id = id.GetString() ?? entry.Id;
            else badFields++;
        }
        if (fields.TryGetValue(nameof(HistoryEntry.When), out var when))
        {
            if (when.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(when.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                entry.When = date;
            else
            {
                badFields++;
                return null;
            }
        }
        entry.ActionId = ReadString(fields, nameof(HistoryEntry.ActionId), ref badFields) ?? "";
        entry.Action = ReadString(fields, nameof(HistoryEntry.Action), ref badFields) ?? "";
        entry.Model = ReadString(fields, nameof(HistoryEntry.Model), ref badFields) ?? "";
        entry.InputPreview = ReadString(fields, nameof(HistoryEntry.InputPreview), ref badFields) ?? "";
        entry.Input = ReadString(fields, nameof(HistoryEntry.Input), ref badFields) ?? "";
        entry.Output = ReadString(fields, nameof(HistoryEntry.Output), ref badFields) ?? "";
        if (fields.TryGetValue(nameof(HistoryEntry.Encrypted), out var encrypted))
        {
            if (encrypted.ValueKind is JsonValueKind.True or JsonValueKind.False) entry.Encrypted = encrypted.GetBoolean();
            else badFields++;
        }
        return entry;
    }

    private static string? ReadString(Dictionary<string, JsonElement> fields, string name, ref int badFields)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        badFields++;
        return null;
    }

    /// <summary>Guarda una copia del historial ilegible antes de que la próxima escritura lo reemplace.</summary>
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

    /// <summary>Nombre libre para la copia: dos corrupciones en el mismo segundo no se pisan.</summary>
    private static string UniqueBackupPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = FilePath + ".corrupto-" + stamp;
        for (int n = 2; File.Exists(path) && n < 1000; n++)
            path = FilePath + ".corrupto-" + stamp + "-" + n;
        return path;
    }

    private static void WriteAllUnlocked(List<HistoryEntry> all)
    {
        if (_config.Encrypt)
        {
            int clear = 0;
            foreach (var entry in all)
                if (!Protect(entry)) clear++;
            // Si DPAPI falla no se puede cumplir «historial cifrado»: se guarda
            // igual (mejor eso que perder el historial) pero queda en el log.
            if (clear > 0) Logger.Warn($"historial: {clear} entrada(s) no se pudieron cifrar y quedan sin cifrar");
        }

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(all, Opts));
        // Volcar a disco antes de intercambiar: si el equipo se apaga entre el
        // Write y el Move, el .tmp puede quedar vacío y pasaría a ser el historial.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            // Best-effort: algunos sistemas de archivos de red no soportan
            // FlushFileBuffers y no debe impedir guardar el historial.
            try { fs.Flush(true); } catch { }
        }
        File.Move(tmp, FilePath, true);
        // Tras una escritura correcta, una corrupción futura merece su propia copia.
        _corruptBackedUp = false;
    }

    /// <summary>Cifra los campos de una entrada. False si DPAPI falló y quedó en claro.</summary>
    private static bool Protect(HistoryEntry entry)
    {
        if (entry.Encrypted) return true;
        // Si DPAPI falla (Protect devuelve ""), no persistir vacío: se perdería
        // el historial al guardar. Se deja sin cifrar esta vez (el llamante avisa).
        string input = SettingsManager.Protect(entry.Input);
        string preview = SettingsManager.Protect(entry.InputPreview);
        string output = SettingsManager.Protect(entry.Output);
        if ((!string.IsNullOrEmpty(entry.Input) && input.Length == 0) ||
            (!string.IsNullOrEmpty(entry.InputPreview) && preview.Length == 0) ||
            (!string.IsNullOrEmpty(entry.Output) && output.Length == 0))
            return false;
        entry.Input = input;
        entry.InputPreview = preview;
        entry.Output = output;
        entry.Encrypted = true;
        return true;
    }

    private static void Unprotect(HistoryEntry entry)
    {
        if (!entry.Encrypted) return;
        string input = SettingsManager.Unprotect(entry.Input);
        string preview = SettingsManager.Unprotect(entry.InputPreview);
        string output = SettingsManager.Unprotect(entry.Output);
        // DPAPI puede fallar (otro usuario/equipo o blob inválido): Unprotect
        // devuelve "" y eso vaciaría la entrada. Se conserva el cifrado para no
        // perder datos: al escribir se salta lo ya cifrado (ver Protect) y el
        // texto original sobrevive hasta que lo abra quien lo cifró.
        if ((!string.IsNullOrEmpty(entry.Input) && input.Length == 0) ||
            (!string.IsNullOrEmpty(entry.InputPreview) && preview.Length == 0) ||
            (!string.IsNullOrEmpty(entry.Output) && output.Length == 0))
            return;
        entry.Input = input;
        entry.InputPreview = preview;
        entry.Output = output;
        entry.Encrypted = false;
    }

    private static string Flatten(string? s) =>
        (s ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = max;
        // No partir un par sustituto (emoji) por la mitad: quedaría un carácter
        // inválido en el JSON y un rombo en la lista.
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return cut <= 0 ? s[..max] + "…" : s[..cut] + "…";
    }
}
