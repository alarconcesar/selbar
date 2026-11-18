using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GdiColor = System.Drawing.Color;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiRectangle = System.Drawing.Rectangle;

namespace Selbar.Ui;

/// <summary>Icono de las ventanas (derivado del icono del tray).</summary>
public static class AppIcon
{
    private static readonly object Gate = new();
    private static ImageSource? _cached;

    /// <summary>Tira la caché (el icono se deriva del acento del tray).</summary>
    public static void Invalidate() => _cached = null;

    public static ImageSource Get()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;
            _cached = Build();
            return _cached;
        }
    }

    private static ImageSource Build()
    {
        using var icon = TrayIcon.Get().ToBitmap();

        // Se copia a un bitmap 32bppArgb propio: el de Icon.ToBitmap() puede venir
        // en otro formato y LockBits fallaría. Además así el fondo transparente
        // del icono (esquinas redondeadas) se conserva.
        using var bmp = new Bitmap(icon.Width, icon.Height, GdiPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(GdiColor.Transparent);
            g.DrawImage(icon, 0, 0, icon.Width, icon.Height);
        }

        var rect = new GdiRectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            // CreateBitmapSourceFromHBitmap (lo anterior) descartaba el canal alfa:
            // las esquinas redondeadas del icono salían negras en la barra de título.
            var source = BitmapSource.Create(
                bmp.Width, bmp.Height, 96, 96,
                PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
