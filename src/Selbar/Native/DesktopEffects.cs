using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Selbar.Native;

/// <summary>
/// Acabado nativo de Windows 11: Mica, modo oscuro del marco, esquinas
/// redondeadas y borde de DWM. Todo falla en silencio en Windows 10.
/// </summary>
public static class DesktopEffects
{
    private enum Attr
    {
        DarkMode = 20,
        CornerPreference = 33,
        BorderColor = 34,
        BackdropType = 38,
        MicaLegacy = 1029
    }

    private enum Backdrop { Auto = 0, Disable = 1, MainWindow = 2, Transient = 3, Tabbed = 4 }
    private enum Corner { Default = 0, Square = 1, Round = 2, RoundSmall = 3 }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Ventanas a las que ya se les enganchó el manejador de SourceInitialized.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Hooked = new();

    /// <summary>
    /// Aplica el acabado nativo y los valores base del tema a una ventana.
    /// Se mantiene por compatibilidad; las ventanas propias usan FluentWindow.
    /// <c>mica = false</c> por ADR-003: en esta configuración de WPF Mica pinta
    /// negro el marco y las esquinas; solo se activa si se pide expresamente.
    /// </summary>
    public static void ApplyWin11(Window w, bool mica = false)
    {
        if (w == null) return;
        ApplyBaseTheme(w);

        // Sin esta guarda, llamar dos veces dejaba dos manejadores y el acabado
        // se aplicaba dos veces por ventana.
        if (Hooked.TryGetValue(w, out _)) return;
        Hooked.Add(w, new object());

        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                ApplyBackdrop(hwnd, Ui.Theming.IsDark, mica);
            }
            catch { }
        };

        // Si ya tenía HWND (llamada después de Show), SourceInitialized no vuelve.
        try
        {
            var existing = new WindowInteropHelper(w).Handle;
            if (existing != IntPtr.Zero) ApplyBackdrop(existing, Ui.Theming.IsDark, mica);
        }
        catch { }
    }

    /// <summary>
    /// Activa Mica (o acrílico translúcido) y ajusta el marco al tema.
    /// Devuelve true si el sistema ha aceptado el material pedido.
    /// </summary>
    public static bool ApplyBackdrop(IntPtr hwnd, bool dark, bool mica = true)
    {
        if (hwnd == IntPtr.Zero) return false;

        // Se ignoran a propósito: si el sistema no soporta el atributo (Windows 10)
        // no hay nada que hacer, y ApplyBackdrop ya devuelve si Mica se aceptó.
        _ = ApplyDarkFrame(hwnd, dark);
        _ = SetRoundedCorners(hwnd, true);
        // Sin Mica no hay nada más que aceptar ni que fallar: antes devolvía
        // false y el llamante no podía distinguir «Mica apagado» de «rechazado».
        if (!mica || !IsWindows11()) return true;

        try
        {
            // Windows 11 22H2+: DWMWA_SYSTEMBACKDROP_TYPE = Mica
            int type = (int)Backdrop.MainWindow;
            if (DwmSetWindowAttribute(hwnd, (int)Attr.BackdropType, ref type, sizeof(int)) == 0)
                return true;

            // Windows 11 21H2: atributo no documentado.
            int enabled = 1;
            return DwmSetWindowAttribute(hwnd, (int)Attr.MicaLegacy, ref enabled, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Título oscuro del marco (DWM dibuja el borde nativo de cada tema).</summary>
    public static bool ApplyDarkFrame(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int value = dark ? 1 : 0;
            // El HRESULT se devuelve: en Windows 10 el atributo no existe y el
            // fallo es esperado; en 11 un false indica que el marco no se aplicó.
            return DwmSetWindowAttribute(hwnd, (int)Attr.DarkMode, ref value, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>Esquinas redondeadas (o cuadradas al maximizar).</summary>
    public static bool SetRoundedCorners(IntPtr hwnd, bool round)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int value = (int)(round ? Corner.Round : Corner.Square);
            // Windows 10 no soporta DWMWA_WINDOW_CORNER_PREFERENCE: false es normal.
            return DwmSetWindowAttribute(hwnd, (int)Attr.CornerPreference, ref value, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>Aplica tema a todas las ventanas abiertas (cambio en caliente).</summary>
    public static void RefreshAllWindows()
    {
        try
        {
            foreach (Window window in System.Windows.Application.Current.Windows)
            {
                try
                {
                    if (window is Ui.FluentWindow) continue; // se refresca solo
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero) _ = ApplyDarkFrame(hwnd, Ui.Theming.IsDark);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Color de texto, tipografía y nitidez de píxeles por defecto.</summary>
    private static void ApplyBaseTheme(Window w)
    {
        try
        {
            w.Foreground = w.TryFindResource("TextPrimary") as Brush ?? Brushes.White;
            w.FontFamily = w.TryFindResource("FontText") as FontFamily
                           ?? new FontFamily("Segoe UI Variable Text, Segoe UI");
            w.FontSize = 14;
            w.UseLayoutRounding = true;
            w.SnapsToDevicePixels = true;
        }
        catch { }
    }

    private static bool IsWindows11() => Environment.OSVersion.Version.Build >= 22000;
}
