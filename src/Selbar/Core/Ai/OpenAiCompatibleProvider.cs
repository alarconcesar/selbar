using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Selbar.Core.Ai;

/// <summary>
/// Error de un proveedor de IA con la información de si merece la pena
/// reintentar sin streaming (endpoints que no soportan SSE, por ejemplo).
/// </summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException(string message, bool retryWithoutStream = false, Exception? inner = null)
        : base(message, inner)
    {
        RetryWithoutStream = retryWithoutStream;
    }

    /// <summary>True si tiene sentido repetir la petición en modo clásico (sin stream).</summary>
    public bool RetryWithoutStream { get; }
}

/// <summary>
/// Proveedor OpenAI-compatible: sirve para OpenAI, OpenRouter, DeepSeek, Groq,
/// Mistral, xAI, Gemini (modo compat), Ollama, LM Studio y OpenCode GO.
/// Reutiliza un único HttpClient (evita agotar sockets), aplica timeouts por
/// petición, reintenta fallos transitorios y tolera streams no-SSE.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    private const int MaxAttempts = 3;

    /// <summary>Tope del cuerpo de una respuesta normal (anti respuesta infinita).</summary>
    private const long MaxBodyBytes = 8L * 1024 * 1024;

    /// <summary>Tope del cuerpo de una respuesta de error (solo se usa el mensaje).</summary>
    private const long MaxErrorBytes = 64L * 1024;

    /// <summary>
    /// Tope del texto acumulado en un stream. Sin él, un proveedor (o un proxy)
    /// que no cierra nunca llenaba la RAM: el tope de cuerpo solo cubría las
    /// respuestas sin streaming.
    /// </summary>
    private const int MaxStreamChars = 8 * 1024 * 1024;

    /// <summary>Tope de una imagen para visión (el base64 crece un 33 %).</summary>
    public const long MaxVisionImageBytes = 20L * 1024 * 1024;

    /// <summary>Cliente compartido: un HttpClient por app, no por petición.</summary>
    private static readonly HttpClient Shared = CreateSharedClient();

    private static readonly string UserAgent =
        "Selbar/" + (typeof(OpenAiCompatibleProvider).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    private readonly string _baseUrl;
    private readonly string _chatUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _sessionId;
    private readonly int _timeoutSeconds;
    private readonly double _temperature;
    private readonly int _maxTokens;

    public string Name => "OpenAI-Compatible";

    /// <summary>Modo offline (tests): bloquea cualquier llamada de red.</summary>
    private static bool Offline =>
        string.Equals(Environment.GetEnvironmentVariable("SELBAR_OFFLINE"), "1", StringComparison.Ordinal);

    public OpenAiCompatibleProvider(string baseUrl, string model, string apiKey,
        int timeoutSeconds = 60, string sessionId = "", double temperature = 0.3, int maxTokens = 0)
    {
        _baseUrl = NormalizeBase(baseUrl);
        _chatUrl = _baseUrl + "/chat/completions";
        _model = (model ?? "").Trim();
        _apiKey = (apiKey ?? "").Trim();
        _sessionId = sessionId ?? "";
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);
        _temperature = Math.Clamp(temperature, 0, 2);
        _maxTokens = Math.Max(0, maxTokens);
    }

    /// <summary>
    /// Deja la Base URL limpia: acepta tanto ".../v1" como ".../v1/chat/completions"
    /// (es muy común pegarla entera).
    /// </summary>
    public static string NormalizeBase(string? baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/models" })
        {
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                b = b[..^suffix.Length].TrimEnd('/');
                break;
            }
        }
        return b;
    }

    // ---------------- API pública ----------------

    public Task<string> CompleteAsync(string systemPrompt, string userText, CancellationToken ct = default)
        => CompleteAsync(BuildMessages(systemPrompt, userText), ct);

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var json = BuildPayload(messages, stream: false);
        using var res = await SendAsync(() => CreateRequest(_chatUrl, json), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        string body;
        try
        {
            body = await ReadBodyAsync(res, timeout.Token, MaxBodyBytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"El proveedor tardó más de {_timeoutSeconds} s en responder.");
        }

        var content = SseParser.ExtractFullContent(body)
            ?? SseParser.TryExtractSseContent(body); // algún servidor ignora stream:false
        if (content == null)
        {
            throw new AiProviderException(SseParser.ExplainNoContent(body), retryWithoutStream: false);
        }
        return content;
    }

    public Task<string> CompleteStreamingAsync(string systemPrompt, string userText,
        Action<string> onToken, CancellationToken ct = default)
        => CompleteStreamingMessagesAsync(BuildMessages(systemPrompt, userText), onToken, ct);

    /// <summary>
    /// Pregunta sobre una imagen (visión). La envía como data URL base64 en el
    /// formato OpenAI-compatible: content = [{type:text}, {type:image_url}].
    /// Sin streaming: la respuesta llega completa.
    /// </summary>
    public async Task<string> CompleteVisionAsync(string systemPrompt, string userText, byte[] imageBytes,
        string mimeType = "image/png", CancellationToken ct = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new InvalidOperationException("No hay imagen que analizar.");
        if (imageBytes.Length > MaxVisionImageBytes)
            throw new InvalidOperationException(
                $"La imagen es demasiado grande para analizarla ({imageBytes.Length / 1024 / 1024} MB; máximo {MaxVisionImageBytes / 1024 / 1024} MB).");
        if (Offline)
            throw new InvalidOperationException("Modo offline: el análisis de imagen necesita conexión.");
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("Elige un modelo en Ajustes → IA.");
        if (!IsSupportedImageMime(mimeType)) mimeType = "image/png";

        string dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userText },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            ["stream"] = false
        };
        if (!IsReasoningModel(_model)) payload["temperature"] = _temperature;
        if (_maxTokens > 0) payload["max_tokens"] = _maxTokens;

        string json = JsonSerializer.Serialize(payload);
        using var res = await SendAsync(() => CreateRequest(_chatUrl, json), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        string body;
        try
        {
            body = await ReadBodyAsync(res, timeout.Token, MaxBodyBytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"El proveedor tardó más de {_timeoutSeconds} s en responder.");
        }

        var content = SseParser.ExtractFullContent(body)
            ?? SseParser.TryExtractSseContent(body); // algún servidor ignora stream:false
        if (content == null)
        {
            throw new AiProviderException(SseParser.ExplainNoContent(body), retryWithoutStream: false);
        }
        return content;
    }

    public Task<string> CompleteStreamingHistoryAsync(IReadOnlyList<ChatMessage> messages,
        Action<string> onToken, CancellationToken ct = default)
        => CompleteStreamingMessagesAsync(messages, onToken, ct);

    public void Dispose() { /* el HttpClient es compartido: no se destruye por instancia */ }

    // ---------------- Streaming ----------------

    private async Task<string> CompleteStreamingMessagesAsync(
        IReadOnlyList<ChatMessage> messages, Action<string> onToken, CancellationToken ct)
    {
        onToken ??= _ => { };
        var json = BuildPayload(messages, stream: true);
        using var res = await SendAsync(() => CreateRequest(_chatUrl, json), ct).ConfigureAwait(false);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        using var stream = await ReadStreamAsync(res, idle, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192);

        // Reinicia el reloj de inactividad con cada línea: una generación larga
        // no se corta, pero un proveedor colgado sí.
        async Task<string?> ReadLineAsync()
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"El proveedor dejó de responder ({_timeoutSeconds} s sin datos).");
            }
            if (line != null)
            {
                try { idle.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds)); } catch { }
            }
            return line;
        }

        // Primera línea no vacía: decide si es SSE o una respuesta JSON de golpe.
        string? first = null;
        while (first == null)
        {
            var line = await ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;
            if (line.Trim().Length > 0) first = line;
        }

        if (first == null)
            throw new AiProviderException("El proveedor no devolvió datos (respuesta vacía).", retryWithoutStream: true);

        if (!SseParser.LooksLikeSse(first))
        {
            // No es SSE: algunos servidores ignoran stream:true y contestan JSON normal.
            string rest;
            try
            {
                rest = await reader.ReadToEndAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"El proveedor dejó de responder ({_timeoutSeconds} s sin datos).");
            }
            var body = first + "\n" + rest;
            var full = SseParser.ExtractFullContent(body);
            if (full == null)
                throw new AiProviderException(
                    "Respuesta inesperada del proveedor: " + SseParser.DescribeError(body), retryWithoutStream: true);
            if (full.Length > 0) onToken(full);
            return full;
        }

        var sb = new StringBuilder();
        var data = new StringBuilder();
        bool done = false;
        bool sawDone = false;

        void Emit()
        {
            if (data.Length == 0) return;
            var payload = data.ToString();
            data.Clear();
            var chunk = SseParser.ParseData(payload);
            switch (chunk.Kind)
            {
                case StreamChunkKind.Content:
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        if (sb.Length + chunk.Text.Length > MaxStreamChars)
                            throw new AiProviderException(
                                $"El proveedor envió más de {MaxStreamChars / (1024 * 1024)} MB de texto en el stream; se corta para no agotar la memoria.",
                                retryWithoutStream: false);
                        sb.Append(chunk.Text);
                        onToken(chunk.Text);
                    }
                    break;
                case StreamChunkKind.Done:
                    done = true;
                    sawDone = true;
                    break;
                case StreamChunkKind.Error:
                    throw new AiProviderException(
                        "El proveedor devolvió un error: " + chunk.Text, retryWithoutStream: false);
            }
        }

        var current = first;
        while (!done)
        {
            if (current == null)
            {
                Emit(); // último evento si el stream cerró sin línea en blanco
                break;
            }

            var t = current.Trim();
            if (t.Length == 0)
            {
                Emit();
            }
            else if (t == "[DONE]")
            {
                // Algunos proxies mandan [DONE] sin el prefijo "data:". Antes se
                // ignoraba y, si el servidor dejaba la conexión abierta (SSE
                // normal), la respuesta acababa en timeout y se perdía lo ya
                // generado.
                done = true;
                sawDone = true;
            }
            else if (t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = t.Length > 6 && t[5] == ' ' ? t[6..] : t[5..];
                if (data.Length > 0) data.Append('\n');
                data.Append(payload);
                if (data.Length > MaxStreamChars)
                    throw new AiProviderException(
                        $"El proveedor envió un evento de más de {MaxStreamChars / (1024 * 1024)} MB; se corta para no agotar la memoria.",
                        retryWithoutStream: false);
            }
            // "event:", ":" (comentarios/keep-alive) y demás líneas: se ignoran.

            // [DONE] visto: no se espera otra línea. Antes se hacía siempre un
            // ReadLine más y, si el servidor dejaba la respuesta abierta (SSE sin
            // cerrar la conexión), la app se quedaba hasta el timeout y perdía
            // todo lo generado.
            if (done)
            {
                Emit(); // un evento pendiente sin línea en blanco no se pierde
                break;
            }

            current = await ReadLineAsync().ConfigureAwait(false);
        }

        var result = sb.ToString().Trim();
        if (result.Length == 0)
        {
            // Si el stream terminó con [DONE], el servidor SÍ soporta streaming: el
            // modelo no devolvió texto (filtrado o solo razonamiento). Reintentar en
            // modo clásico duplicaría la petición para nada.
            throw new AiProviderException(
                sawDone
                    ? "El modelo no devolvió texto (¿respuesta filtrada o solo razonamiento interno?)."
                    : "El proveedor no devolvió texto (stream vacío).",
                retryWithoutStream: !sawDone);
        }
        return result;
    }

    private static async Task<Stream> ReadStreamAsync(HttpResponseMessage res,
        CancellationTokenSource idle, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("El proveedor no empezó a enviar la respuesta a tiempo.");
        }
    }

    // ---------------- HTTP ----------------

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        if (Offline)
            throw new AiProviderException("Modo offline activo (SELBAR_OFFLINE=1): no se hacen llamadas de red.");

        EnsureHttpUrl(_baseUrl, "La Base URL");

        Exception? last = null;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            try
            {
                using var req = requestFactory();
                var res = await Shared.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                if (res.IsSuccessStatusCode) return res;

                int status = (int)res.StatusCode;
                string reason = res.ReasonPhrase ?? res.StatusCode.ToString();
                string body;
                try { body = await ReadBodyAsync(res, timeout.Token, MaxErrorBytes).ConfigureAwait(false); }
                catch { body = ""; }
                res.Dispose();

                var message = $"HTTP {status} {reason}: {SseParser.DescribeError(body)}";
                if (status == 404)
                    message += " (¿Base URL incorrecta? Algunos proveedores necesitan /v1 al final)";
                bool transient = status == 408 || status == 429 || status >= 500;
                if (transient && attempt < MaxAttempts - 1)
                {
                    last = new HttpRequestException(message, null, (HttpStatusCode)status);
                    continue;
                }

                // 405/415/422: el endpoint puede no aceptar el modo stream. Un 400
                // solo se reintenta si el error habla de stream/sse: los 400 por
                // contexto demasiado largo o modelo inválido fallarían igual y
                // duplicarían la petición. Un 404 es una Base URL mal puesta.
                bool retryNoStream = status is 405 or 415 or 422
                    || (status == 400 &&
                        (message.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("sse", StringComparison.OrdinalIgnoreCase)));
                throw new AiProviderException(message, retryNoStream);

            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException(
                    $"El proveedor no respondió a tiempo ({_timeoutSeconds} s). Revisa la Base URL y la conexión.");
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts - 1 && IsTransient(ex))
            {
                last = ex;
            }
            catch (HttpRequestException ex)
            {
                throw new AiProviderException(
                    "No se pudo conectar con el proveedor: " + ex.Message, retryWithoutStream: false, ex);
            }
        }

        throw last ?? new AiProviderException("No se pudo conectar con el proveedor de IA.");
    }

    private static bool IsTransient(HttpRequestException ex)
    {
        if (ex.StatusCode is not { } code) return true; // fallo de red/DNS/TLS
        int value = (int)code;
        return value == 408 || value == 429 || value >= 500;
    }

    /// <summary>
    /// Lee el cuerpo con tope de tamaño y con el token del timeout (sin él, un
    /// proveedor que manda cabeceras y luego se cuelga bloquea la llamada para
    /// siempre, porque el HttpClient compartido no tiene Timeout global).
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage res, CancellationToken ct, long maxBytes)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new AiProviderException(
                    $"El proveedor devolvió una respuesta demasiado grande (más de {maxBytes / 1024 / 1024} MB).");
            buffer.Write(chunk, 0, read);
        }

        // Algunos proxies añaden BOM y eso rompe el parser de JSON.
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
    }

    private static bool IsSupportedImageMime(string? mimeType) => (mimeType ?? "").Trim().ToLowerInvariant()
        is "image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif";

    /// <summary>
    /// La Base URL tiene que ser http/https absoluta: con file://, ftp: o una
    /// ruta relativa, HttpClient lanza un error críptico en inglés.
    /// </summary>
    private static void EnsureHttpUrl(string url, string what)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new AiProviderException(
                $"{what} no es una URL válida: debe empezar por http:// o https:// (revisa Ajustes → IA).");
        }
    }

    private HttpRequestMessage CreateRequest(string url, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyHeaders(req, _baseUrl, _apiKey, _sessionId);
        return req;
    }

    private static void ApplyHeaders(HttpRequestMessage req, string baseUrl, string apiKey, string sessionId)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        if (baseUrl.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://selbar.app");
            req.Headers.TryAddWithoutValidation("X-Title", "Selbar");
        }

        // OpenCode GO exige x-opencode-session (ID estable) o devuelve 400 MissingSessionID.
        if (baseUrl.Contains("opencode.ai/zen/go", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            req.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
        }
    }

    private string BuildPayload(IReadOnlyList<ChatMessage> messages, bool stream)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("Elige un modelo en Ajustes → IA.");

        var list = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            if (m == null || string.IsNullOrEmpty(m.Content)) continue;
            list.Add(new { role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role, content = m.Content });
        }
        if (list.Count == 0)
            throw new InvalidOperationException("No hay texto que enviar al modelo.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = list.ToArray(),
            ["stream"] = stream
        };

        // Los modelos de razonamiento (o1/o3/gpt-5...) rechazan temperature.
        if (!IsReasoningModel(_model)) payload["temperature"] = _temperature;
        if (_maxTokens > 0) payload["max_tokens"] = _maxTokens;

        return JsonSerializer.Serialize(payload);
    }

    private static bool IsReasoningModel(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        return m.StartsWith("o1", StringComparison.Ordinal)
            || m.StartsWith("o3", StringComparison.Ordinal)
            || m.StartsWith("o4", StringComparison.Ordinal)
            || m.StartsWith("gpt-5", StringComparison.Ordinal);
    }

    private static List<ChatMessage> BuildMessages(string systemPrompt, string userText)
    {
        var list = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(systemPrompt)) list.Add(new ChatMessage("system", systemPrompt));
        list.Add(new ChatMessage("user", userText ?? ""));
        return list;
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        // El timeout se controla por petición (así el streaming puede durar más).
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // ---------------- Modelos ----------------

    /// <summary>
    /// Lista de modelos vía GET {baseUrl}/models (formato OpenAI: {"data":[{"id":...}]}).
    /// Funciona con OpenAI, OpenRouter, Ollama, LM Studio y OpenCode GO (público, sin key).
    /// </summary>
    public static async Task<List<string>> FetchModelsAsync(string baseUrl, string apiKey,
        int timeoutSeconds = 15, CancellationToken ct = default)
    {
        if (Offline)
            throw new InvalidOperationException("Modo offline activo (SELBAR_OFFLINE=1): no se consultan modelos.");

        var b = NormalizeBase(baseUrl);
        if (string.IsNullOrEmpty(b)) throw new InvalidOperationException("Indica primero la Base URL.");
        EnsureHttpUrl(b, "La Base URL");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60)));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, b + "/models");
            ApplyHeaders(req, b, apiKey, "");
            using var res = await Shared.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            // Tope: una lista de modelos normal ocupa pocos KB.
            var body = await ReadBodyAsync(res, timeout.Token, 2L * 1024 * 1024).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                string hint = (int)res.StatusCode == 404 ? " (¿falta /v1 en la Base URL?)" : "";
                throw new HttpRequestException(
                    $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}: {SseParser.DescribeError(body)}{hint}");
            }
            return ParseModels(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("El proveedor no respondió (timeout). Revisa la Base URL.");
        }
    }

    /// <summary>Extrae los ids de modelo de las distintas formas que devuelven los proveedores.</summary>
    public static List<string> ParseModels(string body)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse((body ?? "").Trim().TrimStart('\uFEFF'));
            var root = doc.RootElement;

            // Un objeto suelto {"id":"modelo"} también es una lista de uno.
            if (root.ValueKind == JsonValueKind.Object &&
                !root.TryGetProperty("data", out _) && !root.TryGetProperty("models", out _))
            {
                var only = ReadModelId(root);
                if (only != null) return new List<string> { only };
            }

            IEnumerable<JsonElement> items = Array.Empty<JsonElement>();
            if (root.ValueKind == JsonValueKind.Array) items = root.EnumerateArray();
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                items = data.EnumerateArray();
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                items = models.EnumerateArray();

            foreach (var e in items)
            {
                var id = ReadModelId(e);
                if (id != null) result.Add(id);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Antes, un /models que devolvía una cadena o un número lanzaba una
            // InvalidOperationException en inglés desde JsonElement; una respuesta
            // con UTF-16 inválido lanzaba ArgumentException desde JsonDocument.
            throw new InvalidOperationException(
                "El proveedor no devolvió una lista de modelos válida: " + SseParser.DescribeError(body));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadModelId(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        if (e.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in new[] { "id", "name", "model" })
        {
            if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var id = v.GetString();
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
        }
        return null;
    }
}
