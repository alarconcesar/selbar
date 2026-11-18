using System.Globalization;
using System.Text.Json.Serialization;

namespace Selbar.Core.Actions;

/// <summary>De dónde viene la acción (para agrupar en Ajustes y no agobiar).</summary>
public enum ActionCategory
{
    AiTranslate,
    AiRewrite,
    AiExplain,
    Clipboard,   // sin IA: copiar/cortar/pegar/eliminar/seleccionar todo
    Search,      // sin IA: Google/Wikipedia/YouTube/StackOverflow
    Text,        // sin IA: mayúsculas, contar, limpiar, JSON...
    System,      // sin IA: simular tecla, abrir URL, comando
    Custom       // prompts del usuario
}

public enum ActionKind { NoAi, Ai }

/// <summary>Qué hacer con el resultado (igual que Orbital/SnipDo).</summary>
public enum OutputMode
{
    Popup,       // mostrar ventana flotante
    Copy,        // copiar al portapapeles
    Replace,     // pegar sobre la selección (Ctrl+V)
    DirectCopy,  // copiar selección tal cual, sin IA
    Cut,
    Paste,
    PastePlain,  // pegar el portapapeles como texto sin formato
    Delete,
    SelectAll,
    Browser,     // abrir búsqueda en navegador
    SimulateKey, // pulsar tecla (Enter, Tab...)
    OpenUrl,     // abrir la propia selección como URL
    RunCommand   // ejecutar un comando con {text}
}

public sealed class ActionDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public ActionCategory Category { get; set; }
    public ActionKind Kind { get; set; }
    public OutputMode Output { get; set; } = OutputMode.Popup;

    /// <summary>Visible y activable desde Ajustes. Todo es toggleable.</summary>
    public bool IsEnabled { get; set; } = true;
    public int Order { get; set; }

    // Plantillas: {text} = selección, {lang} = idioma destino, {date}, {time}, {app}
    public string? PromptTemplate { get; set; }
    public string? UrlTemplate { get; set; }
    public string? Script { get; set; }
    public string? KeyName { get; set; }

    // Reglas de contexto para no mostrar todo siempre
    public int MinLength { get; set; } = 1;
    public int MaxLength { get; set; } = 20000;
    public bool SingleWordOnly { get; set; }
    public bool MultiWordOnly { get; set; }
    public bool LongTextOnly { get; set; } // > 60 chars, ej. resumir
    /// <summary>Si se indica, la acción solo aparece cuando la selección casa con esta regex.</summary>
    public string? MatchRegex { get; set; }

    /// <summary>En acciones de navegador: codifica la selección para la URL (false para abrir URLs tal cual).</summary>
    public bool UrlEncode { get; set; } = true;

    /// <summary>Explicación propia para el tooltip (si falta se usa la genérica).</summary>
    public string? Description { get; set; }

    // ----- Propiedades calculadas (no se guardan en settings.json) -----

    /// <summary>Glifo Segoe Fluent Icons ("" = sin icono).</summary>
    [JsonIgnore]
    public string IconGlyph => ActionIcons.For(Id);

    /// <summary>¿Tiene glifo propio? (si no, se muestra la inicial).</summary>
    [JsonIgnore]
    public bool HasIcon => IconGlyph.Length > 0;

    /// <summary>Inicial para acciones sin icono.</summary>
    [JsonIgnore]
    public string Initial
    {
        get
        {
            string title = DisplayTitle;
            if (string.IsNullOrWhiteSpace(title)) return "?";
            // Por elemento de texto, no por unidad UTF-16: un título con emoji
            // («🚀 Lanzar») dejaba un surrogate suelto y se pintaba «�».
            return StringInfo.GetNextTextElement(title.TrimStart()).ToUpperInvariant();
        }
    }

    /// <summary>Etiqueta corta para los chips de la barra (si no hay, el título).</summary>
    [JsonIgnore]
    public string DisplayTitle => ActionLabels.For(Id) ?? Title;

    /// <summary>Acciones que se muestran al inicio de la barra solo como icono (lupa, abrir…).</summary>
    [JsonIgnore]
    public bool IsBarIcon => Id is "search.google" or "search.wiki" or "search.youtube"
        or "search.so" or "sys.open-url" or "search.lens-url";

    /// <summary>True para las acciones que llaman a la IA (lo usa Ajustes para la etiqueta).</summary>
    [JsonIgnore]
    public bool IsAi => Kind == ActionKind.Ai;

    /// <summary>Texto corto del distintivo de "qué hace" en Ajustes.</summary>
    [JsonIgnore]
    public string OutputBadge => Output switch
    {
        OutputMode.Popup => "Ventana",
        OutputMode.Copy => "Copia",
        OutputMode.Replace => "Reemplaza",
        OutputMode.DirectCopy => "Copia",
        OutputMode.Cut => "Corta",
        OutputMode.Paste => "Pega",
        OutputMode.PastePlain => "Pega limpio",
        OutputMode.Delete => "Borra",
        OutputMode.SelectAll => "Todo",
        OutputMode.Browser => "Navegador",
        OutputMode.OpenUrl => "Navegador",
        OutputMode.SimulateKey => "Tecla",
        OutputMode.RunCommand => "Comando",
        _ => "—"
    };

    /// <summary>Nombre legible de la categoría.</summary>
    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        ActionCategory.AiTranslate => "Traducir",
        ActionCategory.AiRewrite => "Reescribir",
        ActionCategory.AiExplain => "Explicar",
        ActionCategory.Clipboard => "Portapapeles",
        ActionCategory.Search => "Búsqueda",
        ActionCategory.Text => "Texto",
        ActionCategory.System => "Sistema",
        _ => "Personalizada"
    };
}
