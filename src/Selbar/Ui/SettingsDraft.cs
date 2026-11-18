using System.Globalization;
using System.Text.Json;
using Selbar.Core.Config;

namespace Selbar.Ui;

/// <summary>
/// Operaciones puras del borrador de Ajustes. No tocan WPF ni el estado global:
/// así el clon, los rangos numéricos y la validación de perfiles se pueden probar
/// en Linux (tools/SelfTest o un probe) antes de empaquetar.
/// </summary>
public static class SettingsDraft
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Copia profunda de los ajustes (incluye ExtensionData de versiones futuras).</summary>
    public static AppSettings Clone(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, CloneOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, CloneOptions) ?? new AppSettings();
    }

    /// <summary>Número decimal con coma o punto; exige rango y rechaza texto vacío.</summary>
    public static bool TryParseDouble(string? text, double min, double max, out double value)
    {
        value = 0;
        string raw = (text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return false;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
        if (parsed < min || parsed > max) return false;
        value = parsed;
        return true;
    }

    /// <summary>Entero dentro de rango; acepta la cultura actual y la invariante.</summary>
    public static bool TryParseInt(string? text, int min, int max, out int value)
    {
        value = 0;
        string raw = (text ?? "").Trim();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) &&
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            return false;
        if (parsed < min || parsed > max) return false;
        value = parsed;
        return true;
    }

    /// <summary>
    /// Normaliza la lista de apps excluidas igual que la migración: sin .exe,
    /// sin duplicados y con Selbar siempre presente. Rechaza rutas porque el
    /// proceso se compara por nombre, no por ruta.
    /// </summary>
    public static bool TryNormalizeExcluded(string? text, out List<string> apps, out string error)
    {
        apps = new List<string>();
        error = "";
        int line = 0;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            line++;
            string item = raw.Trim();
            if (item.Length == 0) continue;
            if (item.Contains('\\') || item.Contains('/'))
            {
                error = $"Apps excluidas, línea {line}: escribe solo el nombre del proceso (por ejemplo chrome).";
                return false;
            }
            if (item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) item = item[..^4];
            if (item.Length == 0) continue;
            if (!apps.Contains(item, StringComparer.OrdinalIgnoreCase)) apps.Add(item);
        }

        var powerTools = apps.FirstOrDefault(x => x.Equals("Selbar", StringComparison.OrdinalIgnoreCase));
        apps.RemoveAll(x => x.Equals("Selbar", StringComparison.OrdinalIgnoreCase));
        apps.Insert(0, powerTools ?? "Selbar");
        return true;
    }

    /// <summary>
    /// Valida el texto de perfiles antes de guardar. ProfileRules.Parse ignora las
    /// líneas inválidas en silencio; aquí se avisa con el número de línea para no
    /// perder una regla sin que el usuario lo sepa.
    /// </summary>
    public static bool TryValidateProfiles(string? text, out string error)
    {
        error = "";
        int lineNumber = 0;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            if (line.StartsWith("!", StringComparison.Ordinal))
            {
                line = line[1..].Trim();
                if (line.Length == 0)
                {
                    error = $"Perfiles, línea {lineNumber}: falta el proceso después de «!».";
                    return false;
                }
            }

            int sep = line.IndexOf('=');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep <= 0 || sep >= line.Length - 1)
            {
                error = $"Perfiles, línea {lineNumber}: usa «proceso = id1, id2».";
                return false;
            }

            string process = line[..sep].Trim();
            string idsText = line[(sep + 1)..].Trim();
            if (process.Length == 0 || idsText.Length == 0)
            {
                error = $"Perfiles, línea {lineNumber}: faltan el proceso o los ids de acción.";
                return false;
            }

            // ProfileRules.Parse descarta en silencio una regla sin ids válidos
            // (por ejemplo «chrome = ,,,»): aquí se avisa en vez de perderla.
            var ids = idsText
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (ids.Count == 0)
            {
                error = $"Perfiles, línea {lineNumber}: escribe al menos un id de acción después de «=».";
                return false;
            }
            if (ids.Any(id => id.Any(char.IsWhiteSpace)))
            {
                error = $"Perfiles, línea {lineNumber}: separa los ids con comas (por ejemplo ai.todos, text.qr).";
                return false;
            }
            if (ids.Any(id => id.Contains('#')))
            {
                error = $"Perfiles, línea {lineNumber}: los comentarios van en su propia línea, empezando por #.";
                return false;
            }
        }
        return true;
    }
}
