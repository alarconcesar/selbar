using System.Text.RegularExpressions;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;

namespace Selbar.Core.Actions;

/// <summary>
/// Registro central: combina presets + overrides del usuario (settings.json)
/// y filtra por contexto para no agobiar (longitud, 1 palabra, texto largo, regex...).
/// </summary>
public sealed class ActionRegistry
{
    private readonly List<ActionDefinition> _all = new();

    /// <summary>Acciones retiradas: se purgan de settings antiguos para que no resuciten.</summary>
    private static readonly HashSet<string> RetiredIds = new()
    {
        "ai.code-explain", "ai.code-comment", "ai.code-bugs"
    };

    /// <summary>Caché de regex de contexto (se compilan una vez por patrón).</summary>
    private static readonly Dictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    public ActionRegistry(IEnumerable<ActionDefinition>? overrides = null)
    {
        var defaults = ActionPresets.Defaults();
        var ov = (overrides ?? Enumerable.Empty<ActionDefinition>())
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id))
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var d in defaults)
        {
            if (ov.TryGetValue(d.Id, out var u))
            {
                d.IsEnabled = u.IsEnabled;
                // Un override importado o escrito a mano puede traer solo el Id:
                // Title quedaría "" (chip sin texto) y Order 0 (primero de la barra).
                if (u.Order > 0) d.Order = u.Order;
                if (u.PromptTemplate != null) d.PromptTemplate = u.PromptTemplate;
                if (u.UrlTemplate != null) d.UrlTemplate = u.UrlTemplate;
                if (u.Script != null) d.Script = u.Script;
                if (!string.IsNullOrWhiteSpace(u.Title)) d.Title = u.Title;
            }
            _all.Add(d);
        }

        // Customs que no existan en defaults (las retiradas no resucitan).
        foreach (var u in ov.Values.Where(x => defaults.All(d => d.Id != x.Id) && !RetiredIds.Contains(x.Id)))
        {
            u.Category = ActionCategory.Custom;
            _all.Add(u);
        }
    }

    public IReadOnlyList<ActionDefinition> All => _all.OrderBy(a => a.Order).ThenBy(a => a.Title).ToList();

    public ActionDefinition? Find(string id) => _all.FirstOrDefault(x => x.Id == id);

    public void SetEnabled(string id, bool enabled)
    {
        var a = Find(id);
        if (a != null) a.IsEnabled = enabled;
    }

    public void Move(string id, int newOrder)
    {
        var a = Find(id);
        if (a != null) a.Order = newOrder;
    }

    public void AddCustom(ActionDefinition action)
    {
        if (action == null || string.IsNullOrWhiteSpace(action.Id)) return;
        action.Category = ActionCategory.Custom;
        action.Order = _all.Count == 0 ? 500 : _all.Max(a => a.Order) + 1;
        _all.Add(action);
    }

    /// <summary>Solo se pueden eliminar acciones personalizadas (las de serie se desactivan).</summary>
    public bool RemoveCustom(string id)
    {
        var a = _all.FirstOrDefault(x => x.Id == id && x.Category == ActionCategory.Custom);
        if (a == null) return false;
        _all.Remove(a);
        return true;
    }

    /// <summary>Acciones visibles para este texto (respeta toggles + contexto + perfil de app).</summary>
    public IReadOnlyList<ActionDefinition> GetVisibleActions(string selectedText, string? processName = null)
    {
        var text = (selectedText ?? "").Trim();
        if (text.Length == 0) return Array.Empty<ActionDefinition>();

        bool single = IsSingleWord(text);
        bool hasNewLine = text.Contains('\n');
        var hidden = HiddenFor(processName);

        var list = _all
            .Where(a => a.IsEnabled)
            .Where(a => !hidden.Contains(a.Id))
            .Where(a => text.Length >= a.MinLength && text.Length <= a.MaxLength)
            .Where(a => !a.SingleWordOnly || single)
            .Where(a => !a.MultiWordOnly || !single)
            .Where(a => !a.LongTextOnly || text.Length > 60)
            .Where(a => MatchesRegex(a, text, hasNewLine))
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Title)
            .ToList();

        ApplySmartOrder(text, list);
        return list;
    }

    /// <summary>
    /// Mueve la acción sugerida al frente sin tocar el resto del orden.
    /// Solo si está visible y el texto es corto (evita regex pesadas en textos largos).
    /// </summary>
    private void ApplySmartOrder(string text, List<ActionDefinition> list)
    {
        if (!SmartSuggestEnabled || list.Count < 2 || text.Length > 2000) return;
        try
        {
            var suggestion = TextSuggest.Suggest(text);
            if (suggestion == null) return;
            int index = list.FindIndex(a => a.Id == suggestion.ActionId);
            if (index <= 0) return;
            var item = list[index];
            list.RemoveAt(index);
            list.Insert(0, item);
        }
        catch { }
    }

    /// <summary>Reglas por aplicación (es la misma lista que vive en los ajustes).</summary>
    public List<AppProfile> Profiles { get; set; } = new();

    /// <summary>Si es true, la sugerencia de TextSuggest va primera. Se apaga en Ajustes.</summary>
    public bool SmartSuggestEnabled { get; set; } = true;

    /// <summary>Ids que el perfil de esa app esconde. Compara sin .exe ni mayúsculas.</summary>
    public HashSet<string> HiddenFor(string? processName)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(processName)) return hidden;

        string process = TrimExe(processName!);
        foreach (var profile in Profiles)
        {
            if (profile is null || !profile.Enabled || string.IsNullOrWhiteSpace(profile.Process)) continue;
            if (!string.Equals(TrimExe(profile.Process), process, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string id in profile.HiddenActions)
                if (!string.IsNullOrWhiteSpace(id)) hidden.Add(id.Trim());
        }
        return hidden;
    }

    private static string TrimExe(string name)
    {
        string n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    private static bool IsSingleWord(string text) =>
        !text.Contains(' ') && !text.Contains('\n') && text.Length < 40;

    /// <summary>
    /// Regla de contexto opcional. Si el patrón es inválido se ignora (la acción
    /// se muestra) para que una regex mal escrita no esconda acciones sin avisar.
    /// </summary>
    private static bool MatchesRegex(ActionDefinition action, string text, bool hasNewLine)
    {
        if (string.IsNullOrWhiteSpace(action.MatchRegex)) return true;

        var regex = GetRegex(action.MatchRegex);
        if (regex == null) return true;

        try
        {
            // Las regex de una línea no deberían casar con selecciones multilínea,
            // salvo que pidan explícitamente multilínea ((?s), [\s\S] o \s).
            if (hasNewLine && !action.MatchRegex.Contains("\\n") && !action.MatchRegex.Contains("[\r\n]")
                && !action.MatchRegex.Contains("(?s)") && !action.MatchRegex.Contains(@"[\s\S]"))
                return false;
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            Logger.Warn("regex de contexto agotó el tiempo: " + action.Id);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static Regex? GetRegex(string pattern)
    {
        lock (RegexCache)
        {
            if (RegexCache.TryGetValue(pattern, out var cached)) return cached;
            // Tope de tamaño: las customs eliminadas no se quedan para siempre.
            if (RegexCache.Count > 200) RegexCache.Clear();
            Regex? compiled = null;
            try
            {
                // Tope de tiempo: una regex catastrófica no puede colgar la barra.
                compiled = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                Logger.LogEx(ex, "regex de contexto inválida: " + pattern);
            }
            RegexCache[pattern] = compiled;
            return compiled;
        }
    }
}
