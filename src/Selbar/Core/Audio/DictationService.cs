using System.IO;
using Selbar.Core.Ai;
using Selbar.Core.Config;

namespace Selbar.Core.Audio;

/// <summary>
/// Dictado: graba del micrófono y lo transcribe con Groq Whisper (el mismo
/// servicio que ya usa "Transcribir audio"). Estado en memoria: una sola
/// grabación a la vez.
/// </summary>
public static class DictationService
{
    /// <summary>Máximo de grabación para no superar el límite de Groq (25 MB).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(3);

    private static readonly object Gate = new();
    private static MicRecorder? _recorder;
    private static DateTime _startedAt;
    private static CancellationTokenSource? _autoStop;
    private static bool _autoStopped;
    private static bool _starting;
    /// <summary>Identifica el arranque dueño de <see cref="_starting"/>. Cambia con cada intento.</summary>
    private static int _startId;
    /// <summary>Cambia al cancelar: invalida un arranque que estaba en vuelo.</summary>
    private static int _epoch;

    /// <summary>
    /// Hay una sesión de dictado abierta: el micrófono está grabando, arrancando
    /// o quedó audio pendiente de transcribir (auto-stop). Es lo que consulta el
    /// atajo para decidir entre "grabar" y "transcribir"; para saber si el
    /// micrófono captura ahora mismo, usar <see cref="IsCapturing"/>.
    /// </summary>
    public static bool IsRecording
    {
        get { lock (Gate) return _recorder != null || _starting; }
    }

    /// <summary>El micrófono está capturando ahora mismo (distinto de "hay sesión abierta").</summary>
    public static bool IsCapturing
    {
        get { lock (Gate) return _recorder is { IsRecording: true }; }
    }

    /// <summary>True si la grabación se detuvo sola al llegar al máximo.</summary>
    public static bool AutoStopped
    {
        get { lock (Gate) return _autoStopped; }
    }

    /// <summary>Tiempo grabado (topado al máximo: tras el auto-stop no sigue creciendo).</summary>
    public static TimeSpan Elapsed
    {
        get
        {
            lock (Gate)
            {
                if (_recorder == null) return TimeSpan.Zero;
                var e = DateTime.UtcNow - _startedAt;
                if (e < TimeSpan.Zero) return TimeSpan.Zero;
                return e > MaxDuration ? MaxDuration : e;
            }
        }
    }

    /// <summary>Arranca la grabación (con auto-detención al llegar al máximo).</summary>
    public static async Task StartAsync(CancellationToken ct = default)
    {
        int epoch;
        int startId;
        lock (Gate)
        {
            // Dos toques casi a la vez (hotkey + clic, doble pulsación) no deben
            // crear dos grabadoras: la segunda pisaría a la primera, que quedaría
            // grabando sin dueño (mic ocupado + WAV huérfano).
            //
            // Tampoco se arranca si hay audio pendiente de transcribir (auto-stop):
            // antes se borraba ese WAV y se perdían los últimos minutos grabados.
            if (_recorder != null || _starting) return;
            _starting = true;
            startId = ++_startId;
            epoch = _epoch;
        }

        var recorder = new MicRecorder();
        try
        {
            await recorder.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Este intento ya no es dueño de _starting si otro arranque lo relevó
            // (p. ej. StopAndTranscribe abortó y el usuario volvió a pulsar).
            lock (Gate) { if (startId == _startId) _starting = false; }
            throw;
        }

        CancellationTokenSource? stop = null;
        bool keep;
        lock (Gate)
        {
            keep = epoch == _epoch; // si CancelAsync se cruzó, este arranque ya no vale
            if (startId == _startId) _starting = false;
            if (keep)
            {
                _recorder = recorder;
                _startedAt = DateTime.UtcNow;
                _autoStopped = false;
                CancelAutoStopLocked();
                _autoStop = new CancellationTokenSource();
                stop = _autoStop;
            }
        }

        if (!keep)
        {
            // Cancelado mientras el micrófono arrancaba: apagarlo y borrar el WAV.
            try { MicRecorder.SafeDelete(await recorder.StopAsync().ConfigureAwait(false)); }
            catch { }
            return;
        }

        _ = AutoStopLoopAsync(recorder, stop!.Token);
    }

    /// <summary>
    /// Detiene sola la grabación al agotar el tiempo (sin transcribir ni borrar:
    /// el siguiente toque transcribe lo grabado). El WAV se conserva y la sesión
    /// sigue abierta: <see cref="IsRecording"/> continúa en true para que el atajo
    /// transcriba en vez de empezar de cero y tirar lo grabado.
    /// </summary>
    private static async Task AutoStopLoopAsync(MicRecorder recorder, CancellationToken token)
    {
        try
        {
            await Task.Delay(MaxDuration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { return; }

        lock (Gate)
        {
            if (_recorder != recorder || token.IsCancellationRequested) return;
            _autoStopped = true;
        }
        try { await recorder.StopAsync().ConfigureAwait(false); }
        catch { }
    }

    /// <summary>Cancela el temporizador de auto-detención (llamar con Gate tomado).</summary>
    private static void CancelAutoStopLocked()
    {
        try { _autoStop?.Cancel(); } catch { }
        try { _autoStop?.Dispose(); } catch { }
        _autoStop = null;
    }

    /// <summary>Cancela sin transcribir y borra el audio.</summary>
    public static async Task CancelAsync()
    {
        MicRecorder? recorder;
        lock (Gate)
        {
            _epoch++; // un StartAsync en vuelo se descarta al terminar
            recorder = _recorder;
            _recorder = null;
            _autoStopped = false;
            CancelAutoStopLocked();
        }
        if (recorder == null) return;

        string? path = await recorder.StopAsync().ConfigureAwait(false);
        MicRecorder.SafeDelete(path);
    }

    /// <summary>Detiene, transcribe y devuelve el texto. Borra el WAV siempre.</summary>
    public static async Task<string> StopAndTranscribeAsync(AppSettings settings, CancellationToken ct = default)
    {
        // Doble pulsación muy rápida: el primer toque aún está inicializando el
        // micrófono. Se espera a que termine para no responder "no hay grabación"
        // dejando la captura encendida y sin dueño hasta el auto-stop.
        for (int i = 0; i < 40; i++)
        {
            lock (Gate) { if (!_starting) break; }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        MicRecorder? recorder;
        bool aborted = false;
        lock (Gate)
        {
            // Si sigue arrancando (micrófono Bluetooth lento), se aborta el
            // arranque: el WAV lo borra el propio StartAsync al verse cancelado.
            // _starting se libera para que un micro que no responde no deje el
            // dictado bloqueado: el arranque en vuelo ya no es dueño de la marca.
            if (_starting && _recorder == null) { _epoch++; _starting = false; aborted = true; }
            recorder = _recorder;
            _recorder = null;
            _autoStopped = false;
            CancelAutoStopLocked();
        }
        if (aborted && recorder == null)
            throw new InvalidOperationException("El micrófono aún se estaba preparando. Prueba otra vez.");
        if (recorder == null) throw new InvalidOperationException("No hay ninguna grabación en curso.");

        string? path = await recorder.StopAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("No se grabó ningún audio.");

            var info = new FileInfo(path);
            if (info.Length < 2048)
                throw new InvalidOperationException(
                    "No se captó sonido. Revisa el micrófono en Configuración → Privacidad → Micrófono.");

            string key = SettingsManager.Unprotect(settings.Ai.GroqApiKeyEncrypted);
            string text = await GroqWhisper.TranscribeAsync(path, key, settings.Ai.TranscriptionModel, ct)
                .ConfigureAwait(false);

            text = (text ?? "").Trim();
            if (text.Length == 0)
                throw new InvalidOperationException("No se entendió nada en el audio. Prueba a hablar más cerca del micrófono.");
            return text;
        }
        finally
        {
            MicRecorder.SafeDelete(path);
        }
    }
}
