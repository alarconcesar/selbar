using System.IO;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Selbar.Core.Audio;

/// <summary>
/// Graba del micrófono a un WAV temporal con la API de Windows (MediaCapture).
/// No usa dependencias externas. El archivo se borra después de transcribirlo.
/// </summary>
public sealed class MicRecorder
{
    private readonly object _gate = new();
    private MediaCapture? _capture;
    private Task? _stopping;
    private string? _path;

    public bool IsRecording => _capture != null;
    public string? FilePath => _path;

    /// <summary>Empieza a grabar y devuelve la ruta del WAV.</summary>
    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        if (_capture != null) throw new InvalidOperationException("Ya hay una grabación en curso.");

        CleanupStaleTempFiles();

        var capture = new MediaCapture();
        string? createdPath = null;
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                // Compartido: otra app usando el micro (Teams, Zoom...) no debería
                // impedir grabar; con ExclusiveControl cualquier otro lector del
                // micro convertía el inicio en error.
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            }).AsTask(ct).ConfigureAwait(false);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath())
                .AsTask(ct).ConfigureAwait(false);
            var file = await folder
                .CreateFileAsync($"powertools-dictado-{Guid.NewGuid():N}.wav", CreationCollisionOption.GenerateUniqueName)
                .AsTask(ct).ConfigureAwait(false);
            // Anotar la ruta en cuanto existe: si StartRecord falla, DropPartialFile
            // debe poder borrar el WAV vacío (antes createdPath se asignaba después
            // y el archivo quedaba huérfano en %TEMP% hasta la limpieza de 24 h).
            createdPath = file.Path;

            await capture.StartRecordToStorageFileAsync(
                MediaEncodingProfile.CreateWav(AudioEncodingQuality.Medium), file)
                .AsTask(ct).ConfigureAwait(false);

            _capture = capture;
            _path = file.Path;
            return file.Path;
        }
        catch (UnauthorizedAccessException ex)
        {
            DropPartialFile(capture, createdPath);
            throw new InvalidOperationException(
                "Windows bloqueó el micrófono. Actívalo en Configuración → Privacidad → Micrófono.", ex);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8889000A))
        {
            DropPartialFile(capture, createdPath);
            throw new InvalidOperationException(
                "El micrófono está en uso por otra app (Teams, Zoom…). Ciérrala y vuelve a intentar.", ex);
        }
        catch
        {
            DropPartialFile(capture, createdPath);
            throw;
        }
    }

    /// <summary>Detiene la grabación y devuelve la ruta del WAV (aunque ya estuviera detenida).</summary>
    public async Task<string?> StopAsync()
    {
        Task? pending;
        lock (_gate)
        {
            var capture = _capture;
            _capture = null;
            // Idempotente: si dos llamadas se cruzan (auto-stop + atajo), la
            // segunda espera a la misma parada en vez de borrar el WAV mientras
            // StopRecordAsync todavía lo está cerrando.
            if (capture != null) _stopping = StopCoreAsync(capture);
            pending = _stopping;
        }

        if (pending != null)
        {
            try { await pending.ConfigureAwait(false); } catch { /* si falla, igual devolvemos lo grabado */ }
        }
        return _path;
    }

    private static async Task StopCoreAsync(MediaCapture capture)
    {
        try { await capture.StopRecordAsync().AsTask().ConfigureAwait(false); }
        finally { capture.Dispose(); }
    }

    public static void SafeDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    /// <summary>
    /// Libera la captura y borra el WAV a medio crear: si el inicio falla
    /// después de crear el archivo (p. ej. StartRecord lanza), no debe quedar
    /// un huérfano en %TEMP%.
    /// </summary>
    private static void DropPartialFile(MediaCapture capture, string? createdPath)
    {
        try { capture.Dispose(); } catch { }
        SafeDelete(createdPath);
    }

    /// <summary>Antigüedad a partir de la cual un WAV de dictado se considera huérfano.</summary>
    public static readonly TimeSpan StaleFileAge = TimeSpan.FromHours(24);

    /// <summary>
    /// ¿Es un WAV de dictado nuestro y más viejo que <see cref="StaleFileAge"/>?
    /// (puro: sin E/S, para tests).
    /// </summary>
    public static bool IsStaleDictationFile(string fileName, DateTime lastWriteUtc, DateTime utcNow)
        => fileName.StartsWith("powertools-dictado-", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            && (utcNow - lastWriteUtc) > StaleFileAge;

    /// <summary>
    /// Borra (best-effort) los WAV de dictado huérfanos: si la app se cerró a
    /// mitad de grabación, el finally de transcribir nunca corrió.
    /// </summary>
    public static void CleanupStaleTempFiles()
    {
        try
        {
            string temp = Path.GetTempPath();
            var now = DateTime.UtcNow;
            foreach (var path in Directory.EnumerateFiles(temp, "powertools-dictado-*.wav"))
            {
                try
                {
                    if (IsStaleDictationFile(Path.GetFileName(path), File.GetLastWriteTimeUtc(path), now))
                        File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }
}
