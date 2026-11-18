using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Selbar.Core.Ai;

/// <summary>
/// Transcripción de audio con Whisper en Groq (API compatible con OpenAI).
/// Requiere una API key de Groq guardada por el usuario; sin red no se usa.
/// </summary>
public static class GroqWhisper
{
    public const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    public const string DefaultModel = "whisper-large-v3-turbo";
    public const long MaxBytes = 25L * 1024 * 1024;

    private static readonly string[] Extensions =
    {
        ".mp3", ".mp4", ".mpeg", ".mpga", ".m4a", ".wav", ".webm", ".ogg", ".flac", ".aac"
    };

    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string extension = Path.GetExtension(path);
        return Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Valida el archivo antes de llamar a la API. Devuelve null si es correcto.</summary>
    public static string? ValidateFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Elige un archivo de audio.";
        if (!File.Exists(path)) return "El archivo ya no existe.";
        if (!IsSupportedFile(path)) return "Formato no soportado. Usa mp3, m4a, wav, ogg, webm, flac, mp4, mpeg o aac.";
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception ex) { return "No se pudo leer el archivo: " + ex.Message; }
        if (length == 0) return "El archivo está vacío.";
        if (length > MaxBytes) return "El archivo supera los 25 MB que acepta Groq. Divídelo o comprímelo.";
        // Un .mp3 que en realidad es una página de error (descarga caducada,
        // portal cautivo) no se sube: Groq respondería 400 sin explicar nada.
        if (LooksLikeText(path)) return "El archivo no parece audio (¿una página web guardada con extensión de audio?).";
        return null;
    }

    /// <summary>
    /// ¿El contenido es texto/HTML/JSON? Ningún formato de audio empieza por
    /// '&lt;', '{' o '['. Solo se mira la cabecera, así que es barato.
    /// </summary>
    private static bool LooksLikeText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[64];
            int read = stream.Read(head, 0, head.Length);
            for (int i = 0; i < read; i++)
            {
                byte b = head[i];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) continue;
                return b is (byte)'<' or (byte)'{' or (byte)'[';
            }
            return false;
        }
        catch
        {
            return false; // si no se puede leer, que decida la API
        }
    }

    public static async Task<string> TranscribeAsync(string path, string apiKey, string? model,
        CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("SELBAR_OFFLINE") == "1")
            throw new InvalidOperationException("Modo offline: la transcripción necesita conexión.");

        string? validation = ValidateFile(path);
        if (validation != null) throw new InvalidOperationException(validation);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Falta la API key de Groq. Configúrala en Ajustes → Inteligencia artificial.");

        var client = Shared; // compartido y con timeout: no destruir con using
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(path));
        form.Add(file, "file", Path.GetFileName(path));
        form.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim()), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeError((int)response.StatusCode, body));

        try
        {
            using var document = JsonDocument.Parse(body.TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                string value = (text.GetString() ?? "").Trim();
                if (value.Length == 0) throw new InvalidOperationException(
                    "Groq devolvió una transcripción vacía (¿audio sin voz?).");
                return value;
            }

            throw new InvalidOperationException("Groq no devolvió texto en la respuesta.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("La respuesta de Groq no es válida.");
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" or ".mpga" or ".mpeg" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        _ => "application/octet-stream"
    };

    private static string DescribeError(int status, string body)
    {
        string detail = "";
        try
        {
            using var document = JsonDocument.Parse((body ?? "").Trim().TrimStart('\uFEFF'));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                detail = message.GetString() ?? "";
        }
        catch { }

        string suffix = string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail;
        return status switch
        {
            400 => "Groq rechazó el audio (400). Revisa el formato y el modelo." + suffix,
            401 or 403 => "Groq rechazó la API key (401/403). Revísala en Ajustes → IA." + suffix,
            413 => "El audio es demasiado grande para Groq (máximo 25 MB)." + suffix,
            429 => "Groq está limitando las peticiones. Espera un momento." + suffix,
            _ => $"Groq devolvió HTTP {status}.{suffix}"
        };
    }
}
