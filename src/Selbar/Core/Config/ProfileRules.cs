using System.Text;

namespace Selbar.Core.Config;

/// <summary>
/// Perfiles por aplicación en formato de texto, para poder editarlos en una caja
/// multilínea sin complicar la UI. Una regla por línea:
///
///     chrome = ai.todos, text.qr
///     Code   = ai.translate
///
/// El proceso va sin .exe y a la derecha, los ids de acción que se esconden.
/// Las líneas vacías y las que empiezan por # se ignoran.
/// </summary>
public static class ProfileRules
{
    /// <summary>Convierte los perfiles guardados en texto editable.</summary>
    public static string Format(IEnumerable<AppProfile>? profiles)
    {
        if (profiles == null) return "";
        var sb = new StringBuilder();
        foreach (var p in profiles)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Process)) continue;
            var ids = (p.HiddenActions ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (ids.Count == 0) continue; // sin ids Parse la descartaría: no escribirla
            // El "!" conserva los perfiles desactivados en el ida y vuelta por la caja de texto.
            sb.AppendLine($"{(p.Enabled ? "" : "!")}{p.Process.Trim()} = {string.Join(", ", ids)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Lee el texto editado. Ignora líneas inválidas en vez de fallar.</summary>
    public static List<AppProfile> Parse(string? text)
    {
        var list = new List<AppProfile>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (string raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            // Un perfil desactivado se escribe "!proceso = ids" (ver Format) para
            // que el ida y vuelta por la caja de texto no lo reactive sin querer.
            bool enabled = true;
            if (line.StartsWith("!", StringComparison.Ordinal))
            {
                enabled = false;
                line = line[1..].Trim();
                if (line.Length == 0) continue;
            }

            int sep = line.IndexOf('=');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep <= 0) continue;

            string process = line[..sep].Trim();
            string ids = line[(sep + 1)..].Trim();
            if (process.Length == 0) continue;

            if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
            if (process.Length == 0) continue;

            var hidden = ids
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (hidden.Count == 0) continue;

            var existing = list.FirstOrDefault(p => string.Equals(p.Process, process, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                foreach (string id in hidden.Where(id => !existing.HiddenActions.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    existing.HiddenActions.Add(id);
                // Si alguna de las líneas venía desactivada, el perfil queda desactivado.
                existing.Enabled &= enabled;
                continue;
            }

            list.Add(new AppProfile { Process = process, Enabled = enabled, HiddenActions = hidden });
        }
        return list;
    }
}
