using System.Text.Json;
using System.Text.Json.Serialization;
using Selbar.Core.Actions;

namespace Selbar.Core.Config;

/// <summary>Proveedor de IA y preferencias del modelo.</summary>
public sealed class AiConfig
{
    public string Provider { get; set; } = "OpenCode GO"; // OpenCode GO | OpenAI | OpenRouter | Ollama | LMStudio | Personalizado
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/go/v1";
    public string Model { get; set; } = "deepseek-v4.1-flash";
    /// <summary>API key cifrada con DPAPI (CurrentUser).</summary>
    public string ApiKeyEncrypted { get; set; } = "";
    /// <summary>Idioma al que traduce la acción "Traducir".</summary>
    public string TargetLang { get; set; } = "español";
    /// <summary>Timeout de la petición en segundos.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>ID estable para la cabecera x-opencode-session que exige OpenCode GO.</summary>
    public string GoSessionId { get; set; } = "";
    /// <summary>Instrucción de sistema; vacío = la de serie.</summary>
    public string SystemPrompt { get; set; } = "";
    /// <summary>Creatividad (0 = determinista, 1 = creativa).</summary>
    public double Temperature { get; set; } = 0.3;
    /// <summary>Límite de tokens de salida; 0 = el que decida el proveedor.</summary>
    public int MaxTokens { get; set; } = 0;
    /// <summary>Recorte de seguridad del texto que se envía a la IA.</summary>
    public int MaxInputChars { get; set; } = 12000;
    /// <summary>API key de Groq para transcribir audio con Whisper (DPAPI).</summary>
    public string GroqApiKeyEncrypted { get; set; } = "";
    /// <summary>Modelo de transcripción de Groq.</summary>
    public string TranscriptionModel { get; set; } = "whisper-large-v3-turbo";

    // ---- Decisión inteligente de acciones (gemini-decision-1) ----
    /// <summary>Pedir a Gemini que elija la acción más probable para la selección.</summary>
    public bool DecisionEnabled { get; set; }
    /// <summary>API key de Google AI Studio, cifrada con DPAPI. Vacía = no se usa.</summary>
    public string DecisionApiKeyEncrypted { get; set; } = "";
    /// <summary>Modelo de decisiones (solo devuelve probabilidades, no texto).</summary>
    public string DecisionModel { get; set; } = "gemini-decision-1";
    /// <summary>Timeout de la decisión en milisegundos: si tarda, se usa el orden local.</summary>
    public int DecisionTimeoutMs { get; set; } = 1200;
    /// <summary>Ocultar sugerencias genéricas cuando la IA no ve ninguna acción útil.</summary>
    public bool DecisionHideNoise { get; set; } = true;
}

/// <summary>Cuándo y cómo aparece la barra flotante.</summary>
public sealed class TriggerConfig
{
    public bool AutoOnMouseSelection { get; set; }
    /// <summary>Al seleccionar, mostrar solo un indicador; el menú se abre al pasar el ratón o hacer clic.</summary>
    public bool SelectionPeek { get; set; } = true;
    public bool AutoOnKeyboardSelection { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Shift+Espacio";
    public int MinSelectionLength { get; set; } = 1;
    public int LongPressMs { get; set; } = 350;
    /// <summary>Iconos en la barra; el resto va al menú ···.</summary>
    public int MaxBarIcons { get; set; } = 8;
    /// <summary>Ocultar la barra tras X segundos (0 = nunca).</summary>
    public int AutoHideSeconds { get; set; } = 0;
    /// <summary>Muestra el número de atajo (1-9) en los primeros chips.</summary>
    public bool ShowNumbers { get; set; } = true;
    /// <summary>Atajos 1-9 mientras la barra está visible.</summary>
    public bool DigitShortcuts { get; set; } = true;
    /// <summary>La acción más probable va primera con marca de sugerida.</summary>
    public bool SmartSuggest { get; set; } = true;
    /// <summary>Escribe para filtrar acciones mientras la barra está visible.</summary>
    public bool SearchOnType { get; set; } = true;
}

/// <summary>Qué se guarda del historial y con qué protección.</summary>
public sealed class HistoryConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Cifra el contenido de las entradas con DPAPI.</summary>
    public bool Encrypt { get; set; } = false;
    /// <summary>Borra el historial al cerrar la app.</summary>
    public bool ClearOnExit { get; set; } = false;
    public int MaxEntries { get; set; } = 50;
}

/// <summary>Reglas por aplicación: qué acciones se esconden en un proceso concreto.</summary>
public sealed class AppProfile
{
    /// <summary>Nombre del ejecutable sin .exe (chrome, Code, WINWORD…).</summary>
    public string Process { get; set; } = "";
    /// <summary>Desactiva el perfil sin borrarlo.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Ids de acciones que no deben aparecer en esa app.</summary>
    public List<string> HiddenActions { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>Versión del esquema: permite migrar ajustes antiguos.</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    public const int CurrentVersion = 5;

    public AiConfig Ai { get; set; } = new();
    public TriggerConfig Triggers { get; set; } = new();
    public HistoryConfig History { get; set; } = new();
    public List<string> ExcludedApps { get; set; } = new() { "Selbar" };
    public List<ActionDefinition> ActionOverrides { get; set; } = new();
    public bool StartWithWindows { get; set; }
    /// <summary>Tema de la app: "system" (sigue a Windows), "dark" o "light".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Obsoleto: se conserva para leer ajustes de versiones anteriores.</summary>
    public bool DarkMode { get; set; } = true;
    public bool SeenWelcome { get; set; }
    /// <summary>Migración única a la invocación discreta (sin avisos al seleccionar).</summary>
    public bool InvocationMigrated { get; set; }
    public double ResultFontSize { get; set; } = 13.5;
    public double ResultWidth { get; set; } = 600;
    public double ResultHeight { get; set; } = 470;

    // ----- Integraciones -----

    /// <summary>Carpeta raíz del vault de Obsidian ("" = sin configurar).</summary>
    public string ObsidianVault { get; set; } = "";
    /// <summary>Subcarpeta del vault donde se guardan las notas de Selbar.</summary>
    public string ObsidianFolder { get; set; } = "Selbar";

    // ----- Voz -----

    /// <summary>Velocidad de la lectura en voz alta (0.5 a 3; 1 = normal).</summary>
    public double SpeakRate { get; set; } = 1.0;
    /// <summary>Atajo para dictar ("" = sin atajo, solo desde la barra/bandeja).</summary>
    public string DictationHotkey { get; set; } = "Ctrl+Shift+D";

    // ----- Perfiles por app -----

    /// <summary>Reglas que ocultan acciones según el proceso en primer plano.</summary>
    public List<AppProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Campos de versiones futuras: se conservan tal cual al guardar para que
    /// abrir estos ajustes con una versión anterior no pierda datos al reescribir.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
