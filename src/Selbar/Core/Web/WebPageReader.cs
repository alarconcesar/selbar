using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Selbar.Core.Diagnostics;
using Selbar.Core.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Selbar.Core.Web;

/// <summary>
/// Lee el contenido de un enlace para poder resumirlo: HTML (texto plano) o PDF
/// (descarga + OCR local de las primeras páginas). Sin dependencias nuevas.
/// </summary>
public static class WebPageReader
{
    public const int MaxChars = 24000;
    private const int MaxPdfPages = 3;

    /// <summary>Saltos de redirección que se siguen (validando cada uno).</summary>
    private const int MaxRedirects = 5;

    /// <summary>Tope de red para la petición y la descarga (el OCR va aparte).</summary>
    private const int NetworkTimeoutSeconds = 25;

    public sealed record Page(string Title, string Text, string Url, bool IsPdf, string? LocalPath);

    public static bool LooksLikePdf(string? url)
    {
        string t = (url ?? "").Trim();
        if (t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Descarga el enlace a un archivo temporal y devuelve la ruta.</summary>
    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        EnsureOnline();
        if (!WebFetchGuard.TryNormalizeHttpUrl(url, out string normalized, out string? error))
            throw new InvalidOperationException(error);

        using var timeout = CreateTimeout(ct);
        using var response = await GetFollowingAsync(normalized, timeout.Token, ct).ConfigureAwait(false);
        EnsureSuccess(response);

        string finalUrl = FinalUrl(response, normalized);
        string name = WebFetchGuard.SafeFileName(FileNameFrom(finalUrl));
        string path = Path.Combine(Path.GetTempPath(), $"powertools-{Guid.NewGuid():N}-{name}");

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = File.Create(path);
            await WebFetchGuard.CopyWithLimitAsync(source, target, WebFetchGuard.MaxDownloadBytes, "La descarga", timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            Delete(path);
            throw;
        }
        return path;
    }

    /// <summary>Trae título y texto del enlace (HTML) o descarga + OCR si es PDF.</summary>
    public static async Task<Page> ReadAsync(string url, CancellationToken ct = default)
    {
        EnsureOnline();
        if (!WebFetchGuard.TryNormalizeHttpUrl(url, out string normalized, out string? error))
            throw new InvalidOperationException(error);

        string finalUrl;
        string mediaType;
        string? charset;
        byte[]? htmlBytes = null;
        string? pdfPath = null;
        bool pdf;

        // El tope de red cubre cabeceras + cuerpo. El OCR de las páginas va después
        // con su propio token: antes se reutilizaba este y un PDF de 3 páginas se
        // cancelaba a mitad de OCR.
        using (var timeout = CreateTimeout(ct))
        {
            using var response = await GetFollowingAsync(normalized, timeout.Token, ct).ConfigureAwait(false);
            EnsureSuccess(response);

            finalUrl = FinalUrl(response, normalized);
            mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            charset = response.Content.Headers.ContentType?.CharSet;
            pdf = LooksLikePdf(finalUrl) || mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

            if (pdf)
            {
                pdfPath = Path.Combine(Path.GetTempPath(), $"powertools-pdf-{Guid.NewGuid():N}.pdf");
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await using var file = File.Create(pdfPath);
                    await WebFetchGuard.CopyWithLimitAsync(stream, file, WebFetchGuard.MaxDownloadBytes, "El PDF", timeout.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    Delete(pdfPath);
                    throw;
                }
            }
            else
            {
                htmlBytes = await ReadBytesAsync(response, timeout.Token, WebFetchGuard.MaxHtmlBytes, "La página")
                    .ConfigureAwait(false);
            }
        }

        if (pdf && pdfPath != null)
        {
            try
            {
                // Un enlace .pdf puede devolver la página de login en HTML.
                if (!WebFetchGuard.IsPdfFile(pdfPath))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(pdfPath, ct).ConfigureAwait(false);
                    if (!LooksBinary(bytes))
                    {
                        string fallback = DecodeHtml(bytes, charset);
                        string fallbackText = HtmlText.ToPlain(fallback, MaxChars);
                        if (fallbackText.Length > 0)
                            return new Page(HtmlText.ExtractTitle(fallback), fallbackText, finalUrl, false, null);
                    }
                    throw new InvalidOperationException(
                        "El enlace decía ser un PDF pero el servidor devolvió otra cosa (¿página de inicio de sesión?).");
                }

                // Sin el token de red: el OCR local puede tardar más que la descarga.
                string text = await OcrPdfAsync(pdfPath, ct).ConfigureAwait(false);
                string title = FileNameFrom(finalUrl);
                return new Page(title, text, finalUrl, true, null);
            }
            finally
            {
                Delete(pdfPath);
            }
        }

        byte[] html = htmlBytes ?? Array.Empty<byte>();
        if (IsBinaryMediaType(mediaType) || LooksBinary(html))
            throw new InvalidOperationException(
                $"El enlace no es una página de texto ({mediaType}). Si es un archivo, descárgalo.");

        string page = DecodeHtml(html, charset);
        return new Page(HtmlText.ExtractTitle(page), HtmlText.ToPlain(page, MaxChars), finalUrl, false, null);
    }

    /// <summary>
    /// GET siguiendo redirecciones a mano: HttpClient seguiría a ciegas un 302 a
    /// 127.0.0.1 o a file:, y el texto seleccionado no es de fiar. Cada salto pasa
    /// por la misma validación que el enlace original.
    /// </summary>
    private static async Task<HttpResponseMessage> GetFollowingAsync(string url, CancellationToken ct,
        CancellationToken userToken)
    {
        string current = url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!WebFetchGuard.TryNormalizeHttpUrl(current, out string safe, out string? error))
                throw new InvalidOperationException(
                    "La página redirige a una dirección no permitida: " + (error ?? "no válida"));
            current = safe;

            HttpResponseMessage response;
            try
            {
                response = await Shared.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                // Sin esto el usuario veía "A task was canceled." en inglés. Cubre
                // el tope global y el de conexión (ConnectTimeout).
                throw new TimeoutException(
                    $"El sitio no respondió a tiempo (máximo {NetworkTimeoutSeconds} s). Revisa la conexión o el enlace.");
            }
            catch (HttpRequestException ex) when (FindBlockedAddress(ex) != null)
            {
                throw new InvalidOperationException(FindBlockedAddress(ex)!.Message, ex);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("No se pudo conectar con el sitio: " + ex.Message, ex);
            }

            int status = (int)response.StatusCode;
            bool redirect = status is 301 or 302 or 303 or 307 or 308;

            if (!redirect || response.Headers.Location == null) return response;

            var location = response.Headers.Location;
            if (!location.IsAbsoluteUri) location = new Uri(new Uri(current), location);
            response.Dispose();
            current = location.ToString();
        }

        throw new InvalidOperationException("La página da demasiadas redirecciones.");
    }

    private static string FinalUrl(HttpResponseMessage response, string fallback)
    {
        try { return response.RequestMessage?.RequestUri?.ToString() ?? fallback; }
        catch { return fallback; }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        int status = (int)response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException(status switch
        {
            401 or 403 => $"El enlace pide iniciar sesión o está bloqueado (HTTP {status}). Copia el texto desde el navegador.",
            404 or 410 => $"El enlace ya no existe (HTTP {status}).",
            429 => "El sitio está limitando las peticiones (HTTP 429). Espera un momento e inténtalo de nuevo.",
            _ => $"El enlace respondió HTTP {status}."
        });
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(NetworkTimeoutSeconds));
        return cts;
    }

    /// <summary>Lee el cuerpo con tope de tamaño (una respuesta enorme no llena la RAM).</summary>
    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken ct,
        long maxBytes, string what)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await WebFetchGuard.CopyWithLimitAsync(source, buffer, maxBytes, what, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    // ---------------- Texto / HTML ----------------

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodifica el HTML: cabecera, &lt;meta charset&gt; y, si no hay nada,
    /// UTF-8 válido o Latin-1 (las páginas viejas sin cabecera).
    /// </summary>
    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0) return "";

        if (!string.IsNullOrWhiteSpace(charset) && TryGetEncoding(charset, out var declared))
            return declared.GetString(bytes);

        // <meta charset="..."> en la cabecera del documento.
        string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var match = Regex.Match(head, @"charset\s*=\s*[""']?\s*([\w:.-]+)", RegexOptions.IgnoreCase);
        if (match.Success && TryGetEncoding(match.Groups[1].Value, out var meta))
            return meta.GetString(bytes);

        return IsValidUtf8(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static bool TryGetEncoding(string name, out Encoding encoding)
    {
        var clean = (name ?? "").Trim().Trim('"', '\'');
        // Sin el proveedor de code pages, GetEncoding("windows-1252") falla en
        // .NET moderno; Latin-1 cubre igual el texto imprimible en español.
        switch (clean.ToLowerInvariant())
        {
            case "utf-8" or "utf8": encoding = Encoding.UTF8; return true;
            case "iso-8859-1" or "iso8859-1" or "latin1" or "latin-1" or "windows-1252" or "cp1252":
                encoding = Encoding.Latin1; return true;
        }

        try
        {
            encoding = Encoding.GetEncoding(clean);
            return true;
        }
        catch
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try { StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 512);
        for (int i = 0; i < limit; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    private static bool IsBinaryMediaType(string mediaType)
    {
        var t = (mediaType ?? "").Trim().ToLowerInvariant();
        return t.StartsWith("image/", StringComparison.Ordinal)
            || t.StartsWith("audio/", StringComparison.Ordinal)
            || t.StartsWith("video/", StringComparison.Ordinal)
            || t.StartsWith("font/", StringComparison.Ordinal)
            || t is "application/zip" or "application/x-zip-compressed" or "application/x-msdownload"
            || t.Contains("officedocument", StringComparison.Ordinal);
    }

    private static string FileNameFrom(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
            if (uri.Segments.Length == 0) return "";
            string name = Uri.UnescapeDataString(uri.Segments[^1].Trim('/'));
            return WebFetchGuard.SafeFileName(name, "");
        }
        catch
        {
            return "";
        }
    }

    // ---------------- PDF ----------------

    /// <summary>Renderiza las primeras páginas y las pasa por el OCR de Windows.</summary>
    public static async Task<string> OcrPdfAsync(string path, CancellationToken ct = default)
    {
        PdfDocument document;
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
            document = await PdfDocument.LoadFromFileAsync(storageFile).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "No se pudo leer el PDF (¿archivo dañado, protegido o no es un PDF?).", ex);
        }

        // PdfDocument no implementa IDisposable en la proyección de WinRT: el
        // archivo puede seguir bloqueado, así que el temporal se borra best-effort.
        int pages = (int)Math.Min((uint)MaxPdfPages, document.PageCount);
        var sb = new StringBuilder();

        for (uint i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            string png = Path.Combine(Path.GetTempPath(), $"powertools-pdf-{i}-{Guid.NewGuid():N}.png");
            try
            {
                using var page = document.GetPage(i);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream).AsTask(ct).ConfigureAwait(false);
                await WriteStreamAsync(stream, png, ct).ConfigureAwait(false);

                string text = await OcrService.ReadFileAsync(png).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text.Trim()).AppendLine();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn("OCR de PDF (página " + i + "): " + ex.Message);
            }
            finally
            {
                Delete(png);
            }

            if (sb.Length > MaxChars) break;
        }

        string result = sb.ToString().Trim();
        return result.Length > MaxChars ? SafeCut(result, MaxChars) : result;
    }

    /// <summary>Corta sin partir un par suplente (medio emoji deja UTF-16 inválido).</summary>
    private static string SafeCut(string s, int max)
    {
        int cut = Math.Min(s.Length, max);
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    private static async Task WriteStreamAsync(IRandomAccessStream stream, string path, CancellationToken ct)
    {
        const ulong maxRenderBytes = 256UL * 1024 * 1024;

        // Comprobar ANTES de convertir a uint: un Size > 4 GB se truncaba y el
        // tope no se aplicaba.
        ulong size = stream.Size;
        if (size == 0) throw new InvalidOperationException("La página del PDF se renderizó vacía.");
        if (size > maxRenderBytes) throw new InvalidOperationException("La página renderizada del PDF es demasiado grande.");

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        // LoadAsync puede devolver menos bytes de los pedidos: se usan los reales.
        uint loaded = await reader.LoadAsync((uint)size).AsTask(ct).ConfigureAwait(false);
        if (loaded == 0) throw new InvalidOperationException("No se pudo leer la página renderizada del PDF.");

        var buffer = new byte[loaded];
        reader.ReadBytes(buffer);
        await File.WriteAllBytesAsync(path, buffer, ct).ConfigureAwait(false);
    }

    // ---------------- Infra ----------------

    private static void Delete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void EnsureOnline()
    {
        if (Environment.GetEnvironmentVariable("SELBAR_OFFLINE") == "1")
            throw new InvalidOperationException("Modo offline: leer un enlace necesita conexión.");
    }

    private static readonly HttpClient Shared = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Las redirecciones las sigue GetFollowingAsync para validar cada salto.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Se valida la IP real a la que se conecta, no solo el texto del enlace:
            // un dominio que resuelve a 127.0.0.1 (localtest.me, nip.io, DNS
            // rebinding) pasaba el filtro y la app pedía recursos internos.
            ConnectCallback = ConnectGuardedAsync
        };
        // Sin timeout global: cada lectura lleva su propio tope (ver ReadAsync).
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Selbar/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/pdf;q=0.9,*/*;q=0.8");
        return client;
    }

    /// <summary>El nombre resolvió a una dirección interna: no se conecta.</summary>
    private sealed class BlockedAddressException : Exception
    {
        public BlockedAddressException(string message) : base(message) { }
    }

    /// <summary>
    /// Conecta solo a direcciones permitidas. Se resuelve el nombre aquí y se
    /// comprueba cada IP: así un hostname que apunta a loopback/link-local no
    /// esquiva <see cref="WebFetchGuard.IsBlockedHost"/> (que solo ve texto).
    /// </summary>
    private static async ValueTask<Stream> ConnectGuardedAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("No se pudo resolver el dominio: " + ex.Message, ex);
        }

        if (addresses.Length == 0)
            throw new HttpRequestException("El dominio no resolvió a ninguna dirección.");

        foreach (var address in addresses)
        {
            if (WebFetchGuard.IsBlockedAddress(address))
                throw new BlockedAddressException(
                    "El dominio apunta a una dirección local o interna; no se lee por seguridad.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelación del usuario: debe llegar como tal, no como fallo de red.
                socket.Dispose();
                throw;
            }
            catch (SocketException ex)
            {
                // Se prueba la siguiente dirección (IPv6 sin ruta, IPv4 caída…).
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException("No se pudo conectar con el sitio: " + (last?.Message ?? "sin respuesta"), last);
    }

    private static BlockedAddressException? FindBlockedAddress(Exception? ex)
    {
        for (int depth = 0; ex != null && depth < 5; depth++, ex = ex.InnerException)
        {
            if (ex is BlockedAddressException blocked) return blocked;
        }
        return null;
    }
}
