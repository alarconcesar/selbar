using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Selbar.Core.Platform;
using QRCoder;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Selbar.Ui;

/// <summary>Genera y muestra un código QR del enlace seleccionado.</summary>
public partial class QrWindow : FluentWindow
{
    private readonly string _text;
    private BitmapSource? _bitmap;
    private CancellationTokenSource? _copyCts;
    private bool _closed;

    public QrWindow(string text)
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowKeyDown;

        Closed += (_, _) =>
        {
            _closed = true;
            // La espera entre reintentos se corta aquí: no se escribe el
            // portapapeles después de cerrar la ventana.
            try { _copyCts?.Cancel(); } catch { }
        };

        _text = (text ?? "").Trim();
        LinkText.Text = _text;
        LinkText.ToolTip = _text;
        QrImage.ToolTip = _text;
        AutomationProperties.SetName(QrFrame, string.IsNullOrWhiteSpace(_text)
            ? "Código QR"
            : "Código QR de " + _text);

        if (_text.Length == 0)
        {
            ShowError("No hay un enlace para generar el QR.");
            return;
        }

        try
        {
            _bitmap = Render(_text);
            QrImage.Source = _bitmap;
            QrFrame.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            CopyImageBtn.IsEnabled = true;
            SavePngBtn.IsEnabled = true;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            ShowError("No se pudo generar el QR: " + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        _bitmap = null;
        QrImage.Source = null;
        QrFrame.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        QrErrorText.Text = message;
        CopyImageBtn.IsEnabled = false;
        SavePngBtn.IsEnabled = false;
        StatusText.Text = "";
    }

    private static BitmapSource Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: true);

        var modules = data.ModuleMatrix;
        int count = modules.Count;
        const int quiet = 4;
        int total = count + quiet * 2;
        int scale = Math.Max(2, 360 / total);
        int pixels = total * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixels, pixels));
            var black = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            for (int row = 0; row < count; row++)
            {
                var line = modules[row];
                for (int col = 0; col < count; col++)
                {
                    if (!line[col]) continue;
                    dc.DrawRectangle(black, null, new Rect(
                        (quiet + col) * scale, (quiet + row) * scale, scale, scale));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private async void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_closed || _bitmap == null || !CopyImageBtn.IsEnabled) return;

        CopyImageBtn.IsEnabled = false;
        StatusText.Text = "Copiando imagen…";

        var cts = new CancellationTokenSource();
        _copyCts = cts;
        try
        {
            bool copied = await TrySetImageAsync(_bitmap, cts.Token);
            if (_closed) return;
            StatusText.Text = copied
                ? "Imagen del QR copiada."
                : "No se pudo copiar la imagen (portapapeles ocupado).";
        }
        finally
        {
            if (!_closed) CopyImageBtn.IsEnabled = _bitmap != null;
            if (ReferenceEquals(_copyCts, cts)) _copyCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Clipboard.SetImage falla si otra app tiene el portapapeles bloqueado; se reintenta
    /// como ClipboardService con el texto (el hilo de UI sigue siendo STA). La espera es
    /// asíncrona para que el estado «Copiando» se pinte y no se congele la ventana.
    /// </summary>
    private static async Task<bool> TrySetImageAsync(BitmapSource bitmap, CancellationToken ct)
    {
        const int attempts = 6;
        for (int i = 0; i < attempts; i++)
        {
            if (ct.IsCancellationRequested) return false;
            try { Clipboard.SetImage(bitmap); return true; }
            catch { }

            if (i < attempts - 1)
            {
                try { await Task.Delay(40 * (i + 1), ct); }
                catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }

    private void OnSavePng(object sender, RoutedEventArgs e)
    {
        if (_bitmap == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Imagen PNG (*.png)|*.png",
            FileName = "codigo-qr.png",
            DefaultExt = ".png",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        string temp = dialog.FileName + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            // Codificar en memoria y sustituir el destino al final: un fallo a
            // mitad no deja un PNG truncado sobre el archivo anterior.
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, dialog.FileName, true);
            StatusText.Text = "Guardado en " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            StatusText.Text = "No se pudo guardar: " + ex.Message;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && ctrl)
        {
            OnSavePng(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && ctrl)
        {
            OnCopyImage(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button && CopyImageBtn.IsEnabled)
        {
            OnCopyImage(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
