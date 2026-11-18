using System.Text;

namespace Selbar.Core.Actions;

/// <summary>
/// Lo que devolvió el modelo de decisiones para una selección concreta.
/// Las claves de <see cref="Probabilities"/> son intenciones
/// (<see cref="ActionIntents"/>), no acciones: así la pregunta no depende de
/// cuántas acciones tenga el catálogo ni de sus nombres.
/// </summary>
public sealed class DecisionOutcome
{
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    public double NoAction { get; init; }
}

/// <summary>
/// Taxonomía de intenciones: corta, estable y excluyente. Es lo único que se le
/// pregunta al modelo; la traducción a una acción concreta es determinista.
///
/// Cualquier selección cae en una de estas categorías (o en «ninguna»), así que
/// el sistema funciona igual con un correo, un párrafo, código o basura.
/// </summary>
public static class ActionIntents
{
    public const string None = "ninguna";

    /// <summary>Intención → acción del catálogo que la resuelve.</summary>
    public static readonly IReadOnlyDictionary<string, string> ToAction =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["responder"] = "ai.ask",
            ["resumir"] = "ai.summarize",
            ["mejorar"] = "ai.improve",
            ["corregir"] = "ai.fix",
            ["traducir"] = "ai.translate",
            ["explicar"] = "ai.explain",
            ["calcular"] = "text.calc",
            ["convertir"] = "text.convert",
            ["fecha"] = "date.info",
            ["mapa"] = "place.maps",
            ["archivo"] = "file.open",
            ["enlace"] = "link.clean",
            ["correo"] = "contact.email",
            ["buscar"] = "search.google"
        };

    /// <summary>Descripciones que se envían como criterios (id → qué significa).</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Criteria = new List<KeyValuePair<string, string>>
    {
        new("responder", "Contestar un mensaje, una queja o un reclamo"),
        new("resumir", "Sacar lo esencial de un texto largo o denso"),
        new("mejorar", "Reescribir para que suene mejor, más claro o más formal"),
        new("corregir", "Arreglar ortografía, tildes o errores de escritura"),
        new("traducir", "Pasar el texto a otro idioma"),
        new("explicar", "Explicar qué significa o cómo funciona algo"),
        new("calcular", "Resolver una operación matemática"),
        new("convertir", "Cambiar unidades o moneda (km, kg, dólares, grados)"),
        new("fecha", "Interpretar una fecha y decir qué día es o cuánto falta"),
        new("mapa", "Ver una dirección en un mapa"),
        new("archivo", "Abrir un archivo o una carpeta del equipo"),
        new("enlace", "Abrir o limpiar un enlace web"),
        new("correo", "El texto ES una dirección de correo; hay que escribirle"),
        new("buscar", "Buscar ese texto en internet"),
        new(None, "Ninguna de las anteriores; el texto no necesita nada")
    };

    public static bool IsKnown(string? intent) => intent is not null && ToAction.ContainsKey(intent);
}

/// <summary>Resultado de ordenar la barra: qué se muestra, qué se promovió y si se ocultó ruido.</summary>
public sealed class RankResult
{
    public IReadOnlyList<ActionDefinition> Actions { get; init; } = Array.Empty<ActionDefinition>();
    public string? PromotedId { get; init; }
    public bool HidNoise { get; init; }
}

/// <summary>
/// Convierte la intención elegida por el modelo en un orden de chips.
///
/// Reglas, en orden:
/// 1. Sin decisión (sin key, sin red, timeout) → exactamente lo de siempre.
/// 2. «Ninguna» con fuerza y sin una intención clara → se ocultan las sugerencias
///    genéricas. Los aciertos duros (correo, fecha, ruta, cálculo, enlace) son
///    hechos, no opiniones: nunca se ocultan.
/// 3. Intención clara (≥ <see cref="PromoteAt"/>) → su acción sube al primer
///    puesto; si no estaba en la lista, se antepone. El resto conserva su orden.
/// </summary>
public static class ActionRanker
{
    /// <summary>Confianza mínima para mover una acción al frente.</summary>
    public const double PromoteAt = 0.55;

    /// <summary>Probabilidad de «ninguna» a partir de la cual se puede ocultar ruido.</summary>
    public const double NoActionAt = 0.75;

    /// <summary>Si la mejor intención es más débil que esto, «ninguna» manda.</summary>
    public const double WeakBest = 0.75;

    private static bool IsHardMatch(ActionDefinition a) =>
        a.Kind == ActionKind.NoAi && !string.IsNullOrEmpty(a.MatchRegex);

    public static RankResult Rank(
        IReadOnlyList<ActionDefinition> offline,
        IReadOnlyList<ActionDefinition> catalogue,
        DecisionOutcome? decision,
        bool hideWhenNoAction = true)
    {
        offline ??= Array.Empty<ActionDefinition>();
        if (decision is null || offline.Count == 0)
            return new RankResult { Actions = offline };

        // «ninguna» puede venir como pregunta aparte o como opción del choice.
        double noneScore = Math.Max(decision.NoAction,
            decision.Probabilities.TryGetValue(ActionIntents.None, out double explicitNone) ? explicitNone : 0);

        string? intent = null;
        double bestConf = 0;
        foreach (var (id, p) in decision.Probabilities)
        {
            if (id == ActionIntents.None) continue;
            if (p > bestConf) { bestConf = p; intent = id; }
        }

        // 2. Nada útil: se quedan solo los aciertos duros.
        if (hideWhenNoAction && noneScore >= NoActionAt && bestConf < WeakBest)
        {
            var hard = offline.Where(IsHardMatch).ToList();
            if (hard.Count != offline.Count)
                return new RankResult { Actions = hard, HidNoise = true };
            return new RankResult { Actions = offline };
        }

        // 3. Intención clara → su acción al frente.
        if (intent is null || bestConf < PromoteAt) return new RankResult { Actions = offline };
        if (!ActionIntents.ToAction.TryGetValue(intent, out string? actionId)) return new RankResult { Actions = offline };

        ActionDefinition? target = offline.FirstOrDefault(a => a.Id == actionId)
            ?? (catalogue ?? Array.Empty<ActionDefinition>()).FirstOrDefault(a => a.Id == actionId && a.IsEnabled);
        if (target is null) return new RankResult { Actions = offline };

        if (offline[0].Id == actionId)
            return new RankResult { Actions = offline, PromotedId = actionId };

        var list = offline.Where(a => a.Id != actionId).ToList();
        list.Insert(0, target);
        return new RankResult { Actions = list, PromotedId = actionId };
    }

    /// <summary>Intentar adivinar la intención sin modelo (para pruebas y para el modo sin key).</summary>
    public static string? IntentOf(string? intent) =>
        intent is not null && ActionIntents.ToAction.TryGetValue(intent, out var id) ? id : null;

    /// <summary>Texto que describe una intención para el modelo.</summary>
    public static string Describe(KeyValuePair<string, string> intent, int max = 110) =>
        Describe(intent.Value, max);

    public static string Describe(string? description, int max = 110)
    {
        string text = (description ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length <= max) return text;
        int cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// ¿Parece una credencial? Entonces no se envía a ningún modelo: el texto
    /// seleccionado puede ser una contraseña, un token o una clave.
    /// Los enlaces y las rutas quedan fuera: son texto normal, no secretos.
    /// </summary>
    public static bool LooksSecret(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length is < 16 or > 512) return false;
        if (t.Any(char.IsWhiteSpace)) return false;

        string lower = t.ToLowerInvariant();
        // Un enlace o una ruta tienen pinta de credencial por los símbolos, pero
        // se envían sin problema (y son selecciones muy comunes).
        if (lower.Contains("://") || lower.StartsWith("www.") || lower.Contains(":\\") ||
            lower.StartsWith("/") || lower.Contains("\\") || lower.Contains('@'))
            return false;

        bool digit = false, upper = false, lowerCase = false, symbol = false;
        foreach (char c in t)
        {
            if (char.IsDigit(c)) digit = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lowerCase = true;
            else if (!char.IsLetterOrDigit(c)) symbol = true;
        }
        return digit && (upper || symbol) && (lowerCase || symbol);
    }

    /// <summary>Recorte seguro para el modelo: principio y final, sin partir surrogates.</summary>
    public static string Clip(string? text, int max = 8000)
    {
        string t = text ?? "";
        if (t.Length <= max) return t;
        const string joiner = "\n…\n";
        int budget = max - joiner.Length;
        int head = (int)(budget * 0.75);
        int tail = budget - head;
        if (char.IsHighSurrogate(t[head - 1])) head--;
        int start = t.Length - tail;
        if (char.IsLowSurrogate(t[start])) start++;
        var sb = new StringBuilder(max + 8);
        sb.Append(t, 0, head).Append(joiner).Append(t, start, t.Length - start);
        return sb.ToString();
    }
}
