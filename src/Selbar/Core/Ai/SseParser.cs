using System.Text;
using System.Text.Json;

namespace Selbar.Core.Ai;

public enum StreamChunkKind
{
    /// <summary>Fragmento de texto generado.</summary>
    Content,
    /// <summary>Fin del stream ([DONE]).</summary>
    Done,
    /// <summary>El proveedor envió un error dentro del stream.</summary>
    Error,
    /// <summary>Línea keep-alive o evento sin contenido.</summary>
    Ignore
}

/// <summary>Un trozo ya interpretado de la respuesta SSE.</summary>
public readonly record struct StreamChunk(StreamChunkKind Kind, string? Text)
{
    public static StreamChunk Content(string text) => new(StreamChunkKind.Content, text);
    public static StreamChunk Failure(string message) => new(StreamChunkKind.Error, message);
    public static readonly StreamChunk Done = new(StreamChunkKind.Done, null);
    public static readonly StreamChunk Ignore = new(StreamChunkKind.Ignore, null);
}

/// <summary>
/// Interpreta los eventos de un endpoint /chat/completions. Los proveedores
/// "OpenAI-compatibles" no lo son tanto: unos mandan delta.content, otros el
/// mensaje completo, otros un error a mitad de stream, otros ni siquiera SSE.
/// Esto lo normaliza todo y nunca lanza por una línea rara.
/// </summary>
public static class SseParser
{
    private const int MaxSnippet = 400;

    /// <summary>Interpreta el contenido de una línea <c>data:</c> (ya sin el prefijo).</summary>
    public static StreamChunk ParseData(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return StreamChunk.Ignore;
        var text = data.Trim();
        if (text == "[DONE]") return StreamChunk.Done;

        // Keep-alive de algunos servidores/proxies ("data: null").
        if (text == "null" || text == "undefined") return StreamChunk.Ignore;

        // Prefijo "data:" pegado: algún proxy entrega la línea entera (o duplica
        // el prefijo) y antes se mostraba el JSON crudo como si fuera texto.
        // Solo se quita si lo que queda es un evento de verdad.
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var inner = text.Length > 5 ? text[5..].Trim() : "";
            if (inner == "[DONE]") return StreamChunk.Done;
            if (inner.Length > 0 && (inner[0] == '{' || inner[0] == '[' || inner[0] == '"'))
            {
                var prefixed = TryParseJson(inner);
                if (prefixed.Kind != StreamChunkKind.Ignore) return prefixed;
            }
        }

        // HTML (portal cautivo, proxy con error): mejor un mensaje claro que basura.
        if (text.StartsWith('<'))
            return StreamChunk.Failure("El servidor devolvió HTML en vez de JSON (¿proxy o portal cautivo?): " + Snippet(text));

        // Comentario/keep-alive de SSE.
        if (text.StartsWith(':')) return StreamChunk.Ignore;

        // El evento trae sus propias líneas "data:" (alguien pegó el bloque SSE
        // completo): se interpreta línea a línea.
        if (text.Contains('\n') && StartsWithSseField(text)) return ParseMultiLine(text);

        char first = text[0];
        if (first != '{' && first != '[' && first != '"')
            return StreamChunk.Content(text); // stream de texto plano

        // Varios eventos JSON pegados sin la línea en blanco de separación (o un
        // JSON partido por un proxy): se resuelve línea a línea.
        if (text.Contains('\n')) return ParseMultiLine(text);

        var chunk = TryParseJson(text);
        if (chunk.Kind != StreamChunkKind.Ignore) return chunk;

        // JSON inválido: antes se mostraba tal cual (basura para el usuario).
        // Un evento roto se ignora y el stream sigue.
        return StreamChunk.Ignore;
    }

    /// <summary>
    /// Un evento con varias líneas: puede ser un único JSON partido por un proxy,
    /// o varios eventos completos sin línea en blanco entre ellos.
    /// </summary>
    private static StreamChunk ParseMultiLine(string text)
    {
        var whole = TryParseJson(text);
        if (whole.Kind != StreamChunkKind.Ignore) return whole;

        // Un salto de línea literal nunca es válido dentro de un JSON: reintentar sin ellos.
        var compact = TryParseJson(text.Replace("\n", ""));
        if (compact.Kind != StreamChunkKind.Ignore) return compact;

        var sb = new StringBuilder();
        bool sawContent = false, sawDone = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line.Length > 6 && line[5] == ' ' ? line[6..].Trim() : line[5..].Trim();
            if (line.Length == 0) continue;
            // Metadatos SSE: no son contenido.
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(':')) continue;
            if (line == "[DONE]") { sawDone = true; continue; }

            var piece = TryParseJson(line);
            switch (piece.Kind)
            {
                case StreamChunkKind.Content:
                    if (!string.IsNullOrEmpty(piece.Text)) { sb.Append(piece.Text); sawContent = true; }
                    break;
                case StreamChunkKind.Done:
                    sawDone = true;
                    break;
                case StreamChunkKind.Error:
                    // Si ya había texto, se devuelve el texto; el error llegará en el
                    // siguiente evento (no se pierde lo generado).
                    if (!sawContent) return piece;
                    break;
                default:
                    // Línea de texto plano dentro de un evento multi-línea.
                    if (line[0] != '{' && line[0] != '[' && line[0] != '"')
                    {
                        sb.Append(sb.Length > 0 ? "\n" + line : line);
                        sawContent = true;
                    }
                    break;
            }
        }

        if (sawContent) return StreamChunk.Content(sb.ToString());
        return sawDone ? StreamChunk.Done : StreamChunk.Ignore;
    }

    private static StreamChunk TryParseJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(SanitizeUtf16(text));
            return FromDocument(doc);
        }
        catch (JsonException)
        {
            return StreamChunk.Ignore;
        }
        catch (ArgumentException)
        {
            // JsonDocument.Parse lanza ArgumentException (no JsonException) si el
            // texto no es UTF-16 válido; ya se sanea arriba, pero nunca debe
            // escapar una excepción por una respuesta rara.
            return StreamChunk.Ignore;
        }
    }

    /// <summary>
    /// Sustituye los surrogates sin pareja por U+FFFD. JsonDocument.Parse lanza
    /// <c>ArgumentException: Cannot transcode invalid UTF-16 string to UTF-8 JSON
    /// text</c> con UTF-16 inválido, y un corte de red a mitad de emoji (o un
    /// escape \uD800 del proveedor) abortaba la respuesta entera.
    /// No se toca el texto de los chunks: un par partido entre dos eventos se
    /// recompone al concatenarlos, y sustituirlo aquí metería un U+FFFD en medio.
    /// </summary>
    public static string SanitizeUtf16(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        bool hasSurrogate = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i])) { hasSurrogate = true; break; }
        }
        if (!hasSurrogate) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>¿La primera línea con contenido es un campo SSE?</summary>
    private static bool StartsWithSseField(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(':'); // comentario/keep-alive
        }
        return false;
    }

    /// <summary>Extrae el contenido de un JSON ya parseado (streaming).</summary>
    private static StreamChunk FromDocument(JsonDocument doc) => FromRoot(doc.RootElement);

    private static StreamChunk FromRoot(JsonElement root)
    {
        // Un array en la raíz: algunos gateways envuelven el evento.
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var chunk = FromObject(item);
                if (chunk.Kind != StreamChunkKind.Ignore) return chunk;
            }
            return StreamChunk.Ignore;
        }

        // Un JSON que solo es una cadena: el texto va entre comillas.
        if (root.ValueKind == JsonValueKind.String)
        {
            var s = root.GetString();
            return string.IsNullOrEmpty(s) ? StreamChunk.Ignore : StreamChunk.Content(s);
        }

        return root.ValueKind == JsonValueKind.Object ? FromObject(root) : StreamChunk.Ignore;
    }

    private static StreamChunk FromObject(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                return StreamChunk.Failure(ErrorText(error) ?? "error del proveedor");

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.ValueKind == JsonValueKind.Object)
                {
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        var content = GetContentText(delta, "content");
                        if (!string.IsNullOrEmpty(content)) return StreamChunk.Content(content);
                        // El pensamiento (reasoning_content) NUNCA se muestra: es interno
                        // del modelo. Se ignora y se espera el contenido real.
                    }
                    if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                    {
                        var content = GetContentText(message, "content");
                        if (!string.IsNullOrEmpty(content)) return StreamChunk.Content(content);
                    }
                    if (choice.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String)
                    {
                        var content = legacy.GetString();
                        if (!string.IsNullOrEmpty(content)) return StreamChunk.Content(content);
                    }
                }
            }

            // Variantes tipo Ollama: {"message":{"content":"..."}} / {"response":"..."}
            if (root.TryGetProperty("message", out var msg2) && msg2.ValueKind == JsonValueKind.Object)
            {
                var content = TextOf(msg2, "content");
                if (!string.IsNullOrEmpty(content)) return StreamChunk.Content(content);
            }
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String)
            {
                var content = response.GetString();
                if (!string.IsNullOrEmpty(content)) return StreamChunk.Content(content);
            }

            return StreamChunk.Ignore;
        }
        catch (InvalidOperationException)
        {
            // JsonElement lanza si se pide una propiedad sobre algo que no es objeto;
            // aquí nunca debe escapar una excepción por una respuesta rara.
            return StreamChunk.Ignore;
        }
    }

    /// <summary>
    /// Saca el texto completo de una respuesta NO-streaming
    /// ({"choices":[{"message":{"content":"..."}}]}, {"content":...}, ...).
    /// Devuelve null si no encuentra contenido (incluido content:"" vacío).
    /// El pensamiento (reasoning_content) nunca cuenta como respuesta.
    /// </summary>
    public static string? ExtractFullContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(SanitizeUtf16(body));
            return ContentOf(doc.RootElement)?.Trim();
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        return null;
    }

    private static string? ContentOf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            // Respuesta envuelta en array (o un array de eventos).
            foreach (var item in root.EnumerateArray())
            {
                var content = ContentOf(item);
                if (!string.IsNullOrWhiteSpace(content)) return content;
            }
            return null;
        }

        if (root.ValueKind == JsonValueKind.String) return root.GetString();
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) continue;

                foreach (var key in new[] { "message", "delta" })
                {
                    if (!choice.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object) continue;
                    var content = GetContentText(node, "content");
                    if (!string.IsNullOrWhiteSpace(content)) return content;
                }

                if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    var content = text.GetString();
                    if (!string.IsNullOrWhiteSpace(content)) return content;
                }
                // Sin contenido real no hay respuesta (el pensamiento interno
                // del modelo nunca se muestra).
            }
        }

        // API /responses de OpenAI: {"output":[{"content":[{"type":"output_text","text":"..."}]}]}
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("content", out var blocks)) continue;
                var content = GetContentText(blocks);
                if (!string.IsNullOrWhiteSpace(content)) return content;
            }
        }

        foreach (var key in new[] { "content", "response", "output_text", "text" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;
            var content = GetContentText(value);
            if (!string.IsNullOrWhiteSpace(content)) return content;
        }

        return null;
    }

    /// <summary>
    /// Plan B para servidores que ignoran stream:false y devuelven SSE de todos
    /// modos: concatena los data: con contenido. Null si no hay nada útil.
    /// </summary>
    public static string? TryExtractSseContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var sb = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line.Length > 6 && line[5] == ' ' ? line[6..].Trim() : line[5..].Trim();
            var chunk = ParseData(payload);
            if (chunk.Kind == StreamChunkKind.Content && !string.IsNullOrEmpty(chunk.Text))
                sb.Append(chunk.Text);
            else if (chunk.Kind == StreamChunkKind.Error)
                return null; // el stream trae un error: que lo describa la vía normal
        }
        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    /// <summary>Mensaje de error legible a partir del cuerpo de una respuesta HTTP fallida.</summary>
    public static string DescribeError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "sin detalle";
        // Un surrogate suelto (respuesta cortada a mitad de emoji) rompía
        // JsonDocument.Parse con ArgumentException y con él el mensaje de error.
        var text = SanitizeUtf16(body).Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // {"error": ...} en cualquiera de sus formas (objeto, cadena, array, anidado).
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                var message = ErrorText(error);
                if (!string.IsNullOrWhiteSpace(message)) return Snippet(message);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "message", "detail", "error_description", "title", "msg" })
                {
                    var message = TextOf(root, key);
                    if (!string.IsNullOrWhiteSpace(message)) return Snippet(message);
                }
                // Errores estilo FastAPI: {"detail":[{"msg":"...","type":"..."}]}
                // o {"detail":{"msg":"..."}}.
                if (root.TryGetProperty("detail", out var detail))
                {
                    var message = ErrorText(detail);
                    if (!string.IsNullOrWhiteSpace(message)) return Snippet(message);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var message = ErrorText(item);
                    if (!string.IsNullOrWhiteSpace(message)) return Snippet(message);
                }
            }
            else if (root.ValueKind == JsonValueKind.String)
            {
                var message = root.GetString();
                if (!string.IsNullOrWhiteSpace(message)) return Snippet(message);
            }
        }
        catch (JsonException) { }

        if (text.StartsWith('<'))
        {
            // Fuera etiquetas para que no salga un muro de HTML.
            var plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
            plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
            return Snippet(plain);
        }
        return Snippet(text);
    }

    /// <summary>
    /// Mensaje para una respuesta HTTP correcta que no traía texto: el modelo
    /// puede haber devuelto solo razonamiento, o el servidor una estructura rara.
    /// Antes se volcaba el JSON crudo al usuario ("Respuesta inesperada: {...}").
    /// </summary>
    public static string ExplainNoContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "El proveedor devolvió una respuesta vacía.";

        try
        {
            using var doc = JsonDocument.Parse(SanitizeUtf16(body));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                return "El modelo no devolvió texto (¿respuesta filtrada o solo razonamiento interno?).";
            }
        }
        catch (JsonException) { }
        catch (ArgumentException) { }

        return "Respuesta inesperada del proveedor: " + DescribeError(body);
    }

    /// <summary>¿La primera línea no vacía parece un evento SSE?</summary>
    public static bool LooksLikeSse(string? line)
    {
        if (line == null) return false;
        var t = line.TrimStart();
        // Un evento puede empezar por "event:", "id:" o un comentario antes del data:.
        return t.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("retry:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(':');
    }

    private static string? TextOf(JsonElement element, string? property = null)
    {
        if (property != null)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object => FirstString(element, "message", "msg", "detail", "code", "type"),
            _ => null
        };
    }

    /// <summary>
    /// Texto del error venga como venga: cadena, objeto con message/msg/detail,
    /// objeto anidado ({"error":{"error":{...}}}) o array de errores.
    /// </summary>
    private static string? ErrorText(JsonElement element, int depth = 0)
    {
        if (depth > 3) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Object:
                var direct = FirstString(element, "message", "msg", "detail", "error_description", "code", "type");
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
                if (element.TryGetProperty("error", out var nested))
                {
                    var message = ErrorText(nested, depth + 1);
                    if (!string.IsNullOrWhiteSpace(message)) return message;
                }
                if (element.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        var message = ErrorText(item, depth + 1);
                        if (!string.IsNullOrWhiteSpace(message)) return message;
                    }
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var message = ErrorText(item, depth + 1);
                    if (!string.IsNullOrWhiteSpace(message)) return message;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Texto de un campo de contenido en cualquiera de sus formas: string,
    /// null, array de bloques (Gemini/Anthropic-compat: [{"type":"text",
    /// "text":"..."}]) u objeto con text. Null si no hay texto.
    /// </summary>
    private static string? GetContentText(JsonElement element, string? property = null)
    {
        if (property != null)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
                return null;
            return GetContentText(value);
        }

        return GetContentText(element);
    }

    private static string? GetContentText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => JoinBlocks(value),
        JsonValueKind.Object => value.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null,
        _ => null
    };

    private static string? JoinBlocks(JsonElement array)
    {
        StringBuilder? sb = null;
        foreach (var item in array.EnumerateArray())
        {
            string? part = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when !IsReasoningBlock(item) =>
                    item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null,
                _ => null
            };
            if (!string.IsNullOrEmpty(part)) (sb ??= new StringBuilder()).Append(part);
        }
        return sb?.ToString();
    }

    /// <summary>
    /// ¿Es un bloque de razonamiento? El pensamiento del modelo nunca se muestra
    /// como respuesta, ni siquiera cuando viaja como bloque de contenido.
    /// </summary>
    private static bool IsReasoningBlock(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        var value = type.GetString() ?? "";
        return value.Contains("think", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reason", StringComparison.OrdinalIgnoreCase)
            || value.Equals("analysis", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static string Snippet(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "sin detalle";
        s = s.Trim();
        return Truncate(s, MaxSnippet);
    }

    /// <summary>Corta sin partir un par suplente: medio emoji deja UTF-16 inválido.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = max;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }
}
