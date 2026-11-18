using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Native;

namespace Selbar.Core.Selection;

/// <summary>
/// Vigila ratón/teclado global mediante hooks de bajo nivel.
/// Selección: drag+soltar y doble-click usan extracción completa (UIA + Ctrl+C);
/// click simple, Shift+teclas y long-press solo UIA (nunca tocan el portapapeles).
/// Descarte: cualquier click fuera, clic derecho, rueda o Esc emiten DismissRequested
/// (la App decide si el click cayó en un popup propio).
///
/// Importante: un hook de bajo nivel que tarda más de ~300 ms o que lanza una
/// excepción hace que Windows lo elimine (o tumba el proceso), así que aquí
/// todo va envuelto y el trabajo pesado se hace en segundo plano.
/// </summary>
public sealed class SelectionMonitor : IDisposable
{
    /// <summary>Selección lista: texto + rectángulo de la selección (vacío si no se pudo).</summary>
    public event Action<string, SelectionAnchor>? SelectionReady;
    public event Action? DismissRequested;
    /// <summary>Esc pulsado (para que la barra pueda limpiar la búsqueda antes de cerrar).</summary>
    public event Action? EscapePressed;
    /// <summary>Dígito 1-9 pulsado: la App lo reenvía a la barra si está abierta.</summary>

    /// <summary>
    /// Si devuelve true, la tecla se consume y no llega a la app de origen.
    /// Lo usa la barra para el modo "escribe para filtrar". Se llama desde el
    /// hilo del hook: debe ser rápido.
    /// </summary>
    public Func<int, bool>? KeyFilter { get; set; }

    private readonly TriggerConfig _cfg;
    private readonly Func<bool>? _isOwnPopup;

    private IntPtr _mouseHook, _kbHook;
    private NativeMethods.LowLevelProc? _mouseProc, _kbProc;

    private volatile bool _leftDown;
    private volatile bool _mouseMoved;
    private bool _downOnPopup;
    private (int X, int Y) _downPos;
    private DateTime _lastClickUp = DateTime.MinValue;
    private (int X, int Y) _lastClickPos;
    /// <summary>Throttle entre gestos, en ms de Environment.TickCount64 (monótono).
    /// Interlocked: la comprobación y la escritura forman un solo paso atómico.</summary>
    private long _lastFireTickMs;
    private int _pressGeneration;
    private bool _disposed;

    private const int DragThreshold = 6;
    private const int WM_SYSKEYDOWN = 0x0104;

    public SelectionMonitor(TriggerConfig cfg, Func<bool>? isOwnPopup = null)
    {
        _cfg = cfg ?? new TriggerConfig();
        _isOwnPopup = isOwnPopup;
    }

    public void Start()
    {
        if (_disposed) return;
        try
        {
            // Llamar dos veces sin Dispose goteaba hooks: el handle viejo se
            // perdía sin Unhook y el proceso acumulaba hooks huérfanos. Se
            // instala solo lo que falta, para que un fallo puntual (p. ej. el
            // teclado bloqueado por seguridad) se reintente en la siguiente llamada.
            if (_mouseHook == IntPtr.Zero)
            {
                _mouseProc = MouseHook;
                var module = NativeMethods.ModuleHandle();
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
                if (_mouseHook == IntPtr.Zero)
                    Logger.Warn($"no se pudo instalar el hook de ratón (Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
            if (_kbHook == IntPtr.Zero)
            {
                _kbProc = KbHook;
                var module = NativeMethods.ModuleHandle();
                _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, module, 0);
                if (_kbHook == IntPtr.Zero)
                    Logger.Warn($"no se pudo instalar el hook de teclado (Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
            if (_mouseHook != IntPtr.Zero || _kbHook != IntPtr.Zero)
                Logger.Log($"hooks listos (ratón={_mouseHook != IntPtr.Zero}, teclado={_kbHook != IntPtr.Zero})");
        }
        catch (Exception ex) { Logger.LogEx(ex, "instalar hooks"); }
    }

    // ---------------- Hook de ratón ----------------

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0) HandleMouse(wParam.ToInt32());
        }
        catch (Exception ex) { Logger.LogEx(ex, "hook ratón"); }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleMouse(int msg)
    {
        if (msg == NativeMethods.WM_LBUTTONDOWN)
        {
            _downOnPopup = IsOwnPopup();
            if (!_downOnPopup) SafeInvoke(DismissRequested);
            _leftDown = true;
            _mouseMoved = false;
            _downPos = NativeMethods.Cursor();
            // La generación se incrementa SIEMPRE: un long-press pendiente de una
            // pulsación anterior no debe dispararse durante esta (también cuando
            // se pulsa un popup propio, que no programa long-press).
            int generation = Interlocked.Increment(ref _pressGeneration);
            // Mantener pulsado un botón de nuestro popup (p. ej. un chip) no es
            // una selección: antes el long-press disparaba UIA y abría otra barra.
            if (!_downOnPopup) WatchLongPress(generation);
            return;
        }

        if (msg == NativeMethods.WM_MOUSEMOVE && _leftDown)
        {
            var c = NativeMethods.Cursor();
            if (Math.Abs(c.X - _downPos.X) + Math.Abs(c.Y - _downPos.Y) > DragThreshold)
                _mouseMoved = true;
            return;
        }

        if (msg == NativeMethods.WM_LBUTTONUP && _leftDown)
        {
            _leftDown = false;

            if (_downOnPopup)
            {
                // Click en un botón del popup: no es una selección nueva, ignorar.
                _downOnPopup = false;
                return;
            }

            if (!_cfg.AutoOnMouseSelection) return;

            // Ctrl+click / Alt+drag (selección múltiple, mover ventanas) no son selección de texto.
            if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) || NativeMethods.IsKeyDown(NativeMethods.VK_MENU))
                return;

            var up = NativeMethods.Cursor();
            bool dragged = Math.Abs(up.X - _downPos.X) + Math.Abs(up.Y - _downPos.Y) > DragThreshold;

            if (dragged)
            {
                // Un arrastre no cuenta para el doble clic: si no, un click
                // anterior al drag podía convertir el siguiente click en
                // "doble" y lanzar el Ctrl+C sin necesidad.
                _lastClickUp = DateTime.MinValue;
                FireAsync(fullFallback: true, NativeMethods.GetForegroundWindow()); // arrastrar para seleccionar
            }
            else
            {
                var now = DateTime.UtcNow;
                bool doubleClick = (now - _lastClickUp).TotalMilliseconds < 500
                    && Math.Abs(up.X - _lastClickPos.X) + Math.Abs(up.Y - _lastClickPos.Y) < 8;
                _lastClickUp = now;
                _lastClickPos = up;

                // Doble-click = palabra seleccionada; click simple: solo UIA, sin tocar el portapapeles.
                FireAsync(fullFallback: doubleClick, NativeMethods.GetForegroundWindow());
            }
            return;
        }

        if (msg == NativeMethods.WM_RBUTTONDOWN || msg == NativeMethods.WM_MBUTTONDOWN ||
            msg == NativeMethods.WM_XBUTTONDOWN || msg == NativeMethods.WM_MOUSEWHEEL ||
            msg == NativeMethods.WM_MOUSEHWHEEL)
            SafeInvoke(DismissRequested);
    }

    // ---------------- Hook de teclado ----------------

    private IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool swallow = false;
        try
        {
            if (nCode >= 0)
                swallow = HandleKeyboard(wParam.ToInt32(), System.Runtime.InteropServices.Marshal.ReadInt32(lParam));
        }
        catch (Exception ex) { Logger.LogEx(ex, "hook teclado"); }

        return swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private bool HandleKeyboard(int msg, int vk)
    {
        bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == WM_SYSKEYDOWN;

        // Modo "escribe para filtrar": la barra decide si la tecla es suya.
        if (isDown && KeyFilter != null)
        {
            try
            {
                if (KeyFilter(vk)) return true;
            }
            catch (Exception ex) { Logger.LogEx(ex, "filtro de teclas"); }
        }

        if (msg == NativeMethods.WM_KEYDOWN)
        {
            if (vk == NativeMethods.VK_ESCAPE)
            {
                SafeInvoke(EscapePressed);
                return false;
            }

            // Los atajos 1-9 los decide App.FilterKeyForBar (conoce la barra y los
            // consume solo en modo teclado): aquí no se hace nada para no duplicar
            // ni escribir el dígito en la app.
            return false;
        }

        // Shift al soltar: selección hecha con el teclado (solo UIA, nunca portapapeles).
        if (msg == NativeMethods.WM_KEYUP && vk == NativeMethods.VK_SHIFT && _cfg.AutoOnKeyboardSelection)
            FireAsync(fullFallback: false, NativeMethods.GetForegroundWindow());

        return false;
    }

    // ---------------- Extracción ----------------

    private void WatchLongPress(int generation)
    {
        var downAt = DateTime.UtcNow;
        int delay = Math.Max(150, _cfg.LongPressMs);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                // Mantener pulsado sin mover en un campo con selección -> mostrar barra (solo UIA).
                if (generation != Volatile.Read(ref _pressGeneration) || !_leftDown || _mouseMoved) return;
                if ((DateTime.UtcNow - downAt).TotalMilliseconds < 150) return;
                if (!_cfg.AutoOnMouseSelection) return;
                // Sin ventana esperada: el botón sigue pulsado y el long-press solo
                // usa UIA (no inyecta Ctrl+C), así que no hay riesgo de copiar de
                // otra app; capturar el foco aquí fallaría al pulsar sobre una
                // ventana inactiva (la activación aún no había terminado al bajar).
                FireAsync(fullFallback: false, IntPtr.Zero);
            }
            catch { }
        });
    }

    private void FireAsync(bool fullFallback, IntPtr expectedForeground)
    {
        // Varios gestos pueden disparar a la vez (long-press + mouse-up): una sola barra.
        // CompareExchange: antes dos hilos podían leer el mismo valor caducado y
        // disparar los dos, con dos Ctrl+C simultáneos sobre el portapapeles.
        long now = Environment.TickCount64;
        while (true)
        {
            long last = Interlocked.Read(ref _lastFireTickMs);
            if (last != 0 && now - last < 180) return;
            if (Interlocked.CompareExchange(ref _lastFireTickMs, now, last) == last) break;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(fullFallback ? 130 : 90); // deja que la app actualice la selección

                // La ventana de origen pudo cambiar durante la espera (clic en otra
                // app, Alt+Tab). Extraer entonces mostraría la selección de otra
                // ventana y, en el camino del portapapeles, el Ctrl+C iría a ella
                // (una terminal lo interpretaría como interrumpir el comando).
                if (expectedForeground != IntPtr.Zero &&
                    NativeMethods.GetForegroundWindow() != expectedForeground)
                    return;

                // ShowBar también descarta juegos/vídeo a pantalla completa, pero
                // para entonces ya se habría hecho UIA y, si falla, un Ctrl+C.
                // Comprobarlo aquí evita ese trabajo y la inyección en el juego.
                if (NativeMethods.IsForegroundFullscreen()) return;

                SelectionResult selection;
                if (fullFallback)
                {
                    // Ctrl+C necesita hilo STA (portapapeles).
                    var result = new SelectionResult();
                    var thread = new Thread(() => { result = TextExtractor.GetSelection(
                        allowClipboardFallback: true, expectedForeground: expectedForeground); });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.IsBackground = true;
                    thread.Start();
                    if (!thread.Join(4000)) return;
                    selection = result;
                }
                else
                {
                    selection = TextExtractor.TryGetViaUiaFull(); // no toca el portapapeles
                }

                var text = (selection.Text ?? "").Trim();
                if (text.Length >= Math.Max(1, _cfg.MinSelectionLength))
                {
                    // La extracción puede tardar más de un segundo (UIA + Ctrl+C):
                    // si el usuario ya cambió de ventana, mostrar la barra sería
                    // mostrar la selección vieja sobre otra app.
                    if (expectedForeground != IntPtr.Zero &&
                        NativeMethods.GetForegroundWindow() != expectedForeground)
                        return;
                    SafeInvoke(() => SelectionReady?.Invoke(text, selection.Anchor));
                }
            }
            catch (Exception ex) { Logger.LogEx(ex, "extraer selección"); }
        });
    }

    private bool IsOwnPopup()
    {
        try { return _isOwnPopup?.Invoke() ?? false; }
        catch { return false; }
    }

    private static void SafeInvoke(Action? action)
    {
        if (action == null) return;
        try { action(); }
        catch (Exception ex) { Logger.LogEx(ex, "evento de selección"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            if (_kbHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_kbHook);
        }
        catch { }
        _mouseHook = _kbHook = IntPtr.Zero;
    }
}
