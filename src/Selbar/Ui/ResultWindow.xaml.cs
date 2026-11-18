using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Selbar.Core.Actions;
using Selbar.Core.Diagnostics;
using Selbar.Core.Platform;
using Selbar.Native;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Selbar.Ui;

/// <summary>
/// Ventana de resultado: streaming en vivo, vista markdown o texto editable,
/// versiones (cada refinamiento o pregunta crea una), diff original/resultado,
/// mini-chat de seguimiento y atajos de teclado.
/// </summary>
public partial class ResultWindow : FluentWindow
{
    private sealed class VersionEntry
    {
        public string Text = "";
        public string Label = "";
    }

    private readonly OutputMode _output;
    private readonly string? _sourceText;
    private readonly List<VersionEntry> _versions = new();

    private string _raw = "";
    private string _statusText = "Listo";
    private string _countText = "";
    private string _errorMessage = "";
    private string? _reuseConfirmText;
    private bool _viewMode = true;
    private bool _streaming;
    private bool _refinable;
    private bool _showDiff;
    private bool _errorIsAi;
    private int _versionIndex;
    private DispatcherTimer? _renderTimer;
    private Storyboard? _pulse;
    private Storyboard? _progress;
    private double _fontSize;
    private DateTime _startedAt = DateTime.UtcNow;
    private bool _stickToBottom = true;
    private bool _closed;
    private bool _hasError;
    private bool _chatEnabled;
    private readonly bool _canCopy;
    private readonly bool _canReplace;
    private readonly bool _hasSource;

    private Func<Task>? _retry;
    private Func<string, Task>? _chatSend;
    private Func<string, Task>? _refine;

    public event EventHandler? StopRequested;

    /// <summary>Regenerar como versión nueva; solo existe si hay operación conectada.</summary>
    public event Func<Task>? RetryRequested
    {
        add { _retry += value; RefreshCapabilities(); }
        remove { _retry -= value; RefreshCapabilities(); }
    }

    public event Func<string, Task>? ChatSendRequested
    {
        add { _chatSend += value; RefreshCapabilities(); }
        remove { _chatSend -= value; RefreshCapabilities(); }
    }

    /// <summary>El usuario pidió ajustar el texto visible ("más corto", "más formal"...).</summary>
    public event Func<string, Task>? RefineRequested
    {
        add { _refine += value; RefreshCapabilities(); }
        remove { _refine -= value; RefreshCapabilities(); }
    }

    public ResultWindow(string title, string text, OutputMode output, bool streaming = false, string? sourceText = null)
    {
        InitializeComponent();
        Selbar.App.TrackPopup(this);

        Title = string.IsNullOrWhiteSpace(title) ? "Selbar" : "Selbar — " + title;
        TitleBlock.Text = title ?? "";
        _raw = text ?? "";
        _output = output;
        _sourceText = sourceText;

        var settings = Selbar.App.SettingsMgr.Settings;
        _fontSize = Math.Clamp(settings.ResultFontSize, 10, 22);
        Width = Math.Clamp(settings.ResultWidth, 400, 1600);
        Height = Math.Clamp(settings.ResultHeight, 300, 1200);

        _versions.Add(new VersionEntry { Text = _raw, Label = "Resultado" });

        _canReplace = output == OutputMode.Replace;
        _canCopy = output is OutputMode.Replace or OutputMode.Copy or OutputMode.Popup;
        _hasSource = !string.IsNullOrWhiteSpace(sourceText);

        Body.TextChanged += (_, _) =>
        {
            if (_viewMode) return;
            UpdateCount(Body.Text);
            UpdateCommandStates();
        };
        ChatBox.TextChanged += (_, _) => UpdateChatSendState();
        Body.FontSize = _fontSize;
        RenderCurrent();
        UpdateCount(_raw);
        UpdateVersionUi();
        UpdateCommandStates();

        var pos = PopupPlacement.AboveCursor(Width, Height);
        Left = pos.X;
        Top = pos.Y;

        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateEditButton());
        };

        // Al cambiar de tema, markdown y diff se vuelven a pintar con la paleta nueva.
        Theming.Changed += OnThemeChanged;

        Closed += (_, _) =>
        {
            Theming.Changed -= OnThemeChanged;
            _closed = true;
            try { StopRenderTimer(); StopPulse(); StopProgress(); } catch { }
            try { _statusTimer?.Stop(); _statusTimer = null; } catch { }
            try
            {
                var s = Selbar.App.SettingsMgr.Settings;
                if (WindowState == WindowState.Normal)
                {
                    s.ResultWidth = Width;
                    s.ResultHeight = Height;
                    s.ResultFontSize = _fontSize;
                    Selbar.App.SettingsMgr.Save();
                }
            }
            catch { }
        };


        if (streaming) BeginStreaming();
    }

    /// <summary>Texto actual (el editado si está en modo Texto).</summary>
    public string CurrentText => _viewMode ? _raw : Body.Text;

    /// <summary>Versión visible (1-based) para el contexto de Ajustar/Preguntar.</summary>
    public int VersionNumber => _versionIndex + 1;

    public int VersionCount => _versions.Count;

    /// <summary>Habilita el seguimiento sin mover el foco ni ofrecer controles muertos.</summary>
    public void EnableChat()
    {
        Dispatch(() =>
        {
            try
            {
                if (_closed) return;
                _chatEnabled = true;
                UpdateCommandStates();
                UpdateChatSendState();
            }
            catch { }
        });
    }

    /// <summary>Devuelve la pregunta al campo si un turno falló antes de producir texto.</summary>
    public void RestoreChatDraft(string? draft)
    {
        if (string.IsNullOrEmpty(draft)) return;
        Dispatch(() =>
        {
            try
            {
                if (_closed) return;
                if (ChatBox.Text.Length == 0) ChatBox.Text = draft;
                UpdateChatSendState();
            }
            catch { }
        });
    }

    /// <summary>Activa Ajustar solo cuando hay un callback real para el refinamiento.</summary>
    public void SetRefinable(bool value)
    {
        _refinable = value;
        Dispatch(UpdateCommandStates);
    }

    /// <summary>Invoke tolerante: al cerrar/salir, ignorar en vez de lanzar.</summary>
    private void Dispatch(Action action)
    {
        try
        {
            var dispatcher = Dispatcher;
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch { }
    }

    // ---------------- API de streaming (thread-safe) ----------------

    public void BeginStreaming(string model = "")
    {
        _startedAt = DateTime.UtcNow;
        Dispatch(() =>
        {
            if (_closed) return;
            // Si alguien inicia streaming estando en modo Texto, no perder lo editado.
            if (!_viewMode) _raw = Body.Text;
            EnterReadMode();
            _streaming = true;
            _hasError = false;
            _errorMessage = "";
            _errorIsAi = false;
            _countText = "";
            ClearErrorNotice();
            SetStatus("Escribiendo…" + (string.IsNullOrEmpty(model) ? "" : " · " + model), AccentBrush());
            StartPulse();
            StartProgress();
            UpdateCommandStates();
            // Las flechas de versión se deshabilitan al generar: NavigateVersion
            // ya las ignora en streaming, así que dejarlas activas era un control
            // que respondía al clic sin hacer nada.
            UpdateVersionUi();
            UpdateChatSendState();
        });
    }

    /// <summary>
    /// Empieza una versión nueva (ajuste, pregunta o reintento): guarda el texto
    /// visible de la actual —también las ediciones— y deja una versión limpia.
    /// Si la actual está vacía (un intento fallido sin tokens) se reutiliza para
    /// no acumular versiones fantasma. Debe llamarse antes de BeginStreaming.
    /// </summary>
    public void BeginVersion(string label)
    {
        Dispatch(() =>
        {
            if (_closed || _streaming) return;

            // En modo Texto las ediciones viven en Body: capturarlas antes de vaciar.
            string current = CurrentText;
            string caption = string.IsNullOrWhiteSpace(label) ? "Versión" : label.Trim();
            EnterReadMode();

            if (string.IsNullOrWhiteSpace(current))
            {
                _versions[_versionIndex].Text = "";
                _versions[_versionIndex].Label = caption;
            }
            else
            {
                _versions[_versionIndex].Text = current;
                _versions.Add(new VersionEntry { Label = caption });
                _versionIndex = _versions.Count - 1;
            }

            _raw = "";
            _showDiff = false;
            _stickToBottom = true;
            UpdateDiffButton();
            RenderCurrent();
            UpdateVersionUi();
            UpdateCount(_raw);
            UpdateCommandStates();
        });
    }

    /// <summary>Pasa a vista de lectura y deja el editor vacío: el streaming no se concatena.</summary>
    private void EnterReadMode()
    {
        Body.Clear();
        if (!_viewMode)
        {
            _viewMode = true;
            ViewPane.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Collapsed;
        }
        UpdateEditButton();
    }

    public void AppendToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        Dispatch(() =>
        {
            if (_closed) return;
            _raw += token;

            if (_viewMode)
            {
                // Si el usuario subió a leer, no tirar del scroll al llegar texto.
                _stickToBottom = IsViewAtBottom();
                ScheduleRender();
            }
            else
            {
                // Nunca debería ocurrir (BeginStreaming fuerza lectura), pero el
                // editor y _raw deben decir lo mismo si alguien lo fuerza.
                Body.Text = _raw;
                try { Body.CaretIndex = Body.Text.Length; } catch { }
                if (_stickToBottom) { try { Body.ScrollToEnd(); } catch { } }
                UpdateCount(Body.Text);
            }
        });
    }

    public void FinishSuccess(string note = "")
    {
        Dispatch(() =>
        {
            if (_closed) return;
            _streaming = false;
            StopRenderTimer();
            StopPulse();
            StopProgress();
            ClearErrorNotice();
            _hasError = false;
            _versions[_versionIndex].Text = _raw;
            if (_viewMode) RenderKeepingPosition();
            double seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
            SetStatus($"Listo en {seconds:F1} s" + (string.IsNullOrEmpty(note) ? "" : " · " + note), SuccessBrush());
            UpdateCount(_raw);
            UpdateVersionUi();
            UpdateCommandStates();
            UpdateChatSendState();
        });
    }

    public void FinishCancelled()
    {
        Dispatch(() =>
        {
            if (_closed) return;
            _streaming = false;
            StopRenderTimer();
            StopPulse();
            StopProgress();
            ClearErrorNotice();
            _hasError = false;
            _versions[_versionIndex].Text = _raw;
            if (_viewMode) RenderKeepingPosition();
            SetStatus("Detenido (texto parcial conservado)", WarningBrush());
            UpdateCount(_raw);
            UpdateVersionUi();
            UpdateCommandStates();
            UpdateChatSendState();
        });
    }

    /// <summary>
    /// El error se guarda aparte del contenido: la versión conserva solo el texto
    /// parcial real, nunca el diagnóstico. Copiar/reemplazar quedan bloqueados.
    /// </summary>
    public void FinishError(string message)
    {
        Dispatch(() =>
        {
            if (_closed) return;
            _streaming = false;
            StopRenderTimer();
            StopPulse();
            StopProgress();

            string detail = (message ?? "error desconocido").Trim();
            if (detail.Length == 0) detail = "La operación no terminó.";
            _errorMessage = detail;
            _errorIsAi = _retry != null;
            _hasError = true;

            // Texto parcial, sin añadir el diagnóstico al contenido ni a la versión.
            // El error se lee siempre en Vista: si quedara el editor abierto no habría
            // forma de volver (Editar se oculta en error) ni de comparar nada.
            // En modo Texto las ediciones viven en Body: capturarlas antes de que
            // EnterReadMode vacíe el editor para no perderlas.
            if (!_viewMode) _raw = Body.Text;
            _versions[_versionIndex].Text = _raw;
            _showDiff = false;
            EnterReadMode();
            UpdateDiffButton();

            ShowErrorNotice(detail, _errorIsAi);
            // Después de mostrar el aviso: la banda de error cambia el alto del
            // contenido y el desplazamiento hay que calcularlo con el layout final.
            RenderKeepingPosition();
            SetStatus("Error", DangerBrush());
            StatusText.ToolTip = detail;
            UpdateCount(_raw);
            UpdateVersionUi();
            UpdateCommandStates();
            UpdateChatSendState();
        });
    }

    /// <summary>Error de una acción local: mismo estado de error, sin reintento ni IA.</summary>
    public void SetLocalError(string message)
    {
        Dispatch(() =>
        {
            if (_closed) return;
            _streaming = false;
            StopRenderTimer();
            StopPulse();
            StopProgress();

            string detail = (message ?? "Error desconocido").Trim();
            if (detail.Length == 0) detail = "Error desconocido.";
            _errorMessage = detail;
            _errorIsAi = false;
            _hasError = true;
            _raw = "";
            _versions[_versionIndex].Text = "";
            _showDiff = false;
            EnterReadMode();
            UpdateDiffButton();
            RenderCurrent();
            ShowErrorNotice(detail, false);
            SetStatus("Error", DangerBrush());
            StatusText.ToolTip = detail;
            UpdateCount("");
            UpdateVersionUi();
            UpdateCommandStates();
            UpdateChatSendState();
        });
    }

    /// <summary>Un error de credenciales merece el acceso a Ajustes → IA.</summary>
    private static bool LooksLikeAuthError(string? message)
    {
        string m = message ?? "";
        return m.Contains("401", StringComparison.Ordinal)
            || m.Contains("403", StringComparison.Ordinal)
            || m.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || m.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || m.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || m.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || m.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleRender()
    {
        if (_renderTimer == null)
        {
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _renderTimer.Tick += (_, _) =>
            {
                StopRenderTimer();
                // Un tick ya encolado puede llegar después de Closed.
                if (_closed) return;
                // Nunca volver al principio mientras se genera: si el usuario
                // sigue el texto se queda abajo y, si subió a leer, conserva su
                // sitio en vez de saltar al inicio en cada tick.
                RenderKeepingPosition();
                UpdateCount(_raw);
            };
        }
        if (!_renderTimer.IsEnabled) _renderTimer.Start();
    }

    private void StopRenderTimer()
    {
        try { _renderTimer?.Stop(); } catch { }
    }

    // ---------------- Animaciones de estado ----------------

    private void StartPulse()
    {
        try
        {
            StopPulse();
            _pulse = Motion.Pulse(StatusDot, 0.3, 700);
        }
        catch { }
    }

    private void StopPulse()
    {
        try
        {
            _pulse?.Stop();
            _pulse = null;
            StatusDot.Opacity = 1;
        }
        catch { }
    }

    private void StartProgress()
    {
        try
        {
            StreamTrack.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                try
                {
                    // El turno pudo terminar antes de este callback (error inmediato,
                    // cancelar): sin esta guarda quedaría un bucle eterno sobre una
                    // banda ya oculta.
                    if (_closed || !_streaming) return;
                    double width = StreamTrack.ActualWidth;
                    if (width <= 0) width = 420;
                    _progress?.Stop();
                    _progress = Motion.Indeterminate(StreamShift, -140, width, 1150);
                }
                catch { }
            });
        }
        catch { }
    }

    private void StopProgress()
    {
        try
        {
            _progress?.Stop();
            _progress = null;
            StreamTrack.Visibility = Visibility.Collapsed;
            StreamShift.X = -130;
        }
        catch { }
    }

    // ---------------- Render ----------------

    private void OnThemeChanged()
    {
        try { RenderCurrent(); } catch { }
    }

    private void RenderCurrent()
    {
        if (_showDiff && !string.IsNullOrWhiteSpace(_sourceText) && _viewMode && !string.IsNullOrWhiteSpace(_raw))
        {
            try
            {
                ViewPane.Document = DiffRenderer.ToFlowDocument(_sourceText, _raw, _fontSize);
                return;
            }
            catch { }
        }
        RenderView();
    }

    private void RenderView()
    {
        try
        {
            ViewPane.Document = MarkdownRenderer.ToFlowDocument(_raw, _fontSize);
        }
        catch
        {
            ViewPane.Document = new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(_raw)));
        }
    }

    /// <summary>
    /// Re-render conservando el punto de lectura. Asignar ViewPane.Document crea
    /// un FlowDocument nuevo y, según cómo reaccione el ScrollViewer, la vista
    /// puede volver al principio: si el usuario sigue el texto se baja al final
    /// (ScrollToBottom se autocorrige cuando el documento termine de medirse) y,
    /// si había subido a leer, se restaura su desplazamiento tras el layout
    /// (antes de medir, ScrollToVerticalOffset se recortaría al alto anterior).
    /// </summary>
    private void RenderKeepingPosition()
    {
        var scroll = InnerScroll(ViewPane);
        double offset = scroll?.VerticalOffset ?? 0;
        RenderCurrent();
        if (scroll == null) return;

        if (_stickToBottom)
        {
            StickViewToBottom();
            return;
        }

        if (offset <= 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                try
                {
                    if (_closed) return;
                    scroll.ScrollToVerticalOffset(offset);
                }
                catch { }
            });
        }
        catch { }
    }

    private void ApplyFontSize()
    {
        Body.FontSize = _fontSize;
        if (_viewMode) RenderCurrent();
        UpdateCount(_viewMode ? _raw : Body.Text);
    }

    /// <summary>Zoom Ctrl + / Ctrl − (persiste en Ajustes).</summary>
    public void Zoom(int steps)
    {
        _fontSize = Math.Clamp(_fontSize + steps, 10, 22);
        Selbar.App.SettingsMgr.Settings.ResultFontSize = _fontSize;
        Selbar.App.SettingsMgr.Save();
        // El porcentaje ya va en el contador (UpdateCount). Repetirlo en el estado
        // dejaba «Zoom 110 % · … · 110 %» y, en error o streaming, pisaba el
        // mensaje real («Error», «Escribiendo…») durante 1,6 s.
        ApplyFontSize();
    }

    public void ZoomReset()
    {
        _fontSize = 13.5;
        Selbar.App.SettingsMgr.Settings.ResultFontSize = _fontSize;
        Selbar.App.SettingsMgr.Save();
        ApplyFontSize();
    }

    private DispatcherTimer? _statusTimer;

    /// <summary>Deja el mensaje de estado un momento y vuelve a "Listo".</summary>
    private void StartTemporaryStatusRestore()
    {
        try
        {
            _statusTimer?.Stop();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer?.Stop();
                if (_streaming || _closed) return;
                if (_hasError) SetStatus("Error", DangerBrush());
                else SetStatus("Listo", SuccessBrush());
            };
            _statusTimer.Start();
        }
        catch { }
    }

    // ---------------- Versiones / diff ----------------

    private void UpdateVersionUi()
    {
        try
        {
            VersionNav.Visibility = _versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            VersionLabel.Text = $"{_versionIndex + 1}/{_versions.Count}";
            VersionLabel.ToolTip = _versions[_versionIndex].Label;
            ChatPlaceholder.Text = _versions.Count > 1
                ? $"Pregunta sobre la versión {_versionIndex + 1} de {_versions.Count}…"
                : "Pregunta de seguimiento…";
            VersionPrev.IsEnabled = _versionIndex > 0 && !_streaming && !_hasError;
            VersionNext.IsEnabled = _versionIndex < _versions.Count - 1 && !_streaming && !_hasError;
        }
        catch { }
    }

    private void NavigateVersion(int delta)
    {
        if (_streaming || _hasError) return;
        int next = Math.Clamp(_versionIndex + delta, 0, _versions.Count - 1);
        if (next == _versionIndex) return;

        // CurrentText, no _raw: en modo Texto las ediciones viven en Body
        // y _raw estaría obsoleto (se perderían al cambiar de versión).
        _versions[_versionIndex].Text = CurrentText;
        _versionIndex = next;
        _raw = _versions[next].Text;
        _showDiff = false;
        UpdateDiffButton();
        if (_viewMode) RenderCurrent(); else Body.Text = _raw;
        UpdateCount(_raw);
        UpdateVersionUi();
        UpdateCommandStates();
        SetStatus($"Versión {_versionIndex + 1} de {_versions.Count} · {_versions[_versionIndex].Label}", AccentBrush());
        StartTemporaryStatusRestore();
    }

    private void OnPrevVersion(object s, RoutedEventArgs e) => NavigateVersion(-1);

    private void OnNextVersion(object s, RoutedEventArgs e) => NavigateVersion(1);

    private void OnToggleDiff(object s, RoutedEventArgs e)
    {
        // Comparar exige un texto terminado: el diff de un parcial cambia en cada
        // token y deja el estado diciendo «Comparando» mientras aún se genera.
        if (_hasError || _streaming || string.IsNullOrEmpty(_sourceText)) return;
        if (!_viewMode) OnViewMode(this, new RoutedEventArgs());

        _showDiff = !_showDiff;
        UpdateDiffButton();
        RenderCurrent();
        if (_showDiff) SetStatus("Comparando con el original", AccentBrush());
        else SetStatus("Listo", SuccessBrush());
        StartTemporaryStatusRestore();
    }

    private void UpdateDiffButton()
    {
        try
        {
            if (_hasError || _streaming)
            {
                DiffBtn.Visibility = Visibility.Collapsed;
                return;
            }

            DiffBtn.Visibility = _hasSource && !string.IsNullOrWhiteSpace(_raw)
                ? Visibility.Visible
                : Visibility.Collapsed;
            DiffBtn.Content = _showDiff ? "Ver resultado" : "Ver cambios";
            DiffBtn.Foreground = _showDiff ? AccentBrush() : TryBrush("TextSecondary", Color.FromRgb(0xC7, 0xC7, 0xC7));
        }
        catch { }
    }

    // ---------------- Ventana / teclado ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        var mods = Keyboard.Modifiers;
        bool textFocused = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        bool buttonFocused = Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase;

        // Enter con un botón enfocado es del botón (activación nativa), no de la ventana.
        if (e.Key == Key.Enter && mods == ModifierKeys.None)
        {
            if (textFocused || buttonFocused) return;
            if (CurrentCapabilities().CanCopy)
            {
                // Solo cerrar si el portapapeles confirmó el copiado.
                if (CopyText(CurrentPlainText(), "Copiado como texto")) Close();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Enter = reemplazar y cerrar, solo si el estado lo permite. En modo
        // Texto el foco está en el editor y ese es justo el texto a reemplazar:
        // ahí el atajo debe funcionar igual que el botón, que sí está visible.
        if (e.Key == Key.Enter && mods == ModifierKeys.Control)
        {
            bool inEditor = ReferenceEquals(Keyboard.FocusedElement, Body);
            if (textFocused && !inEditor) return;
            if (CurrentCapabilities().CanReplace) OnReplace(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    Zoom(1); e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract:
                    Zoom(-1); e.Handled = true; break;
                case Key.D0:
                case Key.NumPad0:
                    ZoomReset(); e.Handled = true; break;
                case Key.S:
                    if (CurrentCapabilities().CanSave) OnSaveFile(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.R:
                    // Sin callback (resultado local/OCR) no hace nada: no vacía nada.
                    if (!textFocused && !_streaming && _retry != null) OnRetry(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            if (CurrentCapabilities().CanCopy) OnCopy(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private bool IsViewAtBottom()
    {
        try
        {
            var scroll = InnerScroll(ViewPane);
            if (scroll == null) return true;
            return scroll.ScrollableHeight <= 0 || scroll.VerticalOffset >= scroll.ScrollableHeight - 30;
        }
        catch { return true; }
    }

    private void StickViewToBottom()
    {
        try { InnerScroll(ViewPane)?.ScrollToBottom(); } catch { }
    }

    private static System.Windows.Controls.ScrollViewer? InnerScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer scroll) return scroll;
            var found = InnerScroll(child);
            if (found != null) return found;
        }
        return null;
    }

    // ---------------- Acciones ----------------

    private void OnSaveFile(object s, RoutedEventArgs e)
    {
        if (!CurrentCapabilities().CanSave) return;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md|Texto (*.txt)|*.txt",
                FileName = MakeFileName(TitleBlock.Text),
                DefaultExt = ".md",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true) return;

            string content = dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? MarkdownRenderer.ToPlainText(CurrentText)
                : CurrentText;
            File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(false));
            SetStatus("Guardado en " + System.IO.Path.GetFileName(dialog.FileName), SuccessBrush());
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo guardar: " + ex.Message, DangerBrush());
        }
    }

    private static string MakeFileName(string title)
    {
        var clean = string.Concat((title ?? "resultado")
            .Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (clean.Length == 0) clean = "resultado";
        if (clean.Length > 40) clean = clean[..40];
        return clean;
    }

    /// <summary>
    /// Encadena acciones: cierra esto y abre la barra con el resultado como
    /// texto, para traducir una traducción, resumir un resumen, etc.
    /// </summary>
    private void OnReuseAsSelection(object s, RoutedEventArgs e)
    {
        try
        {
            string full = (CurrentText ?? "").Trim();
            if (full.Length == 0) return;

            var limited = ResultTurn.LimitForReuse(full, ResultTurn.ReuseLimit);
            if (limited.Truncated && !string.Equals(_reuseConfirmText, full, StringComparison.Ordinal))
            {
                // Sin recorte silencioso: la ventana sigue abierta y el segundo
                // intento confirma explícitamente el alcance del texto.
                _reuseConfirmText = full;
                SetStatus(
                    $"El texto tiene {full.Length:N0} caracteres; se usarán los primeros {ResultTurn.ReuseLimit:N0}. " +
                    "Vuelve a elegir «Usar como selección» para continuar.",
                    WarningBrush());
                return;
            }

            string reuse = limited.Text;
            bool hasActions = false;
            try { hasActions = Selbar.App.Registry.GetVisibleActions(reuse).Count > 0; }
            catch (Exception ex) { Selbar.Core.Diagnostics.Logger.LogEx(ex, "reutilizar resultado"); }
            if (!hasActions)
            {
                SetStatus("Nada que sugerir con este texto", DangerBrush());
                return;
            }

            Close();
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Selbar.App.ShowBar(reuse); }
                    catch (Exception ex) { Selbar.Core.Diagnostics.Logger.LogEx(ex, "reutilizar resultado"); }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { Selbar.Core.Diagnostics.Logger.LogEx(ex, "reutilizar resultado"); }
        }
        catch (Exception ex)
        {
            Selbar.Core.Diagnostics.Logger.LogEx(ex, "reutilizar resultado");
        }
    }

    private void OnViewMode(object s, RoutedEventArgs e)
    {
        if (_viewMode) return;
        _raw = Body.Text;
        _viewMode = true;
        _showDiff = false;
        UpdateDiffButton();
        ViewPane.Visibility = Visibility.Visible;
        Body.Visibility = Visibility.Collapsed;
        RenderCurrent();
        UpdateCount(_raw);
        UpdateVersionUi();
        UpdateEditButton();
    }

    private void OnEditMode(object s, RoutedEventArgs e)
    {
        if (!_viewMode) return;
        // Sin edición durante el streaming: el siguiente token pisaría el texto.
        if (_streaming || _hasError) return;
        Body.Text = _raw;
        _viewMode = false;
        _showDiff = false;
        UpdateDiffButton();
        ViewPane.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        UpdateCount(_raw);
        UpdateEditButton();
        Body.Focus();
        Body.CaretIndex = Body.Text.Length;
    }

    /// <summary>
    /// Un solo botón en vez de dos pestañas: "Editar" pasa a texto plano y
    /// "Vista previa" vuelve al formato. El modo por defecto es la vista.
    /// </summary>
    private void UpdateEditButton()
    {
        try
        {
            EditLabel.Text = _viewMode ? "Editar" : "Vista previa";
            EditIcon.Text = _viewMode ? "\uE70F" : "\uE890";
            EditToggleBtn.ToolTip = _viewMode
                ? "Edita el texto antes de copiar o reemplazar"
                : "Vuelve a ver el resultado con formato";
            // Durante el streaming el editor está prohibido: cada token haría
            // Body.Text = _raw y borraría lo que el usuario escribiera.
            EditToggleBtn.Visibility = _hasError || _streaming ? Visibility.Collapsed : Visibility.Visible;
            EditToggleBtn.IsEnabled = !_streaming;
        }
        catch { }
    }

    /// <summary>Cambia entre vista con formato y texto editable.</summary>
    private void OnToggleEdit(object s, RoutedEventArgs e)
    {
        if (_hasError || _streaming) return;
        if (_viewMode) OnEditMode(this, new RoutedEventArgs());
        else OnViewMode(this, new RoutedEventArgs());
    }

    /// <summary>Abre el menú de opciones secundarias con clic izquierdo.</summary>
    private void OnMore(object s, RoutedEventArgs e)
    {
        try
        {
            // El estado de cada fila se decide en un solo lugar antes de abrir.
            UpdateCommandStates();
            if (MoreBtn.ContextMenu is not { } menu) return;
            menu.PlacementTarget = MoreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch { }
    }

    /// <summary>También al abrir con clic derecho: el menú refleja el estado actual.</summary>
    private void OnMoreMenuOpening(object s, System.Windows.Controls.ContextMenuEventArgs e) => UpdateCommandStates();

    private void OnOpenAiSettings(object s, RoutedEventArgs e)
    {
        try { Selbar.App.OpenSettings("ia"); } catch { }
    }

    /// <summary>
    /// Una sola matriz de capacidades para botones, menús y atajos. Durante
    /// streaming o error no se copia ni se reemplaza; Retry/Ajustar/Preguntar
    /// solo aparecen con callback real (OCR sin conversación no ofrece nada muerto).
    /// </summary>
    private ResultCapabilities CurrentCapabilities()
        => ResultCapabilities.For(
            _streaming,
            _hasError,
            _canCopy,
            _canReplace,
            _retry != null,
            _refinable && _refine != null,
            _chatSend != null && _chatEnabled,
            !string.IsNullOrWhiteSpace(CurrentText));

    private bool AiCapable => _retry != null || _refine != null || _chatSend != null;

    private void RefreshCapabilities() => Dispatch(UpdateCommandStates);

    private void UpdateCommandStates()
    {
        try
        {
            var caps = CurrentCapabilities();

            var accent = (Style)FindResource("AccentButton");
            var subtle = (Style)FindResource("SubtleButton");

            CopyBtn.Visibility = caps.CanCopy ? Visibility.Visible : Visibility.Collapsed;
            ReplaceBtn.Visibility = caps.CanReplace ? Visibility.Visible : Visibility.Collapsed;
            RetryBtn.Visibility = caps.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            StopBtn.Visibility = caps.CanStop ? Visibility.Visible : Visibility.Collapsed;
            // Editar/Ver cambios tampoco existen mientras se genera (ver UpdateEditButton
            // y UpdateDiffButton): el editor se vaciaría con cada token y el diff se
            // recalcularía sobre texto a medias.
            DiffBtn.Visibility = !_hasError && !_streaming && _hasSource && !string.IsNullOrWhiteSpace(_raw)
                ? Visibility.Visible
                : Visibility.Collapsed;
            RefinePanel.Visibility = caps.CanRefine ? Visibility.Visible : Visibility.Collapsed;
            ChatRow.Visibility = caps.CanChat ? Visibility.Visible : Visibility.Collapsed;
            ErrorSettingsBtn.Visibility = _hasError && _errorIsAi ? Visibility.Visible : Visibility.Collapsed;

            // Una sola CTA acentuada: Reemplazar o Copiar en reposo; Detener al
            // generar; Reintentar ante error.
            ReplaceBtn.Style = accent;
            CopyBtn.Style = caps.CanReplace ? subtle : accent;
            StopBtn.Style = accent;
            RetryBtn.Style = _hasError ? accent : subtle;
            RetryBtn.Content = _hasError ? "Reintentar" : "Regenerar";
            RetryBtn.ToolTip = _hasError
                ? "Reintenta la última petición como versión nueva (Ctrl+R)"
                : "Genera otra versión con la misma petición (Ctrl+R)";

            ViewCopyMenuItem.IsEnabled = caps.CanCopy;
            ViewSaveMenuItem.IsEnabled = caps.CanSave;
            ViewReplaceMenuItem.IsEnabled = caps.CanReplace;
            MoreCopyMarkdownItem.IsEnabled = caps.CanCopy;
            MoreSaveItem.IsEnabled = caps.CanSave;
            MoreReuseItem.IsEnabled = caps.CanReuse;
            MoreAiSettingsItem.IsEnabled = !_streaming;
            MoreAiSettingsItem.Visibility = AiCapable ? Visibility.Visible : Visibility.Collapsed;
            MoreAiSeparator.Visibility = AiCapable ? Visibility.Visible : Visibility.Collapsed;

            bool partial = _hasError && !string.IsNullOrWhiteSpace(_raw);
            MoreCopyPartialItem.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
            MoreCopyPartialItem.IsEnabled = partial;

            UpdateEditButton();
        }
        catch { }
    }

    private void ClearErrorNotice()
    {
        try
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            ErrorDetailsPanel.Visibility = Visibility.Collapsed;
            ErrorDetailsBtn.Content = "Detalles";
            StatusText.ToolTip = null;
        }
        catch { }
    }

    private void ShowErrorNotice(string detail, bool ai)
    {
        try
        {
            string first = (detail ?? "").Split('\n')[0].Trim();
            if (first.Length > 200) first = first[..200].TrimEnd() + "…";
            ErrorText.Text = first.Length == 0 ? "La operación no terminó." : first;
            ErrorDetailsBox.Text = detail ?? "";
            ErrorDetailsPanel.Visibility = Visibility.Collapsed;
            ErrorDetailsBtn.Content = "Detalles";
            ErrorPanel.Visibility = Visibility.Visible;

            bool partial = !string.IsNullOrWhiteSpace(_raw);
            if (LooksLikeAuthError(detail) && ai)
                ErrorHint.Text = "Revisa la API key y el modelo en Ajustes de IA.";
            else if (partial)
                ErrorHint.Text = "El texto parcial se conserva: puedes copiarlo desde Más.";
            else if (ai)
                ErrorHint.Text = "Puedes reintentar sin perder las versiones anteriores.";
            else
                ErrorHint.Text = "No se modificó el resultado: corrige la causa y vuelve a intentarlo.";
            ErrorHint.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void OnToggleErrorDetails(object s, RoutedEventArgs e)
    {
        bool open = ErrorDetailsPanel.Visibility != Visibility.Visible;
        ErrorDetailsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ErrorDetailsBtn.Content = open ? "Ocultar" : "Detalles";
    }

    /// <summary>Copiar el diagnóstico con nombre explícito; nunca el texto parcial.</summary>
    private void OnCopyErrorDetails(object s, RoutedEventArgs e)
    {
        if (!_hasError || string.IsNullOrWhiteSpace(_errorMessage)) return;
        CopyText(_errorMessage, "Copiado el detalle del error");
    }

    private void SetStatus(string text, Brush dot)
    {
        _statusText = text ?? "";
        StatusDot.Fill = dot;
        ApplyStatus();
    }

    /// <summary>Línea de estado = mensaje + contador (así el pie queda libre para los botones).</summary>
    private void ApplyStatus()
    {
        try
        {
            // Un error no mezcla su mensaje con el contador de palabras.
            StatusText.Text = _hasError || string.IsNullOrEmpty(_countText)
                ? _statusText
                : _statusText + " · " + _countText;
        }
        catch { }
    }

    private Brush AccentBrush() => TryBrush("AccentText", Color.FromRgb(0x60, 0xCD, 0xFF));
    private Brush SuccessBrush() => TryBrush("Success", Color.FromRgb(0x6C, 0xCB, 0x5F));
    private Brush WarningBrush() => TryBrush("Warning", Color.FromRgb(0xF2, 0xC1, 0x4E));
    private Brush DangerBrush() => TryBrush("Danger", Color.FromRgb(0xFF, 0x99, 0xA4));

    private Brush TryBrush(string key, Color fallback)
    {
        try { return (Brush)FindResource(key); }
        catch { return new SolidColorBrush(fallback); }
    }

    private void UpdateCount(string text)
    {
        var words = (text ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        int pct = (int)Math.Round(_fontSize / 13.5 * 100);
        _countText = $"{words} palabras · {(text ?? "").Length} caracteres" + (pct == 100 ? "" : $" · {pct} %");
        ApplyStatus();
    }

    private void UpdateChatSendState()
    {
        try
        {
            if (ChatSendBtn == null || ChatBox == null) return;
            ChatSendBtn.IsEnabled = CurrentCapabilities().CanChat && ChatBox.Text.Trim().Length > 0;
        }
        catch { }
    }

    private void OnStop(object s, RoutedEventArgs e)
    {
        // Un suscriptor que falle no puede tumbar la ventana desde un handler de clic.
        try { StopRequested?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Selbar.Core.Diagnostics.Logger.LogEx(ex, "detener resultado"); }
    }

    private void OnChatSend(object s, RoutedEventArgs e) => SendChat();

    private void OnChatKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Mayús+Enter inserta salto de línea (lo hace el TextBox); Enter envía.
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            SendChat();
        }
    }

    private async void SendChat()
    {
        var text = ChatBox.Text.Trim();
        if (text.Length == 0 || _streaming || _hasError || _chatSend == null) return;

        ChatBox.Clear();
        UpdateChatSendState();

        try { await _chatSend.Invoke(text); }
        catch (Exception ex)
        {
            // Nunca descartar la pregunta si el turno no llegó a empezar.
            if (ChatBox.Text.Length == 0) ChatBox.Text = text;
            FinishError(ex.Message);
        }
    }

    private async void OnRefine(object sender, RoutedEventArgs e)
    {
        if (_streaming || _hasError || _refine == null) return;
        if (sender is not FrameworkElement element || element.Tag is not string instruction) return;

        // Todo dentro del try: un fallo aquí, en un async void, cerraba la app.
        try
        {
            instruction = instruction.Replace("{lang}", Selbar.App.SettingsMgr.Settings.Ai.TargetLang);
            await _refine.Invoke(instruction);
        }
        catch (Exception ex) { FinishError(ex.Message); }
    }

    private void OnRefineMenu(object s, RoutedEventArgs e)
    {
        try
        {
            if (RefineMenuBtn.ContextMenu is not { } menu) return;
            menu.PlacementTarget = RefineMenuBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch { }
    }

    private async void OnRetry(object s, RoutedEventArgs e)
    {
        if (_streaming || _retry == null) return;

        // No se toca ninguna versión aquí: ActionRunner abre una versión nueva
        // (BeginVersion) y luego llama a BeginStreaming. Así no hay doble limpieza.
        try { await _retry.Invoke(); }
        catch (Exception ex) { FinishError(ex.Message); }
    }

    private void OnReplace(object s, RoutedEventArgs e)
    {
        // Guarda central: nunca reemplazar un error, un streaming o texto vacío.
        if (!CurrentCapabilities().CanReplace) return;

        // Se pega texto plano: el markdown (**negritas**, listas...) se vería literal.
        var text = CurrentPlainText();
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("No hay texto que reemplazar", DangerBrush());
            return;
        }
        if (!ClipboardService.TrySetText(text))
        {
            SetStatus("No se pudo usar el portapapeles (¿otra app lo tiene bloqueado?)", DangerBrush());
            return;
        }

        // La ventana tiene el foco: ocultarla, devolvérselo a la app de origen y pegar.
        Hide();
        if (!ForegroundTracker.Restore(140))
        {
            // Origen cerrado o inválido: no pegar a ciegas en otra ventana.
            Show();
            SetStatus("Copiado: no se pudo devolver el foco, pega con Ctrl+V", DangerBrush());
            return;
        }
        NativeMethods.SendCtrlV();
        Close();
    }

    /// <summary>Texto sin marcas de markdown (lo que se ve en Vista).</summary>
    private string CurrentPlainText()
    {
        if (_viewMode)
        {
            try { return MarkdownRenderer.ToPlainText(_raw); }
            catch { return _raw; }
        }
        return Body.Text;
    }

    private void OnCopy(object s, RoutedEventArgs e)
    {
        if (!CurrentCapabilities().CanCopy) return;
        CopyText(CurrentPlainText(), "Copiado como texto");
    }

    private void OnCopyMarkdown(object s, RoutedEventArgs e)
    {
        if (!CurrentCapabilities().CanCopy) return;
        CopyText(CurrentText, "Copiado como markdown");
    }

    /// <summary>Copia explícita del texto parcial; el diagnóstico no se mezcla.</summary>
    private void OnCopyPartial(object s, RoutedEventArgs e)
    {
        if (!_hasError || string.IsNullOrWhiteSpace(_raw)) return;
        CopyText(MarkdownRenderer.ToPlainText(_raw), "Copiado el texto parcial");
    }

    private bool CopyText(string text, string note)
    {
        if (ClipboardService.TrySetText(text))
        {
            SetStatus(note, SuccessBrush());
            StartTemporaryStatusRestore();
            return true;
        }

        SetStatus("No se pudo copiar (portapapeles ocupado)", DangerBrush());
        return false;
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();
}
