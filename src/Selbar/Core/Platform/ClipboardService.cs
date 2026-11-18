using System.Threading;
using Selbar.Core.History;

namespace Selbar.Core.Platform;

/// <summary>
/// Acceso al portapapeles "a prueba de balas". Otro proceso puede tenerlo
/// bloqueado (CLIPBRD_E_CANT_OPEN) y WPF lanza una excepción; como muchas
/// llamadas ocurren en hilos propios, una excepción suelta tumbaría la app.
/// Aquí se reintenta con espera creciente y nunca se lanza.
/// </summary>
public static class ClipboardService
{
    private const int DefaultAttempts = 6;
    private const int RetryDelayMs = 40;
    /// <summary>Tope de espera para una operación en un hilo STA propio.</summary>
    private const int StaTimeoutMs = 2000;
    /// <summary>
    /// Presupuesto total de reintentos. El bucle corre en el hilo llamante (casi
    /// siempre la UI) y WPF ya reintenta por dentro ante CLIPBRD_E_CANT_OPEN:
    /// sin tope, un portapapeles retenido por otra app congelaba la ventana
    /// varios segundos (6 intentos × reintentos internos + esperas).
    /// </summary>
    private const int RetryBudgetMs = 1500;

    /// <summary>Copia texto al portapapeles (STA + reintentos). Devuelve false si no pudo.</summary>
    public static bool TrySetText(string? text, int attempts = DefaultAttempts)
    {
        // Vacío no se copia: SetText("") lanza y, peor, registrar "" en el
        // historial ensucia la lista con entradas inútiles.
        if (string.IsNullOrEmpty(text)) return false;
        if (attempts < 1) attempts = 1;

        // Avisa a TextExtractor: si hay una extracción automática en curso, que
        // deje de esperar/no lea esto como selección ni lo pise al restaurar.
        ClipboardArbiter.NotifyUserWrite();

        long started = Environment.TickCount64;
        int tried = 0;
        for (int i = 0; i < attempts; i++)
        {
            tried++;
            bool ok = OnSta(() =>
            {
                try { System.Windows.Clipboard.SetText(text); return true; }
                catch { return false; }
            }, false);
            if (ok)
            {
                try { ClipboardHistoryStore.Add(text); } catch { }
                return true;
            }
            if (i >= attempts - 1) break;
            long elapsed = Environment.TickCount64 - started;
            if (elapsed >= RetryBudgetMs) break;
            int delay = (int)Math.Min(RetryDelayMs * (i + 1), RetryBudgetMs - elapsed);
            if (delay > 0) Thread.Sleep(delay);
        }
        // Antes fallaba en silencio: el usuario veía «no se pudo copiar» y el log
        // no decía nada. Sin volcar el texto (puede ser privado), solo el tamaño.
        Diagnostics.Logger.Warn($"portapapeles: no se pudo copiar tras {tried} intento(s) ({text.Length} caracteres)");
        return false;
    }

    /// <summary>Lee texto del portapapeles, o null si no hay texto o está bloqueado.</summary>
    public static string? TryGetText()
        => OnSta(() =>
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText()) return null;
                var text = System.Windows.Clipboard.GetText();
                // GetText devuelve "" cuando el portapapeles tiene texto vacío:
                // se normaliza a null para que ningún llamante actúe sobre "".
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch { return null; }
        }, null);

    /// <summary>True si el portapapeles contiene algo que sea texto.</summary>
    public static bool ContainsText()
        => OnSta(() =>
        {
            try { return System.Windows.Clipboard.ContainsText(); }
            catch { return false; }
        }, false);

    /// <summary>True si el portapapeles contiene una imagen.</summary>
    public static bool ContainsImage()
        => OnSta(() =>
        {
            try { return System.Windows.Clipboard.ContainsImage(); }
            catch { return false; }
        }, false);

    /// <summary>Imagen del portapapeles codificada como PNG, o null si no hay.</summary>
    public static byte[]? TryGetImagePng()
        => OnSta<byte[]?>(() =>
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage()) return null;
                var source = System.Windows.Clipboard.GetImage();
                if (source == null) return null;

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                using var ms = new System.IO.MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }, null);

    /// <summary>Ejecuta una acción en un hilo STA (el portapapeles lo exige) con tope de espera.</summary>
    public static bool RunSta(Action action)
    {
        if (action == null) return false;
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { action(); return true; } catch (Exception ex) { Diagnostics.Logger.LogEx(ex, "RunSta"); return false; }
        }

        bool ok = false;
        var t = new Thread(() =>
        {
            try { action(); ok = true; } catch (Exception ex) { Diagnostics.Logger.LogEx(ex, "RunSta"); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (t.Join(StaTimeoutMs)) return ok;
        Diagnostics.Logger.Warn("portapapeles: el hilo STA no terminó a tiempo (¿otra app lo retiene?)");
        return false;
    }

    private static T OnSta<T>(Func<T> func, T fallback)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { return func(); } catch { return fallback; }
        }

        T result = fallback;
        var t = new Thread(() =>
        {
            try { result = func(); } catch { }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (t.Join(StaTimeoutMs)) return result;
        // Si se agota el tope, el hilo sigue vivo y aún podría tocar el
        // portapapeles: queda constancia en el log en vez de fallar a ciegas.
        Diagnostics.Logger.Warn("portapapeles: la operación STA superó " + StaTimeoutMs + " ms");
        return fallback;
    }
}
