using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Selbar.Core.Web;

/// <summary>
/// Validaciones y topes para la lectura de enlaces (HTML/PDF/descargas).
/// Puro (sin tipos de Windows): compila en el probe de Linux.
/// </summary>
public static class WebFetchGuard
{
    /// <summary>Tamaño máximo de un HTML antes de renunciar (el resumen usa 24 KB).</summary>
    public const long MaxHtmlBytes = 5L * 1024 * 1024;

    /// <summary>Tamaño máximo de un PDF o archivo descargado (tope anti ZIP-bomb).</summary>
    public const long MaxDownloadBytes = 30L * 1024 * 1024;

    /// <summary>
    /// Normaliza y valida una URL introducida por el usuario. Solo http/https:
    /// file://, ftp:, javascript:, data: y demás esquemas se rechazan con un
    /// mensaje claro en vez de dejar que HttpClient lance un error críptico
    /// (o que se intente algo raro con un esquema no web).
    /// </summary>
    public static bool TryNormalizeHttpUrl(string? url, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        string t = (url ?? "").Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        if (t.Length == 0)
        {
            error = "La selección no es un enlace válido.";
            return false;
        }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri))
        {
            error = "La selección no es un enlace válido.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Solo se pueden leer enlaces web (http/https).";
            return false;
        }
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "La selección no es un enlace válido.";
            return false;
        }
        // SSRF: el texto seleccionado es entrada no confiable. Un enlace a
        // 127.0.0.1, a la puerta de enlace del router o a 169.254.169.254 haría
        // que la app pidiera recursos internos y los mandara al modelo.
        if (IsBlockedHost(uri.Host))
        {
            error = "Ese enlace apunta a este equipo o a una dirección local; no se lee por seguridad.";
            return false;
        }
        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// ¿Host local/interno que no debe pedirse? Cubre nombres (localhost,
    /// metadata de nube) e IP literales de loopback, link-local y 0.0.0.0/8.
    /// No bloquea rangos privados (192.168.x, 10.x): leer una página de la LAN es
    /// un uso legítimo y el usuario escribió ese enlace. Las redirecciones se
    /// validan salto a salto con este mismo método.
    /// </summary>
    public static bool IsBlockedHost(string? host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return true;

        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (h is "metadata.google.internal" or "metadata.goog" or "instance-data") return true;

        // IPv6 literal entre corchetes ([::1]).
        if (h.Length > 1 && h[0] == '[' && h[^1] == ']') h = h[1..^1];
        return IPAddress.TryParse(h, out var ip) && IsBlockedAddress(ip);
    }

    /// <summary>¿Dirección IP de loopback, link-local o no especificada?</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 0) return true;                          // 0.0.0.0/8
            if (bytes[0] == 169 && bytes[1] == 254) return true;     // link-local + metadata
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;
            return false;
        }

        if (address.Equals(IPAddress.IPv6Any)) return true;
        if (bytes.Length == 16 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true; // fe80::/10
        return false;
    }

    /// <summary>
    /// Copia un stream a otro sin pasarse de maxBytes. Lanza
    /// InvalidOperationException con el tamaño en MB si se supera (el llamador
    /// debe borrar el temporal parcial).
    /// </summary>
    public static async Task CopyWithLimitAsync(Stream source, Stream target, long maxBytes,
        string what, CancellationToken ct = default)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    $"{what} supera el máximo de {FormatSize(maxBytes)}; no se descarga.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ¿La URL pertenece a Google/Lens? Se usa para validar la redirección
    /// tras subir una imagen: un portal cautivo podría redirigir a otro sitio
    /// y esa URL se abre en el navegador. Conservador a propósito: ante la
    /// duda se rechaza (p. ej. google.com.mx) con un error claro.
    /// </summary>
    public static bool IsTrustedGoogleResult(Uri? location)
    {
        if (location == null || location.Scheme != Uri.UriSchemeHttps) return false;
        string host = location.Host.ToLowerInvariant();
        if (host == "lens.google.com" || host == "google.com" || host.EndsWith(".google.com"))
            return true;
        // Dominios de país tipo www.google.es / google.es (el TLD final es corto).
        // google.evil.com NO cuela: su última etiqueta ("com") no es el TLD de google.
        var parts = host.Split('.');
        return parts.Length >= 2 && parts[^1].Length == 2 && parts[^2] == "google";
    }

    /// <summary>Nombre de archivo saneado para temporales (sin caracteres inválidos ni "..").</summary>
    public static string SafeFileName(string? name, string fallback = "descarga")
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0 || n is "." or "..") return fallback;
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim();
        // Windows no admite nombres que terminen en punto o espacio: File.Create
        // fallaría con un error críptico.
        n = n.TrimEnd('.', ' ');
        if (n.Length == 0 || n is "." or "..") return fallback;
        // Aunque vaya tras un GUID, evita segmentos "..x" por si se reutiliza sin prefijo.
        if (n.StartsWith('.')) n = "_" + n;
        // Nombres de dispositivo reservados (CON, NUL, COM1…): tampoco valen.
        if (IsReservedDeviceName(n)) n = "_" + n;
        const int max = 80;
        return Shorten(n, max);
    }

    /// <summary>
    /// Recorta a <paramref name="max"/> conservando la extensión y sin partir un
    /// par suplente. Antes un nombre de 82 caracteres acababa en ".p" (la
    /// extensión se perdía) y un emoji justo en el corte dejaba UTF-16 inválido.
    /// </summary>
    private static string Shorten(string name, int max)
    {
        if (name.Length <= max) return name;

        string extension = Path.GetExtension(name);
        if (extension.Length > 10 || extension.Length == name.Length) extension = "";

        int keep = max - extension.Length;
        if (keep < 1)
        {
            extension = "";
            keep = max;
        }

        int cut = keep;
        if (cut > 0 && char.IsHighSurrogate(name[cut - 1])) cut--;
        string stem = name[..cut].TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "archivo";
        return stem + extension;
    }

    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        string stem = (dot > 0 ? name[..dot] : name).TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;
        if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")))
            return char.IsDigit(stem[3]) && stem[3] != '0';
        return false;
    }

    /// <summary>¿El archivo empieza por la firma %PDF-? (un .pdf puede ser una página de error).</summary>
    public static bool IsPdfFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[5];
            int read = stream.Read(head, 0, head.Length);
            return read == 5 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F' && head[4] == '-';
        }
        catch
        {
            return false;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024 / 1024} MB"
        : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} bytes";
}
