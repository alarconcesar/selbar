using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Selbar.Core.Actions;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;

namespace Selbar.Core.Ai;

/// <summary>
/// Pregunta a `gemini-decision-1` cuál de las acciones candidatas es la más útil
/// para el texto seleccionado. No genera texto: devuelve probabilidades.
///
/// Diseño defensivo (la selección puede ser cualquier cosa):
/// - nunca lanza hacia la UI; sin key, sin red o timeout devuelve <c>null</c>;
/// - no envía credenciales (<see cref="ActionRanker.LooksSecret"/>);
/// - recorta textos largos sin romper emojis;
/// - caché por texto+candidatas y cortacircuitos tras 3 fallos seguidos.
/// </summary>
public sealed class DecisionRouter : IDisposable
{
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/decisions";

    private const int CacheLimit = 64;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TripPause = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<AiConfig> _config;

    /// <summary>Configuración por defecto; App la enlaza con los ajustes vivos.</summary>
    public static Func<AiConfig>? DefaultConfig { get; set; }

    private readonly ConcurrentDictionary<string, (DecisionOutcome Outcome, DateTime At)> _cache = new();
    private int _failures;
    private DateTime _pausedUntil = DateTime.MinValue;

    public DecisionRouter(Func<AiConfig>? config = null, HttpClient? http = null)
    {
        _config = config ?? (() => DefaultConfig?.Invoke() ?? new AiConfig());
        if (http != null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(700),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    public bool IsConfigured
    {
        get
        {
            try
            {
                var cfg = _config();
                return cfg.DecisionEnabled && !string.IsNullOrWhiteSpace(KeyOf(cfg));
            }
            catch { return false; }
        }
    }

    private static string KeyOf(AiConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DecisionApiKeyEncrypted)) return "";
        try { return SettingsManager.Unprotect(cfg.DecisionApiKeyEncrypted); }
        catch { return ""; }
    }

    /// <summary>
    /// Decide entre las candidatas. Devuelve null si no aplica o si algo falla;
    /// el llamador se queda con el orden local.
    /// </summary>
    public async Task<DecisionOutcome?> DecideAsync(string? text, CancellationToken ct = default)
    {
        try
        {
            if (!IsConfigured) return null;
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length < 2) return null;
            if (ActionRanker.LooksSecret(trimmed))
            {
                Logger.Log("decisiones: selección con pinta de credencial, no se envía");
                return null;
            }
            if (DateTime.UtcNow < _pausedUntil) return null;

            var cfg = _config();
            string key = KeyOf(cfg);
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (!cfg.DecisionModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return null;

            string clipped = ActionRanker.Clip(trimmed, 8000);
            string cacheKey = CacheKey(clipped, cfg.DecisionModel);
            if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Outcome;

            string body = BuildRequestJson(clipped, cfg.DecisionModel);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Clamp(cfg.DecisionTimeoutMs, 400, 5000));

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 400 con key inválida no debe reintentarse en bucle; se cuenta como fallo.
                Logger.Warn($"decisiones: HTTP {(int)response.StatusCode}");
                Trip();
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var outcome = ParseResponse(json);
            if (outcome is null) { Trip(); return null; }

            _failures = 0;
            Remember(cacheKey, outcome);
            return outcome;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "decisiones: no se pudo decidir");
            Trip();
            return null;
        }
    }

    private void Trip()
    {
        if (Interlocked.Increment(ref _failures) >= 3)
        {
            _pausedUntil = DateTime.UtcNow + TripPause;
            _failures = 0;
            Logger.Warn("decisiones: 3 fallos seguidos, pausa de 5 minutos");
        }
    }

    private void Remember(string key, DecisionOutcome outcome)
    {
        if (_cache.Count >= CacheLimit)
        {
            // Fuera la entrada más vieja (barato: 64 entradas como mucho).
            var oldest = _cache.OrderBy(kv => kv.Value.At).FirstOrDefault();
            if (!oldest.Equals(default(KeyValuePair<string, (DecisionOutcome, DateTime)>)))
                _cache.TryRemove(oldest.Key, out _);
        }
        _cache[key] = (outcome, DateTime.UtcNow);
    }

    private static string CacheKey(string text, string model)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text + "\u0000" + model));
        return Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>
    /// Cuerpo de la petición. Puro y testeable (sin red).
    /// Se pregunta por INTENCIÓN, no por acciones: la pregunta no cambia aunque el
    /// catálogo crezca, y funciona con cualquier texto seleccionado.
    /// </summary>
    public static string BuildRequestJson(string text, string model)
    {
        var criteria = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in ActionIntents.Criteria)
            criteria[kv.Key] = ActionRanker.Describe(kv);

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "gemini-decision-1" : model,
            input = text,
            questions = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["intencion"] = new
                {
                    type = "choice",
                    instructions = "¿Qué quiere hacer una persona con este texto seleccionado? " +
                                   "Elige la intención más útil. Si el texto es largo y denso (más de 400 caracteres) " +
                                   "y no es una operación, un enlace ni una dirección, lo normal es resumir o responder. " +
                                   "Elige 'ninguna' solo si no hay nada que hacer con él.",
                    criteria
                },
                ["ninguna"] = new
                {
                    type = "noul",
                    instructions = "¿El texto seleccionado no necesita ninguna acción?",
                    criteria = new Dictionary<string, string>
                    {
                        ["true_description"] = "es un saludo, un dato ya completo o algo sin intención",
                        ["false_description"] = "hay algo que hacer con él"
                    }
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Interpreta la respuesta del modelo. Puro y testeable; null si no hay nada usable.</summary>
    public static DecisionOutcome? ParseResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(SseParser.SanitizeUtf16(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("answers", out var answers) ||
                answers.ValueKind != JsonValueKind.Object) return null;

            var probs = new Dictionary<string, double>(StringComparer.Ordinal);
            double noAction = 0;
            bool hasChoice = false;

            foreach (var answer in answers.EnumerateObject())
            {
                if (answer.Value.ValueKind != JsonValueKind.Object) continue;
                var a = answer.Value;
                string type = a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";

                // La primera pregunta de elección manda (no se depende del nombre).
                if (!hasChoice && (type == "choice" || a.TryGetProperty("probabilities", out _)))
                {
                    if (a.TryGetProperty("probabilities", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in p.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double v))
                                probs[prop.Name] = Math.Clamp(v, 0, 1);
                        hasChoice = true;
                    }
                    else if (a.TryGetProperty("choice", out var choice) && choice.ValueKind == JsonValueKind.String)
                    {
                        string id = choice.GetString() ?? "";
                        double c = 0;
                        if (a.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                            confEl.TryGetDouble(out c);
                        if (id.Length > 0) { probs[id] = Math.Clamp(c, 0, 1); hasChoice = true; }
                    }
                }

                if (type == "noul" && a.TryGetProperty("noul", out var noul) && noul.ValueKind == JsonValueKind.Number)
                {
                    noul.TryGetDouble(out double n);
                    if (n > noAction) noAction = n;
                }
            }

            if (probs.Count == 0) return null;
            // 'none' dentro de las opciones también cuenta como «ninguna».
            if (probs.Remove("none", out double noneProb) && noneProb > noAction) noAction = noneProb;

            return new DecisionOutcome { Probabilities = probs, NoAction = Math.Clamp(noAction, 0, 1) };
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
