using System.IO;

namespace Selbar.Core.Imaging;

/// <summary>
/// Validación de archivos de imagen antes de OCR/Lens (extensión y tamaño).
/// Puro (sin tipos de Windows): compila en el probe de Linux.
/// </summary>
public static class ImageValidation
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return IsSupportedImageExtension(Path.GetExtension(path));
    }

    /// <summary>¿Es una extensión de imagen (con punto, p. ej. ".png")?</summary>
    public static bool IsSupportedImageExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) &&
        ImageExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valida un archivo de imagen. Devuelve null si es correcto.</summary>
    public static string? ValidateImageFile(string? path, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No hay ninguna imagen.";
        if (!File.Exists(path)) return "El archivo ya no existe.";
        if (!IsSupportedExtension(path))
            return "Formato no soportado. Usa png, jpg, bmp, gif, tif o webp.";
        long length;
        try { length = new FileInfo(path).Length; }
        catch (Exception ex) { return "No se pudo leer el archivo: " + ex.Message; }
        if (length == 0) return "El archivo está vacío.";
        if (length > maxBytes) return $"El archivo supera el máximo de {maxBytes / 1024 / 1024} MB.";
        // La extensión no basta: un .png que es una página de error se subiría a
        // Lens o reventaría el decodificador con un mensaje incomprensible.
        if (!LooksLikeImage(path))
            return "El archivo no parece una imagen válida (¿está dañado o es otro tipo de archivo?).";
        return null;
    }

    /// <summary>
    /// Comprueba la firma real del archivo (magic bytes). No exige que coincida
    /// con la extensión: un JPEG guardado como .png es una imagen válida (Windows
    /// lo abre igual) y antes se rechazaba como "archivo dañado".
    /// Solo lee la cabecera, así que es barato con archivos grandes.
    /// </summary>
    public static bool LooksLikeImage(string? path)
    {
        var head = ReadHeader(path, out int read);
        return head != null && Sniff(head, read) != ImageKind.Unknown;
    }

    /// <summary>Formatos reconocibles por su firma.</summary>
    private enum ImageKind { Unknown, Png, Jpeg, Gif, Bmp, Tiff, Webp }

    private static byte[]? ReadHeader(string? path, out int read)
    {
        read = 0;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var head = new byte[16];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = stream.Read(head, 0, head.Length);
        }
        catch
        {
            return null;
        }
        return read >= 2 ? head : null;
    }

    private static ImageKind Sniff(byte[] head, int read)
    {
        if (read >= 8 && Starts(head, read, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
            return ImageKind.Png;
        if (read >= 3 && Starts(head, read, 0xFF, 0xD8, 0xFF))
            return ImageKind.Jpeg;
        if (read >= 4 && Starts(head, read, (byte)'G', (byte)'I', (byte)'F', (byte)'8'))
            return ImageKind.Gif;
        if (read >= 2 && Starts(head, read, (byte)'B', (byte)'M'))
            return ImageKind.Bmp;
        // TIFF little/big endian y BigTIFF.
        if (read >= 4 && (Starts(head, read, (byte)'I', (byte)'I', 0x2A, 0x00)
            || Starts(head, read, (byte)'M', (byte)'M', 0x00, 0x2A)
            || Starts(head, read, (byte)'I', (byte)'I', 0x2B, 0x00)
            || Starts(head, read, (byte)'M', (byte)'M', 0x00, 0x2B)))
            return ImageKind.Tiff;
        if (read >= 12 && Starts(head, read, (byte)'R', (byte)'I', (byte)'F', (byte)'F')
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
            return ImageKind.Webp;
        return ImageKind.Unknown;
    }

    private static bool Starts(byte[] head, int read, params byte[] signature)
    {
        if (read < signature.Length) return false;
        for (int i = 0; i < signature.Length; i++)
            if (head[i] != signature[i]) return false;
        return true;
    }

    private static string MimeFor(ImageKind kind) => kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        ImageKind.Gif => "image/gif",
        ImageKind.Bmp => "image/bmp",
        ImageKind.Tiff => "image/tiff",
        ImageKind.Webp => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Content-Type para subir la imagen (Lens/Groq lo usan para oler el formato).
    /// Manda la firma real: un JPEG con nombre .png se subía como image/png.
    /// </summary>
    public static string ContentTypeForImage(string? path)
    {
        var head = ReadHeader(path, out int read);
        var kind = head == null ? ImageKind.Unknown : Sniff(head, read);
        if (kind != ImageKind.Unknown) return MimeFor(kind);

        return Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
    }
}
