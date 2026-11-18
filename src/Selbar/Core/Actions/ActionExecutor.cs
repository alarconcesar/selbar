using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Selbar.Core.Ai;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Core.Platform;
using Selbar.Core.Text;
using Selbar.Core.Web;
using Selbar.Native;

namespace Selbar.Core.Actions;

/// <summary>Ejecuta acciones sin IA en local y con IA vía proveedor.</summary>
public sealed class ActionExecutor
{
    public const string DefaultSystemPrompt =
        "Eres un asistente útil integrado en Windows. Responde de forma directa, breve y en el idioma del texto recibido.";

    private readonly AppSettings _settings;

    /// <summary>
    /// Orden alfabético de líneas en español: con OrdinalIgnoreCase «Árbol» caía
    /// después de «Zorro» porque compara puntos de código, no letras.
    /// </summary>
    private static readonly StringComparer LineComparer = CreateLineComparer();

    private static StringComparer CreateLineComparer()
    {
        try { return StringComparer.Create(CultureInfo.GetCultureInfo("es-ES"), ignoreCase: true); }
        catch { return StringComparer.InvariantCultureIgnoreCase; }
    }

    public ActionExecutor(AppSettings settings) => _settings = settings ?? new AppSettings();

    public record Result(bool Ok, string Title, string Text, OutputMode Output, string? Error = null);

    /// <summary>Instrucción de sistema efectiva (la del usuario o la de serie).</summary>
    public string SystemPrompt =>
        string.IsNullOrWhiteSpace(_settings.Ai.SystemPrompt) ? DefaultSystemPrompt : _settings.Ai.SystemPrompt.Trim();

    /// <summary>
    /// Construye el prompt final: sustituye placeholders y recorta la entrada
    /// (protege el contexto del modelo y la factura).
    /// </summary>
    public string BuildPrompt(ActionDefinition action, string text)
    {
        text ??= "";
        int max = Math.Clamp(_settings.Ai.MaxInputChars, 1000, 200_000);
        if (text.Length > max)
            text = text[..max] + "\n\n[…texto recortado por longitud…]";

        var template = string.IsNullOrWhiteSpace(action.PromptTemplate) ? "{text}" : action.PromptTemplate!;
        var now = DateTime.Now;

        // {text} se sustituye al final: así el texto del usuario no puede inyectar plantillas.
        return template
            .Replace("{lang}", _settings.Ai.TargetLang)
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{app}", SafeForegroundApp())
            .Replace("{text}", text);
    }

    public async Task<Result> ExecuteAsync(ActionDefinition action, string selectedText, CancellationToken ct = default)
    {
        try
        {
            return action.Output switch
            {
                OutputMode.DirectCopy => Copy(selectedText, action),
                OutputMode.Cut => Cut(action),
                OutputMode.Paste => PasteClipboard(action),
                OutputMode.PastePlain => PastePlain(action),
                OutputMode.Delete => Delete(action),
                OutputMode.SelectAll => SelectAll(action),
                OutputMode.Browser => OpenBrowser(action, selectedText),
                OutputMode.OpenUrl => OpenSelectedUrl(action, selectedText),
                OutputMode.RunCommand => RunCommand(action, selectedText),
                OutputMode.SimulateKey => Simulate(action),
                OutputMode.Copy or OutputMode.Popup or OutputMode.Replace when action.Kind == ActionKind.NoAi
                    => LocalText(action, selectedText),
                _ when action.Kind == ActionKind.Ai => await RunAiAsync(action, selectedText, ct).ConfigureAwait(false),
                _ => new Result(false, action.Title, "", action.Output, "Acción no soportada")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "acción " + action.Id);
            return new Result(false, action.Title, "", action.Output, ex.Message);
        }
    }

    // ---------------- Sin IA ----------------

    private static Result Copy(string? text, ActionDefinition a)
    {
        if (string.IsNullOrEmpty(text))
            return new Result(false, a.Title, "", a.Output, "No hay texto seleccionado que copiar.");
        return ClipboardService.TrySetText(text)
            ? new Result(true, a.Title, text, OutputMode.Copy)
            : new Result(false, a.Title, "", a.Output, "No se pudo usar el portapapeles (¿otra app lo tiene bloqueado?).");
    }

    private static Result Cut(ActionDefinition a)
    {
        NativeMethods.SendCtrlX();
        return new Result(true, a.Title, "", OutputMode.Cut);
    }

    private static Result PasteClipboard(ActionDefinition a)
    {
        if (!ClipboardService.ContainsText())
            return new Result(false, a.Title, "", a.Output, "El portapapeles no tiene texto.");
        NativeMethods.SendCtrlV();
        return new Result(true, a.Title, "", OutputMode.Paste);
    }

    /// <summary>Pega el portapapeles como texto plano (quita negritas, colores, tablas).</summary>
    private static Result PastePlain(ActionDefinition a)
    {
        var text = ClipboardService.TryGetText();
        if (string.IsNullOrWhiteSpace(text))
            return new Result(false, a.Title, "", a.Output, "El portapapeles no tiene texto.");

        if (!ClipboardService.TrySetText(text))
            return new Result(false, a.Title, "", a.Output, "No se pudo escribir en el portapapeles.");
        NativeMethods.SendCtrlV();
        return new Result(true, a.Title, "", OutputMode.PastePlain);
    }

    private static Result Delete(ActionDefinition a)
    {
        NativeMethods.SendKey(NativeMethods.VK_DELETE);
        return new Result(true, a.Title, "", OutputMode.Delete);
    }

    private static Result SelectAll(ActionDefinition a)
    {
        NativeMethods.SendChord(NativeMethods.VK_CONTROL, NativeMethods.VK_A);
        return new Result(true, a.Title, "", OutputMode.SelectAll);
    }

    private static Result OpenBrowser(ActionDefinition action, string text)
    {
        // Perfiles sociales: se limpia el @ y no se codifica el resto.
        if (action.Id.StartsWith("social.", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(action.UrlTemplate))
        {
            return OpenExternal(action.UrlTemplate!.Replace("{text}", LinkTools.Handle(text)), action);
        }

        // Redactar correo: se extrae el email (vale "Nombre <a@b.com>" y mailto:).
        if (action.Id == "contact.email")
        {
            string? email = EmailTools.ExtractEmail(text);
            if (email == null)
                return new Result(false, action.Title, "", action.Output,
                    "No encontré un correo válido en la selección.");
            return OpenExternal(EmailTools.ToMailto(email), action);
        }

        if (action.Id == "search.whatsapp")
        {
            string digits = NormalizePhone(text);
            if (digits.Length is < 7 or > 15)
                return new Result(false, action.Title, "", action.Output,
                    "El número no parece válido. Incluye el código de país (por ejemplo +52 55 1234 5678).");
            return OpenExternal("https://wa.me/" + digits, action);
        }

        var template = string.IsNullOrWhiteSpace(action.UrlTemplate)
            ? "https://www.google.com/search?q={text}"
            : action.UrlTemplate!;
        var value = action.UrlEncode ? HttpUtility.UrlEncode(text ?? "") : (text ?? "").Trim();
        var url = template.Replace("{text}", value);
        return OpenExternal(url, action);
    }

    /// <summary>Deja solo los dígitos del teléfono y quita el prefijo internacional (00 o 011).</summary>
    public static string NormalizePhone(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 2)
            digits = digits[2..];
        else if (digits.StartsWith("011", StringComparison.Ordinal) && digits.Length > 12)
            digits = digits[3..];
        return digits;
    }

    /// <summary>Abre la propia selección como URL (añade https:// si falta el esquema).</summary>
    private static Result OpenSelectedUrl(ActionDefinition action, string text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            return new Result(false, action.Title, "", action.Output, "No hay ninguna URL seleccionada.");
        if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            return new Result(false, action.Title, "", action.Output,
                "La selección no parece una URL (http, https o www).");
        }
        return OpenExternal(uri.AbsoluteUri, action);
    }

    private static Result OpenExternal(string url, ActionDefinition action)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return new Result(true, action.Title, url, OutputMode.Browser);
        }
        catch (Exception ex)
        {
            return new Result(false, action.Title, url, action.Output, "No se pudo abrir el navegador: " + ex.Message);
        }
    }

    private static Result Simulate(ActionDefinition action)
    {
        string name = (action.KeyName ?? "").Trim();
        ushort? vk = name.ToLowerInvariant() switch
        {
            "" or "enter" or "return" or "intro" => NativeMethods.VK_RETURN,
            "tab" or "tabulador" => NativeMethods.VK_TAB,
            "space" or "espacio" => NativeMethods.VK_SPACE,
            "delete" or "supr" or "suprimir" => NativeMethods.VK_DELETE,
            "backspace" or "retroceso" => NativeMethods.VK_BACK,
            "escape" or "esc" => NativeMethods.VK_ESCAPE,
            _ => null
        };
        // Antes, una tecla desconocida pulsaba Enter: en un chat eso envía el mensaje.
        if (vk == null)
            return new Result(false, action.Title, "", action.Output,
                $"La tecla «{name}» no está soportada. Usa Enter, Tab, Space, Delete, Backspace o Escape.");
        NativeMethods.SendKey(vk.Value);
        return new Result(true, action.Title, "", OutputMode.SimulateKey);
    }

    /// <summary>Ejecuta un comando de shell con la selección sustituida en {text}.</summary>
    private static Result RunCommand(ActionDefinition action, string text)
    {
        var command = (action.Script ?? "").Trim();
        if (command.Length == 0)
            return new Result(false, action.Title, "", action.Output, "El comando está vacío.");

        command = command.Replace("{text}", text ?? "");
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new Result(false, action.Title, "", action.Output, "No se pudo iniciar el comando.");

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch { }
                return new Result(false, action.Title, "", action.Output, "El comando tardó más de 15 s y se canceló.");
            }

            var output = stdout.Result.Trim();
            var error = stderr.Result.Trim();

            if (p.ExitCode != 0)
            {
                var detail = error.Length > 0 ? error : output;
                return new Result(false, action.Title, detail, action.Output,
                    $"El comando terminó con código {p.ExitCode}." + (detail.Length > 0 ? "\n" + detail : ""));
            }

            return new Result(true, action.Title,
                output.Length > 0 ? output : "(el comando no devolvió texto)", action.Output);
        }
        catch (Exception ex)
        {
            return new Result(false, action.Title, "", action.Output, "No se pudo ejecutar: " + ex.Message);
        }
    }

    private static Result LocalText(ActionDefinition action, string text)
    {
        string? outText;
        string? error = null;

        switch (action.Id)
        {
            case "text.upper":
                outText = text.ToUpperInvariant();
                break;
            case "text.lower":
                outText = text.ToLowerInvariant();
                break;
            case "text.toggle":
                outText = ToggleCase(text);
                break;
            case "text.capitalize":
                outText = new CultureInfo("es-ES").TextInfo.ToTitleCase(text.ToLowerInvariant());
                break;
            case "text.trim":
                outText = CleanSpaces(text);
                break;
            case "text.sort":
                outText = string.Join("\n", SplitLines(text).OrderBy(l => l, LineComparer));
                break;
            case "text.dedupe":
                outText = string.Join("\n", SplitLines(text).Distinct(StringComparer.OrdinalIgnoreCase));
                break;
            case "text.count":
                outText = CountStats(text);
                break;
            case "text.nolines":
                outText = RemoveLineBreaks(text);
                break;
            case "text.json":
                (outText, error) = FormatJson(text);
                break;
            case "date.info":
                outText = DateTools.Describe(text);
                if (outText.StartsWith("No reconozco", StringComparison.Ordinal))
                {
                    error = outText;
                    outText = null;
                }
                break;
            case "text.calc":
                if (CalcEngine.TryCompute(text, out double value))
                {
                    outText = CalcEngine.Explain(text, value);
                }
                else
                {
                    outText = null;
                    error = "No pude resolver esa operación. Prueba con algo como 12*8, (3+4)/2 o 350+20%.";
                }
                break;
            default:
                outText = null; // acción personalizada sin transformación: se devuelve el texto
                break;
        }

        if (error != null)
            return new Result(false, action.Title, "", action.Output, error);
        if (outText == null)
            return new Result(true, action.Title, text, action.Output);

        if (action.Output == OutputMode.Replace)
        {
            if (!ReplaceSelection(outText))
                return new Result(false, action.Title, outText, action.Output,
                    "No se pudo reemplazar la selección (¿ventana de origen cerrada?).");
        }
        else if (action.Output == OutputMode.Copy)
        {
            if (!ClipboardService.TrySetText(outText))
                return new Result(false, action.Title, outText, action.Output,
                    "No se pudo copiar al portapapeles.");
        }

        return new Result(true, action.Title, outText, action.Output);
    }

    /// <summary>Copia el texto y lo pega en la ventana que tenía el foco antes del popup.</summary>
    private static bool ReplaceSelection(string text)
    {
        if (!ClipboardService.TrySetText(text)) return false;
        // La barra no roba el foco (WS_EX_NOACTIVATE), pero si venimos de una ventana
        // de resultado sí hay que devolvérselo a la app de origen antes del Ctrl+V.
        // Si el origen ya no existe, no pegar a ciegas: el llamador avisa.
        if (!ForegroundTracker.Restore(70)) return false;
        NativeMethods.SendCtrlV();
        return true;
    }

    private static string ToggleCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
        return sb.ToString();
    }

    private static string CleanSpaces(string text)
    {
        var collapsed = Regex.Replace(text, @"[ \t]+", " ");
        var lines = collapsed.Split('\n').Select(l => l.Trim());
        var joined = string.Join("\n", lines);
        return Regex.Replace(joined, @"\n{3,}", "\n\n").Trim();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>Une las líneas en un párrafo y normaliza espacios.</summary>
    public static string RemoveLineBreaks(string text)
    {
        string joined = Regex.Replace(text, @"[ \t]*\r?\n[ \t]*", " ");
        joined = Regex.Replace(joined, @"[ \t]{2,}", " ");
        return joined.Trim();
    }

    private static string CountStats(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int chars = text.Length;
        int charsNoSpaces = text.Count(c => !char.IsWhiteSpace(c));
        int lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        int minutes = Math.Max(1, (int)Math.Ceiling(words.Length / 200.0));
        return $"Palabras: {words.Length}\nCaracteres: {chars} (sin espacios: {charsNoSpaces})\nLíneas: {lines}\nTiempo de lectura: ~{minutes} min";
    }

    private static (string? Text, string? Error) FormatJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return (JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }), null);
        }
        catch (JsonException ex)
        {
            return (null, "El texto no es JSON válido: " + ex.Message);
        }
    }

    // ---------------- Con IA ----------------

    private async Task<Result> RunAiAsync(ActionDefinition action, string text, CancellationToken ct)
    {
        var answer = await RunAiStreamingAsync(action, text, _ => { }, ct).ConfigureAwait(false);

        if (action.Output is OutputMode.Copy or OutputMode.Replace)
            ClipboardService.TrySetText(answer);

        return new Result(true, action.Title, answer, action.Output);
    }

    /// <summary>
    /// Ejecuta el prompt con streaming (onToken por fragmento). Si el endpoint no
    /// soporta stream y aún no llegó nada, reintenta en modo clásico antes de fallar.
    /// </summary>
    public async Task<string> RunAiStreamingAsync(ActionDefinition action, string text,
        Action<string> onToken, CancellationToken ct)
    {
        var ai = _settings.Ai;
        if (string.IsNullOrWhiteSpace(ai.BaseUrl))
            throw new InvalidOperationException("Configura la Base URL en Ajustes → IA.");
        if (string.IsNullOrWhiteSpace(ai.Model))
            throw new InvalidOperationException("Elige un modelo en Ajustes → IA.");

        var key = SettingsManager.Unprotect(ai.ApiKeyEncrypted);
        var prompt = BuildPrompt(action, text);
        var system = SystemPrompt;

        int received = 0;
        void Counted(string t) { received++; onToken(t); }

        using var provider = new OpenAiCompatibleProvider(
            ai.BaseUrl, ai.Model, key, ai.TimeoutSeconds, ai.GoSessionId, ai.Temperature, ai.MaxTokens);

        try
        {
            return await provider.CompleteStreamingAsync(system, prompt, Counted, ct).ConfigureAwait(false);
        }
        catch (AiProviderException ex) when (ex.RetryWithoutStream && received == 0 && !ct.IsCancellationRequested)
        {
            Logger.Warn("stream no soportado, reintento clásico: " + ex.Message);
            var full = await provider.CompleteAsync(system, prompt, ct).ConfigureAwait(false);
            onToken(full);
            return full;
        }
    }

    /// <summary>Analiza una imagen con el modelo configurado (visión). Sin streaming.</summary>
    public async Task<string> AskAboutImageAsync(string prompt, byte[] png, CancellationToken ct = default)
    {
        var ai = _settings.Ai;
        if (string.IsNullOrWhiteSpace(ai.BaseUrl))
            throw new InvalidOperationException("Configura la Base URL en Ajustes → IA.");
        if (string.IsNullOrWhiteSpace(ai.Model))
            throw new InvalidOperationException("Elige un modelo en Ajustes → IA.");

        var key = SettingsManager.Unprotect(ai.ApiKeyEncrypted);
        using var provider = new OpenAiCompatibleProvider(
            ai.BaseUrl, ai.Model, key, ai.TimeoutSeconds, ai.GoSessionId, ai.Temperature, ai.MaxTokens);

        return await provider.CompleteVisionAsync(SystemPrompt, prompt, png, "image/png", ct).ConfigureAwait(false);
    }

    /// <summary>Streaming multi-turno con historial (system + últimos 6 mensajes).</summary>
    public async Task<string> RunChatStreamingAsync(List<ChatMessage> history, Action<string> onToken, CancellationToken ct)
    {
        var ai = _settings.Ai;
        if (string.IsNullOrWhiteSpace(ai.BaseUrl))
            throw new InvalidOperationException("Configura la Base URL en Ajustes → IA.");
        if (string.IsNullOrWhiteSpace(ai.Model))
            throw new InvalidOperationException("Elige un modelo en Ajustes → IA.");

        var key = SettingsManager.Unprotect(ai.ApiKeyEncrypted);
        var trimmed = history.Count <= 7
            ? history
            : new List<ChatMessage> { history[0] }.Concat(history.Skip(history.Count - 6)).ToList();

        using var provider = new OpenAiCompatibleProvider(
            ai.BaseUrl, ai.Model, key, ai.TimeoutSeconds, ai.GoSessionId, ai.Temperature, ai.MaxTokens);
        return await provider.CompleteStreamingHistoryAsync(trimmed, onToken, ct).ConfigureAwait(false);
    }

    private static string SafeForegroundApp()
    {
        try { return NativeMethods.ForegroundProcessName(); }
        catch { return ""; }
    }
}
