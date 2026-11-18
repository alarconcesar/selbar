using System.Net;

namespace Selbar.Core.Ai;

public interface IAiProvider
{
    string Name { get; }
    Task<string> CompleteAsync(string systemPrompt, string userText, CancellationToken ct = default);
    /// <summary>Igual pero por streaming: invoca onToken por cada fragmento y devuelve el texto completo.</summary>
    Task<string> CompleteStreamingAsync(string systemPrompt, string userText, Action<string> onToken, CancellationToken ct = default);
    /// <summary>Streaming con historial multi-turno.</summary>
    Task<string> CompleteStreamingHistoryAsync(IReadOnlyList<ChatMessage> messages, Action<string> onToken, CancellationToken ct = default);
}

/// <summary>Mensaje de chat (system/user/assistant).</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>Presets de proveedores compatibles con la API de OpenAI.</summary>
public static class Providers
{
    public const string OpenCodeGoBaseUrl = "https://opencode.ai/zen/go/v1";

    /// <summary>Lista de proveedores que se ofrece en Ajustes (el orden es el del combo).</summary>
    public static readonly string[] All =
    {
        "OpenCode GO", "OpenAI", "OpenRouter", "DeepSeek", "Groq", "Mistral", "xAI", "Gemini",
        "Ollama", "LMStudio", "Personalizado"
    };

    public static string BaseUrlFor(string provider) => provider switch
    {
        "OpenRouter" => "https://openrouter.ai/api/v1",
        "DeepSeek" => "https://api.deepseek.com/v1",
        "Groq" => "https://api.groq.com/openai/v1",
        "Mistral" => "https://api.mistral.ai/v1",
        "xAI" => "https://api.x.ai/v1",
        "Gemini" => "https://generativelanguage.googleapis.com/v1beta/openai",
        "Ollama" => "http://localhost:11434/v1",
        "LMStudio" => "http://localhost:1234/v1",
        "OpenAI" => "https://api.openai.com/v1",
        "OpenCode GO" => OpenCodeGoBaseUrl,
        _ => "" // Personalizado: el usuario pone su URL; vacío = sin configurar
    };

    public static string DefaultModelFor(string provider) => provider switch
    {
        "OpenRouter" => "openrouter/free",
        "DeepSeek" => "deepseek-chat",
        "Groq" => "llama-3.3-70b-versatile",
        "Mistral" => "mistral-small-latest",
        "xAI" => "grok-2-latest",
        "Gemini" => "gemini-2.0-flash",
        "Ollama" => "llama3.1",
        "LMStudio" => "local-model",
        "OpenAI" => "gpt-4o-mini",
        "OpenCode GO" => "deepseek-v4.1-flash", // ver lista en https://opencode.ai/zen/go/v1/models
        _ => "deepseek-v4.1-flash",
    };

    /// <summary>¿Es un servidor local (no necesita API key)?</summary>
    public static bool IsLocal(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        // Se compara el HOST, no la cadena entera: antes "https://mylocalhost.example"
        // contaba como local y la app no pedía API key.
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;

        string host = uri.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (host == "0.0.0.0") return true;
        // Cubre 127.x, ::1 y las formas largas de IPv6 loopback.
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
