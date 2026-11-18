using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows.Threading;
using Selbar.Core.Actions;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Core.History;
using Selbar.Core.Imaging;
using Selbar.Core.Platform;
using Selbar.Core.Selection;
using Selbar.Native;

namespace Selbar;

public partial class App : System.Windows.Application
{
    public static SettingsManager SettingsMgr { get; private set; } = null!;
    public static ActionRegistry Registry { get; private set; } = null!;

    /// <summary>Decide la acción más probable con Gemini (opcional, nunca bloquea).</summary>
    public static Selbar.Core.Ai.DecisionRouter Decisions { get; } = new();
    public static ActionExecutor Executor { get; private set; } = null!;

    /// <summary>Cuando está en pausa no se muestra ningún popup.</summary>
    /// <remarks>Volatile: el hook de teclado lo consulta desde su propio hilo.</remarks>
    public static bool Paused
    {
        get => _paused;
        set => _paused = value;
    }
    private static volatile bool _paused;

    /// <summary>Barra actual (para los atajos 1-9 del hook de teclado).</summary>
    public static Ui.ActionBarWindow? CurrentBar
    {
        get => _currentBar;
        private set => _currentBar = value;
    }
    // Volatile: una lectura obsoleta en el hook podía dejar la barra sin teclado
    // o, peor, consumir teclas de una instancia ya reemplazada.
    private static volatile Ui.ActionBarWindow? _currentBar;

    /// <summary>Mensaje registrado para despertar a la instancia ya en marcha.</summary>
    public static uint ActivateMessage { get; private set; }

    /// <summary>Popups propios abiertos (barra, overflow, resultados).</summary>
    private static readonly List<System.Windows.Window> OpenPopups = new();

    /// <summary>HWND de los popups: permite comprobar "¿el cursor está encima?" sin tocar WPF desde el hook.</summary>
    private static readonly ConcurrentDictionary<IntPtr, byte> PopupHandles = new();

    /// <summary>Ventana → HWND, para poder retirar el handle exacto al cerrar.</summary>
    private static readonly ConcurrentDictionary<System.Windows.Window, IntPtr> PopupHandleByWindow = new();

    private static DateTime _lastBarShown = DateTime.MinValue;
    private static string? _lastBarText;
    private static string? _lastBarProcess;

    /// <summary>
    /// Generación de barras mostradas. Un descarte (clic fuera) emitido antes de
    /// que existiera la barra actual no debe cerrarla: el clic formaba parte de
    /// la selección que la abrió, y el evento puede llegar después por prioridad.
    /// </summary>
    private static int _barGeneration;

    private SelectionMonitor? _monitor;
    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "Selbar_SingleInstance", out bool created);
        _ownsMutex = created;

        try { ActivateMessage = NativeMethods.RegisterWindowMessageW("Selbar.Activate"); }
        catch { ActivateMessage = 0; }

        if (!created)
        {
            // Ya hay una instancia: pídele que abra Ajustes y sal sin hacer nada más.
            try
            {
                if (ActivateMessage != 0)
                    NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        try { if (System.Windows.SystemParameters.ClientAreaAnimation == false) Logger.Log("animaciones desactivadas por el sistema"); }
        catch { }

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.LogEx(args.Exception, "UI");
            args.Handled = true; // no tumbar la app de fondo por un error visual
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Logger.LogEx(ex, "fatal");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.LogEx(args.Exception, "tarea en segundo plano");
            args.SetObserved();
        };

        Logger.Log("arranque v" + Version + " · datos en " + AppPaths.DataDir);
        Logger.RotateFile(Logger.Path);

        SettingsMgr = new SettingsManager();
        SettingsMgr.Load();
        // La decisión inteligente lee la configuración viva: activar la key en
        // Ajustes surte efecto sin reiniciar la app.
        Core.Ai.DecisionRouter.DefaultConfig = () => SettingsMgr.Settings.Ai;

        // Tema: paleta y acento de Windows antes de crear ninguna ventana.
        try { Ui.Theming.Apply(this, SettingsMgr.Settings.Theme); } catch (Exception ex) { Logger.LogEx(ex, "tema"); }

        Registry = new ActionRegistry(SettingsMgr.Settings.ActionOverrides);
        Registry.Profiles = SettingsMgr.Settings.Profiles;
        Registry.SmartSuggestEnabled = SettingsMgr.Settings.Triggers.SmartSuggest;
        Executor = new ActionExecutor(SettingsMgr.Settings);
        HistoryStore.Configure(SettingsMgr.Settings.History);

        _monitor = new SelectionMonitor(SettingsMgr.Settings.Triggers, IsCursorOnOwnPopup);
        _monitor.SelectionReady += (text, anchor) => ShowBar(text, anchor,
            peek: SettingsMgr.Settings.Triggers.SelectionPeek,
            allowTyping: !SettingsMgr.Settings.Triggers.SelectionPeek);

        // Gancho de pruebas/capturas: abre Ajustes al arrancar (SELBAR_OPEN_SETTINGS=1).
        if (Environment.GetEnvironmentVariable("SELBAR_OPEN_SETTINGS") == "1")
        {
            Dispatcher.BeginInvoke(new Action(() => OpenSettings()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        // Modo "escribe para filtrar": el hook consume las teclas mientras la barra está abierta.
        _monitor.KeyFilter = FilterKeyForBar;

        // Los eventos del hook llegan desde su hilo: nada de bloquearlo con Invoke.
        _monitor.DismissRequested += () =>
        {
            // Se anota la generación al recibir el clic: si para cuando se procesa
            // ya se mostró la barra de esa misma selección, el descarte es viejo.
            int generation = Volatile.Read(ref _barGeneration);
            PostToUi(() =>
            {
                try
                {
                    if (generation != Volatile.Read(ref _barGeneration)) return;
                    if (!IsCursorOnOwnPopup()) ClosePopups();
                }
                catch { }
            });
        };

        // Esc con búsqueda activa: primero cierra el menú, luego borra el filtro y
        // por último la barra. Todo inmediato, sin esperar animación.
        _monitor.EscapePressed += () =>
        {
            // Ctrl+Esc abre Inicio y Alt+Esc cambia de ventana: no son para la barra.
            if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) ||
                NativeMethods.IsKeyDown(NativeMethods.VK_MENU) ||
                NativeMethods.IsKeyDown(0x5B) || NativeMethods.IsKeyDown(0x5C)) return;

            PostToUi(() =>
            {
                try
                {
                    var bar = CurrentBar;
                    if (bar != null) { bar.HandleEscape(); return; }
                    ClosePopups();
                }
                catch { }
            });
        };
        _monitor.Start();

        // StartupUri crea MainWindow después de OnStartup y la ventana arranca
        // oculta: ni su Loaded ni su SourceInitialized se disparan. El registro
        // inmediato no encontraba la ventana; este segundo intento, ya con el
        // árbol de arranque montado, registra hotkeys/dictado y crea el HWND que
        // recibe la activación de una segunda instancia.
        Dispatcher.BeginInvoke(new Action(RefreshHotkey), DispatcherPriority.Loaded);

        if (!SettingsMgr.Settings.SeenWelcome)
        {
            try
            {
                var welcome = new Ui.WelcomeWindow();
                // La marca se guarda al cerrarla, no al mostrarla: si el arranque
                // se interrumpe con la ventana abierta (instalador, cierre forzado),
                // la bienvenida no se pierde para siempre.
                welcome.Closed += (_, _) =>
                {
                    try
                    {
                        SettingsMgr.Settings.SeenWelcome = true;
                        SettingsMgr.Save();
                    }
                    catch (Exception ex) { Logger.LogEx(ex, "bienvenida"); }
                };
                welcome.Show();
            }
            catch (Exception ex) { Logger.LogEx(ex, "bienvenida"); }
        }
    }

    /// <summary>Versión de la app (para Ajustes y logs).</summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Ejecuta algo en el hilo de UI sin bloquear al llamador (los hooks lo agradecen).</summary>
    private static void PostToUi(Action action)
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Input, action);
        }
        catch { }
    }

    /// <summary>Muestra la barra (respeta pausa, apps excluidas, toggles y contexto).</summary>
    public static void ShowBar(string text, SelectionAnchor anchor = default, bool peek = false, bool allowTyping = true)
    {
        if (Paused) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (IsExcludedApp()) return;
        if (NativeMethods.IsForegroundFullscreen()) return; // no molestar en juegos/vídeo

        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.Invoke(() =>
            {
                var now = DateTime.UtcNow;

                // La pausa pudo empezar mientras se extraía la selección.
                if (Paused) return;

                string process = SafeForegroundProcess();
                // El mismo texto en otra app no es la misma barra: sus perfiles
                // pueden ocultar acciones distintas y la instancia anterior
                // mostraría un catálogo que ya no corresponde. Si el nombre no se
                // pudo leer, se conserva la instancia para no parpadear.
                bool sameProcess = string.IsNullOrEmpty(process) ||
                                   string.IsNullOrEmpty(_lastBarProcess) ||
                                   string.Equals(_lastBarProcess, process, StringComparison.OrdinalIgnoreCase);
                bool sameText = _lastBarText == text && sameProcess;
                var visibleBar = CurrentBar;
                if (sameText && visibleBar != null && visibleBar.IsVisible && !visibleBar.IsClosing)
                {
                    // Misma selección repetida: el texto no cambió, pero puede
                    // estar en otro punto de la pantalla.
                    visibleBar.RepositionTo(anchor);
                    // La intención explícita (hotkey o clic) manda: se promueve la
                    // instancia existente a teclado deliberado, sin recrearla ni
                    // hacerla parpadear. Un indicador o una barra abierta por hover
                    // quedan así listos para buscar.
                    if (!peek && allowTyping)
                    {
                        visibleBar.Promote(enableTyping: true);
                        _lastBarShown = now;
                    }
                    return;
                }
                // Doble disparo del mismo gesto (long-press + mouse-up, selección
                // repetida). También cubre la barra que acaba de cerrarse: sin esto
                // un disparo tardío la reabría justo después de Esc o de un clic fuera.
                if (sameText && (now - _lastBarShown).TotalMilliseconds < 250) return;

                var visible = Registry.GetVisibleActions(text, process);
                if (visible.Count == 0)
                {
                    // Texto nuevo sin acciones: la barra visible mostraba otra
                    // selección y sus acciones ya no corresponden.
                    ClosePopups();
                    return;
                }

                ClosePopups(); // una sola barra a la vez
                ForegroundTracker.Capture(); // a quién hay que devolver el foco al pegar

                var bar = new Ui.ActionBarWindow(text, visible, anchor, peek, allowTyping);
                try
                {
                    TrackPopup(bar);
                    CurrentBar = bar;
                    _lastBarText = text;
                    _lastBarProcess = process;
                    _lastBarShown = now;
                    Interlocked.Increment(ref _barGeneration);
                    bar.Closed += (_, _) => { if (CurrentBar == bar) CurrentBar = null; };
                    bar.Show();
                }
                catch (Exception ex)
                {
                    // Nunca dejar CurrentBar apuntando a una ventana que no llegó a
                    // verse: el filtro de teclado consumiría todas las pulsaciones
                    // sin que hubiera nada en pantalla.
                    Logger.LogEx(ex, "mostrar barra");
                    if (CurrentBar == bar) CurrentBar = null;
                    try { bar.FadeOutAndClose(); } catch { }
                    try { UntrackPopup(bar); } catch { }
                }
            });
        }
        catch (Exception ex) { Logger.LogEx(ex, "mostrar barra"); }
    }

    /// <summary>
    /// Filtro de teclas del hook: decide si una pulsación pertenece al modo
    /// deliberado de la barra. Se ejecuta en el hilo del hook (rápido y sin UI).
    /// </summary>
    private static bool FilterKeyForBar(int vk)
    {
        try
        {
            if (Paused) return false;

            var bar = CurrentBar;
            if (bar == null) return false;

            // Si no estamos en modo teclado (indicador o menú abierto solo con hover),
            // la pulsación es de la app: cerramos el popup y NO la consumimos. Así
            // escribir para reemplazar el texto funciona como en Windows normal.
            bool modifierOnly = vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
            if (!modifierOnly && !bar.AcceptsTyping)
            {
                PostToUi(() => bar.FadeOutAndClose());
                return false;
            }

            if (!bar.AcceptsTyping) return false;

            // Si el foco está en una ventana nuestra (resultado/ajustes), no interferir.
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && ForegroundTracker.IsOwnWindow(foreground)) return false;

            bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
            bool alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
            bool win = NativeMethods.IsKeyDown(0x5B) || NativeMethods.IsKeyDown(0x5C);
            bool shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);

            // Ctrl/Alt/Win son combinaciones de la app o del sistema (Ctrl+Esc abre
            // Inicio, Alt+Esc cambia de ventana): no son nuestras.
            if (ctrl || alt || win) return false;

            if (vk == 0x1B) // Esc: menú → búsqueda → barra, sin esperar animación
            {
                PostToUi(() => bar.HandleEscape());
                return true;
            }

            bool digitKey = vk is (>= 0x31 and <= 0x39) or (>= 0x61 and <= 0x69);
            bool digitsEnabled = SettingsMgr.Settings.Triggers.DigitShortcuts && !shift;

            // El menú visible manda mientras está abierto: flechas, Enter y dígitos.
            if (bar.OverflowOpen)
            {
                if ((IsOverflowKey(vk) && (!digitKey || digitsEnabled)) ||
                    (SettingsMgr.Settings.Triggers.SearchOnType && IsPrintableKey(vk)))
                {
                    PostToUi(() => bar.OnCapturedKey(vk));
                    return true;
                }
                if (!SettingsMgr.Settings.Triggers.SearchOnType && IsPrintableKey(vk))
                {
                    PostToUi(() => bar.FadeOutAndClose());
                    return false;
                }
                return false;
            }

            // Atajos 1-9: solo con chips numerados disponibles; sin depender de la
            // búsqueda ni de una caducidad silenciosa. Con Mayús se está escribiendo
            // un símbolo (Shift+1 = !), no pidiendo el atajo.
            if (!bar.IsFiltering && digitsEnabled && digitKey)
            {
                int digit = vk >= 0x61 ? vk - 0x61 + 1 : vk - 0x30;
                if (digit < 1 || digit > bar.DigitCount) return false; // fuera de rango: no consumir
                PostToUi(() => bar.InvokeIndex(digit));
                return true;
            }

            if (!SettingsMgr.Settings.Triggers.SearchOnType)
            {
                // Sin escribir-para-buscar, solo los dígitos (resueltos arriba) son
                // nuestros: el resto de teclas vuelve a la app y cierra la barra,
                // como antes, para no retener el teclado sin búsqueda.
                if (!modifierOnly) PostToUi(() => bar.FadeOutAndClose());
                return false;
            }

            // Navegación del modo deliberado cuando escribir-para-buscar está activo.
            // Av/Re Pág no se consumen: en una fila horizontal no hacen nada, así que
            // tragarlas solo rompería la app de origen.
            if (vk is 0x0D or 0x26 or 0x28 or 0x25 or 0x27 or 0x24 or 0x23)
            {
                PostToUi(() => bar.OnCapturedKey(vk));
                return true;
            }

            // Retroceso: nunca debe borrar el texto de detrás de la barra.
            if (vk == 0x08)
            {
                if (bar.IsFiltering) PostToUi(() => bar.OnCapturedKey(vk));
                return true;
            }

            if (IsPrintableKey(vk))
            {
                // El primer espacio se consume (no debe escribir en la app) pero no inicia búsqueda.
                if (!bar.IsFiltering && vk == 0x20) return true;

                PostToUi(() => bar.OnCapturedKey(vk));
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "filtro de teclado");
            return false;
        }
    }

    private static bool IsOverflowKey(int vk) =>
        vk is 0x26 or 0x28 or 0x21 or 0x22 or 0x24 or 0x23 or 0x0D or 0x08 ||
        (vk >= 0x31 && vk <= 0x39) || (vk >= 0x61 && vk <= 0x69);

    private static bool IsPrintableKey(int vk) =>
        (vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69) ||
        (vk >= 0x41 && vk <= 0x5A) ||
        (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDE) || vk == 0x20;

    public static void TrackPopup(System.Windows.Window w)
    {
        bool first;
        lock (OpenPopups)
        {
            first = !OpenPopups.Contains(w);
            if (first) OpenPopups.Add(w);
        }

        void Register()
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                PopupHandleByWindow[w] = h;
                PopupHandles[h] = 1;
            }
            catch { }
        }

        if (w.IsLoaded) Register();
        // Si ya estaba registrada, volver a suscribir duplicaba el alta del HWND
        // y el descarte al cerrar.
        if (!first) return;

        w.SourceInitialized += (_, _) => Register();
        w.Closed += (_, _) => UntrackPopup(w);
    }

    public static void UntrackPopup(System.Windows.Window w)
    {
        lock (OpenPopups) OpenPopups.Remove(w);

        if (PopupHandleByWindow.TryRemove(w, out var hwnd))
            PopupHandles.TryRemove(hwnd, out _);

        // Limpia también handles que se quedaran colgando (ventanas cerradas por la app).
        foreach (var h in PopupHandles.Keys)
        {
            try { if (!NativeMethods.IsWindow(h)) PopupHandles.TryRemove(h, out _); }
            catch { }
        }
    }

    /// <summary>Cierra barra y overflow (no el progreso ni resultados).</summary>
    public static void ClosePopups()
    {
        System.Windows.Window[] copy;
        lock (OpenPopups) { copy = OpenPopups.ToArray(); }

        foreach (var w in copy)
        {
            if (w is not (Ui.ActionBarWindow or Ui.OverflowWindow)) continue;
            try
            {
                // La barra cierra también su menú: al llegar a él ya no está visible
                // y un Close() repetido solo generaba ruido en el log.
                if (!w.IsVisible) continue;
                if (w is Ui.ActionBarWindow bar)
                {
                    bar.FadeOutAndClose();
                    // Si el cierre fallara, que el filtro de teclado no siga
                    // considerando viva esta barra.
                    if (CurrentBar == bar) CurrentBar = null;
                }
                else w.Close();
            }
            catch { }
        }
    }

    /// <summary>¿Está el cursor sobre un popup propio? (para no cerrarlo al pulsar en él).</summary>
    public static bool IsCursorOnOwnPopup()
    {
        try
        {
            var (x, y) = NativeMethods.Cursor();
            foreach (var hwnd in PopupHandles.Keys)
                if (NativeMethods.PointInWindow(hwnd, x, y)) return true;
        }
        catch { }
        return false;
    }

    private static bool IsExcludedApp()
    {
        try
        {
            var fg = NativeMethods.ForegroundProcessName();
            if (string.IsNullOrEmpty(fg)) return false;
            return SettingsMgr.Settings.ExcludedApps.Any(x =>
            {
                var name = (x ?? "").Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                return fg.Equals(name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch { return false; }
    }

    /// <summary>
    /// Lee la selección actual (hilo STA por el portapapeles) y muestra la barra.
    /// Lo usan el monitor automático y la hotkey global.
    /// </summary>
    public static Task ShowBarForCurrentSelectionAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                Thread.Sleep(100);

                // Si hay una imagen copiada, el atajo hace OCR y no toca el portapapeles
                // (la extracción de texto podría reemplazarla con un Ctrl+C).
                bool hasImage = false;
                try
                {
                    var dispatcher = Current?.Dispatcher;
                    if (dispatcher != null) hasImage = dispatcher.Invoke(() => OcrService.ClipboardHasImage());
                }
                catch { }

                if (hasImage)
                {
                    PostToUi(() => _ = RunOcrFromClipboardAsync());
                    return;
                }

                var selection = new SelectionResult();
                var thread = new Thread(() => { selection = TextExtractor.GetSelection(allowClipboardFallback: true); })
                { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                if (!thread.Join(5000)) return;

                var text = (selection.Text ?? "").Trim();
                if (text.Length >= Math.Max(1, SettingsMgr.Settings.Triggers.MinSelectionLength))
                    ShowBar(text, selection.Anchor, peek: false, allowTyping: true);
            }
            catch (Exception ex) { Logger.LogEx(ex, "hotkey/selección"); }
        });
    }

    /// <summary>Proceso en primer plano, o "" si no se puede saber.</summary>
    private static string SafeForegroundProcess()
    {
        try { return NativeMethods.ForegroundProcessName() ?? ""; }
        catch { return ""; }
    }

    /// <summary>Aviso discreto en la bandeja del sistema (sin abrir ventanas).</summary>
    private static void NotifyTray(string title, string text)
    {
        // Application.MainWindow es la primera ventana creada: en el primer
        // arranque es WelcomeWindow y, al cerrarla, deja de servir para siempre.
        // El NotifyIcon vive en MainWindow, así que se busca por tipo y se
        // publica en su hilo (ShowBalloonTip no es fiable desde fondo).
        PostToUi(() =>
        {
            try
            {
                var main = Current?.Windows.OfType<Ui.MainWindow>().FirstOrDefault();
                main?.Notify(title, text);
            }
            catch (Exception ex) { Logger.Warn("aviso de bandeja: " + ex.Message); }
        });
    }

    /// <summary>
    /// Dictado con el micrófono: el primer disparo graba, el segundo transcribe
    /// con Groq y escribe el texto en la app que tenía el foco.
    /// </summary>
    public static async Task ToggleDictationAsync()
    {
        try
        {
            if (!Core.Audio.DictationService.IsRecording)
            {
                await Core.Audio.DictationService.StartAsync();
                NotifyTray("Dictado", "Grabando… pulsa otra vez para transcribir.");
                return;
            }

            NotifyTray("Dictado", "Transcribiendo…");
            string text = await Core.Audio.DictationService.StopAndTranscribeAsync(SettingsMgr.Settings)
                .ConfigureAwait(false);

            PostToUi(() =>
            {
                if (!ClipboardService.TrySetText(text))
                {
                    Ui.ActionRunner.ShowError("Dictado", "No se pudo escribir en el portapapeles.");
                    return;
                }
                if (!ForegroundTracker.Restore(70))
                {
                    NotifyTray("Dictado", "Copiado al portapapeles (pega con Ctrl+V).");
                    return;
                }
                NativeMethods.SendCtrlV();
            });
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "dictado");
            // El fallo puede llegar después de ConfigureAwait(false): una ventana
            // WPF creada desde el pool lanzaba y el usuario no veía nada.
            PostToUi(() => Ui.ActionRunner.ShowError("Dictado", ex.Message));
        }
    }

    /// <summary>
    /// Analiza con el modelo (visión) la imagen del portapapeles. Se lanza desde
    /// el menú del tray; no toca la red si SELBAR_OFFLINE=1.
    /// </summary>
    public static async Task AnalyzeImageFromClipboardAsync(string? question = null)
    {
        byte[]? png = ClipboardService.TryGetImagePng();
        if (png is null || png.Length == 0)
        {
            Ui.ActionRunner.ShowError("Analizar imagen",
                "No hay ninguna imagen en el portapapeles. Cópiala o recorta con Win+Shift+S y vuelve a intentarlo.");
            return;
        }
        if (png.Length > 8 * 1024 * 1024)
        {
            Ui.ActionRunner.ShowError("Analizar imagen",
                "La imagen es demasiado grande (más de 8 MB). Recórtala o redúcela antes de analizarla.");
            return;
        }

        string prompt = string.IsNullOrWhiteSpace(question)
            ? "Describe la imagen en detalle y en español. Si contiene texto, transcríbelo tal cual."
            : question!.Trim();

        string answer;
        try
        {
            answer = await Executor.AskAboutImageAsync(prompt, png).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "visión");
            PostToUi(() => Ui.ActionRunner.ShowError("Analizar imagen", ex.Message));
            return;
        }

        PostToUi(() =>
        {
            try { new Ui.ResultWindow("Imagen analizada", answer, OutputMode.Popup).Show(); }
            catch (Exception ex) { Logger.LogEx(ex, "mostrar visión"); }
        });
    }

    /// <summary>
    /// OCR de la imagen del portapapeles. Lo usan el atajo global (cuando no hay
    /// texto seleccionado) y el menú del tray.
    /// </summary>
    public static async Task RunOcrFromClipboardAsync()
    {
        string? text = null;
        Exception? failure = null;

        try
        {
            text = await OcrService.ReadClipboardImageAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            Logger.LogEx(ex, "OCR");
        }

        void Show()
        {
            try
            {
                if (failure != null)
                {
                    Ui.ActionRunner.ShowError("OCR", failure.Message);
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Ui.ActionRunner.ShowError("OCR", "No se ha reconocido texto en la imagen.");
                    return;
                }

                var win = new Ui.ResultWindow("Texto de la imagen", text, OutputMode.Popup);
                win.SetRefinable(true);
                win.EnableChat();
                win.Show();
                Logger.Log($"OCR: {text.Length} caracteres");
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "OCR/ventana");
            }
        }

        var dispatcher = Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Show();
        else dispatcher.Invoke(Show);
    }

    /// <summary>Sube la imagen del portapapeles a Google Lens y abre los resultados.</summary>
    public static async Task RunLensFromClipboardAsync()
    {
        try
        {
            if (!OcrService.ClipboardHasImage())
            {
                Ui.ActionRunner.ShowError("Google Lens",
                    "No hay ninguna imagen en el portapapeles. Copia una imagen o usa Win+Shift+S.");
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                "Se enviará la imagen copiada a Google Lens para hacer la búsqueda inversa.\n\n¿Continuar?",
                "Google Lens", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            var previous = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                string url = await GoogleLens.UploadClipboardImageAsync();
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Logger.Log("Google Lens: imagen enviada");
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = previous;
            }
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "Google Lens");
            Ui.ActionRunner.ShowError("Google Lens", ex.Message);
        }
    }

    /// <summary>Re-registra la hotkey con los ajustes actuales (llamar tras Guardar).</summary>
    public static void RefreshHotkey()
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            if (!dispatcher.CheckAccess())
            {
                // La colección Windows es de UI: nunca leerla desde un hilo de fondo.
                dispatcher.BeginInvoke(new Action(RefreshHotkey), DispatcherPriority.Input);
                return;
            }

            // MainWindow puede no ser Application.MainWindow (en el primer
            // arranque lo es WelcomeWindow): se localiza por tipo.
            Current!.Windows.OfType<Ui.MainWindow>().FirstOrDefault()?.RegisterHotkeyFromSettings();
        }
        catch (Exception ex) { Logger.LogEx(ex, "registrar hotkey"); }
    }

    /// <summary>Recrea ejecutor y registro tras cambiar ajustes que les afectan.</summary>
    public static void ReloadRuntime()
    {
        try
        {
            Registry = new ActionRegistry(SettingsMgr.Settings.ActionOverrides);
        Registry.Profiles = SettingsMgr.Settings.Profiles;
        Registry.SmartSuggestEnabled = SettingsMgr.Settings.Triggers.SmartSuggest;
            Executor = new ActionExecutor(SettingsMgr.Settings);
            HistoryStore.Configure(SettingsMgr.Settings.History);
        }
        catch (Exception ex) { Logger.LogEx(ex, "recargar ajustes"); }
    }

    /// <summary>Abre una ventana singleton: si ya existe, la trae al frente.</summary>
    public static void ShowSingle<T>(Func<T> create) where T : System.Windows.Window
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.Invoke(() =>
            {
                try
                {
                    var open = Current!.Windows.OfType<T>().FirstOrDefault();
                    if (open != null)
                    {
                        if (!open.IsVisible) open.Show();
                        if (open.WindowState == System.Windows.WindowState.Minimized)
                            open.WindowState = System.Windows.WindowState.Normal;
                        open.Activate();
                        open.Focus();
                        return;
                    }
                    create().Show();
                }
                catch (Exception ex) { Logger.LogEx(ex, "ShowSingle"); }
            });
        }
        catch { }
    }

    /// <summary>
    /// Abre Ajustes (lo usan el tray, la bienvenida, la segunda instancia y el
    /// resultado cuando hay un error de IA). <paramref name="section"/> acepta
    /// "acciones", "ia", "disparadores", "apariencia" o "avanzado".
    /// </summary>
    public static void OpenSettings(string section = "acciones")
    {
        try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.Invoke(() =>
            {
                try
                {
                    var open = Current!.Windows.OfType<Ui.SettingsWindow>().FirstOrDefault();
                    if (open != null)
                    {
                        if (!open.IsVisible) open.Show();
                        if (open.WindowState == System.Windows.WindowState.Minimized)
                            open.WindowState = System.Windows.WindowState.Normal;
                        open.SelectSection(section);
                        open.Activate();
                        open.Focus();
                        return;
                    }
                    new Ui.SettingsWindow(section).Show();
                }
                catch (Exception ex) { Logger.LogEx(ex, "abrir ajustes"); }
            });
        }
        catch { }
    }

    /// <summary>Abre el historial de resultados.</summary>
    public static void OpenHistory() => ShowSingle(() => new Ui.HistoryWindow());

    public static void OpenClipboardHistory()
    {
        // Capturar la app actual antes de que la ventana tome el foco,
        // para que "Pegar" sepa a dónde devolverlo.
        try { ForegroundTracker.Capture(); } catch { }
        ShowSingle(() => new Ui.ClipboardHistoryWindow());
    }

    /// <summary>Vuelve a las acciones de serie (tras Restablecer en Ajustes).</summary>
    public static void ResetActions()
    {
        SettingsMgr.Settings.ActionOverrides = new();
        SettingsMgr.Save();
        Registry = new ActionRegistry(null);
        Registry.Profiles = SettingsMgr.Settings.Profiles;
        Registry.SmartSuggestEnabled = SettingsMgr.Settings.Triggers.SmartSuggest;
        Executor = new ActionExecutor(SettingsMgr.Settings);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        // Si salimos por instancia duplicada, los estáticos aún son null.
        try
        {
            if (SettingsMgr != null && Registry != null)
            {
                SettingsMgr.Settings.ActionOverrides = Registry.All.ToList();
                SettingsMgr.Save();
            }
        }
        catch { }

        try { _monitor?.Dispose(); } catch { }
        try { Ui.Theming.Shutdown(); } catch { }

        // Privacidad: borrar el historial al salir si el usuario lo pidió.
        try { HistoryStore.ClearIfConfigured(); } catch { }

        // Solo libera el mutex quien lo adquirió (la 2ª instancia nunca lo tuvo).
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        try { _instanceMutex?.Dispose(); } catch { }

        base.OnExit(e);
    }
}
