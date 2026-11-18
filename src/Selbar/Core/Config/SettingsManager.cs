using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Selbar.Core.Diagnostics;
using Selbar.Core.Platform;

namespace Selbar.Core.Config;

/// <summary>
/// settings.json en la carpeta de datos + DPAPI para la API key.
/// Guardado atómico (tmp → replace) con copia de seguridad y validación/migración
/// de valores para que un JSON editado a mano no rompa la app.
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string FilePath { get; }
    public AppSettings Settings { get; private set; } = new();

    /// <summary>El archivo existía pero no se pudo leer (bloqueo, permisos, unidad caída).</summary>
    private bool _loadFailed;

    public SettingsManager(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.DataFile("settings.json");
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { }
    }

    public void Load()
    {
        bool needsSave = false;
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                // Campo a campo: un número escrito como texto ya no reinicia TODA
                // la configuración (y el archivo del usuario se conserva).
                Settings = ReadTolerant(json, out int ignoredFields);
                if (ignoredFields > 0)
                    Logger.Warn($"ajustes: {ignoredFields} campo(s) con tipo inválido se ignoraron; se usan los valores de serie para esos campos");
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // JSON corrupto: guardar copia y arrancar con valores de serie.
            BackupCorrupt();
            Logger.LogEx(ex, "settings corruptos, se usan valores de serie");
            Settings = new();
            needsSave = true;
        }
        catch (Exception ex)
        {
            // Archivo bloqueado, sin permisos o en una unidad que falló: NO es
            // corrupción. Arrancar con valores de serie en memoria, pero no
            // reescribir el archivo del usuario (un fallo transitorio de OneDrive
            // o del antivirus no puede borrarle la configuración).
            Logger.LogEx(ex, "no se pudo leer settings.json; se conserva el archivo");
            _loadFailed = true; // y no se sobrescribe al salir
            Settings.SeenWelcome = true; // no repetir la bienvenida por un fallo de lectura
            Settings.Ai.GoSessionId = EnsureSessionId(Settings.Ai.GoSessionId);
            return;
        }

        needsSave |= Migrate(Settings);

        // OpenCode GO necesita un session ID estable: generarlo una vez y persistirlo.
        string session = EnsureSessionId(Settings.Ai.GoSessionId);
        if (session != Settings.Ai.GoSessionId)
        {
            Settings.Ai.GoSessionId = session;
            needsSave = true;
        }

        if (needsSave) Save();
    }

    /// <summary>Copia el archivo ilegible antes de que la siguiente escritura lo reemplace.</summary>
    private void BackupCorrupt()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string copy = FilePath + ".corrupto-" + stamp;
            for (int n = 2; File.Exists(copy) && n < 1000; n++)
                copy = FilePath + ".corrupto-" + stamp + "-" + n;
            File.Copy(FilePath, copy, false);
        }
        catch { }
    }

    /// <summary>Id de sesión estable para OpenCode GO (nunca vacío: devuelve 400 MissingSessionID).</summary>
    private static string EnsureSessionId(string? current)
    {
        string id = (current ?? "").Trim();
        // Viaja como valor de la cabecera x-opencode-session: un espacio o un
        // salto de línea hacía fallar TODAS las peticiones con un error críptico.
        if (id.Length is > 0 and <= 64 &&
            id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return id;
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Lee los ajustes sin que un campo con el tipo equivocado tumbe el resto.
    /// Antes, un solo <c>"MaxEntries": "muchas"</c> hacía fallar el Deserialize
    /// completo: se copiaba el archivo y se arrancaba con valores de serie, y la
    /// primera escritura dejaba al usuario sin vault, sin atajo y sin sus perfiles.
    /// </summary>
    private static AppSettings ReadTolerant(string json, out int ignoredFields)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("El archivo de ajustes no es un objeto.");

        var settings = new AppSettings();
        ignoredFields = Populate(settings, doc.RootElement, preserveUnknown: true);
        return settings;
    }

    /// <summary>
    /// Copia un objeto JSON sobre una instancia ya creada campo a campo. Los
    /// valores con el tipo equivocado se descartan (queda el valor de serie) y
    /// los campos desconocidos de la raíz se conservan en <see cref="AppSettings.ExtensionData"/>.
    /// </summary>
    private static int Populate(object target, JsonElement json, bool preserveUnknown = false)
    {
        int ignored = 0;
        var type = target.GetType();
        foreach (var property in json.EnumerateObject())
        {
            var info = type.GetProperty(property.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (info == null || !info.CanWrite || info.GetIndexParameters().Length > 0 ||
                info.Name == nameof(AppSettings.ExtensionData))
            {
                if (preserveUnknown && target is AppSettings settings)
                {
                    settings.ExtensionData ??= new Dictionary<string, JsonElement>();
                    if (!settings.ExtensionData.ContainsKey(property.Name))
                        settings.ExtensionData[property.Name] = property.Value.Clone();
                }
                continue;
            }

            Type propertyType = info.PropertyType;
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                // Un null en un texto se normaliza a "": así ningún .Trim() de la UI
                // se encuentra un null por un JSON editado a mano.
                if (propertyType == typeof(string)) { info.SetValue(target, ""); continue; }
                if (propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null) { ignored++; continue; }
                info.SetValue(target, null); // Migrate vuelve a crear las secciones que hagan falta
                continue;
            }

            try
            {
                if (IsSimple(propertyType))
                {
                    info.SetValue(target, property.Value.Deserialize(propertyType, JsonOpts));
                }
                else if (propertyType.IsClass && property.Value.ValueKind == JsonValueKind.Object)
                {
                    object? child = info.GetValue(target) ?? Activator.CreateInstance(propertyType);
                    if (child == null) { ignored++; continue; }
                    info.SetValue(target, child);
                    ignored += Populate(child, property.Value);
                }
                else if (property.Value.ValueKind == JsonValueKind.Array &&
                         propertyType.IsGenericType &&
                         propertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // Elemento a elemento: una entrada mala no se lleva la lista entera.
                    Type elementType = propertyType.GetGenericArguments()[0];
                    var list = (System.Collections.IList?)Activator.CreateInstance(propertyType);
                    if (list == null) { ignored++; continue; }
                    foreach (var element in property.Value.EnumerateArray())
                    {
                        try
                        {
                            object? item = element.Deserialize(elementType, JsonOpts);
                            if (item == null) ignored++; else list.Add(item);
                        }
                        catch { ignored++; }
                    }
                    info.SetValue(target, list);
                }
                else
                {
                    info.SetValue(target, property.Value.Deserialize(propertyType, JsonOpts));
                }
            }
            catch { ignored++; }
        }
        return ignored;
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);

    /// <summary>Normaliza valores y migra ajustes de versiones anteriores. True si cambió algo.</summary>
    private static bool Migrate(AppSettings s)
    {
        bool changed = false;

        s.Ai ??= new();
        s.Triggers ??= new();
        s.History ??= new();
        s.ExcludedApps ??= new();
        s.ActionOverrides ??= new();

        // Perfiles: un JSON editado a mano puede traer nulls, saltos de línea o
        // ids repetidos que rompen la caja de texto de Ajustes (una regla con
        // "\n" se partía en dos y creaba una regla falsa al guardar).
        s.Profiles ??= new();
        s.Profiles.RemoveAll(p => p == null);
        foreach (var p in s.Profiles)
        {
            p.HiddenActions ??= new();
            string process = SingleLine(p.Process).Trim();
            if (!string.Equals(process, p.Process, StringComparison.Ordinal))
            {
                p.Process = process;
                changed = true;
            }
            var hidden = p.HiddenActions
                .Select(id => SingleLine(id).Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hidden.Count != p.HiddenActions.Count ||
                !hidden.SequenceEqual(p.HiddenActions, StringComparer.OrdinalIgnoreCase))
            {
                p.HiddenActions = hidden;
                changed = true;
            }
        }
        s.Profiles.RemoveAll(p => string.IsNullOrWhiteSpace(p.Process));

        if (s.SettingsVersion < AppSettings.CurrentVersion)
        {
            // v2 → v3: el tema pasa a ser una preferencia explícita (sistema/claro/oscuro).
            // Quien venía de una versión anterior mantenía el tema oscuro de serie.
            if (s.SettingsVersion < 3)
            {
                s.Theme = s.DarkMode ? "dark" : "light";
                s.DarkMode = s.Theme != "light";
            }

            // v5: orden inteligente (los ajustes viejos no traen el campo: queda en false).
            if (s.SettingsVersion < 5)
            {
                s.Triggers.SmartSuggest = true;
            }

            s.SettingsVersion = AppSettings.CurrentVersion;
            changed = true;
        }

        string theme = (s.Theme ?? "").Trim().ToLowerInvariant();
        if (theme is not ("system" or "dark" or "light")) theme = "system";
        if (theme != s.Theme) { s.Theme = theme; changed = true; }

        int historyMax = Math.Clamp(s.History.MaxEntries, 5, 500);
        if (historyMax != s.History.MaxEntries) { s.History.MaxEntries = historyMax; changed = true; }

        if (string.IsNullOrWhiteSpace(s.Ai.Provider)) { s.Ai.Provider = "OpenCode GO"; changed = true; }
        if (string.IsNullOrWhiteSpace(s.Ai.TargetLang)) { s.Ai.TargetLang = "español"; changed = true; }
        if (string.IsNullOrWhiteSpace(s.Ai.TranscriptionModel)) { s.Ai.TranscriptionModel = "whisper-large-v3-turbo"; changed = true; }

        int timeout = Math.Clamp(s.Ai.TimeoutSeconds, 5, 300);
        if (timeout != s.Ai.TimeoutSeconds) { s.Ai.TimeoutSeconds = timeout; changed = true; }

        double temp = Math.Clamp(s.Ai.Temperature, 0, 2);
        if (Math.Abs(temp - s.Ai.Temperature) > 0.0001) { s.Ai.Temperature = temp; changed = true; }

        int maxTokens = Math.Clamp(s.Ai.MaxTokens, 0, 200_000);
        if (maxTokens != s.Ai.MaxTokens) { s.Ai.MaxTokens = maxTokens; changed = true; }

        int maxChars = Math.Clamp(s.Ai.MaxInputChars, 1000, 200_000);
        if (maxChars != s.Ai.MaxInputChars) { s.Ai.MaxInputChars = maxChars; changed = true; }

        // Decisión inteligente: un timeout fuera de rango dejaría la barra colgada
        // esperando o cortando siempre.
        int decisionMs = Math.Clamp(s.Ai.DecisionTimeoutMs, 400, 5000);
        if (decisionMs != s.Ai.DecisionTimeoutMs) { s.Ai.DecisionTimeoutMs = decisionMs; changed = true; }
        string model = (s.Ai.DecisionModel ?? "").Trim();
        if (model.Length == 0) model = "gemini-decision-1";
        if (model != s.Ai.DecisionModel) { s.Ai.DecisionModel = model; changed = true; }
        // Sin key no hay decisión posible: se evita quedar «activado» en falso.
        if (s.Ai.DecisionEnabled && string.IsNullOrEmpty(s.Ai.DecisionApiKeyEncrypted))
        {
            s.Ai.DecisionEnabled = false;
            changed = true;
        }

        // Discreción por defecto: la barra se abre con el atajo, no al seleccionar.
        // Se aplica una sola vez para no revertir a quien después active los avisos.
        if (!s.InvocationMigrated)
        {
            s.InvocationMigrated = true;
            s.Triggers.AutoOnMouseSelection = false;
            s.Triggers.AutoOnKeyboardSelection = false;
            // Si además tenía el atajo apagado, la barra no se podría abrir nunca.
            if (!s.Triggers.HotkeyEnabled) s.Triggers.HotkeyEnabled = true;
            changed = true;
        }

        int minLen = Math.Clamp(s.Triggers.MinSelectionLength, 1, 500);
        if (minLen != s.Triggers.MinSelectionLength) { s.Triggers.MinSelectionLength = minLen; changed = true; }

        int longPress = Math.Clamp(s.Triggers.LongPressMs, 150, 2000);
        if (longPress != s.Triggers.LongPressMs) { s.Triggers.LongPressMs = longPress; changed = true; }

        int icons = Math.Clamp(s.Triggers.MaxBarIcons, 3, 15);
        if (icons != s.Triggers.MaxBarIcons) { s.Triggers.MaxBarIcons = icons; changed = true; }

        int hide = Math.Clamp(s.Triggers.AutoHideSeconds, 0, 600);
        if (hide != s.Triggers.AutoHideSeconds) { s.Triggers.AutoHideSeconds = hide; changed = true; }

        double font = Math.Clamp(s.ResultFontSize, 10, 22);
        if (Math.Abs(font - s.ResultFontSize) > 0.0001) { s.ResultFontSize = font; changed = true; }

        double w = Math.Clamp(s.ResultWidth, 360, 1600);
        if (Math.Abs(w - s.ResultWidth) > 0.5) { s.ResultWidth = w; changed = true; }
        double h = Math.Clamp(s.ResultHeight, 240, 1200);
        if (Math.Abs(h - s.ResultHeight) > 0.5) { s.ResultHeight = h; changed = true; }

        // Voz: la UI recorta a 0.5–3 al guardar, pero un JSON editado a mano
        // llegaba sin recortar al sintetizador. Se sanea aquí, en la entrada.
        double rate = Math.Clamp(s.SpeakRate, 0.5, 3.0);
        if (Math.Abs(rate - s.SpeakRate) > 0.0001) { s.SpeakRate = rate; changed = true; }

        // Apps excluidas: limpias, sin duplicados (da igual "x.exe" que "x") y con nosotros siempre dentro.
        var excluded = s.ExcludedApps
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Select(StripExe)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!excluded.Any(x => StripExe(x).Equals("Selbar", StringComparison.OrdinalIgnoreCase)))
            excluded.Insert(0, "Selbar");
        if (excluded.Count != s.ExcludedApps.Count || !excluded.SequenceEqual(s.ExcludedApps))
        {
            s.ExcludedApps = excluded;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(s.Triggers.Hotkey)) { s.Triggers.Hotkey = "Ctrl+Shift+Espacio"; changed = true; }

        return changed;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Quita saltos de línea y caracteres de control de un texto de una línea.</summary>
    private static string SingleLine(string? value) =>
        new((value ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    /// <summary>
    /// Escribe de forma atómica: primero un temporal y luego lo intercambia.
    /// Así un corte de luz o un OneDrive a medio sincronizar no deja el JSON roto.
    /// Se conserva como envoltorio compatible: no informa del fallo (ver <see cref="TrySave"/>).
    /// </summary>
    public void Save()
    {
        _ = TrySave(out _);
    }

    /// <summary>
    /// Igual que <see cref="Save"/> pero devuelve si el archivo quedó realmente escrito.
    /// Un fallo de permisos o de disco no puede confundirse con un guardado correcto.
    /// </summary>
    public bool TrySave(out string error) => TrySaveCore(Settings, out error);

    /// <summary>Escribe en disco el objeto indicado (usado también por ApplyInPlace).</summary>
    private bool TrySaveCore(AppSettings settings, out string error)
    {
        error = "";

        // Si al arrancar el archivo existía pero no se pudo leer (antivirus, OneDrive,
        // bloqueo momentáneo), la memoria tiene valores de serie. Guardar aquí borraría
        // la configuración real del usuario: se conserva el archivo y se avisa.
        if (_loadFailed && File.Exists(FilePath))
        {
            // Solo se permite sobrescribir si el archivo es ilegible por JSON corrupto
            // (entonces no hay nada que conservar). Si sigue bloqueado o se puede leer,
            // mandan los datos del usuario y la memoria (valores de serie) no se escribe.
            bool corrupt = false;
            try { _ = ReadTolerant(File.ReadAllText(FilePath), out _); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { corrupt = true; }
            catch { corrupt = false; }

            if (!corrupt)
            {
                error = "No se pudo leer settings.json al arrancar; para no perder tu configuración no se sobrescribe.";
                Logger.Warn(error);
                return false;
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            var tmp = FilePath + ".tmp";
            // Volcar a disco antes de intercambiar: un corte de luz podía dejar el
            // temporal vacío y File.Replace lo ascendía a settings.json.
            var bytes = Utf8NoBom.GetBytes(json);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                try { fs.Flush(true); } catch { } // best-effort en unidades de red
            }

            if (File.Exists(FilePath))
            {
                var backup = FilePath + ".bak";
                try
                {
                    File.Replace(tmp, FilePath, backup, true);
                    if (File.Exists(FilePath)) { _loadFailed = false; return true; }
                    error = "el archivo de ajustes no existe después de reemplazarlo";
                    return false;
                }
                catch
                {
                    // File.Replace falla en algunos sistemas de ficheros (red, OneDrive).
                    // Se guarda copia del anterior antes de sobrescribir con el temporal.
                    try { File.Copy(FilePath, backup, true); } catch { }
                    File.Copy(tmp, FilePath, true);
                }
            }
            else
            {
                File.Move(tmp, FilePath, true);
            }

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            if (!File.Exists(FilePath))
            {
                error = "el archivo de ajustes no existe después de escribir";
                return false;
            }
            _loadFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // No dejar un temporal a medias que confunda al siguiente guardado.
            try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
            Logger.LogEx(ex, "guardar ajustes");
            return false;
        }
    }

    /// <summary>
    /// Aplica un candidato solo si es válido y se pudo persistir. La escritura se
    /// hace con el candidato, pero los valores se copian EN SITIO sobre la instancia
    /// viva: así los componentes que guardaron una referencia (SelectionMonitor con
    /// <c>Triggers</c>, ActionExecutor con <c>Ai</c>…) ven los cambios al instante.
    /// Si la validación o el disco fallan, la memoria queda exactamente como estaba.
    /// </summary>
    public bool TryApply(AppSettings candidate, out string error)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Validate(candidate) is { } invalid)
        {
            error = invalid;
            return false;
        }

        // Mismos recortes y normalizaciones que al cargar: un borrador con
        // valores fuera de rango no debe quedar escrito en el archivo.
        Migrate(candidate);

        // Un candidato de fábrica (Restablecer) no trae session ID: generarlo antes
        // de escribir para que restablecer no deje la IA con 400 hasta reiniciar.
        candidate.Ai.GoSessionId = EnsureSessionId(candidate.Ai.GoSessionId);

        // La bienvenida se marca vista una sola vez: un borrador abierto antes de
        // cerrarla no puede hacer que vuelva a aparecer en el siguiente arranque.
        if (Settings.SeenWelcome) candidate.SeenWelcome = true;

        if (!TrySaveCore(candidate, out error)) return false;
        ApplyInPlace(candidate);
        return true;
    }

    /// <summary>
    /// Un candidato incompleto no debe llegar al disco: ApplyInPlace necesita estas
    /// secciones y escribir primero dejaría memoria y archivo en desacuerdo.
    /// </summary>
    private static string? Validate(AppSettings candidate)
    {
        if (candidate.Ai == null) return "faltan los ajustes de IA";
        if (candidate.Triggers == null) return "faltan los ajustes de selección";
        if (candidate.History == null) return "faltan los ajustes del historial";
        return null;
    }

    /// <summary>Copia los valores del candidato sobre la configuración viva sin cambiar sus referencias.</summary>
    private void ApplyInPlace(AppSettings source)
    {
        var target = Settings;
        target.SettingsVersion = source.SettingsVersion;

        if (source.Ai != null) { target.Ai ??= new AiConfig(); CopyAi(target.Ai, source.Ai); }
        if (source.Triggers != null) { target.Triggers ??= new TriggerConfig(); CopyTriggers(target.Triggers, source.Triggers); }
        if (source.History != null) { target.History ??= new HistoryConfig(); CopyHistory(target.History, source.History); }

        target.ExcludedApps = new List<string>(source.ExcludedApps ?? new List<string>());
        // Copia profunda: lo vivo no debe compartir instancias con el candidato (ni
        // con un archivo importado) porque mutarlo después cambiaría los ajustes
        // guardados en memoria sin pasar por disco.
        target.ActionOverrides = CloneList(source.ActionOverrides);
        target.Profiles = CloneList(source.Profiles);

        target.StartWithWindows = source.StartWithWindows;
        target.Theme = source.Theme;
        target.DarkMode = source.DarkMode;
        target.SeenWelcome = source.SeenWelcome;
        target.ResultFontSize = source.ResultFontSize;
        target.ResultWidth = source.ResultWidth;
        target.ResultHeight = source.ResultHeight;
        target.ObsidianVault = source.ObsidianVault;
        target.ObsidianFolder = source.ObsidianFolder;
        target.SpeakRate = source.SpeakRate;
        target.DictationHotkey = source.DictationHotkey;

        target.ExtensionData = source.ExtensionData == null
            ? null
            : new Dictionary<string, JsonElement>(source.ExtensionData);
    }

    /// <summary>
    /// Copia profunda de una lista de ajustes con el mismo serializador que el
    /// archivo: conserva todos los campos (incluidos los que añada el futuro) y
    /// descarta entradas nulas que romperían el registro.
    /// </summary>
    private static List<T> CloneList<T>(List<T>? items) where T : class
    {
        if (items == null || items.Count == 0) return new List<T>();
        var clean = items.Where(x => x != null).ToList();
        if (clean.Count == 0) return new List<T>();
        var json = JsonSerializer.Serialize(clean, JsonOpts);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new List<T>();
    }

    private static void CopyAi(AiConfig target, AiConfig source)
    {
        target.Provider = source.Provider;
        target.BaseUrl = source.BaseUrl;
        target.Model = source.Model;
        target.ApiKeyEncrypted = source.ApiKeyEncrypted;
        target.TargetLang = source.TargetLang;
        target.TimeoutSeconds = source.TimeoutSeconds;
        target.GoSessionId = source.GoSessionId;
        target.SystemPrompt = source.SystemPrompt;
        target.Temperature = source.Temperature;
        target.MaxTokens = source.MaxTokens;
        target.MaxInputChars = source.MaxInputChars;
        target.GroqApiKeyEncrypted = source.GroqApiKeyEncrypted;
        target.TranscriptionModel = source.TranscriptionModel;
    }

    private static void CopyTriggers(TriggerConfig target, TriggerConfig source)
    {
        target.AutoOnMouseSelection = source.AutoOnMouseSelection;
        target.SelectionPeek = source.SelectionPeek;
        target.AutoOnKeyboardSelection = source.AutoOnKeyboardSelection;
        target.HotkeyEnabled = source.HotkeyEnabled;
        target.Hotkey = source.Hotkey;
        target.MinSelectionLength = source.MinSelectionLength;
        target.LongPressMs = source.LongPressMs;
        target.MaxBarIcons = source.MaxBarIcons;
        target.AutoHideSeconds = source.AutoHideSeconds;
        target.ShowNumbers = source.ShowNumbers;
        target.DigitShortcuts = source.DigitShortcuts;
        target.SmartSuggest = source.SmartSuggest;
        target.SearchOnType = source.SearchOnType;
    }

    private static void CopyHistory(HistoryConfig target, HistoryConfig source)
    {
        target.Enabled = source.Enabled;
        target.Encrypt = source.Encrypt;
        target.ClearOnExit = source.ClearOnExit;
        target.MaxEntries = source.MaxEntries;
    }

    /// <summary>Vuelve a los valores de fábrica (mantiene la bienvenida vista).</summary>
    public void Reset()
    {
        // Igual que TryApply: si el disco falla no se toca la configuración viva.
        _ = TryApply(new AppSettings { SeenWelcome = true }, out _);
    }

    /// <summary>
    /// Exporta una copia de los ajustes. Por defecto NO incluye la API key
    /// (un archivo de ajustes no debería llevarse secretos sin querer).
    /// </summary>
    public void Export(string path, bool includeApiKey = false)
    {
        var json = JsonSerializer.Serialize(Settings, JsonOpts);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        copy.Ai ??= new();
        if (!includeApiKey)
        {
            copy.Ai.ApiKeyEncrypted = "";
            copy.Ai.GroqApiKeyEncrypted = "";
        }
        // Atómico como Save: un corte a mitad de escritura no deja el export truncado.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOpts), Utf8NoBom);
        File.Move(tmp, path, true);
    }

    /// <summary>Lee y valida un archivo de ajustes exportado.</summary>
    public static AppSettings ReadSettingsFile(string path)
    {
        string json = File.ReadAllText(path);
        AppSettings imported;
        try
        {
            // Campo a campo, como al cargar: un valor con el tipo equivocado no
            // impide importar el resto de la configuración.
            imported = ReadTolerant(json, out _);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El archivo no contiene ajustes válidos.", ex);
        }
        Migrate(imported);
        if (string.IsNullOrWhiteSpace(imported.Ai.GoSessionId))
            imported.Ai.GoSessionId = Guid.NewGuid().ToString("N");
        return imported;
    }

    /// <summary>
    /// Sustituye los ajustes actuales por los importados (conserva la key local si
    /// el archivo no trae una utilizable en este equipo). Solo aplica si el archivo
    /// se pudo escribir: un fallo de disco no deja la importación a medias.
    /// </summary>
    public bool TryAdopt(AppSettings imported, out string error)
    {
        ArgumentNullException.ThrowIfNull(imported);
        error = "";
        imported.Ai ??= new();
        imported.Triggers ??= new();
        imported.History ??= new();
        // Un archivo importado puede traer valores de otra versión o fuera de
        // rango (ancho 99999, tipografía 40): se normaliza antes de escribirlo.
        Migrate(imported);
        imported.Ai.GoSessionId = EnsureSessionId(imported.Ai.GoSessionId);

        // Una key cifrada con DPAPI en otro equipo/usuario no se puede descifrar
        // aquí: adoptarla dejaría la IA sin credencial aunque la key local
        // funcionase. Solo se acepta si realmente se puede descifrar.
        if (string.IsNullOrEmpty(imported.Ai.ApiKeyEncrypted) ||
            string.IsNullOrEmpty(Unprotect(imported.Ai.ApiKeyEncrypted)))
            imported.Ai.ApiKeyEncrypted = Settings.Ai?.ApiKeyEncrypted ?? "";
        if (string.IsNullOrEmpty(imported.Ai.GroqApiKeyEncrypted) ||
            string.IsNullOrEmpty(Unprotect(imported.Ai.GroqApiKeyEncrypted)))
            imported.Ai.GroqApiKeyEncrypted = Settings.Ai?.GroqApiKeyEncrypted ?? "";

        // La bienvenida es del equipo, no del archivo: manda lo que ya pasó aquí.
        // Solo se promueve false→true: un archivo importado no puede desactivar la
        // bienvenida ya vista en este equipo (misma política que TryApply).
        if (Settings.SeenWelcome) imported.SeenWelcome = true;

        if (!TrySaveCore(imported, out error)) return false;
        // En sitio: el monitor de selección y el ejecutor guardan referencias a
        // Triggers/Ai y deben ver los valores importados sin reiniciar.
        ApplyInPlace(imported);
        return true;
    }

    /// <summary>Compatibilidad: adopta sin informar del fallo (ver <see cref="TryAdopt"/>).</summary>
    public void Adopt(AppSettings imported) => TryAdopt(imported, out _);

    // --- DPAPI (CurrentUser) ---
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "cifrar API key");
            return "";
        }
    }

    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Otro usuario/equipo o cifrado inválido: la key deja de servir, no es un error grave.
            return "";
        }
    }
}
