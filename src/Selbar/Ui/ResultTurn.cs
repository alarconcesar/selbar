namespace Selbar.Ui;

/// <summary>
/// Capacidades de la ventana de resultado según el estado real. Una sola matriz
/// gobierna botones, menús y atajos: durante streaming o error no se copia ni se
/// reemplaza, y Reintentar/Ajustar/Preguntar solo existen si hay callback.
/// Sin tipos de WPF a propósito: se puede probar en Linux.
/// </summary>
public readonly record struct ResultCapabilities(
    bool CanCopy,
    bool CanReplace,
    bool CanRetry,
    bool CanRefine,
    bool CanChat,
    bool CanSave,
    bool CanReuse,
    bool CanStop)
{
    public static ResultCapabilities For(
        bool streaming,
        bool hasError,
        bool canCopy,
        bool canReplace,
        bool hasRetry,
        bool hasRefine,
        bool hasChat,
        bool hasText)
    {
        bool idle = !streaming && !hasError;
        return new ResultCapabilities(
            CanCopy: idle && canCopy && hasText,
            CanReplace: idle && canReplace && hasText,
            CanRetry: !streaming && hasRetry,
            CanRefine: idle && hasRefine && hasText,
            CanChat: idle && hasChat,
            CanSave: idle && hasText,
            CanReuse: idle && hasText,
            CanStop: streaming);
    }
}

/// <summary>
/// Construcción pura de los turnos de refinamiento/seguimiento y de los límites
/// de reutilización. El contexto se arma con el texto VISIBLE (edición o versión
/// elegida), no con la última respuesta del historial.
/// </summary>
public static class ResultTurn
{
    public const int ReuseLimit = 20000;

    /// <summary>Describe la versión visible solo cuando hay más de una.</summary>
    public static string VersionContext(int index, int count)
    {
        if (count <= 1) return "";
        int current = Math.Clamp(index, 1, count);
        return $"El usuario está viendo la versión {current} de {count}; trabaja sobre ese texto.";
    }

    /// <summary>Etiqueta corta y estable para el navegador de versiones.</summary>
    public static string ShortLabel(string? instruction)
    {
        string text = (instruction ?? "").Trim();
        if (text.Length == 0) return "Versión";

        if (text.StartsWith("Hazlo más breve", StringComparison.Ordinal)) return "Más corto";
        if (text.StartsWith("Reescríbelo con un tono más formal", StringComparison.Ordinal)) return "Más formal";
        if (text.StartsWith("Hazlo más claro", StringComparison.Ordinal)) return "Más claro";
        if (text.StartsWith("Conviértelo en una lista", StringComparison.Ordinal)) return "En viñetas";
        if (text.StartsWith("Explícalo", StringComparison.Ordinal)) return "Explicar";
        if (text.StartsWith("Traduce el texto", StringComparison.Ordinal)) return "Traducir";

        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length <= 40) return flat;

        // Sin partir un par suplente: cortar en medio de un emoji deja un «�»
        // en el tooltip de la versión.
        int cut = 40;
        if (char.IsHighSurrogate(flat[cut - 1])) cut--;
        return flat[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// Historial de un turno: sistema (+ versión visible), encargo original,
    /// texto visible como respuesta actual y la instrucción nueva. No reenvía
    /// respuestas antiguas que ya no son lo que el usuario está viendo.
    /// </summary>
    public static List<(string Role, string Content)> BuildMessages(
        string? systemPrompt,
        string? initialPrompt,
        string? visibleText,
        int versionIndex,
        int versionCount,
        string? instruction)
    {
        string system = (systemPrompt ?? "").Trim();
        string context = VersionContext(versionIndex, versionCount);
        if (context.Length > 0) system = system.Length == 0 ? context : system + "\n\n" + context;

        var messages = new List<(string Role, string Content)>(4);
        if (system.Length > 0) messages.Add(("system", system));
        if (!string.IsNullOrWhiteSpace(initialPrompt)) messages.Add(("user", initialPrompt!.Trim()));
        if (!string.IsNullOrWhiteSpace(visibleText)) messages.Add(("assistant", visibleText!));
        messages.Add(("user", (instruction ?? "").Trim()));
        return messages;
    }

    /// <summary>Recorta para reutilizar sin hacerlo en silencio; el llamador avisa.</summary>
    public static (string Text, bool Truncated) LimitForReuse(string? text, int limit = ReuseLimit)
    {
        string value = text ?? "";
        int max = Math.Max(1, limit);
        if (value.Length <= max) return (value, false);

        // Igual que en ShortLabel: cortar entre los dos chars de un emoji deja un
        // sustituto suelto que se pega como «�» en la app de destino.
        if (max > 1 && char.IsHighSurrogate(value[max - 1])) max--;

        return (value[..max], true);
    }
}
