using System.Runtime.InteropServices;
using System.Windows;

namespace Selbar.Native;

/// <summary>
/// Coloca el popup encima del cursor sin salirse de la pantalla (multi-monitor).
/// Todo en DIPs de WPF: el cursor y Screen.WorkingArea vienen en píxeles físicos
/// y se convierten con el DPI del monitor (si no, con escala ≠100% sale desplazado).
/// </summary>
public static class PopupPlacement
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Factor píxel→DIP en el monitor del punto dado.</summary>
    public static double DpiScaleAt(int x, int y)
    {
        try
        {
            var m = MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, 2);
            if (m != IntPtr.Zero && GetDpiForMonitor(m, 0, out uint dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { }
        try
        {
            var prim = System.Windows.Forms.Screen.PrimaryScreen;
            if (prim != null && SystemParameters.PrimaryScreenWidth > 0)
                return prim.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        }
        catch { }
        return 1.0;
    }

    public static System.Windows.Point AboveCursor(double popupWidth = 420, double popupHeight = 48, double margin = 12)
    {
        var (x, y) = NativeMethods.Cursor();
        double s = DpiScaleAt(x, y);
        // pasar todo a DIPs
        double cx = x / s, cy = y / s;
        double px = cx - popupWidth / 2;
        double py = cy - popupHeight - margin;

        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        double left = area.Left / s, top = area.Top / s;
        double right = area.Right / s, bottom = area.Bottom / s;
        px = Math.Clamp(px, left + 8, Math.Max(left + 8, right - popupWidth - 8));
        if (py < top + 8) py = cy + margin + 8; // si no cabe arriba, debajo
        py = Math.Clamp(py, top + 8, Math.Max(top + 8, bottom - popupHeight - 8));
        return new System.Windows.Point(px, py);
    }

    /// <summary>
    /// Coloca el popup centrado sobre un rectángulo de selección (píxeles físicos
    /// de pantalla). Si no cabe arriba, lo pone debajo; siempre dentro del monitor.
    /// </summary>
    public static System.Windows.Point AboveAnchor(double left, double top, double width, double height,
        double popupWidth, double popupHeight, double margin = 10)
    {
        double centerX = left + width / 2;
        double centerY = top + height / 2;
        int x = (int)centerX, y = (int)centerY;

        double s = DpiScaleAt(x, y);
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        double dipLeft = area.Left / s, dipTop = area.Top / s;
        double dipRight = area.Right / s, dipBottom = area.Bottom / s;

        double px = (centerX / s) - popupWidth / 2;
        double above = (top / s) - popupHeight - margin;
        double below = ((top + height) / s) + margin;
        double py = above >= dipTop + 8 ? above : below;

        px = Math.Clamp(px, dipLeft + 8, Math.Max(dipLeft + 8, dipRight - popupWidth - 8));
        py = Math.Clamp(py, dipTop + 8, Math.Max(dipTop + 8, dipBottom - popupHeight - 8));
        return new System.Windows.Point(px, py);
    }

    /// <summary>Reposiciona una ventana SizeToContent encima de la selección (o del cursor si no hay ancla).</summary>
    public static void PlaceAboveAnchor(System.Windows.Window w,
        double left, double top, double width, double height, double margin = 10)
    {
        if (w == null) return;
        if (width <= 1 || height <= 1)
        {
            PlaceAboveCursor(w, margin);
            return;
        }

        try
        {
            w.UpdateLayout();
            double pw = Math.Max(w.ActualWidth, 24);
            double ph = Math.Max(w.ActualHeight, 24);
            var pos = AboveAnchor(left, top, width, height, pw, ph, margin);
            w.Left = pos.X;
            w.Top = pos.Y;
        }
        catch { PlaceAboveCursor(w, margin); }
    }

    /// <summary>Reposiciona una ventana SizeToContent usando su tamaño real ya medido.</summary>
    public static void PlaceAboveCursor(System.Windows.Window w, double margin = 12)
    {
        if (w == null) return;
        try
        {
            w.UpdateLayout();
            double pw = Math.Max(w.ActualWidth, 24);
            double ph = Math.Max(w.ActualHeight, 24);
            var pos = AboveCursor(pw, ph, margin);
            w.Left = pos.X;
            w.Top = pos.Y;
        }
        catch { }
    }

    /// <summary>Convierte un punto de pantalla (píxeles) a DIPs para Left/Top.</summary>
    public static System.Windows.Point ScreenToDip(int x, int y)
    {
        double s = DpiScaleAt(x, y);
        return new System.Windows.Point(x / s, y / s);
    }
}
