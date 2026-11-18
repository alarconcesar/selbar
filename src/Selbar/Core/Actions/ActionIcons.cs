namespace Selbar.Core.Actions;

/// <summary>
/// Glifos Segoe Fluent Icons por acción ("" = se muestra la inicial).
/// Incluye Segoe MDL2 Assets como reserva para Windows 10, donde Fluent Icons
/// no está instalado (los códigos usados existen en ambas).
/// </summary>
public static class ActionIcons
{
    public const string Font = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static readonly Dictionary<string, string> Map = new()
    {
        // IA
        ["ai.translate"] = "\uE774",      // globo
        ["ai.translate-en"] = "\uE774",
        ["ai.improve"] = "\uE70F",        // lápiz
        ["ai.fix"] = "\uE73E",            // check
        ["ai.summarize"] = "\uE8FD",      // lista
        ["ai.summarize-one"] = "\uE8FD",
        ["ai.keypoints"] = "\uE8FD",
        ["ai.explain"] = "\uE82F",        // bombilla
        ["ai.define"] = "\uE946",         // información
        ["ai.ask"] = "\uE8BD",            // burbuja de chat
        ["ai.todos"] = "\uE9D5",          // lista de verificación

        // Portapapeles
        ["copy"] = "\uE8C8",
        ["cut"] = "\uE8C6",
        ["paste"] = "\uE77F",
        ["pasteplain"] = "\uE77F",
        ["delete"] = "\uE74D",
        ["selectall"] = "\uE8B3",

        // Búsqueda y sistema
        ["search.google"] = "\uE721",
        ["search.wiki"] = "\uE774",
        ["search.youtube"] = "\uE714",
        ["search.so"] = "\uE721",
        ["search.whatsapp"] = "\uE8BD",
        ["search.lens-url"] = "\uE721",
        ["sys.youtube-video"] = "\uE896",
        ["sys.youtube-audio"] = "\uE8D6",
        ["sys.open-url"] = "\uE8A7",      // abrir en ventana nueva
        ["sys.speak"] = "\uE767",         // altavoz
        ["sys.obsidian"] = "\uE70B",      // nota
        ["contact.email"] = "\uE8BD",       // correo
        ["date.info"] = "\uE787",           // calendario
        ["file.open"] = "\uE838",           // carpeta
        ["link.clean"] = "\uE71B",        // quitar
        ["link.expand"] = "\uE71E",       // enlace
        ["link.summarize"] = "\uE8A1",    // documento
        ["place.maps"] = "\uE707",        // mapa
        ["social.x"] = "\uE8BD",
        ["social.instagram"] = "\uE8BD",
        ["social.facebook"] = "\uE8BD",

        // Texto
        ["text.json"] = "\uE943",         // llaves
        ["text.toggle"] = "\uE8AB",       // alternar
        ["text.count"] = "\uE8EF",        // calculadora
        ["text.color"] = "\uE790",        // paleta
        ["text.styles"] = "\uE8D2",       // tipografía
        ["text.nolines"] = "\uE8AB",
        ["text.qr"] = "\uE8A5",
        ["text.calc"] = "\uE8EF",       // calculadora
        ["text.convert"] = "\uE895",    // conversión
    };

    public static string For(string id) => Map.TryGetValue(id, out var g) ? g : "";

    /// <summary>Todos los glifos usados (para el test de cobertura de la fuente).</summary>
    public static IReadOnlyCollection<string> AllGlyphs => Map.Values;
}
