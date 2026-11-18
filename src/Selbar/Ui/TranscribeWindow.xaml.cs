using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Selbar.Core.Ai;
using Selbar.Core.Config;
using Selbar.Core.Platform;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Selbar.Ui;

/// <summary>Transcribe un archivo de audio con Whisper en Groq y abre el resultado.</summary>
public partial class TranscribeWindow : FluentWindow
{
    private string? _filePath;
    private string? _validation;
    private CancellationTokenSource? _cts;
    private StoryboardHandle? _busy;
    private bool _running;
    private bool _cancelling;
    private bool _closed;

    private sealed class StoryboardHandle : IDisposable
    {
        public System.Windows.Media.Animation.Storyboard? Storyboard;
        public void Dispose()
        {
            try { Storyboard?.Stop(); } catch { }
        }
    }

    public TranscribeWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowKeyDown;

        Loaded += (_, _) =>
        {
            RefreshState();
            if (_filePath == null) PickFileBtn.Focus();
        };
        Activated += (_, _) => RefreshState();
        Closed += (_, _) =>
        {
            _closed = true;
            // Solo se pide la cancelación: el CTS lo dispone el finally de la
            // transcripción cuando la petición termina. Disponerlo aquí mientras
            // HttpClient todavía lo usa puede provocar ObjectDisposedException.
            try { _cts?.Cancel(); } catch { }
            StopBusy();
        };
    }

    private AppSettings Settings => Selbar.App.SettingsMgr.Settings;

    private bool HasKey =>
        !string.IsNullOrWhiteSpace(SettingsManager.Unprotect(Settings.Ai.GroqApiKeyEncrypted));

    /// <summary>Una sola predicación decide si la primaria puede completar su tarea.</summary>
    private bool CanTranscribe => !_running && !_cancelling && HasKey && _filePath != null && _validation == null;

    private void RefreshState()
    {
        if (_filePath != null && !_running)
        {
            _validation = GroqWhisper.ValidateFile(_filePath);
            if (_validation != null)
            {
                SetFileHint(_validation, error: true);
            }
            else
            {
                try
                {
                    double mb = new FileInfo(_filePath).Length / 1024.0 / 1024.0;
                    SetFileHint($"{mb:0.0} MB · listo para transcribir", error: false);
                }
                catch
                {
                    _validation = "No se pudo leer el archivo. Vuelve a elegirlo.";
                    SetFileHint(_validation, error: true);
                }
            }
        }

        bool hasKey = HasKey;
        KeyPanel.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        KeySummary.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;

        if (hasKey)
        {
            string model = string.IsNullOrWhiteSpace(Settings.Ai.TranscriptionModel)
                ? GroqWhisper.DefaultModel
                : Settings.Ai.TranscriptionModel;
            KeySummary.Text = $"Groq configurado · modelo {model}";
        }
        else
        {
            KeyStatus.Text = "Pega una API key de Groq en Ajustes → IA. Sin ella no se puede transcribir.";
            KeyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        }

        TranscribeBtn.IsEnabled = CanTranscribe;
        TranscribeBtn.Content = _running ? "Transcribiendo…" : "Transcribir";
        PickFileBtn.IsEnabled = !_running && !_cancelling;
        MoreBtn.IsEnabled = !_running && !_cancelling;
        CloseBtn.Content = _cancelling ? "Cancelando…" : _running ? "Cancelar" : "Cerrar";
        CloseBtn.IsEnabled = !_cancelling;
    }

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        if (_running || _cancelling) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Audio compatible (*.mp3;*.m4a;*.wav;*.ogg;*.webm;*.flac;*.mp4;*.aac;*.mpeg;*.mpga)|*.mp3;*.m4a;*.wav;*.ogg;*.webm;*.flac;*.mp4;*.aac;*.mpeg;*.mpga|Todos los archivos (*.*)|*.*",
            Title = "Elegir audio para transcribir"
        };
        if (dialog.ShowDialog(this) != true) return;

        _filePath = dialog.FileName;
        FileText.Text = Path.GetFileName(_filePath);
        FileText.ToolTip = _filePath;

        _validation = GroqWhisper.ValidateFile(_filePath);
        if (_validation == null)
        {
            try
            {
                double mb = new FileInfo(_filePath).Length / 1024.0 / 1024.0;
                SetFileHint($"{mb:0.0} MB · listo para transcribir", error: false);
                SetStatus("", error: false);
            }
            catch
            {
                _validation = "No se pudo leer el archivo. Vuelve a elegirlo.";
                SetFileHint(_validation, error: true);
                SetStatus(_validation, error: true);
            }
        }
        else
        {
            SetFileHint(_validation, error: true);
            SetStatus(_validation, error: true);
        }

        RefreshState();
        if (CanTranscribe) TranscribeBtn.Focus();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Selbar.App.OpenSettings("ia");

    private void OnShowFormats(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Formatos: mp3, m4a, wav, ogg, webm, flac, mp4, aac, mpeg y mpga. Tamaño máximo: 25 MB. " +
            "El audio se envía a Groq solo cuando pulsas Transcribir.",
            "Formatos y límite", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if (_running || _cancelling) return;

        string? file = _filePath;
        if (string.IsNullOrWhiteSpace(file))
        {
            _validation = "Elige un archivo de audio.";
            SetStatus(_validation, error: true);
            RefreshState();
            return;
        }

        _validation = GroqWhisper.ValidateFile(file);
        if (_validation != null)
        {
            SetStatus(_validation, error: true);
            RefreshState();
            return;
        }

        string key = SettingsManager.Unprotect(Settings.Ai.GroqApiKeyEncrypted);
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Falta la API key de Groq. Ábrela en Ajustes → IA.", error: true);
            RefreshState();
            return;
        }

        // El archivo y el modelo se capturan al empezar: la tarjeta no puede cambiar bajo la petición.
        string model = string.IsNullOrWhiteSpace(Settings.Ai.TranscriptionModel)
            ? GroqWhisper.DefaultModel
            : Settings.Ai.TranscriptionModel;
        string fileName = Path.GetFileName(file!);

        _running = true;
        _cancelling = false;
        RefreshState();
        SetStatus($"Transcribiendo {fileName}… puede tardar según la duración.", error: false);

        try { _cts?.Dispose(); } catch { }
        var cts = _cts = new CancellationTokenSource();
        try
        {
            StartBusy();
            string text = await GroqWhisper.TranscribeAsync(file!, key, model, cts.Token);
            if (_closed) return;
            if (cts.IsCancellationRequested)
            {
                // La petición terminó justo después de pedir Cancelar: no dejar
                // «Cancelando…» con los botones ya habilitados y sin estado final.
                SetStatus("Transcripción cancelada.", error: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Groq no devolvió texto. Prueba con otro archivo o vuelve a intentarlo.", error: true);
                return;
            }

            var win = new ResultWindow("Transcripción", text, Core.Actions.OutputMode.Popup);
            win.SetRefinable(true);
            win.EnableChat();
            win.Show();
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!_closed) SetStatus("Transcripción cancelada.", error: false);
        }
        catch (Exception ex)
        {
            if (!_closed) SetStatus(ex.Message, error: true);
        }
        finally
        {
            _running = false;
            _cancelling = false;
            StopBusy();
            if (ReferenceEquals(_cts, cts))
            {
                try { cts.Dispose(); } catch { }
                _cts = null;
            }
            if (!_closed) RefreshState();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cancelling = true;
            try { _cts?.Cancel(); } catch { }
            SetStatus("Cancelando transcripción…", error: false);
            RefreshState();
            return;
        }
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        OnClose(this, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MoreBtn.ContextMenu is not { } menu) return;
            menu.PlacementTarget = MoreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch { }
    }

    private void SetFileHint(string text, bool error)
    {
        FileHint.Text = text;
        FileHint.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }

    private void StartBusy()
    {
        BusyTrack.Visibility = Visibility.Visible;
        if (!Motion.Enabled)
        {
            // Sin movimiento: barra estática comprensible, sin storyboard en bucle.
            BusyShift.X = 0;
            return;
        }
        _busy = new StoryboardHandle
        {
            Storyboard = Motion.Indeterminate(BusyShift, -120, Math.Max(300, ActualWidth), 1100)
        };
    }

    private void StopBusy()
    {
        try { BusyTrack.Visibility = Visibility.Collapsed; } catch { }
        _busy?.Dispose();
        _busy = null;
    }
}
