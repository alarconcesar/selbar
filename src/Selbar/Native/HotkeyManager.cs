using System.Runtime.InteropServices;
using System.Windows.Forms;
using Selbar.Core.Diagnostics;

namespace Selbar.Native;

/// <summary>
/// Hotkey global vía RegisterHotKey + conversión entre texto ("Ctrl+Shift+Espacio")
/// y la pareja (modificadores, tecla virtual).
/// </summary>
public static class Hotkeys
{
    public const int WM_HOTKEY = 0x0312;
    public const int HOTKEY_ID = 0xB001;
    /// <summary>Segundo atajo global: dictado (grabar / transcribir).</summary>
    public const int DICTATION_HOTKEY_ID = 0xB002;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    /// <summary>
    /// Sin MOD_NOREPEAT, mantener pulsado el atajo genera un WM_HOTKEY por cada
    /// repetición del teclado: la barra se recreaba varias veces y el dictado
    /// alternaba grabar/transcribir en cadena. Windows Vista+.
    /// </summary>
    public const uint ModNoRepeat = 0x4000;

    public const string DefaultHotkey = "Ctrl+Shift+Espacio";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>¿El texto se puede registrar como hotkey? (útil para validar en Ajustes).</summary>
    public static bool IsValid(string? text) => TryParse(text, out _, out _);

    /// <summary>Convierte "Ctrl+Shift+Espacio" en modificadores + código de tecla.</summary>
    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string? keyPart = null;
        foreach (var raw in text.Split('+'))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;

            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= ModControl;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= ModAlt;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase) || p.Equals("Mayus", StringComparison.OrdinalIgnoreCase))
                mods |= ModShift;
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                mods |= ModWin;
            else if (keyPart != null) return false; // dos teclas ("Ctrl+A+B"): ambiguo, no adivinar
            else keyPart = p; // la única tecla "normal" es la tecla
        }

        if (keyPart == null || mods == 0) return false;
        if (!TryParseKey(keyPart, out vk)) return false;
        return vk != 0;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        var t = token.Trim();

        // Nombres "amigables" (los que muestra la app y los que escribe el usuario).
        switch (t.ToLowerInvariant())
        {
            case "espacio" or "space": vk = 0x20; return true;
            case "intro" or "enter" or "return": vk = 0x0D; return true;
            case "supr" or "suprimir" or "delete" or "del": vk = 0x2E; return true;
            case "escape" or "esc": vk = 0x1B; return true;
            case "retroceso" or "backspace": vk = 0x08; return true;
            case "tab" or "tabulador": vk = 0x09; return true;
            case "inicio" or "home": vk = 0x24; return true;
            case "fin" or "end": vk = 0x23; return true;
            case "insertar" or "insert": vk = 0x2D; return true;
            // Format() escribe «RePág»/«AvPág» (con tilde). Sin estas variantes
            // el texto que produce Format no se podía volver a parsear y el
            // grabador de Ajustes rechazaba el atajo como inválido.
            case "repag" or "re pag" or "repág" or "re pág" or "pageup": vk = 0x21; return true;
            case "avpag" or "av pag" or "avpág" or "av pág" or "pagedown": vk = 0x22; return true;
            case "izquierda" or "left": vk = 0x25; return true;
            case "derecha" or "right": vk = 0x27; return true;
            case "arriba" or "up": vk = 0x26; return true;
            case "abajo" or "down": vk = 0x28; return true;
            case "imprpant" or "printscreen": vk = 0x2C; return true;
            case "pausa" or "pause": vk = 0x13; return true;
            case "menu" or "apps": vk = 0x5D; return true;
        }

        // F1..F24
        if (t.Length is >= 2 and <= 3 && (t[0] == 'F' || t[0] == 'f') &&
            int.TryParse(t[1..], out int fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        // Un solo carácter: letra ASCII o dígito. (Ñ, Ü, etc. no tienen código
        // virtual propio: antes se aceptaban y el registro fallaba sin explicación.)
        if (t.Length == 1)
        {
            if (t[0] >= '0' && t[0] <= '9') { vk = (uint)t[0]; return true; }
            char upper = char.ToUpperInvariant(t[0]);
            if (upper >= 'A' && upper <= 'Z') { vk = upper; return true; }
            return false;
        }

        // Nombres del enum Keys ("OemPlus", "NumPad5", "Subtract"...). Nunca números:
        // Enum.TryParse("5") devolvería la tecla con ese código, no el dígito 5.
        if (t.All(char.IsDigit)) return false;
        if (Enum.TryParse<Keys>(t, true, out var key) && Enum.GetName(key) != null)
        {
            vk = (uint)key;
            return true;
        }
        return false;
    }

    /// <summary>Texto legible y reconstruible para mostrar en Ajustes.</summary>
    public static string Format(uint mods, uint vk)
    {
        if (vk == 0) return "";
        var parts = new List<string>(5);
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);

        switch (vk)
        {
            case 0x20: return "Espacio";
            case 0x0D: return "Intro";
            case 0x09: return "Tab";
            case 0x1B: return "Esc";
            case 0x2E: return "Supr";
            case 0x08: return "Retroceso";
            case 0x24: return "Inicio";
            case 0x23: return "Fin";
            case 0x2D: return "Insert";
            case 0x21: return "RePág";
            case 0x22: return "AvPág";
            case 0x25: return "Izquierda";
            case 0x27: return "Derecha";
            case 0x26: return "Arriba";
            case 0x28: return "Abajo";
        }

        var name = Enum.GetName(typeof(Keys), (int)vk);
        return string.IsNullOrEmpty(name) ? "Tecla" + vk : name;
    }

    /// <summary>Atajos registrados ahora mismo, por id (para poder restaurarlos).</summary>
    private static readonly Dictionary<int, (uint Mods, uint Vk)> Registered = new();

    /// <summary>Register/Unregister pueden coincidir (Ajustes y bandeja).</summary>
    private static readonly object Gate = new();

    public static bool Register(IntPtr hwnd, string text, int id = HOTKEY_ID)
    {
        // Validar ANTES de desregistrar: con el orden anterior, un texto inválido
        // en Ajustes dejaba al usuario sin el atajo que ya funcionaba.
        if (hwnd == IntPtr.Zero || !TryParse(text, out var mods, out var vk)) return false;

        lock (Gate)
        {
            bool had = Registered.TryGetValue(id, out var previous);
            UnregisterCore(hwnd, id);
            try
            {
                if (RegisterHotKey(hwnd, id, mods | ModNoRepeat, vk))
                {
                    Registered[id] = (mods, vk);
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                // El texto es válido pero otro programa se quedó el atajo: antes se
                // perdía también el anterior y el usuario se quedaba sin ninguno.
                if (had && RegisterHotKey(hwnd, id, previous.Mods | ModNoRepeat, previous.Vk))
                {
                    Registered[id] = previous;
                    Logger.Warn($"hotkey: «{text}» ocupado por otro programa (Win32 {error}); se mantiene «{Format(previous.Mods, previous.Vk)}»");
                }
                else
                {
                    Logger.Warn($"hotkey: no se pudo registrar «{text}» (Win32 {error})");
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "hotkey");
                return false;
            }
        }
    }

    public static void Unregister(IntPtr hwnd, int id = HOTKEY_ID)
    {
        lock (Gate) UnregisterCore(hwnd, id);
    }

    private static void UnregisterCore(IntPtr hwnd, int id)
    {
        try { if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, id); }
        catch { }
        Registered.Remove(id);
    }
}
