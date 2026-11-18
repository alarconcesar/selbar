using System.Globalization;
using System.Text;

namespace Selbar.Core.Actions;

/// <summary>
/// Búsqueda de acciones para el modo "escribe para filtrar" de la barra.
/// Puntúa por prefijo &gt; título &gt; etiqueta &gt; categoría &gt; descripción
/// &gt; subsecuencia (fuzzy), con soporte de varias palabras (todas deben casar).
/// </summary>
public static class ActionSearch
{
    /// <summary>Filtra y ordena por relevancia. Con consulta vacía devuelve todo tal cual.</summary>
    public static List<ActionDefinition> Filter(IEnumerable<ActionDefinition> actions, string? query)
    {
        var list = (actions ?? Enumerable.Empty<ActionDefinition>()).ToList();
        var text = (query ?? "").Trim();
        if (text.Length == 0) return list;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(ActionDefinition Action, int Score)>(list.Count);

        foreach (var action in list)
        {
            int total = 0;
            bool all = true;
            foreach (var token in tokens)
            {
                int score = ScoreToken(action, token);
                if (score == 0) { all = false; break; }
                total += score;
            }
            if (all) scored.Add((action, total));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Action.Order)
            .ThenBy(x => x.Action.Title)
            .Select(x => x.Action)
            .ToList();
    }

    /// <summary>0 si no casa; más alto = mejor coincidencia.</summary>
    public static int ScoreToken(ActionDefinition action, string token)
    {
        if (action == null || string.IsNullOrWhiteSpace(token)) return 0;
        // Sin tildes: escribir «mayusculas» debe encontrar «MAYÚSCULAS».
        var t = Fold(token.Trim());

        var title = Fold(action.Title);
        var display = Fold(action.DisplayTitle);
        var category = Fold(action.CategoryLabel);
        var description = Fold(action.Description);

        if (title.StartsWith(t, StringComparison.Ordinal)) return 1000;
        if (display.StartsWith(t, StringComparison.Ordinal)) return 850;
        if (title.Contains(t, StringComparison.Ordinal)) return 700;
        if (display.Contains(t, StringComparison.Ordinal)) return 620;
        if (category.StartsWith(t, StringComparison.Ordinal)) return 520;
        if (category.Contains(t, StringComparison.Ordinal)) return 420;
        if (description.Contains(t, StringComparison.Ordinal)) return 320;
        if (IsSubsequence(t, title)) return 140;
        if (IsSubsequence(t, description)) return 60;

        return 0;
    }

    /// <summary>
    /// Minúsculas y sin diacríticos, para que la búsqueda no dependa de las tildes
    /// (en español se escribe «mayusculas» o «código» indistintamente). Nunca lanza.
    /// </summary>
    internal static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string lower = value.ToLowerInvariant();
        try
        {
            string decomposed = lower.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
        catch
        {
            // Texto con surrogates inválidos: mejor buscar con lo que hay.
            return lower;
        }
    }

    /// <summary>¿Las letras de <paramref name="needle"/> aparecen en orden dentro de <paramref name="haystack"/>?</summary>
    public static bool IsSubsequence(string needle, string haystack)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;

        int j = 0;
        for (int i = 0; i < haystack.Length && j < needle.Length; i++)
            if (haystack[i] == needle[j]) j++;

        return j == needle.Length;
    }
}
