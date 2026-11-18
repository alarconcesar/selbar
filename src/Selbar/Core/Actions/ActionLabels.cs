namespace Selbar.Core.Actions;

/// <summary>
/// Etiquetas cortas para los chips de la barra. Solo donde difieren del título;
/// el resto usa el título completo.
/// </summary>
public static class ActionLabels
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["ai.translate"] = "Traducir",
        ["ai.translate-en"] = "Inglés",
        ["ai.improve"] = "Mejorar",
        ["ai.fix"] = "Corregir",
        ["ai.shorten"] = "Conciso",
        ["ai.formal"] = "Formal",
        ["ai.summarize"] = "Resumir",
        ["ai.summarize-one"] = "1 línea",
        ["ai.explain"] = "Explicar",
        ["ai.keypoints"] = "Claves",
        ["ai.define"] = "Definir",
        ["ai.ask"] = "Preguntar",
        ["ai.todos"] = "Tareas",
        ["copy"] = "Copiar",
        ["cut"] = "Cortar",
        ["paste"] = "Pegar",
        ["pasteplain"] = "Sin formato",
        ["selectall"] = "Todo",
        ["delete"] = "Eliminar",
        ["search.google"] = "Google",
        ["search.wiki"] = "Wikipedia",
        ["search.youtube"] = "YouTube",
        ["search.so"] = "StackOverflow",
        ["search.whatsapp"] = "WhatsApp",
        ["search.lens-url"] = "Lens",
        ["sys.youtube-video"] = "Vídeo YT",
        ["sys.youtube-audio"] = "Audio YT",
        ["sys.open-url"] = "Abrir URL",
        ["sys.speak"] = "Leer",
        ["sys.obsidian"] = "Obsidian",
        ["contact.email"] = "Correo",
        ["date.info"] = "Fecha",
        ["file.open"] = "Abrir",
        ["link.clean"] = "Limpiar",
        ["link.expand"] = "Destino",
        ["link.summarize"] = "Resumir web",
        ["place.maps"] = "Maps",
        ["social.x"] = "X",
        ["social.instagram"] = "IG",
        ["social.facebook"] = "FB",
        ["sys.enter"] = "Enter",
        ["sys.tab"] = "Tab",
        ["text.upper"] = "MAYÚS",
        ["text.lower"] = "minús",
        ["text.count"] = "Contar",
        ["text.toggle"] = "Alternar",
        ["text.capitalize"] = "Capitalizar",
        ["text.trim"] = "Limpiar",
        ["text.sort"] = "Ordenar",
        ["text.dedupe"] = "Únicas",
        ["text.json"] = "JSON",
        ["text.color"] = "Color",
        ["text.styles"] = "Estilos",
        ["text.nolines"] = "Sin saltos",
        ["text.qr"] = "QR",
        ["text.calc"] = "Calcular",
        ["text.convert"] = "Convertir",
    };

    public static string? For(string id) => Map.TryGetValue(id, out var s) ? s : null;

    /// <summary>Qué hace la acción, para el tooltip (segunda línea).</summary>
    public static string Outcome(ActionDefinition a)
    {
        if (!string.IsNullOrWhiteSpace(a.Description)) return a.Description!;
        return a.Output switch
        {
            OutputMode.Replace => "Reemplaza la selección",
            OutputMode.Copy => "Copia el resultado",
            OutputMode.Popup => "Muestra el resultado aquí",
            OutputMode.DirectCopy => "Copia la selección",
            OutputMode.Cut => "Corta la selección",
            OutputMode.Paste => "Pega aquí",
            OutputMode.PastePlain => "Pega como texto plano",
            OutputMode.Delete => "Elimina la selección",
            OutputMode.SelectAll => "Selecciona todo",
            OutputMode.Browser => "Abre en el navegador",
            OutputMode.OpenUrl => "Abre la URL seleccionada",
            OutputMode.SimulateKey => $"Pulsa {a.KeyName ?? "Enter"}",
            OutputMode.RunCommand => "Ejecuta un comando",
            _ => ""
        };
    }
}
