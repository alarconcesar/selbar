using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Selbar.Native;

/// <summary>
/// Importaciones Win32 que usa la app: hooks de bajo nivel, inyección de teclas
/// (SendInput, la API recomendada), foco de ventanas y utilidades de proceso.
/// </summary>
public static class NativeMethods
{
    // ---------- Hooks ----------
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;

    // ---------- Teclas virtuales ----------
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_BACK = 0x08;
    public const ushort VK_A = 0x41;
    public const ushort VK_C = 0x43;
    public const ushort VK_V = 0x56;
    public const ushort VK_X = 0x58;

    // ---------- Ventanas ----------
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int SW_RESTORE = 9;

    /// <summary>HWND_BROADCAST: envía el mensaje a todas las ventanas de nivel superior.</summary>
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    // ---------- Tipos ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ---------- user32: hooks y ratón ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>Módulo del proceso actual (para instalar hooks de bajo nivel).</summary>
    public static IntPtr ModuleHandle() => GetModuleHandleW(null);

    // ---------- user32: ventanas ----------
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint RegisterWindowMessageW(string lpString);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // ---------- SendInput ----------
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Win32 declara MOUSEINPUT como el miembro más grande del union; es el que
    // fija el tamaño de INPUT. Hacía falta declararlo: con KEYBDINPUT + un
    // IntPtr de relleno el tamaño salía 24 en 32 bits (debe ser 28) y SendInput
    // rechazaba la llamada entera con ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // ---------- Utilidades ----------
    /// <summary>Posición del cursor en píxeles físicos.</summary>
    public static (int X, int Y) Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : (500, 500);

    /// <summary>¿El punto cae dentro del rectángulo de la ventana?</summary>
    public static bool PointInWindow(IntPtr hwnd, int x, int y)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !GetWindowRect(hwnd, out var r)) return false;
            return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
        }
        catch { return false; }
    }

    /// <summary>¿Está pulsada una tecla ahora mismo? (para no reaccionar a Ctrl+click, etc.)</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Nombre del proceso en primer plano (sin .exe), o "" si no se puede saber.</summary>
    public static string ForegroundProcessName()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            uint thread = GetWindowThreadProcessId(h, out var pid);
            if (thread == 0 || pid == 0) return "";
            return ProcessNameOfPid((int)pid);
        }
        catch { return ""; }
    }

    /// <summary>Nombre del proceso de un PID (sin .exe), o "" si ya no existe.</summary>
    public static string ProcessNameOfPid(int pid)
    {
        if (pid <= 0) return "";
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Clase de la ventana (p.ej. "Chrome_WidgetWin_1").</summary>
    public static string ClassNameOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>Título de la ventana ("" si no tiene).</summary>
    public static string WindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// ¿La ventana en primer plano ocupa toda la pantalla? (juegos, vídeo a pantalla
    /// completa, presentaciones). Se ignoran el escritorio y la barra de tareas.
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return false;

            var cls = ClassNameOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
                return false;

            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            var b = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)).Bounds;
            // margen de 2 px para no fallar por bordes redondeados/overscan
            return r.Left <= b.Left + 2 && r.Top <= b.Top + 2
                && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
        }
        catch { return false; }
    }

    // ---------- Inyección de teclado ----------
    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    /// <summary>Reenvía con keybd_event el evento número <paramref name="index"/> del chord.</summary>
    private static void FallbackKey(ushort[] keys, int index)
    {
        int n = keys.Length;
        if (index < n) keybd_event((byte)keys[index], 0, 0, UIntPtr.Zero);
        else keybd_event((byte)keys[2 * n - 1 - index], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Pulsa una combinación de teclas: todas a la vez y se sueltan en orden inverso.
    /// Usa SendInput (respeta el estado real del teclado) con vuelta atrás a keybd_event.
    /// Devuelve true si el sistema aceptó la inyección.
    /// </summary>
    public static bool SendChord(params ushort[] keys)
    {
        if (keys == null || keys.Length == 0) return false;
        try
        {
            var inputs = new List<INPUT>(keys.Length * 2);
            foreach (var k in keys) inputs.Add(KeyInput(k, false));
            for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(KeyInput(keys[i], true));

            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            if (sent == (uint)inputs.Count) return true;

            // Inyección parcial (UIPI, otro hook...): SendInput no mete el resto y
            // dejaría, por ejemplo, el Ctrl pulsado y sin soltar. Se completan los
            // eventos que faltan (incluidos los key-up) con keybd_event.
            for (int i = (int)sent; i < inputs.Count; i++) FallbackKey(keys, i);
            // keybd_event no informa del resultado. Devolver false aquí hacía que
            // TextExtractor restaurara el portapapeles de inmediato y descartara un
            // Ctrl+C que sí había llegado por la vía antigua; si al menos se
            // intentó la inyección, el llamante debe esperar la copia.
            return true;
        }
        catch
        {
            try { for (int i = 0; i < keys.Length * 2; i++) FallbackKey(keys, i); }
            catch { }
            return false;
        }
    }

    public static bool SendCtrlC() => SendChord(VK_CONTROL, VK_C);
    public static bool SendCtrlV() => SendChord(VK_CONTROL, VK_V);
    public static bool SendCtrlX() => SendChord(VK_CONTROL, VK_X);
    public static bool SendKey(ushort vk) => SendChord(vk);

    /// <summary>Tamaño de la estructura INPUT que espera SendInput (diagnóstico/tests).</summary>
    public static int KeyboardInputSize => Marshal.SizeOf<INPUT>();

    // ---------- Estilos de ventana ----------
    /// <summary>
    /// Marca la ventana como no activable (un click no le quita el foco a la app de origen).
    /// Devuelve true si los flags quedaron aplicados (se relee el estilo: si
    /// SetWindowLongPtr falló en silencio, el popup robaría el foco y hay que saberlo).
    /// </summary>
    public static bool MakeNonActivating(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            // Leer el estilo puede fallar (ventana ajena o ya cerrada). Si fallara
            // y usáramos el 0 devuelto, SetWindowLongPtr borraría TODOS los estilos
            // extendidos; se comprueba el error como manda Win32.
            Marshal.SetLastSystemError(0);
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if (style == 0 && Marshal.GetLastSystemError() != 0) return false;
            long updated = style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (updated == style) return true; // ya estaban
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
            // Verificar: el Set puede fallar (ventana ajena, carrera al cerrar).
            long applied = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            return (applied & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) == (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch { return false; }
    }
}
