using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Selbar.Core.Platform;
using Selbar.Native;

namespace Selbar.Core.Selection;

/// <summary>Resultado de una extracción: texto y (si UIA lo sabe) rectángulo de la selección.</summary>
public sealed class SelectionResult
{
    public string Text { get; set; } = "";
    public SelectionAnchor Anchor { get; set; }
    public bool FromUia { get; set; }
}

/// <summary>Cómo funciona mejor la extracción en una app concreta.</summary>
public enum SelectionMethod
{
    Unknown,
    Uia,
    Clipboard
}

/// <summary>
/// Extrae el texto seleccionado: 1) UI Automation, 2) fallback portapapeles (Ctrl+C).
/// UIA no lo cubre todo (Word, Firefox...), por eso el fallback es imprescindible.
///
/// Aprende por proceso: si en una app UIA no devuelve nada y el portapapeles sí,
/// a la segunda vez va directo al portapapeles con un UIA corto (solo para el
/// ancla). Eso recorta hasta ~800 ms en las apps problemáticas.
/// </summary>
public static class TextExtractor
{
    private const int UiaTimeoutMs = 800;
    private const int UiaQuickTimeoutMs = 220;
    /// <summary>
    /// Tope de consultas UIA simultáneas que pueden quedar colgadas. Un proveedor
    /// que no responde deja el hilo de RunMta bloqueado para siempre aunque el
    /// llamante reciba el fallback por timeout; sin tope, cada gesto nuevo añadía
    /// otro hilo (y su árbol COM) al proceso. Con el tope las siguientes consultas
    /// fallan rápido y el servicio se recupera cuando el proveedor contesta.
    /// </summary>
    private const int MaxUiaThreads = 3;
    private static int _uiaInFlight;

    /// <summary>Serializa el ciclo Clear → Ctrl+C → restaurar del portapapeles.</summary>
    private static int _clipboardBusy;
    private static long _clipboardBusySince;

    /// <summary>Estadísticas por proceso (en memoria, por sesión).</summary>
    private static readonly ConcurrentDictionary<string, (SelectionMethod Method, int Strikes)> Methods = new();

    /// <summary>
    /// ¿El foco está en un campo de contraseña? En ese caso no se extrae nada:
    /// ni mostrar, ni copiar, ni enviar a la IA.
    /// </summary>
    public static bool IsPasswordField()
        => RunMta(() => IsPasswordFieldCore(), UiaTimeoutMs, false);

    private static bool IsPasswordFieldCore()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return false;
            return (bool)focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
        }
        catch { return false; }
    }

    /// <summary>Texto seleccionado vía UIA (no toca el portapapeles). "" si no hay.</summary>
    public static string TryGetViaUia() => UiaSelection(UiaTimeoutMs).Text;

    /// <summary>Selección actual completa (texto + ancla), sin tocar el portapapeles.</summary>
    public static SelectionResult TryGetViaUiaFull() => UiaSelection(UiaTimeoutMs);

    /// <summary>
    /// Selección actual: UIA primero y, si no hay nada, Ctrl+C con restauración.
    /// Usa la caché por proceso para elegir el orden y el timeout.
    /// </summary>
    public static SelectionResult GetSelection(bool allowClipboardFallback = true, IntPtr expectedForeground = default)
    {
        var app = SafeForegroundApp();

        // Ventana de la que se va a copiar. Si el llamante la capturó al hacer el
        // gesto, se respeta; si no, se toma ahora. El Ctrl+C se comprobará contra
        // ella para no inyectarlo en otra app si el foco cambia durante la espera.
        IntPtr owner = expectedForeground;
        if (owner == IntPtr.Zero)
        {
            try { owner = NativeMethods.GetForegroundWindow(); } catch { }
        }

        var (method, strikes) = Methods.TryGetValue(app, out var hit) ? hit : (SelectionMethod.Unknown, 0);

        bool clipboardFirst = method == SelectionMethod.Clipboard;
        int uiaTimeout = clipboardFirst ? UiaQuickTimeoutMs : UiaTimeoutMs;

        var uia = UiaSelection(uiaTimeout);
        if (!string.IsNullOrWhiteSpace(uia.Text))
        {
            Remember(app, SelectionMethod.Uia, 0);
            uia.Text = uia.Text.Trim();
            return uia;
        }

        if (!allowClipboardFallback)
            return new SelectionResult { Anchor = uia.Anchor };

        if (IsPasswordField()) return new SelectionResult();

        var text = CopyViaClipboardFallback(expectedForeground: owner);
        if (!string.IsNullOrWhiteSpace(text) && app.Length > 0)
        {
            // UIA no vio nada y el portapapeles sí: esta app necesita el fallback.
            int nextStrikes = strikes + 1;
            Remember(app, nextStrikes >= 2 ? SelectionMethod.Clipboard : method, nextStrikes);
        }

        return new SelectionResult { Text = text.Trim(), Anchor = uia.Anchor };
    }

    /// <summary>Compatibilidad: solo el texto.</summary>
    public static string GetSelectedText(bool allowClipboardFallback = true)
        => GetSelection(allowClipboardFallback).Text;

    /// <summary>Método aprendido para un proceso ("uia"/"clipboard"/"") — diagnóstico.</summary>
    public static string MethodFor(string app) =>
        Methods.TryGetValue(app ?? "", out var hit) ? hit.Method.ToString().ToLowerInvariant() : "";

    private static void Remember(string app, SelectionMethod method, int strikes)
    {
        if (string.IsNullOrEmpty(app)) return;
        Methods[app] = (method, Math.Max(0, Math.Min(3, strikes)));
    }

    private static string SafeForegroundApp()
    {
        try { return NativeMethods.ForegroundProcessName(); }
        catch { return ""; }
    }

    // ---------------- UIA ----------------

    private static SelectionResult UiaSelection(int timeoutMs)
        => RunMta(UiaSelectionCore, timeoutMs, new SelectionResult());

    private static SelectionResult UiaSelectionCore()
    {
        try
        {
            if (IsPasswordFieldCore()) return new SelectionResult();
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return new SelectionResult();

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern tp)
                return new SelectionResult();
            if (tp.SupportedTextSelection == SupportedTextSelection.None) return new SelectionResult();

            var ranges = tp.GetSelection();
            if (ranges.Length == 0) return new SelectionResult();

            var text = new StringBuilder();
            var anchor = SelectionAnchor.Empty;

            foreach (var range in ranges)
            {
                var piece = range.GetText(-1)?.Trim();
                if (!string.IsNullOrEmpty(piece)) text.AppendLine(piece);
                if (anchor.IsEmpty) anchor = BoundsOf(range);
            }

            return new SelectionResult { Text = text.ToString().Trim(), Anchor = anchor, FromUia = true };
        }
        catch { return new SelectionResult(); }
    }

    /// <summary>Une los rectángulos de la selección (coordenadas de pantalla, píxeles físicos).</summary>
    private static SelectionAnchor BoundsOf(TextPatternRange range)
    {
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects == null || rects.Length == 0) return SelectionAnchor.Empty;

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var rect in rects)
            {
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                left = Math.Min(left, rect.Left);
                top = Math.Min(top, rect.Top);
                right = Math.Max(right, rect.Right);
                bottom = Math.Max(bottom, rect.Bottom);
            }

            if (left == double.MaxValue) return SelectionAnchor.Empty;
            return new SelectionAnchor(left, top, right - left, bottom - top);
        }
        catch { return SelectionAnchor.Empty; }
    }

    // ---------------- Portapapeles ----------------

    /// <summary>
    /// Copia con Ctrl+C preservando TODO el clipboard original (texto, archivos, imágenes).
    /// Acepta llamarse desde cualquier hilo: si no es STA se ejecuta en uno propio
    /// (el portapapeles lo exige; llamarlo desde el hilo del hook o un MTA fallaba).
    /// Si el usuario copia algo durante la espera, se respeta lo nuevo y no se
    /// restaura lo viejo (evita destruir un copiado ajeno).
    /// <paramref name="expectedForeground"/>, si se indica, es la ventana donde se
    /// hizo la selección: si el foco ya no es esa, no se toca el portapapeles.
    /// </summary>
    public static string CopyViaClipboardFallback(int waitMs = 900, IntPtr expectedForeground = default)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            string bridged = "";
            var sta = new Thread(() => { bridged = CopyViaClipboardFallback(waitMs, expectedForeground); });
            sta.SetApartmentState(ApartmentState.STA);
            sta.IsBackground = true;
            sta.Start();
            return sta.Join(waitMs + 4000) ? bridged : "";
        }

        // Un solo ciclo de portapapeles a la vez: dos gestos seguidos lanzaban dos
        // Clear/Ctrl+C/restauración solapados y podían devolver el texto del otro.
        if (Interlocked.CompareExchange(ref _clipboardBusy, 1, 0) != 0)
        {
            // Si el dueño lleva demasiado tiempo, se asume un hilo colgado en OLE
            // y se recupera el turno; si no, se descarta este gesto.
            long since = Interlocked.Read(ref _clipboardBusySince);
            if (since != 0 && Environment.TickCount64 - since < 10_000) return "";
            Interlocked.Exchange(ref _clipboardBusy, 1);
        }
        Interlocked.Exchange(ref _clipboardBusySince, Environment.TickCount64);

        try
        {
            IntPtr owner = expectedForeground;
            if (owner == IntPtr.Zero)
            {
                try { owner = NativeMethods.GetForegroundWindow(); } catch { }
            }
            // Si el foco cambió antes de empezar, el Ctrl+C iría a otra app (en una
            // terminal es Ctrl+C = interrumpir): no tocar el portapapeles.
            if (owner != IntPtr.Zero && NativeMethods.GetForegroundWindow() != owner) return "";

            if (IsPasswordField()) return "";

            // Si el usuario copia algo mientras esperamos, su copia manda: no se
            // lee como selección ni se restaura el portapapeles encima.
            long userWrites = ClipboardArbiter.UserWriteCount;

            // Copia materializada del portapapeles: Clipboard.GetDataObject()
            // devuelve un proxy COM del proceso dueño y el Clear() de abajo (o su
            // cierre) lo dejaba sin datos, así que la restauración no restauraba nada.
            var saved = CloneClipboardData();
            string savedText = ClipboardTextOf(saved);

            if (ClipboardArbiter.UserWriteCount != userWrites) return "";

            bool cleared = false;
            try { System.Windows.Clipboard.Clear(); cleared = true; } catch { }
            if (!cleared)
            {
                // Sin vaciar no se puede distinguir la copia nueva de lo que ya
                // había: antes se devolvía el texto viejo como si fuera la
                // selección. El portapapeles no se ha tocado, no hay nada que
                // restaurar.
                return "";
            }
            // El foco pudo cambiar mientras se clonaba el portapapeles.
            if (owner != IntPtr.Zero && NativeMethods.GetForegroundWindow() != owner)
            {
                RestoreClipboard(saved, savedText, "", []);
                return "";
            }
            if (!NativeMethods.SendCtrlC())
            {
                // Sin inyección no hay nada que esperar: devolver el portapapeles ya.
                RestoreClipboard(saved, savedText, "", []);
                return "";
            }

            // Polling en vez de espera fija: apps lentas (Word, navegadores) tardan
            // más de 300 ms; salimos en cuanto hay texto (máx ~900 ms).
            string result = "";
            string[] copiedFormats = [];
            int waited = 0;
            while (waited < waitMs && result.Length == 0)
            {
                Thread.Sleep(60);
                waited += 60;
                // El usuario pulsó Copiar: lo que haya ahora no es nuestra copia.
                if (ClipboardArbiter.UserWriteCount != userWrites) break;
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        result = (System.Windows.Clipboard.GetText() ?? "").Trim();
                        if (result.Length > 0) copiedFormats = CurrentFormats();
                    }
                }
                catch { }

                // La copia del usuario pudo colarse entre la comprobación y la
                // lectura: no devolver ese texto como si fuera la selección.
                if (result.Length > 0 && ClipboardArbiter.UserWriteCount != userWrites)
                {
                    result = "";
                    copiedFormats = [];
                    break;
                }
            }

            // Ctrl+C también puede copiar archivos o una imagen (Explorer, vista
            // previa): sin texto, los formatos presentes son la única prueba de
            // que la copia existió. Antes se dejaba el portapapeles con esos datos
            // y se perdía el contenido original del usuario.
            if (result.Length == 0) copiedFormats = CurrentFormats();

            bool userCopied = ClipboardArbiter.UserWriteCount != userWrites;
            // La copia del usuario no se toca: ni se restaura lo viejo encima.
            if (!userCopied) RestoreClipboard(saved, savedText, result, copiedFormats);
            if (userCopied && result.Length == 0) return "";
            return result;
        }
        catch { return ""; }
        finally { Volatile.Write(ref _clipboardBusy, 0); }
    }

    /// <summary>¿Es un formato de portapapeles puramente textual?</summary>
    private static bool IsClipboardTextFormat(string format) =>
        string.Equals(format, System.Windows.DataFormats.Text, StringComparison.Ordinal)
        || string.Equals(format, System.Windows.DataFormats.UnicodeText, StringComparison.Ordinal)
        || string.Equals(format, System.Windows.DataFormats.OemText, StringComparison.Ordinal)
        || string.Equals(format, System.Windows.DataFormats.Locale, StringComparison.Ordinal);

    /// <summary>Texto de un DataObject ya capturado ("" si no hay o falla).</summary>
    private static string ClipboardTextOf(System.Windows.IDataObject? obj)
    {
        try
        {
            if (obj == null) return "";
            var formats = obj.GetFormats() ?? [];
            if (!Array.Exists(formats, IsClipboardTextFormat)) return "";
            return ((obj.GetData(System.Windows.DataFormats.UnicodeText) as string)
                ?? (obj.GetData(System.Windows.DataFormats.Text) as string)
                ?? "").Trim();
        }
        catch { return ""; }
    }

    private static string[] SafeFormats(System.Windows.IDataObject? obj)
    {
        try { return obj?.GetFormats() ?? []; }
        catch { return []; }
    }

    private static string[] CurrentFormats()
    {
        try { return System.Windows.Clipboard.GetDataObject()?.GetFormats() ?? []; }
        catch { return []; }
    }

    /// <summary>
    /// Copia en el acto el contenido del portapapeles a un DataObject propio.
    /// Sin esto, el objeto que devuelve el portapapeles es un proxy del proceso
    /// dueño: tras Clipboard.Clear() ya no puede renderizar nada y la
    /// restauración se quedaba en un portapapeles vacío (o solo con el texto).
    /// </summary>
    private static System.Windows.DataObject? CloneClipboardData()
    {
        try
        {
            var source = System.Windows.Clipboard.GetDataObject();
            if (source == null) return null;

            var clone = new System.Windows.DataObject();
            foreach (var format in source.GetFormats() ?? [])
            {
                if (string.IsNullOrEmpty(format)) continue;
                // "Serializable" obliga a deserializar un objeto .NET ajeno
                // (BinaryFormatter) solo para copiarlo: no se toca.
                if (string.Equals(format, System.Windows.DataFormats.Serializable, StringComparison.Ordinal)) continue;
                try
                {
                    // autoConvert: pide la conversión ahora, mientras el dueño vive.
                    var data = source.GetData(format, true);
                    if (data == null) continue;
                    clone.SetData(format, Snapshot(data));
                }
                catch { /* formato que el dueño no puede renderizar: se omite */ }
            }
            return clone.GetFormats().Length > 0 ? clone : null;
        }
        catch { return null; }
    }

    /// <summary>Materializa un dato para que no dependa del proceso que lo puso.</summary>
    private static object Snapshot(object data)
    {
        try
        {
            switch (data)
            {
                case System.IO.MemoryStream ms:
                    return new System.IO.MemoryStream(ms.ToArray());
                case System.Windows.Media.Imaging.BitmapSource bmp:
                    var copy = bmp.Clone();
                    copy.Freeze();
                    return copy;
                case System.IO.Stream stream:
                    var buffer = new System.IO.MemoryStream();
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    return buffer;
                default:
                    return data;
            }
        }
        catch { return data; }
    }

    /// <summary>
    /// ¿El portapapeles sigue conteniendo exactamente la copia que hicimos
    /// nosotros? Se compara con el texto y los formatos que dejó nuestro Ctrl+C:
    /// si algo cambió, lo puso el usuario o otra app.
    /// </summary>
    private static bool IsStillOurCopy(string currentText, string[] currentFormats,
        string result, string[] copiedFormats)
    {
        // Sin foto de formatos de nuestra copia: si no hubo texto, solo es nuestra
        // cuando nadie escribió nada tras el Clear; si lo hubo, basta el texto.
        if (copiedFormats.Length == 0)
        {
            return result.Length == 0
                ? currentText.Length == 0 && currentFormats.Length == 0
                // Sin foto de formatos (no se pudieron leer): decidir por el texto.
                : string.Equals(currentText, result, StringComparison.Ordinal);
        }

        if (currentFormats.Length != copiedFormats.Length) return false;

        foreach (var format in currentFormats)
        {
            if (!Array.Exists(copiedFormats, c => string.Equals(c, format, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Copia sin texto (archivos, imagen): los formatos coinciden, es la nuestra.
        if (result.Length == 0) return true;

        return currentText.Length > 0 && string.Equals(currentText, result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deja el portapapeles como estaba: restaura lo guardado y, si el objeto
    /// ya no responde, al menos el texto. Nunca destruye un copiado ajeno hecho
    /// durante la espera.
    /// </summary>
    private static void RestoreClipboard(
        System.Windows.DataObject? saved, string savedText, string result, string[] copiedFormats)
    {
        try
        {
            System.Windows.IDataObject? current = null;
            try { current = System.Windows.Clipboard.GetDataObject(); } catch { }
            string currentText = ClipboardTextOf(current);
            string[] currentFormats = SafeFormats(current);

            // Si el portapapeles ya no es nuestra copia, es del usuario: no tocarlo.
            // (Antes cualquier formato "rico" —el HTML que añade Chrome al copiar—
            // contaba como ajeno y la restauración no ocurría nunca.)
            if (!IsStillOurCopy(currentText, currentFormats, result, copiedFormats))
                return;

            // Nada que restaurar (no se pudo leer el original): no destruir la
            // copia recién hecha, dejarla como está.
            if (saved == null && savedText.Length == 0)
                return;

            try { System.Windows.Clipboard.Clear(); } catch { }
            if (saved != null)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(saved, true);
                    return;
                }
                catch { /* el objeto guardado murió: plan B con el texto */ }
            }
            if (savedText.Length > 0)
            {
                try { System.Windows.Clipboard.SetText(savedText); } catch { }
            }
        }
        catch { }
    }

    // ---------------- Hilo MTA con timeout ----------------

    private static T RunMta<T>(Func<T> work, int timeoutMs, T fallback)
    {
        // Un proveedor de UIA que no responde deja este hilo bloqueado para siempre
        // (no hay cancelación para las llamadas COM de UIA). El tope evita que
        // gestos sucesivos acumulen hilos colgados; se libera al terminar de verdad.
        if (Interlocked.Increment(ref _uiaInFlight) > MaxUiaThreads)
        {
            Interlocked.Decrement(ref _uiaInFlight);
            return fallback;
        }

        T result = fallback;
        using var done = new ManualResetEventSlim(false);

        Thread thread;
        try
        {
            thread = new Thread(() =>
            {
                try { result = work(); }
                catch { }
                finally
                {
                    Interlocked.Decrement(ref _uiaInFlight);
                    try { done.Set(); } catch { /* ya liberado: la app se está cerrando */ }
                }
            })
            { IsBackground = true, Name = "Selbar-UIA" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        catch
        {
            // No se llegó a crear/arrancar el hilo: liberar el cupo o el tope
            // acabaría desactivando UIA para el resto de la sesión.
            Interlocked.Decrement(ref _uiaInFlight);
            return fallback;
        }

        return done.Wait(timeoutMs) ? result : fallback;
    }
}
