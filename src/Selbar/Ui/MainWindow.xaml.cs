using System.Windows;
using System.Windows.Forms; // NotifyIcon (WinForms)
using System.Windows.Interop;
using Selbar.Core.Diagnostics;
using Selbar.Core.Platform;
using Selbar.Native;

namespace Selbar.Ui;

/// <summary>Ventana oculta: tray icon + hotkey global (RegisterHotKey) + segunda instancia.</summary>
public partial class MainWindow : Window
{
    private readonly NotifyIcon _tray = new();
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _pauseTimedItem;
    private ToolStripMenuItem? _dictationItem;
    private ToolStripMenuItem? _adjustItem;
    private ContextMenuStrip? _menu;
    private System.Windows.Threading.DispatcherTimer? _pauseTimer;
    private DateTime? _pausedUntil;
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;
    private bool _hotkeyWarned;
    private bool _dictationWarned;
    private bool _registeringHotkeys;

    public MainWindow()
    {
        InitializeComponent();

        _tray.Text = "Selbar";
        _tray.Icon = TrayIcon.Get();
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => Selbar.App.OpenSettings();

        var menu = new ContextMenuStrip();
        menu.ImageScalingSize = new System.Drawing.Size(16, 16);

        // Frecuentes en la raíz: ajustes, historiales, dictado y OCR.
        _adjustItem = new ToolStripMenuItem("Ajustes…", TrayIcon.GlyphBitmap("\uE713"),
            (_, _) => Selbar.App.OpenSettings()) { Tag = "\uE713" };
        menu.Items.Add(_adjustItem);
        menu.Items.Add(new ToolStripMenuItem("Historial…", TrayIcon.GlyphBitmap("\uE81C"),
            (_, _) => Selbar.App.OpenHistory()) { Tag = "\uE81C" });
        menu.Items.Add(new ToolStripMenuItem("Portapapeles…", TrayIcon.GlyphBitmap("\uE8C8"),
            (_, _) => Selbar.App.OpenClipboardHistory()) { Tag = "\uE8C8" });
        menu.Items.Add(new ToolStripSeparator());

        _dictationItem = new ToolStripMenuItem("Dictar (grabar → transcribir)", TrayIcon.GlyphBitmap("\uE720"),
            (_, _) => _ = Selbar.App.ToggleDictationAsync()) { Tag = "\uE720" };
        menu.Items.Add(_dictationItem);
        menu.Items.Add(new ToolStripMenuItem("Leer texto de una imagen (OCR)", TrayIcon.GlyphBitmap("\uE8A5"),
            (_, _) => _ = Selbar.App.RunOcrFromClipboardAsync()) { Tag = "\uE8A5" });

        // Imagen: familia ocasional, agrupada para no competir con lo diario.
        var imageMenu = new ToolStripMenuItem("Imagen", TrayIcon.GlyphBitmap("\uE8B8")) { Tag = "\uE8B8" };
        imageMenu.DropDownItems.Add(new ToolStripMenuItem("Analizar imagen con IA (visión)",
            TrayIcon.GlyphBitmap("\uE8B8"), (_, _) => _ = Selbar.App.AnalyzeImageFromClipboardAsync())
            { Tag = "\uE8B8" });
        imageMenu.DropDownItems.Add(new ToolStripMenuItem("Buscar imagen en Google Lens",
            TrayIcon.GlyphBitmap("\uE721"), (_, _) => _ = Selbar.App.RunLensFromClipboardAsync())
            { Tag = "\uE721" });
        menu.Items.Add(imageMenu);

        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pausar selección", TrayIcon.GlyphBitmap("\uE769"), (_, _) => TogglePause())
        {
            Tag = "\uE769"
        };
        menu.Items.Add(_pauseItem);

        _pauseTimedItem = new ToolStripMenuItem("Pausar selección temporalmente");
        foreach (var minutes in new[] { 15, 30, 60, 120 })
        {
            int value = minutes;
            _pauseTimedItem.DropDownItems.Add(
                minutes < 60 ? $"{minutes} minutos" : $"{minutes / 60} hora{(minutes >= 120 ? "s" : "")}",
                null, (_, _) => PauseFor(value));
        }
        menu.Items.Add(_pauseTimedItem);

        // Mantenimiento y tareas poco frecuentes: un solo nivel secundario.
        menu.Items.Add(new ToolStripSeparator());
        var moreMenu = new ToolStripMenuItem("Más", TrayIcon.GlyphBitmap("\uE712")) { Tag = "\uE712" };
        var transcribe = new ToolStripMenuItem("Transcribir audio…", TrayIcon.GlyphBitmap("\uE8D6"),
            (_, _) => new TranscribeWindow().Show()) { Tag = "\uE8D6" };
        transcribe.ToolTipText = "Transcribe un archivo de audio con Groq (Whisper).";
        moreMenu.DropDownItems.Add(transcribe);
        moreMenu.DropDownItems.Add(new ToolStripMenuItem("Abrir carpeta de datos",
            TrayIcon.GlyphBitmap("\uE838"), (_, _) => AppPaths.OpenInExplorer()) { Tag = "\uE838" });
        menu.Items.Add(moreMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", TrayIcon.GlyphBitmap("\uE8BB"),
            (_, _) => System.Windows.Application.Current.Shutdown()) { Tag = "\uE8BB" });
        if (menu.Items[0] is ToolStripItem first)
            first.Font = new System.Drawing.Font(first.Font, System.Drawing.FontStyle.Bold);

        menu.ShowImageMargin = true;
        menu.Opening += (_, _) => UpdatePauseState();
        _menu = menu;
        _tray.ContextMenuStrip = menu;

        ApplyTrayTheme();
        Theming.Changed += ApplyTrayTheme;
        Closed += (_, _) => Theming.Changed -= ApplyTrayTheme;

        Loaded += (_, _) => RegisterHotkeyFromSettings();
        // StartupUri crea esta ventana oculta y no dispara Loaded; se registra
        // ya aquí para que la hotkey y el HWND de segunda instancia existan al
        // arrancar. El intento diferido de App sigue como red de seguridad.
        try { RegisterHotkeyFromSettings(); } catch { }
        Hide();
    }

    /// <summary>WinForms no sigue el tema de Windows: pintamos el menú del tray a mano.</summary>
    private void ApplyTrayTheme()
    {
        if (_menu == null) return;

        bool dark = Theming.IsDark;
        var renderer = new ToolStripProfessionalRenderer(dark ? new DarkColorTable() : new LightColorTable())
        {
            RoundedEdges = false
        };

        var back = dark ? System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B) : System.Drawing.Color.FromArgb(0xFA, 0xFA, 0xFA);
        var fore = dark ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A);

        _menu.Renderer = renderer;
        _menu.BackColor = back;
        _menu.ForeColor = fore;
        ApplyTrayTheme(_menu.Items, renderer, back, fore);

        // El icono del tray también usa el acento del sistema.
        try
        {
            TrayIcon.Refresh();
            AppIcon.Invalidate();
            _tray.Icon = TrayIcon.Get();
        }
        catch { }
    }

    private static void ApplyTrayTheme(ToolStripItemCollection items, ToolStripRenderer renderer,
        System.Drawing.Color back, System.Drawing.Color fore)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripMenuItem menuItem) continue;

            if (menuItem.Tag is string glyph)
            {
                try { menuItem.Image = TrayIcon.GlyphBitmap(glyph); } catch { }
            }

            if (!menuItem.HasDropDownItems) continue;

            menuItem.DropDown.Renderer = renderer;
            menuItem.DropDown.BackColor = back;
            menuItem.DropDown.ForeColor = fore;
            ApplyTrayTheme(menuItem.DropDownItems, renderer, back, fore);
        }
    }

    private void TogglePause()
    {
        if (Selbar.App.Paused) Resume("Selección activa de nuevo.");
        else
        {
            _pauseTimer?.Stop();
            _pauseTimer = null;
            _pausedUntil = null;
            ApplyPause(true);
            Notify("Selección en pausa. Las herramientas manuales siguen disponibles.");
        }
    }

    /// <summary>Pausa durante N minutos y se reanuda sola.</summary>
    private void PauseFor(int minutes)
    {
        _pauseTimer?.Stop();
        _pausedUntil = DateTime.Now.AddMinutes(Math.Max(1, minutes));
        ApplyPause(true);
        Notify($"Selección en pausa hasta {_pausedUntil:HH:mm}.");

        _pauseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, minutes))
        };
        _pauseTimer.Tick += (_, _) => Resume("Pausa terminada: selección activa.");
        _pauseTimer.Start();
    }

    private void Resume(string message)
    {
        _pauseTimer?.Stop();
        _pauseTimer = null;
        _pausedUntil = null;
        ApplyPause(false);
        Notify(message);
    }

    private void ApplyPause(bool paused)
    {
        Selbar.App.Paused = paused;

        // En pausa la barra no debe quedarse en pantalla: seguiría viéndose pero
        // ya no recibiría teclado (el filtro la ignora), así que parece rota.
        if (paused) Selbar.App.ClosePopups();

        if (_pauseItem != null)
        {
            string glyph = paused ? "\uE768" : "\uE769";
            _pauseItem.Tag = glyph;
            try { _pauseItem.Image = TrayIcon.GlyphBitmap(glyph); } catch { }
            _pauseItem.ToolTipText = paused
                ? "La selección automática está en pausa; el resto de herramientas sigue disponible."
                : "Deja de mostrar la barra al seleccionar texto.";
        }
        if (_pauseTimedItem != null) _pauseTimedItem.Enabled = !paused;

        _tray.Text = paused ? "Selbar (selección en pausa)" : "Selbar";
        UpdatePauseState();
    }

    /// <summary>Etiqueta de pausa con la hora de fin, actualizada al abrir el menú.</summary>
    private void UpdatePauseState()
    {
        if (_pauseItem == null) return;
        if (!Selbar.App.Paused)
        {
            _pauseItem.Text = "Pausar selección";
            return;
        }

        _pauseItem.Text = _pausedUntil is { } until
            ? $"Reanudar selección · hasta {until:HH:mm}"
            : "Reanudar selección";
    }

    private void Notify(string message)
    {
        try { _tray.ShowBalloonTip(1500, "Selbar", message, ToolTipIcon.Info); }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterHotkeyFromSettings();
    }

    /// <summary>Aviso discreto en la bandeja (globo), sin abrir ventanas.</summary>
    public void Notify(string title, string text)
    {
        try { _tray.ShowBalloonTip(2500, title, text, ToolTipIcon.Info); }
        catch { }
    }

    public void RegisterHotkeyFromSettings()
    {
        // EnsureHandle dispara SourceInitialized, que vuelve a llamar aquí; el
        // guard evita registrar dos veces el mismo atajo.
        if (_registeringHotkeys) return;
        _registeringHotkeys = true;
        try
        {
            if (Selbar.App.SettingsMgr == null) return;

            if (_hwnd == IntPtr.Zero)
            {
                // La ventana aún no tiene handle (arranque): forzarlo es seguro en ventanas ocultas.
                try { _hwnd = new WindowInteropHelper(this).EnsureHandle(); } catch { return; }
                if (_hwnd != IntPtr.Zero && _source == null)
                {
                    _source = HwndSource.FromHwnd(_hwnd);
                    _source?.AddHook(WndProc);
                }
            }

            var t = Selbar.App.SettingsMgr.Settings.Triggers;
            Hotkeys.Unregister(_hwnd);
            Hotkeys.Unregister(_hwnd, Hotkeys.DICTATION_HOTKEY_ID);

            // Atajo de dictado (independiente del de la barra).
            string dictation = Selbar.App.SettingsMgr.Settings.DictationHotkey ?? "";
            if (!string.IsNullOrWhiteSpace(dictation) && Hotkeys.IsValid(dictation))
            {
                bool registered = Hotkeys.Register(_hwnd, dictation, Hotkeys.DICTATION_HOTKEY_ID);
                if (_dictationItem != null)
                    _dictationItem.ShortcutKeyDisplayString = registered ? dictation : "No disponible";
                if (!registered && !_dictationWarned)
                {
                    _dictationWarned = true;
                    Logger.Warn("no se pudo registrar el atajo de dictado '" + dictation + "'");
                    try
                    {
                        _tray.ShowBalloonTip(4000, "Selbar",
                            $"No se pudo registrar el atajo de dictado '{dictation}' (¿en uso por otro programa?). " +
                            "Puedes dictar desde el menú de la bandeja.",
                            ToolTipIcon.Warning);
                    }
                    catch { }
                }
                else if (registered)
                {
                    _dictationWarned = false;
                }
            }
            else
            {
                // Sin atajo válido no hay nada que avisar: si el usuario vuelve a
                // poner uno que falle, debe recibir el aviso otra vez.
                _dictationWarned = false;
                if (_dictationItem != null) _dictationItem.ShortcutKeyDisplayString = "";
            }

            if (!t.HotkeyEnabled || string.IsNullOrWhiteSpace(t.Hotkey))
            {
                _hotkeyWarned = false;
                // Quitar el aviso de un fallo anterior: ya no hay atajo que falle.
                if (_adjustItem != null) _adjustItem.ToolTipText = "Ajustes de Selbar.";
                return;
            }

            bool barRegistered = Hotkeys.Register(_hwnd, t.Hotkey);
            if (!barRegistered)
            {
                if (_adjustItem != null)
                    _adjustItem.ToolTipText = $"No se pudo registrar el atajo '{t.Hotkey}'. Cámbialo en Ajustes → Disparadores.";
                if (_hotkeyWarned) return;
                _hotkeyWarned = true;
                Logger.Warn("no se pudo registrar la hotkey '" + t.Hotkey + "'");
                try
                {
                    _tray.ShowBalloonTip(4000, "Selbar",
                        $"No se pudo registrar la hotkey '{t.Hotkey}' (¿en uso por otro programa?). " +
                        "Cámbiala en Ajustes → Disparadores.",
                        ToolTipIcon.Warning);
                }
                catch { }
            }
            else
            {
                _hotkeyWarned = false;
                if (_adjustItem != null)
                    _adjustItem.ToolTipText = $"Ajustes de Selbar. Atajo de acciones: {t.Hotkey}";
            }
        }
        catch (Exception ex) { Logger.LogEx(ex, "registrar hotkey"); }
        finally { _registeringHotkeys = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == Hotkeys.WM_HOTKEY && wParam.ToInt32() == Hotkeys.HOTKEY_ID)
            {
                handled = true;
                _ = Selbar.App.ShowBarForCurrentSelectionAsync();
            }
            else if (msg == Hotkeys.WM_HOTKEY && wParam.ToInt32() == Hotkeys.DICTATION_HOTKEY_ID)
            {
                handled = true;
                _ = Selbar.App.ToggleDictationAsync();
            }
            else if (Selbar.App.ActivateMessage != 0 && (uint)msg == Selbar.App.ActivateMessage)
            {
                // Otra instancia ha arrancado: traer Ajustes al frente.
                handled = true;
                Selbar.App.OpenSettings();
            }
        }
        catch (Exception ex) { Logger.LogEx(ex, "WndProc"); }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { if (_source != null) _source.RemoveHook(WndProc); } catch { }
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                Hotkeys.Unregister(_hwnd);
                Hotkeys.Unregister(_hwnd, Hotkeys.DICTATION_HOTKEY_ID);
            }
        }
        catch { }
        try { _pauseTimer?.Stop(); _pauseTimer = null; } catch { }
        try
        {
            // El menú y sus imágenes viven fuera del árbol WPF: sin esto se
            // quedaban colgados del NotifyIcon hasta que el proceso moría.
            _tray.ContextMenuStrip = null;
            _menu?.Dispose();
            _menu = null;
        }
        catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        base.OnClosed(e);
    }
}
