using System.IO;
using System.Media;
using System.Threading;
using Selbar.Core.Diagnostics;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Selbar.Core.Speech;

/// <summary>
/// Lectura en voz alta con las voces de Windows (WinRT SpeechSynthesizer).
/// Sintetiza a WAV y lo reproduce con SoundPlayer: sin dependencias nuevas y
/// sin red. Respeta el idioma del sistema eligiendo la primera voz que coincida.
/// </summary>
public static class SpeechService
{
    /// <summary>Un SpeechSynthesizer no admite dos síntesis simultáneas: se serializan.</summary>
    private static readonly SemaphoreSlim SynthGate = new(1, 1);
    private static readonly object PlayerGate = new();

    private static SpeechSynthesizer? _synth;
    private static SoundPlayer? _player;
    private static CancellationTokenSource? _speakCts;
    private static string? _voiceName;
    /// <summary>Sube con cada «Detener»: invalida lecturas que esperaban turno.</summary>
    private static int _stopEpoch;

    /// <summary>¿Hay alguna voz instalada?</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return SpeechSynthesizer.AllVoices.Count > 0; }
            catch { return false; }
        }
    }

    /// <summary>Voz que se usará (nombre legible) para mostrarla en Ajustes.</summary>
    public static string VoiceName
    {
        get
        {
            try
            {
                if (_voiceName is { Length: > 0 }) return _voiceName;
                var name = PickVoice()?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) _voiceName = name;
                return string.IsNullOrWhiteSpace(name) ? "(sin voces instaladas)" : name!;
            }
            catch { return "(sin voces instaladas)"; }
        }
    }

    /// <summary>Sintetiza y reproduce. Bloquea hasta terminar (llamar desde fondo).</summary>
    public static async Task SpeakAsync(string text, double rate = 1.0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsAvailable) throw new InvalidOperationException(
            "Windows no tiene ninguna voz instalada. Añádela en Configuración → Hora e idioma → Voz.");

        // Esperar turno: dos lecturas a la vez (doble clic en «Leer») lanzaban
        // 0x8000000E y la segunda se perdía en silencio.
        int epoch = Volatile.Read(ref _stopEpoch);
        await SynthGate.WaitAsync(ct).ConfigureAwait(false);

        // «Detener» durante la espera solo cancelaba la síntesis ya en curso: la
        // lectura encolada arrancaba después y sonaba igual. Si el epoch cambió,
        // esta lectura ya no debe empezar.
        if (epoch != Volatile.Read(ref _stopEpoch) || ct.IsCancellationRequested)
        {
            SynthGate.Release();
            return;
        }

        string wav = Path.Combine(Path.GetTempPath(), "powertools-tts-" + Guid.NewGuid().ToString("N") + ".wav");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            _speakCts = cts;

            var synth = EnsureSynth();
            synth.Options.SpeakingRate = ClampRate(rate);

            using (SpeechSynthesisStream stream = await synth.SynthesizeTextToStreamAsync(text).AsTask().ConfigureAwait(false))
            {
                await WriteStreamAsync(stream, wav, cts.Token).ConfigureAwait(false);
            }

            // «Detener» durante la síntesis marca la cancelación: no se reproduce
            // lo ya generado (antes sonaba igual porque Stop solo paraba el audio).
            cts.Token.ThrowIfCancellationRequested();

            var player = new SoundPlayer(wav);
            lock (PlayerGate)
            {
                _player?.Dispose();
                _player = player;
            }
            try { player.PlaySync(); }
            finally
            {
                lock (PlayerGate)
                {
                    if (ReferenceEquals(_player, player)) _player = null;
                }
                try { player.Dispose(); } catch { }
            }
        }
        finally
        {
            if (ReferenceEquals(_speakCts, cts)) _speakCts = null;
            cts.Dispose();
            // El reproductor ya está cerrado (PlaySync terminó o se paró): el WAV
            // temporal se borra siempre, también si la síntesis falló a medias.
            try { if (File.Exists(wav)) File.Delete(wav); } catch { }
            SynthGate.Release();
        }
    }

    /// <summary>Corta la reproducción actual (si hay) y anula la lectura pendiente.</summary>
    public static void Stop()
    {
        // Invalida también las lecturas que aún esperan turno en SynthGate.
        Interlocked.Increment(ref _stopEpoch);
        try { _speakCts?.Cancel(); } catch { }
        try
        {
            SoundPlayer? player;
            lock (PlayerGate) player = _player;
            player?.Stop();
        }
        catch (Exception ex) { Logger.Warn("stop TTS: " + ex.Message); }
    }

    private static SpeechSynthesizer EnsureSynth()
    {
        if (_synth != null) return _synth;
        var synth = new SpeechSynthesizer();
        var match = PickVoice();
        if (match != null) synth.Voice = match;
        _synth = synth;
        return synth;
    }

    /// <summary>Voz del idioma de la interfaz, o null para dejar la predeterminada.</summary>
    private static VoiceInformation? PickVoice()
    {
        var all = SpeechSynthesizer.AllVoices;
        if (all.Count == 0) return null;

        // Preferir el idioma de la interfaz; si no, la voz por defecto.
        string lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return all.FirstOrDefault(v =>
            !string.IsNullOrEmpty(v.Language) &&
            v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>SpeakingRate documentado: 0,5 (lento) a 3,0 (rápido); 1,0 = normal.</summary>
    private static double ClampRate(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate)) return 1.0;
        return Math.Clamp(rate, 0.5, 3.0);
    }

    /// <summary>Copia un SpeechSynthesisStream a disco sin depender de AsStreamForRead.</summary>
    private static async Task WriteStreamAsync(SpeechSynthesisStream stream, string path, CancellationToken ct)
    {
        ulong size = stream.Size;
        if (size == 0 || size > int.MaxValue)
            throw new InvalidOperationException("La síntesis de voz devolvió un tamaño inválido (" + size + " bytes).");

        int length = (int)size;
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)length).AsTask(ct).ConfigureAwait(false);

        var buffer = new byte[length];
        reader.ReadBytes(buffer);

        using var file = File.Create(path);
        await file.WriteAsync(buffer, ct).ConfigureAwait(false);
    }
}
