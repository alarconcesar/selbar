using System.Threading;
using Selbar.Native;

namespace Selbar.Core.Platform;

/// <summary>
/// Recuerda la ventana que tenía el foco antes de mostrar un popup y sabe
/// devolvérselo. Es lo que hace que "Reemplazar" (Ctrl+V) pegue en la app de
/// origen y no en nuestras ventanas.
/// </summary>
public static class ForegroundTracker
{
    private static readonly object Gate = new();
    private static IntPtr _last;
    private static uint _lastPid;
    private static long _lastStartTicks;
    private static DateTime _lastAt = DateTime.MinValue;

    /// <summary>
    /// Cuánto vale un recuerdo. Antes eran 5 minutos y "Reemplazar" fallaba en
    /// cuanto el usuario se quedaba leyendo un resultado largo; ahora que la
    /// identidad de la ventana se comprueba (PID + arranque del proceso) se puede
    /// alargar sin riesgo de pegar en una ventana reciclada.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    /// <summary>Ventana recordada (IntPtr.Zero si no hay, caducó o ya no existe).</summary>
    public static IntPtr Last
    {
        get
        {
            lock (Gate)
            {
                if (_last == IntPtr.Zero || DateTime.UtcNow - _lastAt > MaxAge) return IntPtr.Zero;
                return NativeMethods.IsWindow(_last) ? _last : IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Olvida la ventana recordada. Con <paramref name="only"/> solo la borra si
    /// sigue siendo esa: entre la lectura y el fallo otra captura pudo guardar una
    /// ventana nueva y no se debe perder.
    /// </summary>
    public static void Forget(IntPtr only = default)
    {
        lock (Gate)
        {
            if (only != IntPtr.Zero && _last != only) return;
            _last = IntPtr.Zero;
            _lastPid = 0;
            _lastStartTicks = 0;
            _lastAt = DateTime.MinValue;
        }
    }

    /// <summary>Guarda la ventana en primer plano actual. Llamar justo antes de abrir un popup.</summary>
    public static void Capture()
    {
        try
        {
            var h = NativeMethods.GetForegroundWindow();
            if (h == IntPtr.Zero || !NativeMethods.IsWindow(h)) return;
            if (IsOwnWindow(h)) return; // nunca recordar nuestras propias ventanas
            uint thread = NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            // 0 = la ventana murió entre IsWindow y la consulta; el out queda en 0
            // y ProcessStartTicks(0)/GetProcessById(0) no deben ejecutarse.
            if (thread == 0 || pid == 0) return;
            long started = ProcessStartTicks(pid);
            lock (Gate)
            {
                _last = h;
                _lastPid = pid;
                _lastStartTicks = started;
                _lastAt = DateTime.UtcNow;
            }
        }
        catch { }
    }

    /// <summary>
    /// ¿Sigue ese HWND perteneciendo al proceso capturado? Windows reutiliza
    /// valores de HWND: sin esta comprobación se podría devolver el foco (y
    /// pegar) en una ventana ajena que heredó el número.
    /// </summary>
    public static bool IsSameOwner(uint capturedPid, IntPtr hwnd)
    {
        if (capturedPid == 0 || hwnd == IntPtr.Zero) return false;
        try
        {
            uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return thread != 0 && pid != 0 && pid == capturedPid;
        }
        catch { return false; }
    }

    /// <summary>
    /// Identidad completa: mismo proceso y, si se pudo leer, mismo instante de
    /// arranque. Windows también reutiliza PIDs; sin esto un PID reciclado por
    /// otra app pasaba la comprobación y se pegaba en ella.
    /// </summary>
    public static bool IsSameProcess(uint capturedPid, long capturedStartTicks, IntPtr hwnd)
    {
        if (!IsSameOwner(capturedPid, hwnd)) return false;
        if (capturedStartTicks == 0) return true; // no se pudo leer el arranque: basta el PID
        long started = ProcessStartTicks(capturedPid);
        if (started == 0) return true;            // ya no se puede leer: no empeorar el caso
        // Tolerancia de 2 s por redondeos de zona horaria al comparar DateTime.
        return Math.Abs(started - capturedStartTicks) <= TimeSpan.FromSeconds(2).Ticks;
    }

    /// <summary>Devuelve el foco a la ventana recordada. Devuelve true si lo consiguió.</summary>
    public static bool Restore(int settleMs = 60)
    {
        IntPtr h;
        uint pid;
        long started;
        lock (Gate)
        {
            h = _last;
            pid = _lastPid;
            started = _lastStartTicks;
            if (h == IntPtr.Zero || DateTime.UtcNow - _lastAt > MaxAge) return false;
        }
        try
        {
            // La ventana ya no existe (la app de origen se cerró): se olvida para
            // no reintentar un HWND muerto en cada «Reemplazar».
            if (!NativeMethods.IsWindow(h)) { Forget(h); return false; }
            // El HWND puede haberse reciclado (otra ventana, otro proceso o un PID
            // reutilizado): enfocar a ciegas pegaría el texto en el sitio equivocado.
            if (!IsSameProcess(pid, started, h)) { Forget(h); return false; }
            if (NativeMethods.GetForegroundWindow() == h) return true;
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);

            var fg = NativeMethods.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(h, out _);
            // La ventana pudo cerrarse entre IsWindow y esta consulta: sin hilo no
            // hay nada que enfocar y el HWND ya no sirve.
            if (targetThread == 0) { Forget(h); return false; }
            bool attach = fgThread != 0 && fgThread != targetThread;

            if (attach) NativeMethods.AttachThreadInput(fgThread, targetThread, true);
            try
            {
                NativeMethods.SetForegroundWindow(h);
                NativeMethods.BringWindowToTop(h);
            }
            finally
            {
                // Si no se suelta, las colas de entrada quedan unidas y el
                // teclado se comporta de forma errática en ambas apps.
                if (attach) NativeMethods.AttachThreadInput(fgThread, targetThread, false);
            }

            if (settleMs > 0) Thread.Sleep(settleMs);

            // SetForegroundWindow puede devolver true y aun así no dejar el foco
            // donde pedimos. Se comprueba quién lo tiene de verdad: pegar sin
            // esta comprobación era pegar a ciegas.
            var now = NativeMethods.GetForegroundWindow();
            if (now == h) return true;
            // Algunas apps activan otra ventana suya (p. ej. el marco principal):
            // pegar ahí sigue siendo pegar en la app de origen.
            return now != IntPtr.Zero && IsSameOwner(pid, now);
        }
        catch { return false; }
    }

    /// <summary>¿La ventana pertenece a nuestro proceso?</summary>
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            // Si la consulta falla, pid queda en 0: nunca coincide con el PID propio
            // (siempre > 0), pero se hace explícito para no depender de ello.
            return thread != 0 && pid != 0 && pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    /// <summary>Nombre del proceso dueño de una ventana ("" si no se puede saber).</summary>
    public static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            // GetProcessById(0) lanza ArgumentException; no llegar ahí.
            if (thread == 0 || pid == 0) return "";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Arranque del proceso en ticks UTC (0 si no se puede leer).</summary>
    private static long ProcessStartTicks(uint pid)
    {
        if (pid == 0) return 0;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch { return 0; }
    }
}
