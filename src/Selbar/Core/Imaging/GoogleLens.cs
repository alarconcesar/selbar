using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace Selbar.Core.Imaging;

/// <summary>
/// Búsqueda inversa de imágenes con Google Lens. Igual que el OCR, la imagen
/// solo sale del equipo cuando el usuario pide explícitamente la búsqueda.
/// </summary>
public static class GoogleLens
{
    public const string UploadEndpoint = "https://www.google.com/searchbyimage/upload";

    /// <summary>Tamaño máximo a subir (las imágenes de pantalla/PNG rara vez lo superan).</summary>
    public const long MaxUploadBytes = 20L * 1024 * 1024;

    private static readonly HttpClient Shared = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        // Sin timeout global: cada subida lleva su propio tope (ver UploadFileAsync).
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Selbar/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        return client;
    }

    /// <summary>URL de Lens para una imagen que ya está publicada en internet.</summary>
    public static string ByUrl(string imageUrl) =>
        "https://lens.google.com/uploadbyurl?url=" + Uri.EscapeDataString(imageUrl.Trim());

    /// <summary>¿La selección es una URL de imagen (para la acción contextual)?</summary>
    public static bool IsImageUrl(string? text)
    {
        string raw = (text ?? "").Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        string extension = Path.GetExtension(uri.AbsolutePath);
        return ImageValidation.IsSupportedImageExtension(extension);
    }

    /// <summary>Sube la imagen del portapapeles a Lens y devuelve la URL de resultados.</summary>
    public static async Task<string> UploadClipboardImageAsync(CancellationToken ct = default)
    {
        string? file = ReadClipboardImageFile();
        if (file == null)
            throw new InvalidOperationException(
                "No hay ninguna imagen en el portapapeles. Copia una imagen o usa Win+Shift+S.");

        bool temporary = file.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                         Path.GetFileName(file).StartsWith("powertools-lens-", StringComparison.Ordinal);

        try
        {
            return await UploadFileAsync(file, ct).ConfigureAwait(false);
        }
        finally
        {
            if (temporary)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>Sube un archivo de imagen y devuelve la URL de resultados de Lens.</summary>
    public static async Task<string> UploadFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("No se encontró la imagen.", path);
        string? invalid = ImageValidation.ValidateImageFile(path, MaxUploadBytes);
        if (invalid != null) throw new InvalidOperationException(invalid);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        using var stream = File.OpenRead(path);
        using var form = new MultipartFormDataContent();
        var image = new StreamContent(stream);
        image.Headers.ContentType = new MediaTypeHeaderValue(ImageValidation.ContentTypeForImage(path));
        form.Add(image, "encoded_image", Path.GetFileName(path));
        form.Add(new StringContent(""), "image_content");

        using var response = await SendUploadAsync(form, timeout.Token, ct).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
        {
            var location = response.Headers.Location;
            if (!location.IsAbsoluteUri) location = new Uri(new Uri(UploadEndpoint), location);
            // Open-redirect: un portal cautivo o un MITM podría redirigir a otro
            // sitio; solo se acepta el destino dentro de Google/Lens porque la
            // URL resultante se abre en el navegador.
            if (!IsTrustedResultUrl(location))
                throw new InvalidOperationException(
                    "Google Lens devolvió una redirección inesperada; no se abre por seguridad.");
            return location.AbsoluteUri;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Google rechazó la búsqueda (¿demasiadas peticiones?). Inténtalo más tarde.");

        if ((int)response.StatusCode is 404 or 410)
            throw new InvalidOperationException(
                $"Google Lens no respondió como se esperaba (HTTP {(int)response.StatusCode}): " +
                "es posible que Google haya cambiado el servicio; revisa UploadEndpoint.");

        // Un 200 con HTML es la página de consentimiento o un error: abrirla en el
        // navegador no da resultados y el usuario no sabría por qué.
        if (response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Google Lens devolvió una página en vez de la URL de resultados " +
                "(¿consentimiento de cookies o servicio cambiado?). Abre lens.google.com en el navegador.");

        throw new InvalidOperationException(
            $"Google Lens no respondió como se esperaba (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>
    /// Sube el formulario traduciendo los fallos de red y el timeout a mensajes
    /// en español: antes el usuario veía "A task was canceled." tras 60 s.
    /// </summary>
    private static async Task<HttpResponseMessage> SendUploadAsync(
        MultipartFormDataContent form, CancellationToken timeoutToken, CancellationToken userToken)
    {
        try
        {
            return await Shared.PostAsync(UploadEndpoint, form, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new TimeoutException("Google Lens tardó más de 60 s en responder. Inténtalo más tarde.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("No se pudo conectar con Google Lens: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// ¿La URL de resultados pertenece a Google/Lens? Se rechaza userinfo
    /// (https://algo@otro-sitio) porque la URL se abre con el navegador.
    /// </summary>
    public static bool IsTrustedResultUrl(Uri location) =>
        location.UserInfo.Length == 0 && Selbar.Core.Web.WebFetchGuard.IsTrustedGoogleResult(location);

    private static string? ReadClipboardImageFile()
    {
        BitmapSource? source = null;
        string? existing = null;

        void Read()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    source = Clipboard.GetImage();
                    return;
                }

                if (!Clipboard.ContainsFileDropList()) return;
                existing = Clipboard.GetFileDropList().Cast<string>()
                    .FirstOrDefault(ImageValidation.IsSupportedExtension);
            }
            catch { }
        }

        var dispatcher = Application.Current?.Dispatcher;

        // El portapapeles puede estar bloqueado por otro proceso (CLIPBRD_E_CANT_OPEN).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (dispatcher == null || dispatcher.CheckAccess()) Read();
                else dispatcher.Invoke(Read);
            }
            catch { }
            if (source != null || existing != null) break;
            if (attempt < 2) Thread.Sleep(40 * (attempt + 1));
        }

        if (existing != null) return existing;
        if (source == null) return null;

        // La imagen viene del hilo de UI: se congela antes de codificarla aquí.
        if (source.CanFreeze && !source.IsFrozen) source.Freeze();

        string temp = Path.Combine(Path.GetTempPath(), "powertools-lens-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var file = File.Create(temp)) encoder.Save(file);
        }
        catch
        {
            // Si la codificación falla (disco lleno, imagen rara) el temporal a
            // medias se quedaba en %TEMP%: nadie lo borraba.
            try { File.Delete(temp); } catch { }
            throw;
        }
        return temp;
    }
}
