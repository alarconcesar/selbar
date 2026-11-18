using System.Net;
using System.Net.Http;
using System.Text;

namespace Selbar.Core.Web;

/// <summary>
/// Utilidades de enlaces: limpiar parámetros de seguimiento, detectar acortadores
/// y resolver la URL final. Todo local salvo ExpandAsync (necesita red).
/// </summary>
public static class LinkTools
{
    /// <summary>Saltos máximos al expandir un enlace (más es sospechoso).</summary>
    private const int MaxHops = 8;

    /// <summary>Parámetros que solo sirven para medir clics.</summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name",
        "utm_reader", "utm_referrer", "utm_social", "utm_social-type", "utm_brand",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "twclid", "ttclid",
        "mc_cid", "mc_eid", "mkt_tok", "igshid", "igsh", "si", "_ga", "_gl", "ref_src", "ref_url",
        "vero_id", "oly_enc_id", "oly_anon_id", "s_kwcid", "trk", "trkCampaign", "sc_campaign",
        "sc_channel", "sc_content", "sc_medium", "sc_outcome", "sc_geo", "sc_country",
        "pk_campaign", "pk_kwd", "piwik_campaign", "matomo_campaign", "hsa_cam", "hsa_grp"
    };

    /// <summary>Acortadores habituales (para ofrecer "expandir" solo cuando toca).</summary>
    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "bitly.com", "t.co", "goo.gl", "tinyurl.com", "ow.ly", "is.gd", "buff.ly",
        "cutt.ly", "shorturl.at", "rebrand.ly", "rb.gy", "t.ly", "lnkd.in", "amzn.to", "youtu.be",
        "fb.me", "wp.me", "trib.al", "shorte.st", "adf.ly", "bl.ink", "s.id", "urlz.fr", "v.gd",
        "tiny.cc", "x.gd", "short.io", "linktr.ee", "beacons.ai"
    };

    public static bool IsUrl(string? text)
    {
        string t = (text ?? "").Trim();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string url)
    {
        string t = url.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        return t;
    }

    private static bool TryHost(string url, out string host)
    {
        host = "";
        if (!Uri.TryCreate(Normalize(url), UriKind.Absolute, out var uri)) return false;
        host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return uri.Scheme is "http" or "https";
    }

    public static bool IsShortener(string? url)
        => TryHost(url ?? "", out string host) && Shorteners.Contains(host);

    /// <summary>¿Tiene parámetros de seguimiento? (para mostrar el chip solo entonces)</summary>
    public static bool HasTracking(string? url)
    {
        if (!Uri.TryCreate(Normalize(url ?? ""), UriKind.Absolute, out var uri)) return false;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = pair.Split('=')[0];
            if (TrackingParams.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Quita los parámetros de seguimiento y deja el resto (incluido el fragmento).
    /// Devuelve la URL limpia; si no hay nada que quitar, la original.
    /// </summary>
    public static string CleanTracking(string? url)
    {
        string original = (url ?? "").Trim();
        if (!Uri.TryCreate(Normalize(original), UriKind.Absolute, out var uri)) return original;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return original;

        var keep = new List<string>();
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = pair.Split('=')[0];
            if (!TrackingParams.Contains(name)) keep.Add(pair);
        }

        var builder = new StringBuilder()
            .Append(uri.Scheme).Append("://").Append(uri.Authority).Append(uri.AbsolutePath);

        if (keep.Count > 0) builder.Append('?').Append(string.Join("&", keep));
        if (!string.IsNullOrEmpty(uri.Fragment)) builder.Append(uri.Fragment);

        return builder.ToString();
    }

    /// <summary>
    /// Sigue las redirecciones (máx. 8) y devuelve la URL final y cuántos saltos dio.
    /// Lanza si no hay red o el enlace no responde.
    /// </summary>
    public static async Task<(string Url, int Hops)> ExpandAsync(string? url, CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("SELBAR_OFFLINE") == "1")
            throw new InvalidOperationException("Modo offline: expandir un enlace necesita conexión.");

        string current = Normalize(url ?? "");
        if (!Uri.TryCreate(current, UriKind.Absolute, out var first)
            || (first.Scheme != Uri.UriSchemeHttp && first.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("La selección no es un enlace válido.");

        for (int hop = 1; hop <= MaxHops; hop++)
        {
            // Cada salto se valida igual que el enlace inicial: un acortador (o un
            // portal cautivo) no puede redirigir a file:, javascript: ni a tu propio
            // equipo para que la app pida recursos internos.
            if (!WebFetchGuard.TryNormalizeHttpUrl(current, out string safe, out string? invalid))
                throw new InvalidOperationException(invalid ?? "La redirección del enlace no es válida.");
            current = safe;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");

            HttpResponseMessage response;
            try
            {
                response = await Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("El enlace tardó demasiado en responder.");
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("No se pudo conectar con el enlace: " + ex.Message, ex);
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                // Solo se sigue Location en una redirección real: un 200 con
                // cabecera Location (descargas, APIs) devolvía como URL final una
                // dirección que no era el destino del enlace.
                bool redirect = status is 301 or 302 or 303 or 307 or 308;
                string? location = redirect ? response.Headers.Location?.ToString() : null;
                if (string.IsNullOrWhiteSpace(location))
                {
                    // Un 4xx/5xx sin redirección no es un destino: es un enlace roto.
                    if (status >= 400)
                        throw new InvalidOperationException(
                            $"El enlace respondió HTTP {status}. Puede que el acortador haya caducado.");
                    return (current, hop - 1);
                }

                if (!Uri.TryCreate(new Uri(current), location, out var next))
                    throw new InvalidOperationException("La redirección del enlace no es válida.");
                // file:, javascript:, data: y demás esquemas no se siguen.
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("La redirección del enlace apunta fuera de la web; no se sigue.");
                current = next.ToString();
            }
        }

        throw new InvalidOperationException("El enlace da demasiados saltos; puede ser sospechoso.");
    }

    /// <summary>Quita el @ inicial de un usuario para armar la URL del perfil.</summary>
    public static string Handle(string? text)
    {
        string t = (text ?? "").Trim().TrimStart('@');
        return WebUtility.UrlEncode(t);
    }

    private static readonly HttpClient Shared = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        // Sin timeout global: cada salto lleva su propio tope (ver ExpandAsync).
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Selbar/1.0");
        return client;
    }
}
