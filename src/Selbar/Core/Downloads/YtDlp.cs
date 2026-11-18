using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;

namespace Selbar.Core.Downloads;

/// <summary>
/// Integración con yt-dlp (herramienta externa, no incluida). Acepta URLs de
/// YouTube y otras 30 plataformas (ver SupportedDomains) y construye los
/// argumentos sin pasar por el shell.
/// </summary>
public static class YtDlp
{
    public const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string ProjectUrl = "https://github.com/yt-dlp/yt-dlp";
    /// <summary>Checksums oficiales de la release (formato "hash  nombre").</summary>
    public const string ChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    /// <summary>Tope del binario descargado (hoy ronda 30 MB; 64 MB deja margen).</summary>
    public const long MaxInstallerBytes = 64L * 1024 * 1024;

    /// <summary>Dominios principales soportados por yt-dlp que la app reconoce.</summary>
    public static IReadOnlyList<string> SupportedDomains { get; } = new[]
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com",
        "tiktok.com", "instagram.com", "facebook.com", "fb.watch",
        "x.com", "twitter.com", "vimeo.com", "twitch.tv",
        "reddit.com", "redd.it", "soundcloud.com", "dailymotion.com", "dai.ly",
        "pinterest.com", "pin.it", "linkedin.com", "rumble.com", "odysee.com",
        "bitchute.com", "kick.com", "vk.com", "ok.ru", "bilibili.com", "b23.tv",
        "nicovideo.jp", "bandcamp.com", "archive.org", "snapchat.com", "ted.com"
    };

    /// <summary>Lista legible para tooltips y documentación.</summary>
    public const string SupportedSitesText =
        "YouTube, TikTok, Instagram, Facebook, X/Twitter, Vimeo, Twitch, Reddit, SoundCloud, " +
        "Dailymotion, Pinterest, LinkedIn, Rumble, Odysee, Bitchute, Kick, VK, OK.ru, Bilibili, " +
        "Niconico, Bandcamp, Archive.org, Snapchat y TED";

    /// <summary>Regex para que las acciones solo aparezcan con enlaces de sitios compatibles.</summary>
    public static string SupportedUrlPattern { get; } = BuildUrlPattern();

    private static string BuildUrlPattern()
    {
        string domains = string.Join("|", SupportedDomains.Select(Regex.Escape));
        return @"^(?:https?://)?(?:[\w-]+\.)*(?:" + domains + @")(?::\d+)?(?:/\S*)?$";
    }

    // El id de YouTube se extrae con Uri (segmentos y query), no con una regex:
    // la regex anterior hacía backtracking exponencial y lanzaba
    // RegexMatchTimeoutException con un enlace tan normal como "watch?v=abc".

    /// <summary>Carpeta de descargas de Selbar.</summary>
    public static string DownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Selbar");

    /// <summary>Ruta local donde se instalaría yt-dlp si el usuario lo descarga.</summary>
    public static string LocalInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Selbar", "tools", "yt-dlp.exe");

    /// <summary>
    /// Valida una URL de YouTube y devuelve una forma canónica
    /// (https://www.youtube.com/watch?v=ID) para no arrastrar parámetros raros.
    /// </summary>
    public static bool TryNormalizeUrl(string? text, out string normalized)
    {
        normalized = "";
        string raw = (text ?? "").Trim();
        if (raw.Length == 0) return false;

        // "youtube.com/watch?v=..." sin esquema: la acción ya se ofrece para ese
        // texto (SupportedUrlPattern lo permite), así que aquí también vale.
        if (!raw.Contains("://", StringComparison.Ordinal) && IsSupportedHost(HostOf(raw)))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;

        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (!IsSupportedHost(host)) return false;

        // YouTube se canonicaliza para quitar parámetros raros; el resto conserva su URL.
        if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            if (!TryGetYouTubeId(uri, out string videoId)) return false;
            normalized = "https://www.youtube.com/watch?v=" + videoId;
            return true;
        }

        // Sin credenciales: userinfo (user:pass@) no debe llegar a yt-dlp ni a los logs.
        var clean = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        normalized = clean.Uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Saca el id (11 caracteres) de un enlace de YouTube sin regex: segmentos
    /// para /shorts/, /live/, /embed/ y youtu.be, y el parámetro v= para /watch.
    /// </summary>
    private static bool TryGetYouTubeId(Uri uri, out string id)
    {
        id = "";
        string path = uri.AbsolutePath;
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();

        if (host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            string segment = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return IsVideoId(segment, out id);
        }

        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        string head = segments[0].ToLowerInvariant();
        if (head == "watch")
            return IsVideoId(GetQueryParameter(uri.Query, "v"), out id);
        if (head is "shorts" or "live" or "embed" or "v" or "e")
            return segments.Length > 1 && IsVideoId(segments[1], out id);
        return false;
    }

    private static bool IsVideoId(string? value, out string id)
    {
        id = "";
        string candidate = (value ?? "").Trim();
        if (candidate.Length != 11) return false;
        foreach (char c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        id = candidate;
        return true;
    }

    private static string? GetQueryParameter(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals >= 0 ? pair[..equals] : pair;
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..]) : "";
        }
        return null;
    }

    public static bool IsSupportedHost(string host)
    {
        var h = (host ?? "").Trim().TrimEnd('.');
        if (h.Length == 0) return false;
        return SupportedDomains.Any(domain =>
            h.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Host de un texto con pinta de URL, sin esquema, credenciales ni puerto.</summary>
    private static string HostOf(string raw)
    {
        int end = raw.IndexOfAny(new[] { '/', '?', '#', '\\' });
        string authority = end >= 0 ? raw[..end] : raw;
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        int colon = authority.IndexOf(':');
        if (colon >= 0 && colon == authority.LastIndexOf(':')) authority = authority[..colon];
        return authority.Trim();
    }

    /// <summary>Busca yt-dlp junto a la app, en su carpeta de datos o en el PATH.</summary>
    public static string? FindExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"),
            LocalInstallPath
        };

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                // Algunas entradas del PATH vienen entre comillas.
                try { candidates.Add(Path.Combine(directory.Trim().Trim('"'), "yt-dlp.exe")); } catch { }
            }
        }

        return candidates.FirstOrDefault(candidate =>
        {
            try { return File.Exists(candidate); } catch { return false; }
        });
    }

    /// <summary>Argumentos de descarga (sin shell). Vídeo o audio, sin listas de reproducción.</summary>
    public static List<string> BuildArguments(string url, bool audio, string folder)
    {
        // Falla rápido con un error claro en vez de lanzar yt-dlp con basura
        // (p. ej. "-o algo" u otra bandera que podría reinterpretar).
        if (!TryNormalizeUrl(url, out string clean))
            throw new ArgumentException("La selección no es un enlace de vídeo válido.", nameof(url));

        // La carpeta va como valor de -P: una cadena que empiece por '-' o con
        // comillas/saltos de línea no es una ruta válida y no debe llegar a yt-dlp.
        if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith('-') ||
            folder.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
            throw new ArgumentException("La carpeta de descargas no es válida.", nameof(folder));

        var args = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--no-mtime",
            "-P", folder,
            "-o", "%(title).140s [%(id)s].%(ext)s",
            "--print", "after_move:filepath"
        };

        if (audio)
        {
            args.Add("-f");
            args.Add("ba[ext=m4a]/ba");
        }
        else
        {
            // Sin ffmpeg: se prefiere un formato ya mezclado (hasta 1080p).
            args.Add("-f");
            args.Add("b[height<=1080]/b");
        }

        args.Add("--");
        args.Add(clean);
        return args;
    }

    /// <summary>Crea la carpeta de descargas y devuelve la ruta.</summary>
    public static string EnsureDownloadFolder()
    {
        string folder = DownloadFolder;
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Instala yt-dlp descargándolo de la release oficial de GitHub.</summary>
    public static async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await DownloadHttp.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        if (total is > MaxInstallerBytes)
            throw new InvalidOperationException("La descarga de yt-dlp es demasiado grande; no se instala.");

        string target = LocalInstallPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string temp = target + ".download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long read = 0;
                int chunk;
                while ((chunk = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    read += chunk;
                    // Sin tope, un servidor comprometido llena el disco.
                    if (read > MaxInstallerBytes)
                        throw new InvalidOperationException("La descarga de yt-dlp es demasiado grande; no se instala.");
                    await output.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                    if (total is > 0) progress?.Report(Math.Min(100.0, read * 100.0 / total.Value));
                }
            }

            var info = new FileInfo(temp);
            if (info.Length < 1_000_000)
                throw new InvalidOperationException("La descarga de yt-dlp no parece válida.");

            // Comprobación mínima de que es un ejecutable de Windows (cabecera MZ).
            using (var stream = File.OpenRead(temp))
            {
                int m = stream.ReadByte();
                int z = stream.ReadByte();
                if (m != 'M' || z != 'Z')
                    throw new InvalidOperationException("El archivo descargado no es un ejecutable válido.");
            }

            // Verificar ANTES de mover: un binario sin comprobar no debe quedar
            // instalado (antes, si fallaba la red al traer los checksums, el
            // ejecutable sin verificar quedaba en su sitio y se usaba igual).
            await VerifyChecksumAsync(temp, ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw new InvalidOperationException(
                    "No se pudo reemplazar yt-dlp.exe (¿está en uso?). Cierra cualquier descarga en curso y reintenta.", ex);
            }
        }

        File.Move(temp, target);
        return target;
    }

    /// <summary>
    /// Compara el SHA-256 del binario con el publicado en la release oficial.
    /// Lanza si no coincide o si no se pudo comprobar.
    /// </summary>
    private static async Task VerifyChecksumAsync(string file, CancellationToken ct)
    {
        string expected;
        try
        {
            string sums = await SumsHttp.GetStringAsync(ChecksumsUrl, ct).ConfigureAwait(false);
            expected = FindChecksum(sums, "yt-dlp.exe")
                ?? throw new InvalidOperationException(
                    "La lista de checksums oficial no incluye yt-dlp.exe; por seguridad no se instala.");
        }
        catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
        {
            // Una cancelación del usuario no es un fallo de red: se propaga tal cual
            // para que la ventana muestre «Cancelado» y no «revisa tu conexión».
            throw new InvalidOperationException(
                "No se pudo descargar la lista de checksums oficial; por seguridad no se instala. Revisa tu conexión y reintenta.", ex);
        }

        string actual;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidOperationException(
                "El hash del binario no coincide con el oficial. No se instala por seguridad.");
        }
    }

    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HttpClient SumsHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string? FindChecksum(string sums, string fileName)
    {
        foreach (string line in sums.Split('\n'))
        {
            // Formato: "<hash><espacios><nombre>"; el nombre puede llevar ./ o * delante.
            string t = line.Trim().TrimEnd('\r');
            if (t.Length < 70) continue;
            string[] parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            // Un SHA-256 son 64 dígitos hex: así una página de error de GitHub no
            // puede colarse como si fuera un checksum.
            if (!IsSha256(parts[0])) continue;
            if (parts[1].TrimStart('.', '/', '*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>Abre la carpeta de descargas (o selecciona el archivo si se conoce).</summary>
    public static void OpenFolder(string? filePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                return;
            }

            string folder = EnsureDownloadFolder();
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { }
    }
}
