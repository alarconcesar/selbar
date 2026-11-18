using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace Selbar.Core.Imaging;

/// <summary>
/// OCR local con la API de Windows (Windows.Media.Ocr). No usa red ni modelos
/// externos: reconoce el texto de una imagen del portapapeles o de un archivo.
/// Requiere que Windows tenga instalado el paquete OCR de algún idioma (viene
/// con los idiomas del sistema).
/// </summary>
public static class OcrService
{
    /// <summary>Tamaño máximo de imagen para OCR local.</summary>
    public const long MaxOcrBytes = 50L * 1024 * 1024;

    /// <summary>Dimensión y píxeles máximos (tope anti bomba de descompresión).</summary>
    public const uint MaxOcrDimension = 12000;
    public const long MaxOcrPixels = 100L * 1024 * 1024;

    /// <summary>¿Hay algún motor OCR instalado?</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return CreateEngine() != null; }
            catch { return false; }
        }
    }

    /// <summary>Idiomas OCR instalados (para diagnóstico).</summary>
    public static IReadOnlyList<string> Languages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// ¿El portapapeles tiene una imagen (o un archivo de imagen)? Debe llamarse
    /// desde el hilo de UI.
    /// </summary>
    public static bool ClipboardHasImage()
    {
        try
        {
            if (Clipboard.ContainsImage()) return true;
            if (!Clipboard.ContainsFileDropList()) return false;

            var files = Clipboard.GetFileDropList();
            return files.Cast<string>().Any(IsImageFile);
        }
        catch { return false; }
    }

    /// <summary>Reconoce el texto de la imagen que hay en el portapapeles.</summary>
    public static async Task<string> ReadClipboardImageAsync()
    {
        var (source, filePath) = ReadClipboardOnUiThread();

        if (filePath != null)
            return await ReadFileAsync(filePath).ConfigureAwait(false);

        if (source == null)
            throw new InvalidOperationException(
                "No hay ninguna imagen en el portapapeles. Copia una imagen o usa Win+Shift+S para recortar una zona.");

        string temp = Path.Combine(Path.GetTempPath(), "powertools-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // La imagen se capturó en el hilo de UI: sin congelarla, codificar en
            // otro hilo lanza "The calling thread cannot access this object".
            if (source.CanFreeze && !source.IsFrozen) source.Freeze();

            // Comprobar el tamaño ANTES de codificar: una captura de varios
            // monitores generaba un PNG enorme solo para rechazarlo después.
            EnsureOcrSize(source.PixelWidth, source.PixelHeight);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(WpfBitmapFrame.Create(source));
            using (var stream = File.Create(temp)) encoder.Save(stream);

            return await ReadFileAsync(temp).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>Reconoce el texto de un archivo de imagen.</summary>
    public static async Task<string> ReadFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("No se encontró la imagen.", path);
        string? invalid = ImageValidation.ValidateImageFile(path, MaxOcrBytes);
        if (invalid != null) throw new InvalidOperationException(invalid);

        var engine = CreateEngine()
            ?? throw new InvalidOperationException(
                "Windows no tiene ningún paquete de OCR instalado. Añade un idioma con OCR en Configuración → Hora e idioma.");

        SoftwareBitmap bitmap;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await WinBitmapDecoder.CreateAsync(stream);
            // Bomba de descompresión: un PNG pequeño puede declarar gigapíxeles.
            EnsureOcrSize(decoder.PixelWidth, decoder.PixelHeight);
            // El OCR solo trabaja con un formato concreto: un PNG de 16 bits o un
            // JPEG CMYK hacían que RecognizeAsync lanzara una excepción COM críptica.
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "No se pudo leer la imagen (¿archivo dañado o formato no soportado?).", ex);
        }

        using (bitmap)
        {
            OcrResult result;
            try
            {
                result = await engine.RecognizeAsync(bitmap);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows no pudo reconocer el texto de esta imagen. Prueba con una captura más nítida.", ex);
            }
            return Normalize(result.Text);
        }
    }
    private static (BitmapSource? Source, string? FilePath) ReadClipboardOnUiThread()
    {
        BitmapSource? source = null;
        string? filePath = null;

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
                var files = Clipboard.GetFileDropList();
                filePath = files.Cast<string>().FirstOrDefault(IsImageFile);
            }
            catch
            {
                source = null;
                filePath = null;
            }
        }

        var dispatcher = Application.Current?.Dispatcher;

        // Otro proceso puede tener el portapapeles abierto (CLIPBRD_E_CANT_OPEN):
        // un solo intento convertía eso en "no hay ninguna imagen".
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (dispatcher == null || dispatcher.CheckAccess()) Read();
                else dispatcher.Invoke(Read);
            }
            catch { }
            if (source != null || filePath != null) break;
            if (attempt < 2) Thread.Sleep(40 * (attempt + 1));
        }

        return (source, filePath);
    }

    private static OcrEngine? CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;

        var available = OcrEngine.AvailableRecognizerLanguages;
        if (available.Count == 0) return null;

        // Sin paquete del idioma del usuario: mejor el idioma de la interfaz que
        // el primero de la lista (podía ser coreano con un texto en español).
        string ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var language = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(ui, StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.StartsWith(ui + "-", StringComparison.OrdinalIgnoreCase))
            ?? available.First();

        Selbar.Core.Diagnostics.Logger.Warn("OCR sin paquete del idioma del usuario; se usa " + language.LanguageTag);
        return OcrEngine.TryCreateFromLanguage(language);
    }

    private static bool IsImageFile(string? path) => ImageValidation.IsSupportedExtension(path);

    /// <summary>
    /// Tope real por lado: el de la API (OcrEngine.MaxImageDimension) es más bajo
    /// que <see cref="MaxOcrDimension"/>, y por encima RecognizeAsync fallaba con
    /// una excepción COM sin explicar nada.
    /// </summary>
    private static uint MaxDimension
    {
        get
        {
            try { return Math.Min(MaxOcrDimension, OcrEngine.MaxImageDimension); }
            catch { return MaxOcrDimension; } // sin runtime de OCR: decide CreateEngine
        }
    }

    /// <summary>Tope anti bomba de descompresión (lado y píxeles totales).</summary>
    private static void EnsureOcrSize(long width, long height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("La imagen no tiene un tamaño válido.");

        uint max = MaxDimension;
        if (width > max || height > max || width * height > MaxOcrPixels)
            throw new InvalidOperationException(
                $"La imagen es demasiado grande para el OCR ({width}×{height} píxeles; máximo {max} por lado). " +
                "Reduce su tamaño y vuelve a intentarlo.");
    }

    /// <summary>Une las líneas, quita líneas vacías repetidas y espacios sobrantes.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();

        var output = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (output.Count == 0 || output[^1].Length == 0)) continue;
            output.Add(line);
        }

        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return string.Join("\n", output);
    }
}
