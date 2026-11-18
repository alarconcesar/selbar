using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Selbar.Core.Diagnostics;
using Selbar.Core.Downloads;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Selbar.Ui;

/// <summary>
/// Descarga un vídeo o el audio de un enlace compatible con yt-dlp. La vista principal
/// muestra etapa, progreso y destino; el registro, la compatibilidad y el aviso legal
/// quedan bajo demanda. Instalar la herramienta también se puede cancelar.
/// </summary>
public partial class DownloadWindow : FluentWindow
{
    private static readonly Regex PercentRegex = new(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

    private readonly string _url;
    private readonly bool _audio;
    private readonly bool _autoStart;
    private Process? _process;
    private bool _processStarted;
    private CancellationTokenSource? _installCts;
    private bool _running;
    private bool _closed;
    private bool _installing;
    private bool _cancelled;
    private string? _filePath;
    private string _lastError = "";

    public DownloadWindow(string url, bool audio, bool autoStart = true)
    {
        InitializeComponent();

        _audio = audio;
        _autoStart = autoStart;
        PreviewKeyDown += OnWindowKeyDown;

        if (!YtDlp.TryNormalizeUrl(url, out _url))
        {
            _url = (url ?? "").Trim();
            Title = TitleBarTitle = "Enlace no válido";
            DownloadTitle.Text = "Enlace no válido";
            UrlText.Text = _url;
            Bar.Visibility = Visibility.Collapsed;
            StatusText.Text = "No es un enlace válido de un sitio compatible.";
            SetStatusColor("Danger");
            DetailText.Text = "";
            DetailsExpander.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Collapsed;
            OpenFolderBtn.IsEnabled = false;
            CloseActionBtn.Content = "Cerrar";
            CloseActionBtn.Click -= OnCloseAction;
            CloseActionBtn.Click += (_, _) => Close();
            return;
        }

        DownloadTitle.Text = audio ? "Descargar audio" : "Descargar vídeo";
        Title = TitleBarTitle = audio ? "Descargar audio" : "Descargar vídeo";
        HeaderIcon.Text = audio ? "\uE8D6" : "\uE896";
        UrlText.Text = _url;
        UrlText.ToolTip = _url;

        Loaded += (_, _) =>
        {
            if (_autoStart) Start();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            try { _installCts?.Cancel(); } catch { }
            if (!KillProcess())
                Logger.Warn("yt-dlp sigue en ejecución tras cerrar la ventana de descarga");
        };
    }

    private void Start()
    {
        if (_closed || _running) return;

        try
        {
            string? exe = YtDlp.FindExecutable();
            if (exe == null)
            {
                ShowMissingYtDlp();
                return;
            }

            string folder = YtDlp.EnsureDownloadFolder();
            var args = YtDlp.BuildArguments(_url, _audio, folder);

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = folder
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process = process;
            _processStarted = false;
            // Cada evento comprueba que siga siendo el proceso actual: los de un
            // proceso cancelado no deben pisar el estado de un reintento nuevo.
            process.OutputDataReceived += (_, e) =>
            {
                string? data = e.Data;
                if (data != null)
                    Post(() => { if (ReferenceEquals(_process, process)) OnLogLine(data); });
            };
            process.ErrorDataReceived += (_, e) =>
            {
                string? data = e.Data;
                if (data != null)
                    Post(() => { if (ReferenceEquals(_process, process)) OnLogLine(data, error: true); });
            };
            process.Exited += (_, _) => Post(() => OnExited(process));

            _running = true;
            _cancelled = false;
            _lastError = "";
            _filePath = null;
            SetOpenFolderPrimary(false);
            MissingPanel.Visibility = Visibility.Collapsed;
            ProgressHost.Visibility = Visibility.Visible;
            Bar.Visibility = Visibility.Visible;
            StatusText.Text = "Preparando…";
            SetStatusColor("TextPrimary");
            if (LogText.Text.Length == 0) LogText.Text = "Iniciando yt-dlp…";
            else AppendLog("— Reintento —");
            DetailText.Text = _audio ? "Audio de la mejor calidad disponible" : "Vídeo hasta 1080p";
            Bar.Value = 0;
            RetryBtn.Visibility = Visibility.Collapsed;
            OpenFolderBtn.IsEnabled = false;
            CloseActionBtn.Content = "Cancelar";
            CloseActionBtn.IsEnabled = true;

            process.Start();
            _processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            if (!KillProcess())
                Logger.Warn("no se pudo detener yt-dlp tras un fallo de inicio");
            ShowError("No se pudo iniciar yt-dlp: " + ex.Message);
        }
    }

    private void OnLogLine(string line, bool error = false)
    {
        if (_closed) return;

        string text = line.Trim();
        if (text.Length == 0) return;

        AppendLog(text);

        // Un error nunca es progreso, aunque su texto incluya un porcentaje:
        // se registra antes de intentar interpretar la barra.
        if (error)
        {
            _lastError = text;
            DetailText.Text = Brief(text);
            return;
        }

        if (text.Contains('%'))
        {
            var match = PercentRegex.Match(text);
            if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct))
            {
                Bar.Value = Math.Clamp(pct, 0, 100);
                StatusText.Text = $"Descargando… {pct:0}%";
                SetStatusColor("TextPrimary");
                return;
            }
        }

        if (text.StartsWith("[download]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[info]", StringComparison.OrdinalIgnoreCase))
        {
            DetailText.Text = text;
            return;
        }

        // Ruta final que imprime yt-dlp con --print after_move:filepath.
        if ((text.Contains(Path.DirectorySeparatorChar) || text.Contains('/')) && File.Exists(text))
        {
            _filePath = text;
            return;
        }

        if (!text.StartsWith("[", StringComparison.Ordinal))
            DetailText.Text = text;
    }

    private void AppendLog(string line)
    {
        try
        {
            var lines = (LogText.Text + "\n" + line).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(8);
            LogText.Text = string.Join("\n", lines);
            LogText.ScrollToEnd();
        }
        catch { }
    }

    private void OnExited(Process process)
    {
        if (_closed || !ReferenceEquals(_process, process))
        {
            // Evento de un proceso ya reemplazado, cancelado o de una ventana
            // cerrada: liberarlo sin tocar el estado del proceso actual.
            try { process.Dispose(); } catch { }
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _processStarted = false;
            }
            return;
        }

        _running = false;
        int exitCode;
        try { exitCode = process.ExitCode; }
        catch { exitCode = -1; }
        try { process.Dispose(); } catch { }
        _process = null;
        _processStarted = false;

        if (_cancelled)
        {
            StatusText.Text = "Cancelado.";
            SetStatusColor("TextPrimary");
            DetailText.Text = "";
            CloseActionBtn.Content = "Cerrar";
            CloseActionBtn.IsEnabled = true;
            Bar.Value = 0;
            OpenFolderBtn.IsEnabled = false;
            SetOpenFolderPrimary(false);
            RetryBtn.Visibility = Visibility.Visible;
            return;
        }

        bool ok = exitCode == 0;
        CloseActionBtn.Content = "Cerrar";
        CloseActionBtn.IsEnabled = true;

        if (ok)
        {
            Bar.Value = 100;
            StatusText.Text = "Listo. El archivo está en tu carpeta de descargas.";
            SetStatusColor("TextPrimary");
            DetailText.Text = _filePath ?? YtDlp.DownloadFolder;
            OpenFolderBtn.IsEnabled = true;
            SetOpenFolderPrimary(true);
            RetryBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowError(string.IsNullOrWhiteSpace(_lastError)
                ? "yt-dlp terminó con un error. Revisa el enlace e inténtalo de nuevo."
                : _lastError);
        }
    }

    private void ShowError(string message)
    {
        _running = false;
        StatusText.Text = "No se pudo descargar";
        SetStatusColor("Danger");
        DetailText.Text = Brief(message);
        CloseActionBtn.Content = "Cerrar";
        CloseActionBtn.IsEnabled = true;
        OpenFolderBtn.IsEnabled = false;
        SetOpenFolderPrimary(false);
        Bar.Value = 0;
        RetryBtn.Visibility = Visibility.Visible;
    }

    private void ShowMissingYtDlp()
    {
        _running = false;
        ProgressHost.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;
        InstallBtn.IsEnabled = true;
        InstallStatus.Text = "";
        CloseActionBtn.Content = "Cerrar";
        CloseActionBtn.IsEnabled = true;
        OpenFolderBtn.IsEnabled = false;
        SetOpenFolderPrimary(false);
        RetryBtn.Visibility = Visibility.Collapsed;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_installing || _closed) return;

        _installing = true;
        _cancelled = false;
        InstallBtn.IsEnabled = false;
        InstallStatus.Text = "Descargando herramienta…";
        ProgressHost.Visibility = Visibility.Visible;
        MissingPanel.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        StatusText.Text = "Descargando yt-dlp…";
        SetStatusColor("TextPrimary");
        DetailText.Text = "";
        RetryBtn.Visibility = Visibility.Collapsed;
        CloseActionBtn.Content = "Cancelar";
        CloseActionBtn.IsEnabled = true;

        using var cts = new CancellationTokenSource();
        _installCts = cts;
        try
        {
            var progress = new Progress<double>(pct =>
            {
                // Un callback tardío de la instalación no debe pisar el progreso
                // de la descarga que ya empezó.
                if (_closed || _cancelled || !_installing) return;
                Bar.Value = Math.Clamp(pct, 0, 100);
                if (pct >= 99.5)
                {
                    InstallStatus.Text = "Verificando instalación…";
                    StatusText.Text = "Verificando instalación…";
                }
                else
                {
                    InstallStatus.Text = $"Descargando herramienta… {pct:0}%";
                    StatusText.Text = $"Descargando herramienta… {pct:0}%";
                }
            });

            await YtDlp.InstallAsync(progress, cts.Token);
            _installing = false;
            if (_closed) return;
            if (cts.IsCancellationRequested)
            {
                // La cancelación llegó cuando la descarga ya había terminado: no
                // dejar «Cancelando…» con el botón deshabilitado para siempre.
                ShowMissingCancelled();
                return;
            }

            InstallStatus.Text = "yt-dlp instalado.";
            MissingPanel.Visibility = Visibility.Collapsed;
            Start();
        }
        catch (OperationCanceledException)
        {
            if (_closed) return;
            ShowMissingCancelled();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            if (_cancelled || cts.IsCancellationRequested)
            {
                // Un fallo de red provocado por el propio Cancelar no es un error
                // de instalación: el usuario ya decidió abortar.
                ShowMissingCancelled();
                return;
            }
            ProgressHost.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Visible;
            InstallBtn.IsEnabled = true;
            InstallStatus.Text = "No se pudo instalar: " + ex.Message;
            CloseActionBtn.Content = "Cerrar";
            CloseActionBtn.IsEnabled = true;
            StatusText.Text = "No se pudo instalar yt-dlp";
            SetStatusColor("Danger");
            DetailText.Text = ex.Message;
            RetryBtn.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _installing = false;
            if (ReferenceEquals(_installCts, cts)) _installCts = null;
        }
    }

    private void ShowMissingCancelled()
    {
        ProgressHost.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;
        InstallBtn.IsEnabled = true;
        InstallStatus.Text = "Instalación cancelada.";
        StatusText.Text = "Instalación cancelada.";
        SetStatusColor("TextPrimary");
        DetailText.Text = "";
        Bar.Value = 0;
        CloseActionBtn.Content = "Cerrar";
        CloseActionBtn.IsEnabled = true;
        RetryBtn.Visibility = Visibility.Collapsed;
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_closed || _running || _installing) return;

        RetryBtn.Visibility = Visibility.Collapsed;
        _cancelled = false;
        _lastError = "";
        _filePath = null;
        StatusText.Text = "Preparando…";
        SetStatusColor("TextPrimary");
        DetailText.Text = "";
        Bar.Value = 0;
        Bar.Visibility = Visibility.Visible;
        ProgressHost.Visibility = Visibility.Visible;
        MissingPanel.Visibility = Visibility.Collapsed;
        CloseActionBtn.Content = "Cancelar";
        CloseActionBtn.IsEnabled = true;
        OpenFolderBtn.IsEnabled = false;
        SetOpenFolderPrimary(false);
        Start();
    }

    private void OnOpenYtDlpPage(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(YtDlp.ProjectUrl) { UseShellExecute = true }); }
        catch { }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => YtDlp.OpenFolder(_filePath);

    private void OnSupportedSites(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            "Sitios compatibles:\n\n" + YtDlp.SupportedSitesText +
            "\n\nLa lista completa de yt-dlp está en github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md",
            "Sitios compatibles", System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void OnCloseAction(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            _cancelled = true;
            try { _installCts?.Cancel(); } catch { }
            CloseActionBtn.Content = "Cancelando…";
            CloseActionBtn.IsEnabled = false;
            StatusText.Text = "Cancelando instalación…";
            SetStatusColor("TextPrimary");
            return;
        }

        if (_running)
        {
            _cancelled = true;
            if (!KillProcess())
            {
                // El Kill puede fallar (permisos, proceso protegido). No anunciar
                // «Cancelado» ni habilitar Reintentar mientras yt-dlp siga vivo:
                // se lanzaría una segunda descarga sobre la primera.
                _cancelled = false;
                StatusText.Text = "No se pudo cancelar la descarga.";
                SetStatusColor("Danger");
                DetailText.Text = "yt-dlp sigue en ejecución. Espera a que termine o cierra la ventana.";
                CloseActionBtn.Content = "Cancelar";
                CloseActionBtn.IsEnabled = true;
                return;
            }

            _running = false;
            StatusText.Text = "Cancelado.";
            SetStatusColor("TextPrimary");
            DetailText.Text = "";
            CloseActionBtn.Content = "Cerrar";
            CloseActionBtn.IsEnabled = true;
            Bar.Value = 0;
            OpenFolderBtn.IsEnabled = false;
            RetryBtn.Visibility = Visibility.Visible;
            return;
        }

        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCloseAction(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    /// Mata el proceso actual. Devuelve false si no se pudo confirmar su cierre:
    /// en ese caso NO se descarta la instancia, para no perder el evento Exited
    /// ni fingir una cancelación que no ocurrió.
    /// </summary>
    private bool KillProcess()
    {
        var process = _process;
        if (process == null) return true;

        bool exited;
        try { exited = process.HasExited; }
        catch { exited = !_processStarted; }

        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                exited = true;
            }
            catch
            {
                // Pudo terminar entre HasExited y Kill: comprobar de nuevo antes
                // de declarar que no se pudo cancelar.
                try { exited = process.HasExited; }
                catch { exited = false; }
            }
        }

        if (!exited) return false;

        try { process.Dispose(); } catch { }
        if (ReferenceEquals(_process, process))
        {
            _process = null;
            _processStarted = false;
        }
        return true;
    }

    private void Post(Action action)
    {
        try
        {
            if (_closed) return;
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(action);
            else action();
        }
        catch { }
    }

    private void SetStatusColor(string resource) =>
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, resource);

    private void SetOpenFolderPrimary(bool primary)
    {
        if (TryFindResource(primary ? "AccentButton" : "SubtleButton") is Style style)
            OpenFolderBtn.Style = style;
    }

    private static string Brief(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";
}
