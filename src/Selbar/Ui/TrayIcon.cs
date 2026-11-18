using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Selbar.Ui;

/// <summary>Icono del tray e iconos de menú generados en runtime (sin ficheros externos).</summary>
public static class TrayIcon
{
    private static Icon? _cached;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Icono cacheado (evita fugas GDI).</summary>
    public static Icon Get() => _cached ??= Create();

    /// <summary>Tira la caché para redibujar el icono tras cambiar el acento o el tema.</summary>
    public static void Refresh()
    {
        try { _cached?.Dispose(); } catch { }
        _cached = null;
    }

    /// <summary>
    /// Dibuja el isotipo oficial de Selbar (Opción 2: Dual-Bar S) en un lienzo 64x64:
    /// squircle jade profundo (#286473 -> #12353F) + barra superior blanca ("Sel") +
    /// barra inferior cian luminosa ("Bar", #94EFFA -> #4FD0E1) entrelazadas formando una S.
    /// </summary>
    public static Icon Create()
    {
        const int s = 64;
        using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using (var bgBrush = new LinearGradientBrush(
                new PointF(6f, 4f),
                new PointF(58f, 60f),
                Color.FromArgb(0x28, 0x64, 0x73),
                Color.FromArgb(0x12, 0x35, 0x3F)))
            using (var bgPath = RoundedRectF(2f, 2f, 60f, 60f, 15f))
            {
                g.FillPath(bgBrush, bgPath);
            }

            using (var borderPen = new Pen(Color.FromArgb(90, 0x7C, 0xE5, 0xF2), 1.5f))
            using (var borderPath = RoundedRectF(2.75f, 2.75f, 58.5f, 58.5f, 14.25f))
            {
                g.DrawPath(borderPen, borderPath);
            }

            using (var topBar = CreateTopBarPath(1f))
            {
                g.FillPath(Brushes.White, topBar);
            }

            using (var cyanBrush = new LinearGradientBrush(
                new PointF(12f, 27f),
                new PointF(52f, 53f),
                Color.FromArgb(0x94, 0xEF, 0xFA),
                Color.FromArgb(0x4F, 0xD0, 0xE1)))
            using (var bottomBar = CreateBottomBarPath(1f))
            {
                g.FillPath(cyanBrush, bottomBar);
            }
        }

        var hicon = bmp.GetHicon();
        try
        {
            // Clone() se queda con su propia copia: así podemos destruir el HICON original
            // y no dejamos handles GDI colgando.
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    /// <summary>
    /// Dibuja un glifo de Segoe Fluent Icons en un bitmap (para los iconos del
    /// menú del tray, que WinForms no hereda del tema).
    /// </summary>
    public static Bitmap GlyphBitmap(string glyph, int size = 16, bool? dark = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var font = new Font("Segoe Fluent Icons", size, FontStyle.Regular, GraphicsUnit.Pixel);
            var ink = (dark ?? Theming.IsDark) ? Color.FromArgb(0xEA, 0xEA, 0xEA) : Color.FromArgb(0x1A, 0x1A, 0x1A);
            using var brush = new SolidBrush(ink);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), sf);
        }
        return bmp;
    }

    private static GraphicsPath CreateTopBarPath(float k)
    {
        // M 25.0 11.6 H 46.2 A 4.8 4.8 0 0 1 46.2 21.2 H 25.0 A 3.0 3.0 0 0 0 25.0 27.2
        // H 31.8 Q 33.0 27.2 32.6 28.3 L 29.9 35.7 Q 29.5 36.8 28.3 36.8 H 25.0 A 12.6 12.6 0 0 1 25.0 11.6 Z
        var p = new GraphicsPath();
        p.AddLine(25.0f * k, 11.6f * k, 46.2f * k, 11.6f * k);
        p.AddArc((46.2f - 4.8f) * k, 11.6f * k, 9.6f * k, 9.6f * k, 270f, 180f);
        p.AddLine(46.2f * k, 21.2f * k, 25.0f * k, 21.2f * k);
        p.AddArc((25.0f - 3.0f) * k, 21.2f * k, 6.0f * k, 6.0f * k, 270f, -180f);
        p.AddLine(25.0f * k, 27.2f * k, 31.8f * k, 27.2f * k);
        AddQuadBezier(p, 31.8f * k, 27.2f * k, 33.0f * k, 27.2f * k, 32.6f * k, 28.3f * k);
        p.AddLine(32.6f * k, 28.3f * k, 29.9f * k, 35.7f * k);
        AddQuadBezier(p, 29.9f * k, 35.7f * k, 29.5f * k, 36.8f * k, 28.3f * k, 36.8f * k);
        p.AddLine(28.3f * k, 36.8f * k, 25.0f * k, 36.8f * k);
        p.AddArc((25.0f - 12.6f) * k, 11.6f * k, 25.2f * k, 25.2f * k, 90f, 180f);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath CreateBottomBarPath(float k)
    {
        // M 39.0 52.4 H 17.2 A 4.8 4.8 0 0 1 17.2 42.8 H 39.0 A 3.0 3.0 0 0 0 39.0 36.8
        // H 32.2 Q 31.0 36.8 31.4 35.7 L 34.1 28.3 Q 34.5 27.2 35.7 27.2 H 39.0 A 12.6 12.6 0 0 1 39.0 52.4 Z
        var p = new GraphicsPath();
        p.AddLine(39.0f * k, 52.4f * k, 17.2f * k, 52.4f * k);
        p.AddArc((17.2f - 4.8f) * k, 42.8f * k, 9.6f * k, 9.6f * k, 90f, 180f);
        p.AddLine(17.2f * k, 42.8f * k, 39.0f * k, 42.8f * k);
        p.AddArc((39.0f - 3.0f) * k, 36.8f * k, 6.0f * k, 6.0f * k, 90f, -180f);
        p.AddLine(39.0f * k, 36.8f * k, 32.2f * k, 36.8f * k);
        AddQuadBezier(p, 32.2f * k, 36.8f * k, 31.0f * k, 36.8f * k, 31.4f * k, 35.7f * k);
        p.AddLine(31.4f * k, 35.7f * k, 34.1f * k, 28.3f * k);
        AddQuadBezier(p, 34.1f * k, 28.3f * k, 34.5f * k, 27.2f * k, 35.7f * k, 27.2f * k);
        p.AddLine(35.7f * k, 27.2f * k, 39.0f * k, 27.2f * k);
        p.AddArc((39.0f - 12.6f) * k, 27.2f * k, 25.2f * k, 25.2f * k, 270f, 180f);
        p.CloseFigure();
        return p;
    }

    private static void AddQuadBezier(GraphicsPath p, float x0, float y0, float cx, float cy, float x1, float y1)
    {
        float c1x = x0 + (2f / 3f) * (cx - x0);
        float c1y = y0 + (2f / 3f) * (cy - y0);
        float c2x = x1 + (2f / 3f) * (cx - x1);
        float c2y = y1 + (2f / 3f) * (cy - y1);
        p.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x1, y1);
    }

    private static GraphicsPath RoundedRectF(float x, float y, float w, float h, float r)
    {
        float d = r * 2f;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180f, 90f);
        p.AddArc(x + w - d, y, d, d, 270f, 90f);
        p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
        p.AddArc(x, y + h - d, d, d, 90f, 90f);
        p.CloseFigure();
        return p;
    }
}
