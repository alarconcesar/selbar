using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;
using Selbar.Core.Actions;
using Selbar.Core.Ai;
using Selbar.Core.Audio;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Core.Downloads;
using Selbar.Core.History;
using Selbar.Core.Imaging;
using Selbar.Core.Integrations;
using Selbar.Core.Speech;
using Selbar.Core.Text;
using Selbar.Core.Web;
using Selbar.Core.Platform;
using Selbar.Core.Selection;
using Selbar.Native;
using Selbar.Ui;
using Window = System.Windows.Window;

namespace Selbar.SelfTest;

/// <summary>
/// Pruebas de la lógica que no toca hardware (parser SSE, hotkeys, registro de
/// acciones, prompts, markdown, ajustes e historial). Sin dependencias externas:
/// se ejecuta con `dotnet run --project tools/SelfTest` y devuelve 1 si falla algo.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // Aísla los datos (historial, ajustes de prueba) en una carpeta temporal.
        var temp = Path.Combine(Path.GetTempPath(), "Selbar-SelfTest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SELBAR_DATA", temp);
        // Sin llamadas de red: cualquier intento al proveedor de IA falla al instante.
        Environment.SetEnvironmentVariable("SELBAR_OFFLINE", "1");
        // Sin diálogos modales: un MessageBox bloquearía la prueba de humo.
        Environment.SetEnvironmentVariable("SELBAR_NO_PROMPTS", "1");

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Selbar · auto-tests (offline: sin llamadas a modelos)");
        Console.WriteLine(new string('-', 60));

        if (args.Any(a => a.Equals("--ui", StringComparison.OrdinalIgnoreCase)))
            return RunUiSmoke();

        Run("SseParser", SseTests);
        Run("Proveedor", ProviderTests);
        Run("Hotkeys", HotkeyTests);
        Run("Nativo", NativeTests);
        Run("Iconos", IconTests);
        Run("Registro de acciones", RegistryTests);
        Run("Presets", PresetTests);
        Run("Ejecutor / prompts", ExecutorTests);
        Run("Buscador de acciones", SearchTests);
        Run("Estilos Unicode", TextStyleTests);
        Run("Color HEX", ColorTests);
        Run("Calculadora y conversión", CalcTests);
        Run("Integraciones y voz", IntegrationTests);
        Run("Enlaces", LinkTests);
        Run("Contacto, fecha y ruta", ContactDatePathTests);
        Run("Sugerencia inteligente", SuggestTests);
        Run("Registro", LogTests);
        Run("Decisión con IA", DecisionTests);
        Run("Historial del portapapeles", ClipboardHistoryTests);
        Run("YouTube / yt-dlp", YouTubeTests);
        Run("OCR", OcrTests);
        Run("Google Lens", LensTests);
        Run("Código QR", QrTests);
        Run("Groq Whisper", GroqTests);
        Run("Biblioteca de prompts", LibraryTests);
        Run("Diff", DiffTests);
        Run("Ajustes", SettingsTests);
        Run("Exportar / importar", ExportImportTests);
        Run("Historial", HistoryTests);
        Run("Markdown", MarkdownTests);
        Run("Versiones (csproj = instalador)", BuildVersionTests);
        Run("Turnos de resultado", ResultTurnTests);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"OK: {_passed}   Fallos: {_failed}");

        if (_failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Fallos:");
            foreach (var failure in Failures) Console.WriteLine("  · " + failure);
        }

        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    // ---------------- infraestructura de test ----------------

    private static void Run(string group, Action tests)
    {
        Console.WriteLine();
        Console.WriteLine(group);
        try { tests(); }
        catch (Exception ex) { Check(group + " lanzó una excepción", false, ex.GetType().Name + ": " + ex.Message); }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            _failed++;
            Failures.Add(name + (string.IsNullOrEmpty(detail) ? "" : " -> " + detail));
            Console.WriteLine("  FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " -> " + detail));
        }
    }

    private static void CheckEqual<T>(string name, T actual, T expected)
        => Check(name, EqualityComparer<T>.Default.Equals(actual, expected), $"esperado «{expected}», obtenido «{actual}»");

    // ---------------- prueba de humo de UI ----------------

    /// <summary>
    /// Crea y muestra brevemente todas las ventanas para validar XAML, recursos,
    /// plantillas y constructores. Se lanza con `--ui`.
    /// </summary>
    private static int RunUiSmoke()
    {
        Console.WriteLine("Prueba de humo de UI (las ventanas se abren y cierran al instante)");

        // Tema determinista para las capturas (por defecto, oscuro).
        string theme = Environment.GetCommandLineArgs().Any(a => a.Equals("--light", StringComparison.OrdinalIgnoreCase))
            ? "light"
            : "dark";

        var manager = new SettingsManager();
        manager.Settings.Ai.Provider = "Personalizado";
        manager.Settings.Ai.BaseUrl = "";           // sin red: no se carga la lista de modelos
        manager.Settings.Ai.Model = "deepseek-v4.1-flash";
        manager.Settings.SeenWelcome = true;
        manager.Settings.Theme = theme;
        manager.Save();

        var app = new Selbar.App();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        SetStatic("SettingsMgr", manager);
        SetStatic("Registry", new ActionRegistry(null));
        SetStatic("Executor", new ActionExecutor(manager.Settings));

        Theming.Apply(app, theme);

        var registry = Selbar.App.Registry;
        var selection = "texto de prueba con varias palabras";

        // Capturas deterministas: sin animaciones a medias.
        Motion.OverrideEnabled = false;

        // Datos de ejemplo para que el historial tenga contenido.
        HistoryStore.Clear();
        HistoryStore.Add("ai.summarize", "Resumir", "deepseek-v4.1-flash", "Selbar es un menú flotante que aparece al seleccionar texto.",
            "# Puntos clave\n\n- Se abre al **seleccionar** texto.\n- Acciones de IA y utilidades.\n\n| Acción | Resultado |\n|---|---|\n| Resumir | Puntos clave |\n| Traducir | Otro idioma |\n\n> Todo se configura en Ajustes.\n\n```csharp\nvar x = 1;\n```\n");
        HistoryStore.Add("ai.translate", "Traducir", "deepseek-v4.1-flash", "Hello world", "Hola mundo");

        bool saveShots = Environment.GetCommandLineArgs().Any(a => a.Equals("--shots", StringComparison.OrdinalIgnoreCase));
        var shotsDir = Path.Combine(Path.GetTempPath(), "Selbar-Shots-" + Guid.NewGuid().ToString("N"));
        if (saveShots) Directory.CreateDirectory(shotsDir);

        var windows = new List<(string Name, Func<Window> Create)>
        {
            ("WelcomeWindow", () => new WelcomeWindow()),
            ("AskDialog", () => new AskDialog(selection)),
            ("ActionEditDialog", () => new ActionEditDialog(null)),
            ("HistoryWindow", () => new HistoryWindow()),
            ("SettingsWindow", () => new SettingsWindow()),
            ("ResultWindow", () => new ResultWindow("Prueba", "# Título\n\n**hola** y `código`", OutputMode.Popup)),
            ("ResultWindow (reemplazar)", () => new ResultWindow("Corregir", "texto", OutputMode.Replace)),
            ("ResultWindow (versiones)", () =>
            {
                var w = new ResultWindow("Mejorar redacción", "Texto mejorado de ejemplo.", OutputMode.Popup);
                w.SetRefinable(true);
                w.EnableChat();
                w.BeginVersion("Más corto");
                w.AppendToken("Texto breve de ejemplo.");
                w.FinishSuccess();
                return w;
            }),
            ("ResultWindow (diff)", () =>
            {
                var w = new ResultWindow("Corregir ortografía", "Texto corregido de ejemplo.", OutputMode.Replace,
                    sourceText: "Texto con errres de ejemplo.");
                return w;
            }),
            ("ResultWindow (error)", () =>
            {
                var w = new ResultWindow("Traducir", "", OutputMode.Popup, streaming: true);
                w.BeginStreaming("deepseek-v4.1-flash");
                w.FinishError("HTTP 401 Unauthorized: Upstream request failed: Go requires a credential");
                return w;
            }),
            ("QrWindow", () => new QrWindow("https://github.com/HanaokaYuzu/Gemini-API")),
            ("TranscribeWindow", () => new TranscribeWindow()),
            ("TextStyleWindow", () => new TextStyleWindow("Hola mundo • 123")),
            ("ColorPreviewWindow", () => new ColorPreviewWindow("#459BAC")),
            ("DownloadWindow", () => new DownloadWindow("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false, autoStart: false)),
            ("ClipboardHistoryWindow", () => new ClipboardHistoryWindow()),
            ("ActionBarWindow (indicador)", () => new ActionBarWindow(selection, registry.GetVisibleActions(selection), peek: true, allowTyping: false)),
            ("ActionBarWindow", () => new ActionBarWindow(selection, registry.GetVisibleActions(selection))),
            ("ActionBarWindow (buscando)", () => new ActionBarWindow(selection, registry.GetVisibleActions(selection))),
            ("OverflowWindow", () => new OverflowWindow(selection, registry.All.Take(8).ToList())),
            ("PromptLibraryWindow", () => new PromptLibraryWindow()),
        };

        foreach (var (name, create) in windows)
        {
            try
            {
                var window = create();
                window.Show();
                window.UpdateLayout();

                // Regresión visual: ningún texto debe quedar ilegible sobre su fondo.
                CheckTextContrast(window, name);

                if (window is ActionBarWindow peekBar && name.Contains("indicador", StringComparison.OrdinalIgnoreCase))
                    Check("el indicador no captura el teclado", !peekBar.AcceptsTyping);

                if (saveShots)
                {
                    if (window is ActionBarWindow searching && name.Contains("buscando", StringComparison.OrdinalIgnoreCase))
                    {
                        // Igual que en uso real: la barra ya está visible y el usuario teclea.
                        searching.OnCapturedKey(0x54); // t
                        searching.OnCapturedKey(0x52); // r
                        window.UpdateLayout();

                    }

                    if (name.Contains("diff", StringComparison.OrdinalIgnoreCase))
                        InvokePrivate(window, "OnToggleDiff", window, new System.Windows.RoutedEventArgs());

                    if (window is DownloadWindow)
                    {
                        if (FindByName<System.Windows.Controls.ProgressBar>(window, "Bar") is { } bar) bar.Value = 42;
                        if (FindByName<System.Windows.Controls.TextBlock>(window, "StatusText") is { } status)
                            status.Text = "Descargando… 42%";
                        if (FindByName<System.Windows.Controls.TextBlock>(window, "DetailText") is { } detail)
                            detail.Text = "Vídeo hasta 1080p · 2,4 MB/s";
                    }

                    SaveShot(window, Path.Combine(shotsDir, SafeName(name) + ".png"));

                    // Ajustes: una captura por sección de la navegación lateral.
                    if (window is SettingsWindow settings && FindByName<System.Windows.Controls.ListBox>(settings, "NavList") is { } nav)
                    {
                        for (int index = 0; index < nav.Items.Count; index++)
                        {
                            nav.SelectedIndex = index;
                            settings.UpdateLayout();
                            SaveShot(settings, Path.Combine(shotsDir, $"SettingsWindow-tab{index}.png"));
                        }
                    }

                    // Historial: con un resultado seleccionado y otro vacío.
                    if (window is HistoryWindow history && FindByName<System.Windows.Controls.ListBox>(history, "EntryList") is { } entries &&
                        entries.Items.Count > 0)
                    {
                        entries.SelectedIndex = 0;
                        history.UpdateLayout();
                        SaveShot(history, Path.Combine(shotsDir, "HistoryWindow-selected.png"));

                        if (entries.Items.Count > 1)
                        {
                            entries.SelectedIndex = 1;
                            history.UpdateLayout();
                            SaveShot(history, Path.Combine(shotsDir, "HistoryWindow-rich.png"));
                        }
                    }
                }

                if (window is SettingsWindow themeSettings)
                    CheckLiveTheme(themeSettings);

                if (window is ActionBarWindow indicator && indicator.IsIndicator)
                {
                    SetStatic("CurrentBar", indicator);
                    var filter = typeof(Selbar.App).GetMethod("FilterKeyForBar",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    bool swallowed = filter != null && (bool)filter.Invoke(null, new object[] { 0x41 })!;
                    Check("escribir no se consume con el indicador", !swallowed);
                    Pump(160);
                    Check("el indicador se cierra al escribir", !indicator.IsVisible);
                    SetStatic("CurrentBar", null!);
                }

                window.Close();
                Check(name, true);
            }
            catch (Exception ex)
            {
                Check(name, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // OCR de extremo a extremo: imagen en el portapapeles → ventana de resultado.
        if (OcrService.IsAvailable)
        {
            string ocrPath = Path.Combine(Path.GetTempPath(), "powertools-ocr-ui-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var bitmap = new System.Drawing.Bitmap(700, 120))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                using (var font = new System.Drawing.Font("Segoe UI", 36, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
                {
                    graphics.Clear(System.Drawing.Color.White);
                    graphics.DrawString("Hola desde OCR 12345", font, System.Drawing.Brushes.Black, 10, 20);
                    bitmap.Save(ocrPath, System.Drawing.Imaging.ImageFormat.Png);
                }

                System.Windows.Clipboard.SetImage(new System.Windows.Media.Imaging.BitmapImage(new Uri(ocrPath)));
                _ = Selbar.App.RunOcrFromClipboardAsync();
                Pump(2500);

                var ocrWindow = System.Windows.Application.Current.Windows.OfType<ResultWindow>().LastOrDefault();
                Check("OCR abre la ventana de resultado", ocrWindow != null &&
                    ocrWindow.CurrentText.Contains("Hola", StringComparison.OrdinalIgnoreCase),
                    ocrWindow == null ? "sin ventana" : ocrWindow.Title + " | " + (ocrWindow.CurrentText ?? ""));

                if (ocrWindow != null)
                {
                    if (saveShots) SaveShot(ocrWindow, Path.Combine(shotsDir, "ResultWindow-ocr.png"));
                    ocrWindow.Close();
                }
            }
            catch (Exception ex)
            {
                Check("OCR de extremo a extremo", false, ex.ToString());
            }
            finally
            {
                try { System.Windows.Clipboard.Clear(); } catch { }
                try { File.Delete(ocrPath); } catch { }
            }
        }


        if (saveShots) Console.WriteLine("Capturas: " + shotsDir);

        try { app.Shutdown(); } catch { }

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"OK: {_passed}   Fallos: {_failed}");
        if (_failed > 0)
        {
            Console.WriteLine("Fallos:");
            foreach (var failure in Failures) Console.WriteLine("  · " + failure);
        }
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Cambiar de tema en caliente debe repintar la paleta (pinceles dinámicos) y
    /// no dejar la ventana con colores del tema anterior.
    /// </summary>
    private static void CheckLiveTheme(SettingsWindow window)
    {
        var box = FindByName<System.Windows.Controls.ComboBox>(window, "ThemeBox");
        if (box == null)
        {
            Check("Ajustes: selector de tema", false, "no se encontró ThemeBox");
            return;
        }

        int original = box.SelectedIndex;

        box.SelectedIndex = 1; // Claro
        window.UpdateLayout();
        var light = (System.Windows.Application.Current.TryFindResource("BgWindow")
            as System.Windows.Media.SolidColorBrush)?.Color;
        Check("tema claro en caliente", !Theming.IsDark && light is { } l && l.R > 0xE0,
            light is { } lc ? lc.ToString() : "sin pincel");

        box.SelectedIndex = 2; // Oscuro
        window.UpdateLayout();
        var dark = (System.Windows.Application.Current.TryFindResource("BgWindow")
            as System.Windows.Media.SolidColorBrush)?.Color;
        Check("tema oscuro en caliente", Theming.IsDark && dark is { } d && d.R < 0x40,
            dark is { } dc ? dc.ToString() : "sin pincel");

        box.SelectedIndex = original;
        window.UpdateLayout();
    }

    // ---------------- funciones nuevas ----------------

    private static void TextStyleTests()
    {
        string bold = UnicodeStyles.Apply("bold", "Hola 123");
        Check("negrita", bold == "\U0001D407\U0001D428\U0001D425\U0001D41A \U0001D7CF\U0001D7D0\U0001D7D1", bold);
        Check("cursiva con h especial", UnicodeStyles.Apply("italic", "h") == "\u210E");
        Check("doble trazo con excepciones", UnicodeStyles.Apply("doublestruck", "CN") == "\u2102\u2115");
        Check("caligráfica con excepciones", UnicodeStyles.Apply("script", "Be") == "\u212C\u212F");
        Check("versalitas", UnicodeStyles.Apply("smallcaps", "hola") == "\u029C\u1D0F\u029F\u1D00");
        Check("ancha", UnicodeStyles.Apply("fullwidth", "A1") == "\uFF21\uFF11");
        Check("círculos", UnicodeStyles.Apply("circled", "A1") == "\u24B6\u2460");
        Check("todos los estilos conservan el texto", UnicodeStyles.All.All(s =>
            s.Apply("Hola").Length > 0 && !s.Apply("Hola").Contains("Hola")));
        Check("estilos únicos", UnicodeStyles.All.Select(s => s.Id).Distinct().Count() == UnicodeStyles.All.Count);
    }

    private static void CalcTests()
    {
        // Aritmética
        Check("calc suma", CalcEngine.TryEvaluate("3+4", out var r) && r == "7", r);
        Check("calc producto", CalcEngine.TryEvaluate("12*8", out r) && r == "96", r);
        Check("calc división", CalcEngine.TryEvaluate("10/4", out r) && r == "2,5", r);
        Check("calc paréntesis", CalcEngine.TryEvaluate("(3+4)/2", out r) && r == "3,5", r);
        Check("calc potencia", CalcEngine.TryEvaluate("2^10", out r) && r == "1 024", r);
        Check("calc negativo", CalcEngine.TryEvaluate("-5+8", out r) && r == "3", r);
        Check("calc prioridad", CalcEngine.TryEvaluate("2+3*4", out r) && r == "14", r);
        Check("calc hexadecimal", CalcEngine.TryEvaluate("0x10+2", out r) && r == "18", r);
        Check("calc hex solo", CalcEngine.TryEvaluate("0xFF", out r) && r == "255", r);
        Check("calc potencia con negativo", CalcEngine.TryEvaluate("-2^2", out r) && r == "-4", r);
        Check("calc prioridad con ^", CalcEngine.TryEvaluate("2+3^2", out r) && r == "11", r);

        // Porcentajes
        Check("calc +20%", CalcEngine.TryEvaluate("350+20%", out r) && r == "420", r);
        Check("calc -10%", CalcEngine.TryEvaluate("200-10%", out r) && r == "180", r);
        Check("calc *15%", CalcEngine.TryEvaluate("200*15%", out r) && r == "30", r);
        Check("calc 20% de", CalcEngine.TryEvaluate("20% de 350", out r) && r == "70", r);
        Check("calc 15% del", CalcEngine.TryEvaluate("15% del 200", out r) && r == "30", r);

        // No son operaciones (no deben mostrar el chip)
        Check("calc no es texto", !CalcEngine.TryEvaluate("hola mundo", out _));
        Check("calc no es fecha ISO", !CalcEngine.TryEvaluate("2026-10-26", out _));
        Check("calc no es fecha corta", !CalcEngine.TryEvaluate("10/26/2026", out _));
        Check("calc no es número", !CalcEngine.TryEvaluate("42", out _));
        Check("calc no es URL", !CalcEngine.TryEvaluate("https://x.com/a-b", out _));

        // La regex del preset coincide con lo que el motor sabe resolver
        var calc = ActionPresets.Defaults().First(a => a.Id == "text.calc");
        var rx = new Regex(calc.MatchRegex!);
        Check("chip calc operación", rx.IsMatch("12*8") && rx.IsMatch("350+20%") && rx.IsMatch("(3+4)/2"));
        Check("chip calc no en fecha", !rx.IsMatch("2026-10-26"));
        Check("chip calc no en teléfono", !rx.IsMatch("+51 987 654 321"));
        Check("chip calc no en fecha corta", !rx.IsMatch("12/03/2025"));
        Check("chip calc sí en resta", rx.IsMatch("100 - 45"));

        // Cadenas de resta que la heurística de fecha confundía
        Check("calc resta encadenada", CalcEngine.TryEvaluate("1000-100-50", out r) && r == "850", r);
        Check("calc descuentos encadenados", CalcEngine.TryEvaluate("100-10%-10%", out r) && r == "81", r);
        Check("calc miles con puntos", CalcEngine.TryEvaluate("1.000.000+1", out r) && r == "1 000 001", r);
        Check("calc miles con coma decimal", CalcEngine.TryEvaluate("1.234,5*2", out r) && r == "2 469", r);
        Check("calc hex gigante no lanza", !CalcEngine.TryEvaluate("0x" + new string('F', 20), out _));
        Check("calc hex 64 bits sin falso", !CalcEngine.TryEvaluate("0xFFFFFFFFFFFFFFFF", out _));

        // Unidades (offline)
        Check("conv km/millas", (CalcEngine.ConvertAsync("3 km a millas").GetAwaiter().GetResult() ?? "").Contains("1,864114"));
        Check("conv kg/lb", (CalcEngine.ConvertAsync("10 kg a libras").GetAwaiter().GetResult() ?? "").Contains("22,046226"));
        Check("conv GB/MB", (CalcEngine.ConvertAsync("2 gb a mb").GetAwaiter().GetResult() ?? "").Contains("2 048 MB"));
        Check("conv C/F", (CalcEngine.ConvertAsync("30 c a f").GetAwaiter().GetResult() ?? "").Contains("86 °F"));
        Check("conv número pegado", (CalcEngine.ConvertAsync("3km a millas").GetAwaiter().GetResult() ?? "").Contains("1,864114"));
        Check("conv grados pegados", (CalcEngine.ConvertAsync("30°C a F").GetAwaiter().GetResult() ?? "").Contains("86 °F"));
        Check("conv mixto inválido", (CalcEngine.ConvertAsync("3 km a kg").GetAwaiter().GetResult() ?? "").Contains("distintas"));

        // Moneda con tasas inyectadas (sin red: el proveedor falso las da)
        var previous = CalcEngine.RatesProvider;
        try
        {
            CalcEngine.RatesProvider = _ => Task.FromResult<IReadOnlyDictionary<string, double>?>(
                new Dictionary<string, double> { ["USD"] = 1, ["PEN"] = 3.75, ["EUR"] = 0.92 });
            Check("conv USD/PEN", (CalcEngine.ConvertAsync("15 usd a pen").GetAwaiter().GetResult() ?? "").Contains("56,25 PEN"));
        }
        finally { CalcEngine.RatesProvider = previous; }

        // Con SELBAR_OFFLINE=1 la moneda avisa en vez de conectar
        Check("conv moneda offline", (CalcEngine.ConvertAsync("10 usd a eur").GetAwaiter().GetResult() ?? "").Contains("No hay conexión"));
    }

    private static void IntegrationTests()
    {
        string vault = Path.Combine(Path.GetTempPath(), "pt-obsidian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vault);
        try
        {
            var saved = ObsidianSink.Save(vault, "Selbar", "Línea uno\nlínea dos", "chrome",
                new DateTime(2026, 10, 26, 15, 30, 0));
            Check("obsidian guarda", saved.Ok && File.Exists(saved.Path), saved.Path ?? saved.Message);
            Check("obsidian nombre", Path.GetFileName(saved.Path ?? "") == "2026-10-26 1530 Línea-uno.md",
                Path.GetFileName(saved.Path ?? ""));

            string body = File.Exists(saved.Path) ? File.ReadAllText(saved.Path!) : "";
            Check("obsidian frontmatter", body.StartsWith("---") && body.Contains("origen:") && body.Contains("chrome") && body.Contains("tags: [powertools]"));
            Check("obsidian cuerpo", body.Contains("línea dos"));

            var again = ObsidianSink.Save(vault, "Selbar", "Línea uno", null, new DateTime(2026, 10, 26, 15, 30, 0));
            Check("obsidian no sobrescribe", again.Ok && (again.Path ?? "").Contains("(2)"), Path.GetFileName(again.Path ?? ""));

            // Escritura exclusiva: no pisa nota ajena ni sale del vault
            var clashText = "Choque exclusivo de escritura"; var stamp2 = new DateTime(2026, 10, 27, 9, 0, 0);
            string target2 = Path.Combine(vault, "Selbar", ObsidianSink.SuggestFileName(clashText, stamp2));
            Directory.CreateDirectory(Path.GetDirectoryName(target2)!); File.WriteAllText(target2, "nota ajena");
            var clash = ObsidianSink.Save(vault, "Selbar", clashText, null, stamp2);
            Check("obsidian no pisa nota ajena", clash.Ok && clash.Path != null && !clash.Path.Equals(target2, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(target2) == "nota ajena", clash.Path);
            var evil = ObsidianSink.Save(vault, "../../fuera", "intento de escape", null);
            Check("obsidian .. rechazado", !evil.Ok);
            Check("obsidian barra sola es la raíz", ObsidianSink.Save(vault, "/", "nota raíz", null).Ok);
            Check("obsidian dentro del vault", ObsidianSink.Save(vault, "Selbar", "x", null).Path!.StartsWith(Path.GetFullPath(vault), StringComparison.OrdinalIgnoreCase));

            Check("obsidian sin configurar", !ObsidianSink.Save("", "", "x", null).Ok);
            Check("obsidian vault inexistente", !ObsidianSink.Save(Path.Combine(vault, "nope"), "", "x", null).Ok);
            Check("obsidian sin subcarpeta", ObsidianSink.Save(vault, "", "x", null).Ok);

            var empty = ObsidianSink.Save(vault, "", "   ", null);
            Check("obsidian sin texto", !empty.Ok && empty.Message.Contains("texto"));
        }
        finally
        {
            try { Directory.Delete(vault, true); } catch { }
        }

        // Voz: no debe lanzar aunque el equipo no tenga voces instaladas.
        Check("voz consultable", SpeechService.VoiceName.Length > 0, SpeechService.VoiceName);
        Check("atajo de dictado por defecto", new AppSettings().DictationHotkey == "Ctrl+Shift+D");

        // Perfiles por app: texto ↔ lista y filtrado del registro.
        var parsed = ProfileRules.Parse("# comentario\nchrome = ai.todos, text.qr\nexcel.exe:ai.translate\n\nbasura\n");
        Check("perfiles parsea", parsed.Count == 2, parsed.Count.ToString());
        Check("perfiles proceso", parsed[0].Process == "chrome" && parsed[1].Process == "excel");
        Check("perfiles ids", parsed[0].HiddenActions.Count == 2 && parsed[1].HiddenActions[0] == "ai.translate");
        Check("perfiles formato", ProfileRules.Format(parsed).Contains("chrome = ai.todos, text.qr"));
        Check("perfil formato omite vacíos", ProfileRules.Format(new[] { new AppProfile { Process = "x", HiddenActions = new() } }) == "");

        var registry = new ActionRegistry();
        registry.Profiles = parsed;
        registry.SetEnabled("ai.todos", true);   // por defecto viene apagada
        Check("perfil esconde acción", registry.HiddenFor("chrome.exe").Contains("ai.todos"));
        Check("perfil no afecta a otra app", registry.HiddenFor("notepad").Count == 0);
        Check("perfil sin proceso", registry.HiddenFor("").Count == 0);

        var chromeVisible = registry.GetVisibleActions("un texto cualquiera", "chrome").Select(a => a.Id).ToList();
        var notepadVisible = registry.GetVisibleActions("un texto cualquiera", "notepad").Select(a => a.Id).ToList();
        Check("perfil filtra visibles", !chromeVisible.Contains("ai.todos") && notepadVisible.Contains("ai.todos"));

        // Perfiles desactivados con ! (ida y vuelta por la caja de Ajustes)
        var dis = ProfileRules.Parse("!chrome = ai.todos\ncode = ai.translate");
        Check("perfil desactivado", dis.Count == 2 && !dis[0].Enabled && dis[1].Enabled);
        Check("formato conserva desactivado", ProfileRules.Format(dis).Contains("!chrome = ai.todos"));
        Check("ida y vuelta conserva flags", string.Join(",", ProfileRules.Parse(ProfileRules.Format(dis)).Select(p => p.Enabled)) == "False,True");
        var dup = ProfileRules.Parse("!chrome = a\nchrome = b");
        Check("perfiles: ! se fusiona con duplicados", dup.Count == 1 && !dup[0].Enabled);
    }

    private static void LinkTests()
    {
        Check("link limpia utm", LinkTools.CleanTracking("https://t.com/p?utm_source=fb&id=7") == "https://t.com/p?id=7",
            LinkTools.CleanTracking("https://t.com/p?utm_source=fb&id=7"));
        Check("link limpia todo", LinkTools.CleanTracking("https://x.com/a?fbclid=1") == "https://x.com/a");
        Check("link conserva #", LinkTools.CleanTracking("https://x.com/a?gclid=1#s") == "https://x.com/a#s");
        Check("link sin cambios", LinkTools.CleanTracking("https://x.com/a?id=7") == "https://x.com/a?id=7");
        Check("link detecta seguimiento", LinkTools.HasTracking("https://a.com/?fbclid=x") && !LinkTools.HasTracking("https://a.com/?id=2"));
        Check("link acortadores", LinkTools.IsShortener("https://bit.ly/abc") && LinkTools.IsShortener("https://youtu.be/x")
            && !LinkTools.IsShortener("https://youtube.com/watch?v=1"));
        Check("link handle", LinkTools.Handle("@cesar") == "cesar");

        string html = "<html><head><title> Mi  título </title><style>a{}</style></head><body>"
                    + "<script>x()</script><p>Hola &amp; chau</p><p>Otra línea</p></body></html>";
        Check("html título", HtmlText.ExtractTitle(html) == "Mi título", HtmlText.ExtractTitle(html));
        string plain = HtmlText.ToPlain(html);
        Check("html sin scripts", !plain.Contains("x()") && !plain.Contains("a{}"));
        Check("html texto", plain.Contains("Hola & chau") && plain.Contains("Otra línea"));

        // Las regex de la barra coinciden solo cuando toca
        var presets = ActionPresets.Defaults();
        var clean = new Regex(presets.First(a => a.Id == "link.clean").MatchRegex!);
        var expand = new Regex(presets.First(a => a.Id == "link.expand").MatchRegex!);
        var maps = new Regex(presets.First(a => a.Id == "place.maps").MatchRegex!);
        var social = new Regex(presets.First(a => a.Id == "social.instagram").MatchRegex!);
        Check("chip limpiar enlace", clean.IsMatch("https://t.com/?utm_source=x") && !clean.IsMatch("https://t.com/?id=7"));
        Check("chip expandir", expand.IsMatch("https://bit.ly/abc") && !expand.IsMatch("https://google.com/x"));
        Check("chip maps", maps.IsMatch("Av. Larco 123, Miraflores") && maps.IsMatch("Calle 50 1200"));
        Check("chip social", social.IsMatch("@cesar.dev") && !social.IsMatch("cesar"));
        Check("file:// rechazado", !WebFetchGuard.TryNormalizeHttpUrl("file:///c:/x.txt", out _, out _));
        Check("loopback rechazado", !WebFetchGuard.TryNormalizeHttpUrl("http://127.0.0.1/x", out _, out _)
            && !WebFetchGuard.TryNormalizeHttpUrl("http://localhost/x", out _, out _));
        Check("metadata rechazada", !WebFetchGuard.TryNormalizeHttpUrl("http://169.254.169.254/latest/meta-data/", out _, out _));
        Check("IPv6 loopback rechazado", !WebFetchGuard.TryNormalizeHttpUrl("http://[::1]:8080/x", out _, out _));
        Check("LAN permitida", WebFetchGuard.TryNormalizeHttpUrl("http://192.168.1.10/x", out _, out _));
        Check("mailto intacto", LinkTools.CleanTracking("mailto:a@b.com?subject=x") == "mailto:a@b.com?subject=x");
        Check("SafeFileName sin escape", !WebFetchGuard.SafeFileName("../evil").StartsWith('.'));
        Check("SafeFileName sin punto final", WebFetchGuard.SafeFileName("a.") == "a");
        Check("SafeFileName dispositivo reservado", WebFetchGuard.SafeFileName("CON") == "_CON");
    }

    private static void ContactDatePathTests()
    {
        // Correo
        Check("email simple", EmailTools.IsEmail("juan@x.com"));
        Check("email con nombre", EmailTools.IsEmail("Juan Pérez <juan@x.com>"));
        Check("email mailto", EmailTools.IsEmail("mailto:ana@empresa.com"));
        Check("email no texto", !EmailTools.IsEmail("hola mundo"));
        Check("email no tld", !EmailTools.IsEmail("a@b"));
        Check("email con basura detrás", !EmailTools.IsEmail("a<b@c.com> extra"));
        Check("email puntos dobles", !EmailTools.IsEmail("a..b@x.com") && !EmailTools.IsEmail(".a@x.com"));
        Check("email dominio inválido", !EmailTools.IsEmail("a@-bad.com") && !EmailTools.IsEmail("a@b..com"));
        Check("email extrae <>", EmailTools.ExtractEmail("Juan Pérez <juan@x.com>") == "juan@x.com");
        Check("email extrae mailto", EmailTools.ExtractEmail("mailto:Ana@Empresa.com?subject=h") == "ana@empresa.com");
        Check("email mailto arma", EmailTools.ToMailto("juan@x.com") == "mailto:juan@x.com");
        Check("email mailto robusto", EmailTools.ToMailto("Juan <juan@x.com>") == "mailto:juan@x.com"
            && EmailTools.ToMailto("basura") == "");
        Check("email con coma final", EmailTools.IsEmail("juan@x.com,"));
        Check("email entre paréntesis", EmailTools.IsEmail("(juan@x.com)") && EmailTools.IsEmail("<juan@x.com>,"));
        Check("email local >64", !EmailTools.IsEmail(new string('a', 65) + "@x.com"));
        Check("email etiqueta >63", !EmailTools.IsEmail("a@" + new string('b', 64) + ".com"));

        // La regex del preset acompaña al detector
        var presets = ActionPresets.Defaults();
        var emailRx = new Regex(presets.First(a => a.Id == "contact.email").MatchRegex!);
        Check("chip correo", emailRx.IsMatch("juan@x.com") && emailRx.IsMatch("Juan Pérez <juan@x.com>")
            && !emailRx.IsMatch("hola mundo") && !emailRx.IsMatch("mailto:a@b.com basura"));

        // Fecha (referencia fija para no depender del día real)
        var hoy = new DateTime(2026, 10, 29);
        Check("fecha dmy", DateTools.TryParse("12/03/2025", hoy, out var d1) && d1 == new DateTime(2025, 3, 12));
        Check("fecha iso", DateTools.TryParse("2025-03-12", hoy, out var d2) && d2 == new DateTime(2025, 3, 12));
        Check("fecha nombre", DateTools.TryParse("12 de marzo de 2025", hoy, out var d3) && d3 == new DateTime(2025, 3, 12));
        Check("fecha hoy/mañana/ayer",
            DateTools.TryParse("hoy", hoy, out var h) && h == hoy &&
            DateTools.TryParse("mañana", hoy, out var m) && m == new DateTime(2026, 10, 30) &&
            DateTools.TryParse("ayer", hoy, out var a) && a == new DateTime(2026, 10, 28));
        Check("fecha inválida", !DateTools.TryParse("32/13/2025", hoy, out _) && !DateTools.TryParse("hola", hoy, out _));
        Check("fecha año corto", DateTools.TryParse("12/03/25", hoy, out var d5) && d5 == new DateTime(2025, 3, 12));
        Check("fecha pivote 1999", DateTools.TryParse("12/03/99", hoy, out var d99) && d99 == new DateTime(1999, 3, 12), d99.ToString());
        Check("fecha 29feb rueda", DateTools.TryParse("29/02", new DateTime(2026, 1, 1), out var d29) && d29 == new DateTime(2028, 2, 29), d29.ToString());
        Check("fecha con hora", DateTools.TryParse("mañana 15:30", hoy, out var dpm) && dpm == new DateTime(2026, 10, 30));
        Check("fecha día con hora pm", DateTools.TryParse("12/03/2025 5:30pm", hoy, out var dpm2) && dpm2 == new DateTime(2025, 3, 12));
        Check("fecha describe", DateTools.Describe("12/03/2025", hoy).Contains("2025-03-12"));
        Check("fecha sin año rueda", DateTools.TryParse("12 de marzo", hoy, out var dma) && dma == new DateTime(2027, 3, 12), dma.ToString());
        Check("fecha 1 de enero", DateTools.TryParse("1 de enero", hoy, out var den) && den == new DateTime(2027, 1, 1), den.ToString());
        Check("fecha 29 de febrero sin año", DateTools.TryParse("29 de febrero", hoy, out var dfb) && dfb == new DateTime(2028, 2, 29), dfb.ToString());
        Check("fecha del año", DateTools.TryParse("12 de marzo del 2025", hoy, out var ddel) && ddel == new DateTime(2025, 3, 12));
        Check("fecha mes abreviado", DateTools.TryParse("12 mar. 2025", hoy, out var dmar) && dmar == new DateTime(2025, 3, 12));
        Check("fecha con guion y mes", DateTools.TryParse("12-mar-2025", hoy, out var dgm) && dgm == new DateTime(2025, 3, 12));
        Check("fecha con 'a las'", DateTools.TryParse("12/03/2025 a las 3pm", hoy, out var dal) && dal == new DateTime(2025, 3, 12));
        Check("fecha en MaxValue no lanza", !DateTools.TryParse("mañana", DateTime.MaxValue, out _));
        Check("describe en MinValue no lanza", !string.IsNullOrEmpty(DateTools.Describe("ayer", DateTime.MinValue)));
        var dateRx = new Regex(presets.First(a => a.Id == "date.info").MatchRegex!);
        Check("chip fecha", dateRx.IsMatch("12/03/2025") && dateRx.IsMatch("mañana")
            && dateRx.IsMatch("12/03/2025 5:30pm") && !dateRx.IsMatch("hola mundo"));

        // Ruta
        Check("ruta unidad", FilePathTools.IsFilePath(@"C:\Users\cesar\doc.pdf"));
        Check("ruta unc", FilePathTools.IsFilePath(@"\\server\share\archivo.docx"));
        Check("ruta env", FilePathTools.IsFilePath(@"%USERPROFILE%\Documents\a.txt"));
        Check("ruta home", FilePathTools.IsFilePath("~/docs/nota.md"));
        Check("ruta home corta", FilePathTools.IsFilePath("~/a"));
        Check("ruta no url", !FilePathTools.IsFilePath("https://x.com/a/b"));
        Check("ruta no email", !FilePathTools.IsFilePath("juan@x.com"));
        Check("ruta normaliza", FilePathTools.Normalize("\"C:/a/b.txt\"") == @"C:\a\b.txt");
        Check("ruta comillas tipográficas", FilePathTools.Normalize("“C:/a/b.txt”") == @"C:\a\b.txt");
        Check("ruta comillas latinas", FilePathTools.Normalize("«c:/a/b.txt»") == @"c:\a\b.txt");
        var fileRx = new Regex(presets.First(a => a.Id == "file.open").MatchRegex!);
        Check("chip ruta", fileRx.IsMatch(@"C:\a\b.txt") && !fileRx.IsMatch("hola mundo"));
    }

    private static void SuggestTests()
    {
        string? SugId(string t) => TextSuggest.Suggest(t)?.ActionId;
        Check("sugiere correo", SugId("juan@x.com") == "contact.email");
        Check("sugiere correo con nombre", SugId("Juan Pérez <juan@x.com>") == "contact.email");
        Check("sugiere no basura", SugId("mailto:a@b.com basura") == null);
        Check("sugiere ruta", SugId(@"C:\a\b.txt") == "file.open");
        Check("sugiere fecha", SugId("12/03/2025") == "date.info" && SugId("mañana") == "date.info");
        Check("sugiere acortador", SugId("https://bit.ly/abc") == "link.expand");
        Check("sugiere seguimiento", SugId("https://t.com/p?utm_source=fb") == "link.clean");
        Check("no adivina", TextSuggest.Suggest("hola mundo") == null
            && TextSuggest.Suggest("a@b.com\notra línea") == null
            && TextSuggest.Suggest("") == null);

        // El registro pone la sugerida primera y respeta el interruptor
        var registry = new ActionRegistry();
        Check("email va primero", registry.GetVisibleActions("juan@x.com").First().Id == "contact.email");
        Check("ruta va primero", registry.GetVisibleActions(@"C:\a\b.txt").First().Id == "file.open");
        Check("fecha va primero", registry.GetVisibleActions("12/03/2025").First().Id == "date.info");
        Check("texto normal igual", registry.GetVisibleActions("hola mundo").First().Id == "ai.translate");
        registry.SmartSuggestEnabled = false;
        Check("apagado no reordena", registry.GetVisibleActions("juan@x.com").First().Id == "ai.translate");
    }

    private static void ClipboardHistoryTests()
    {
        ClipboardHistoryStore.Clear();
        Check("clip empieza vacío", ClipboardHistoryStore.All().Count == 0);
        ClipboardHistoryStore.Add("  ");
        Check("clip ignora vacío", ClipboardHistoryStore.All().Count == 0);
        ClipboardHistoryStore.Add("primero");
        ClipboardHistoryStore.Add("segundo");
        ClipboardHistoryStore.Add("segundo");
        Check("clip sin duplicar", ClipboardHistoryStore.All().Count == 2);
        Check("clip lo último primero", ClipboardHistoryStore.All()[0].Text == "segundo");
        Check("clip filtra", ClipboardHistoryStore.All("prim").Count == 1);
        ClipboardHistoryStore.Remove(ClipboardHistoryStore.All()[1].Id);
        Check("clip elimina", ClipboardHistoryStore.All().Count == 1);
        for (int i = 0; i < 25; i++) ClipboardHistoryStore.Add("texto " + i);
        Check("clip tope 20", ClipboardHistoryStore.All().Count == 20);
        ClipboardHistoryStore.Clear();
        Check("clip vacía", ClipboardHistoryStore.All().Count == 0);

        // JSON editado a mano: no tumbar ni colar nulls a la UI
        File.WriteAllText(ClipboardHistoryStore.FilePath, "[null,{\"Id\":null,\"Text\":null}]");
        Check("portapapeles con nulls no tumba", ClipboardHistoryStore.All().Count == 0);
        ClipboardHistoryStore.Add("tras nulls");
        Check("portapapeles sigue guardando", ClipboardHistoryStore.All().Any(e => e.Text == "tras nulls"));
        ClipboardHistoryStore.Clear();
    }

    private static void ColorTests()
    {
        Check("color #RRGGBB", ColorCode.TryParse("#0078D4", out var blue) && blue.Hex == "#0078D4"
            && blue.Rgb == "rgb(0, 120, 212)" && !blue.HasAlpha, blue?.Hex ?? "null");
        Check("color corto", ColorCode.TryParse("#f00", out var red) && red.Hex == "#FF0000");
        Check("color con alfa", ColorCode.TryParse("#00FF0080", out var alpha) && alpha.HasAlpha
            && alpha.Hex == "#00FF00", alpha?.Alpha ?? "null");
        Check("color sin #", ColorCode.TryParse("FF8800", out var orange) && orange.Hex == "#FF8800");
        Check("color inválido", !ColorCode.TryParse("red", out _) && !ColorCode.TryParse("#12", out _));
        Check("regex de color no confunde palabras", ColorCode.IsHex("#abc") && !ColorCode.IsHex("abc"));
        Check("HSL correcto", ColorCode.TryParse("#FF0000", out var hsl) && hsl.Hsl.StartsWith("hsl(0, 100%", StringComparison.Ordinal), hsl?.Hsl ?? "null");
    }

    private static void YouTubeTests()
    {
        Check("youtu.be", YtDlp.TryNormalizeUrl("https://youtu.be/dQw4w9WgXcQ", out var a)
            && a == "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Check("watch con parámetros", YtDlp.TryNormalizeUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s", out var b)
            && b.Contains("dQw4w9WgXcQ"));
        Check("shorts", YtDlp.TryNormalizeUrl("https://youtube.com/shorts/dQw4w9WgXcQ", out var c)
            && c.Contains("dQw4w9WgXcQ"));
        Check("dominio no soportado", !YtDlp.TryNormalizeUrl("https://ejemplo.com/video", out _)
            && !YtDlp.TryNormalizeUrl("javascript:alert(1)", out _));
        Check("TikTok soportado", YtDlp.TryNormalizeUrl("https://www.tiktok.com/@user/video/123", out _));
        Check("Instagram soportado", YtDlp.TryNormalizeUrl("https://www.instagram.com/reel/abc/", out _));
        Check("X soportado", YtDlp.TryNormalizeUrl("https://x.com/user/status/123", out _));
        Check("lista de dominios amplia", YtDlp.SupportedDomains.Count >= 20);
        var video = YtDlp.BuildArguments("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false, "C:\\tmp");
        var audio = YtDlp.BuildArguments("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true, "C:\\tmp");
        Check("argumentos de vídeo", video.Contains("--no-playlist") && video.Contains("-P") && video.Contains("b[height<=1080]/b"));
        Check("argumentos de audio", audio.Contains("ba[ext=m4a]/ba"));
        Check("URL validada al final", video[^1].StartsWith("https://www.youtube.com/watch?v=", StringComparison.Ordinal));
        Check("teléfono normalizado", Selbar.Core.Actions.ActionExecutor.NormalizePhone("+52 (55) 1234-5678") == "525512345678");
        Check("prefijo 00", Selbar.Core.Actions.ActionExecutor.NormalizePhone("0052 55 1234 5678") == "525512345678");
        Check("quitar saltos", Selbar.Core.Actions.ActionExecutor.RemoveLineBreaks("Hola\n\nmundo  de\nprueba") == "Hola mundo de prueba", Selbar.Core.Actions.ActionExecutor.RemoveLineBreaks("Hola\n\nmundo  de\nprueba"));
        Check("URL sin credenciales", YtDlp.TryNormalizeUrl("https://user:pass@www.tiktok.com/@u/video/123", out var nc) && !nc.Contains("user"), nc);
        bool lanzaArgs = false;
        try { YtDlp.BuildArguments("-o /tmp/evil", false, "C:\\tmp"); } catch (ArgumentException) { lanzaArgs = true; }
        Check("args rechazan basura", lanzaArgs);
        Check("youtube sin esquema", YtDlp.TryNormalizeUrl("youtube.com/watch?v=dQw4w9WgXcQ", out var sy)
            && sy == "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Check("watch con id corto no lanza", !YtDlp.TryNormalizeUrl("https://www.youtube.com/watch?v=abc", out _));
        Check("embed y live", YtDlp.TryNormalizeUrl("https://www.youtube.com/embed/dQw4w9WgXcQ", out _)
            && YtDlp.TryNormalizeUrl("https://www.youtube.com/live/dQw4w9WgXcQ", out _));
        Check("watch con app=desktop", YtDlp.TryNormalizeUrl("https://www.youtube.com/watch?app=desktop&v=dQw4w9WgXcQ", out _));
        bool lanzaCarpeta = false;
        try { YtDlp.BuildArguments("https://youtu.be/dQw4w9WgXcQ", true, "--exec=calc"); } catch (ArgumentException) { lanzaCarpeta = true; }
        Check("carpeta con guion rechazada", lanzaCarpeta);
    }

    private static void LensTests()
    {
        Check("Lens acepta URL de imagen", GoogleLens.IsImageUrl("https://ejemplo.com/foto.JPG?x=1"));
        Check("Lens rechaza URL normal", !GoogleLens.IsImageUrl("https://ejemplo.com/noticia"));
        Check("Lens rechaza texto", !GoogleLens.IsImageUrl("hola mundo"));
        Check("Lens construye uploadbyurl", GoogleLens.ByUrl("https://ejemplo.com/foto.png")
            .StartsWith("https://lens.google.com/uploadbyurl?url=", StringComparison.Ordinal));
        Check("Lens confía en google", GoogleLens.IsTrustedResultUrl(new Uri("https://lens.google.com/v3/upload?stcs=1")));
        Check("Lens rechaza google.evil", !GoogleLens.IsTrustedResultUrl(new Uri("https://google.evil.com/x")));
        Check("Lens valida tamaño", GoogleLens.MaxUploadBytes == 20L * 1024 * 1024);
    }

    private static void QrTests()
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode("https://ejemplo.com", QRCoder.QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        Check("QR genera matriz válida", data.ModuleMatrix.Count >= 21);
    }

    private static void GroqTests()
    {
        Check("Groq valida extensión", GroqWhisper.IsSupportedFile("audio.mp3") && GroqWhisper.IsSupportedFile("nota.aac")
            && !GroqWhisper.IsSupportedFile("audio.exe"));
        Check("Groq valida archivo inexistente", GroqWhisper.ValidateFile(Path.Combine(Path.GetTempPath(), "no-existe.mp3")) != null);
        try
        {
            GroqWhisper.TranscribeAsync("audio.mp3", "fake", null).GetAwaiter().GetResult();
            Check("Groq respeta modo offline", false);
        }
        catch (Exception ex)
        {
            Check("Groq respeta modo offline", ex.Message.Contains("offline", StringComparison.OrdinalIgnoreCase), ex.Message);
        }
    }

    private static void OcrTests()
    {
        // Validación pura de imágenes (no necesita OCR instalado)
        string img = Path.Combine(Path.GetTempPath(), "powertools-imgval-" + Guid.NewGuid().ToString("N") + ".png");
        using (var bitmap = new System.Drawing.Bitmap(4, 4))
        {
            bitmap.Save(img, System.Drawing.Imaging.ImageFormat.Png);
        }
        string fake = Path.Combine(Path.GetTempPath(), "powertools-imgval-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(fake, "no soy una imagen");
        try
        {
            Check("imagen válida", ImageValidation.ValidateImageFile(img, 20L * 1024 * 1024) == null);
            Check("imagen con extensión mala", ImageValidation.ValidateImageFile(img + ".exe", 20L * 1024 * 1024) != null);
            Check("texto con .png rechazado", ImageValidation.ValidateImageFile(fake, 20L * 1024 * 1024) != null);
            Check("content-type jpeg", ImageValidation.ContentTypeForImage("f.jpg") == "image/jpeg");
        }
        finally
        {
            try { File.Delete(img); } catch { }
            try { File.Delete(fake); } catch { }
        }

        if (!OcrService.IsAvailable)
        {
            Check("OCR (sin paquetes de idioma instalados)", true);
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), "powertools-ocr-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(700, 120))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Segoe UI", 36, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawString("Hola mundo 12345", font, System.Drawing.Brushes.Black, 10, 20);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            string text = OcrService.ReadFileAsync(path).GetAwaiter().GetResult();
            Check("OCR lee texto", text.Contains("Hola", StringComparison.OrdinalIgnoreCase)
                && text.Contains("12345", StringComparison.Ordinal), text);

            var image = new System.Windows.Media.Imaging.BitmapImage(new Uri(path));
            System.Windows.Clipboard.SetImage(image);
            string fromClipboard = OcrService.ReadClipboardImageAsync().GetAwaiter().GetResult();
            Check("OCR desde el portapapeles", fromClipboard.Contains("Hola", StringComparison.OrdinalIgnoreCase), fromClipboard);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void SetStatic(string propertyName, object value)
    {
        var property = typeof(Selbar.App).GetProperty(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var setter = property?.GetSetMethod(true);
        if (setter == null) throw new InvalidOperationException("No existe App." + propertyName);
        setter.Invoke(null, new[] { value });
    }

    /// <summary>Deja correr el dispatcher un momento para que terminen las animaciones.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Renderiza la ventana completa (marco + contenido) sobre el color base del tema.</summary>
    private static void SaveShot(Window window, string path)
    {
        if (window.Content is not System.Windows.FrameworkElement root) return;

        Pump(240); // que las animaciones (switches, fundidos) lleguen a su estado final
        window.UpdateLayout();
        root.UpdateLayout();

        double width = window.ActualWidth;
        double height = window.ActualHeight;

        // Las ventanas con SizeToContent se miden por su contenido; las de tamaño
        // fijo, por el área de cliente.
        if (width < 20 || height < 20)
        {
            bool autoSize = window.SizeToContent != System.Windows.SizeToContent.Manual;
            if (autoSize)
            {
                root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(40, root.DesiredSize.Width);
                height = Math.Max(40, root.DesiredSize.Height);
            }
            else
            {
                width = Math.Max(40, window.Width - 16);
                height = Math.Max(40, window.Height - 39);
            }

            root.Measure(new System.Windows.Size(width, height));
            root.Arrange(new System.Windows.Rect(0, 0, width, height));
            root.UpdateLayout();
        }

        const double dpi = 120; // un poco más nítido que 96
        double scale = dpi / 96.0;
        int pixelWidth = (int)Math.Ceiling(width * scale);
        int pixelHeight = (int)Math.Ceiling(height * scale);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            pixelWidth, pixelHeight, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);

        // El fondo de la ventana es transparente cuando Mica está activo; en una
        // captura estática hay que aplanarlo sobre el color base del tema.
        var backdrop = new System.Windows.Media.DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(BaseBackground()), null,
                new System.Windows.Rect(0, 0, width, height));
        }
        bitmap.Render(backdrop);

        try
        {
            bitmap.Render(window);
        }
        catch
        {
            bitmap.Render(root);
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Color base del tema actual (el mismo que pinta la ventana sin Mica).</summary>
    private static System.Windows.Media.Color BaseBackground()
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("BgWindow") is System.Windows.Media.SolidColorBrush brush)
                return brush.Color;
        }
        catch { }
        return System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');

    /// <summary>
    /// Recorre los TextBlock visibles y comprueba que su color tenga contraste
    /// suficiente con el fondo real. Detecta textos "negros sobre oscuro" como
    /// los que provocaba el estilo implícito de Window en clases derivadas.
    /// </summary>
    private static void CheckTextContrast(Window window, string name)
    {
        var low = new List<string>();

        WalkVisual(window, element =>
        {
            if (element is not System.Windows.Controls.TextBlock block) return;
            if (!block.IsVisible || block.ActualWidth < 1 || !block.IsEnabled) return;

            var text = (block.Text ?? "").Trim();
            if (text.Length == 0) return;
            if (block.Foreground is not System.Windows.Media.SolidColorBrush foreground || foreground.Color.A < 200) return;

            var background = EffectiveBackground(block);
            if (background == null) return;

            double ratio = ContrastRatio(foreground.Color, background.Value);
            if (ratio < 2.5)
                low.Add($"«{(text.Length > 22 ? text[..22] + "…" : text)}» {ratio:F1}:1");
        });

        Check($"{name}: texto legible (contraste)", low.Count == 0, string.Join(", ", low.Take(4)));
    }

    private static void WalkVisual(System.Windows.DependencyObject root, Action<System.Windows.DependencyObject> visit)
    {
        visit(root);
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            WalkVisual(System.Windows.Media.VisualTreeHelper.GetChild(root, i), visit);
    }

    private static System.Windows.Media.Color? EffectiveBackground(System.Windows.DependencyObject? element)
    {
        System.Windows.Media.Color? accumulated = null;

        while (element != null)
        {
            System.Windows.Media.Brush? brush = element switch
            {
                // Solo contenedores que pintan de verdad (el fondo de un Control
                // puede venir del estilo de tema y no verse por la plantilla).
                System.Windows.Controls.Panel panel => panel.Background,
                System.Windows.Controls.Border border => border.Background,
                _ => null
            };

            if (brush is System.Windows.Media.SolidColorBrush solid && solid.Color.A > 0)
            {
                // El primero que aparece está delante; los ancestros van detrás.
                accumulated = accumulated == null
                    ? solid.Color
                    : CompositeOver(accumulated.Value, solid.Color);

                if (accumulated.Value.A >= 250) return accumulated;
            }

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        var windowColor = BaseBackground();
        return accumulated == null ? windowColor : CompositeOver(accumulated.Value, windowColor);
    }

    /// <summary>Mezcla un color translúcido (top) sobre otro opaco (bottom).</summary>
    private static System.Windows.Media.Color CompositeOver(System.Windows.Media.Color top, System.Windows.Media.Color bottom)
    {
        double alpha = top.A / 255.0;
        return System.Windows.Media.Color.FromArgb(255,
            (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
            (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
            (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));
    }

    private static double ContrastRatio(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    private static double RelativeLuminance(System.Windows.Media.Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        try
        {
            var method = target.GetType().GetMethod(methodName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            method?.Invoke(target, args);
        }
        catch { }
    }

    /// <summary>Busca un control con nombre en la ventana (namescope XAML).</summary>
    private static T? FindByName<T>(Window window, string name) where T : class
    {
        try { return window.FindName(name) as T; }
        catch { return null; }
    }

    /// <summary>Busca el primer elemento de tipo T en el árbol visual.</summary>
    private static T? FindFirst<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindFirst<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    // ---------------- tests ----------------

    private static void SseTests()
    {
        CheckEqual("data [DONE] → Done", SseParser.ParseData("[DONE]").Kind, StreamChunkKind.Done);

        var delta = SseParser.ParseData("{\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}");
        Check("delta.content → Content", delta.Kind == StreamChunkKind.Content && delta.Text == "hola");

        var whole = SseParser.ParseData("{\"choices\":[{\"message\":{\"content\":\"completo\"}}]}");
        Check("message.content → Content", whole.Kind == StreamChunkKind.Content && whole.Text == "completo");

        var legacy = SseParser.ParseData("{\"choices\":[{\"text\":\"viejo\"}]}");
        Check("choices[0].text → Content", legacy.Kind == StreamChunkKind.Content && legacy.Text == "viejo");

        var streamError = SseParser.ParseData("{\"error\":{\"message\":\"sin saldo\"}}");
        Check("error en stream", streamError.Kind == StreamChunkKind.Error && streamError.Text!.Contains("sin saldo"));

        CheckEqual("keep-alive → Ignore", SseParser.ParseData(": ping").Kind, StreamChunkKind.Ignore);
        CheckEqual("JSON vacío → Ignore", SseParser.ParseData("{}").Kind, StreamChunkKind.Ignore);
        CheckEqual("vacío → Ignore", SseParser.ParseData("   ").Kind, StreamChunkKind.Ignore);
        CheckEqual("HTML → Error", SseParser.ParseData("<html>error</html>").Kind, StreamChunkKind.Error);
        CheckEqual("texto plano → Content", SseParser.ParseData("texto suelto").Kind, StreamChunkKind.Content);

        CheckEqual("ExtractFullContent", SseParser.ExtractFullContent("{\"choices\":[{\"message\":{\"content\":\"  listo  \"}}]}"), "listo");
        Check("ExtractFullContent sin contenido", SseParser.ExtractFullContent("{\"choices\":[]}") == null);
        Check("ExtractFullContent content", SseParser.ExtractFullContent("{\"content\":\"x\"}") == "x");

        Check("DescribeError JSON", SseParser.DescribeError("{\"error\":{\"message\":\"cuota agotada\"}}").Contains("cuota agotada"));
        Check("DescribeError HTML", !SseParser.DescribeError("<html><body>caído</body></html>").Contains("<"));
        CheckEqual("DescribeError vacío", SseParser.DescribeError(""), "sin detalle");

        Check("LooksLikeSse sí", SseParser.LooksLikeSse("data: {}"));
        Check("LooksLikeSse no", !SseParser.LooksLikeSse("{\"a\":1}"));

        // Robustez ante respuestas raras de proveedores (el pensamiento interno
        // del modelo nunca se muestra como respuesta)
        Check("content vacío no es éxito", SseParser.ExtractFullContent("{\"choices\":[{\"message\":{\"content\":\"\"}}]}") == null);
        Check("reasoning en stream se ignora", SseParser.ParseData("{\"choices\":[{\"delta\":{\"reasoning_content\":\"t\"}}]}").Kind == StreamChunkKind.Ignore);
        Check("content en array", SseParser.ParseData("{\"choices\":[{\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"ho\"},{\"type\":\"text\",\"text\":\"la\"}]}}]}") is { Kind: StreamChunkKind.Content, Text: "hola" });
        Check("reasoning completo se ignora", SseParser.ExtractFullContent("{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"clave\"}}]}") == null);
        CheckEqual("data null → Ignore", SseParser.ParseData("null").Kind, StreamChunkKind.Ignore);
        Check("SSE como plan B", SseParser.TryExtractSseContent("data: {\"choices\":[{\"delta\":{\"content\":\"ho\"}}]}\n\ndata: [DONE]\n\n") == "ho");
        Check("DescribeError FastAPI", SseParser.DescribeError("{\"detail\":[{\"msg\":\"campo requerido\"}]}").Contains("campo requerido"));

        // Respuestas con raíz array/string, pensamiento filtrado y errores anidados
        Check("array raíz se interpreta", SseParser.ParseData("[{\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}]")
            is { Kind: StreamChunkKind.Content, Text: "hola" });
        Check("JSON roto se ignora", SseParser.ParseData("{\"choices\":[{\"delta\":").Kind == StreamChunkKind.Ignore);
        Check("thinking no se muestra", SseParser.ParseData("{\"choices\":[{\"delta\":{\"content\":[{\"type\":\"thinking\",\"text\":\"interno\"},{\"type\":\"text\",\"text\":\"visible\"}]}}]}")
            is { Kind: StreamChunkKind.Content, Text: "visible" });
        Check("error anidado en stream", SseParser.ParseData("{\"error\":{\"error\":{\"message\":\"sin saldo\"}}}")
            is { Kind: StreamChunkKind.Error, Text: "sin saldo" });
        Check("error anidado legible", SseParser.DescribeError("{\"error\":{\"error\":{\"message\":\"sin saldo\"}}}") == "sin saldo");
        Check("full con raíz array", SseParser.ExtractFullContent("[{\"choices\":[{\"message\":{\"content\":\"hola\"}}]}]") == "hola");
        Check("full con output[]", SseParser.ExtractFullContent("{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"hola\"}]}]}") == "hola");
        Check("full ignora pensamiento", SseParser.ExtractFullContent("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"thinking\",\"text\":\"x\"},{\"type\":\"text\",\"text\":\"ok\"}]}}]}") == "ok");
    }

    private static void ProviderTests()
    {
        CheckEqual("normaliza /chat/completions",
            OpenAiCompatibleProvider.NormalizeBase("https://api.x.ai/v1/chat/completions"), "https://api.x.ai/v1");
        CheckEqual("normaliza barra final",
            OpenAiCompatibleProvider.NormalizeBase("http://localhost:11434/v1/"), "http://localhost:11434/v1");
        CheckEqual("normaliza /models",
            OpenAiCompatibleProvider.NormalizeBase("https://openrouter.ai/api/v1/models"), "https://openrouter.ai/api/v1");

        var models = OpenAiCompatibleProvider.ParseModels("{\"data\":[{\"id\":\"z\"},{\"id\":\"a\"},{\"id\":\"a\"}]}");
        CheckEqual("modelos ordenados y sin duplicados", string.Join(",", models), "a,z");

        var ollama = OpenAiCompatibleProvider.ParseModels("{\"models\":[{\"name\":\"llama3.1\"}]}");
        CheckEqual("modelos estilo Ollama", string.Join(",", ollama), "llama3.1");
        Check("modelos con raíz string no revientan", OpenAiCompatibleProvider.ParseModels("\"ok\"").Count == 0);
        Check("modelos array de strings", string.Join(",", OpenAiCompatibleProvider.ParseModels("[\"b\",\"a\"]")) == "a,b");
        Check("modelo objeto suelto", string.Join(",", OpenAiCompatibleProvider.ParseModels("{\"id\":\"solo\"}")) == "solo");

        var offlineBlocked = false;
        try
        {
            OpenAiCompatibleProvider.FetchModelsAsync("https://api.openai.com/v1", "sk-test")
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) { offlineBlocked = ex.Message.Contains("offline", StringComparison.OrdinalIgnoreCase); }
        Check("SELBAR_OFFLINE bloquea cualquier llamada de red", offlineBlocked);

        Check("local detectado", Providers.IsLocal("http://localhost:11434/v1"));
        Check("local IPv6", Providers.IsLocal("http://[::1]:11434/v1"));
        Check("local IPv6 larga", Providers.IsLocal("http://[0:0:0:0:0:0:0:1]:11434/v1"));
        Check("no confunde subdominios con local", !Providers.IsLocal("https://mylocalhost.example/v1"));
        Check("remoto no local", !Providers.IsLocal("https://api.openai.com/v1"));
        Check("presets con URL", Providers.All.Where(p => p != "Personalizado").All(p => Providers.BaseUrlFor(p).StartsWith("http")));
    }

    private static void HotkeyTests()
    {
        Check("Ctrl+Shift+Espacio",
            Hotkeys.TryParse("Ctrl+Shift+Espacio", out var mods, out var vk) &&
            mods == (Hotkeys.ModControl | Hotkeys.ModShift) && vk == 0x20,
            $"mods={mods} vk={vk}");

        Check("Ctrl+1 es el dígito 1",
            Hotkeys.TryParse("Ctrl+1", out _, out var digit) && digit == 0x31, $"vk={digit}");

        Check("Alt+F5",
            Hotkeys.TryParse("Alt+F5", out _, out var f5) && f5 == 0x74, $"vk={f5}");

        Check("Shift+A",
            Hotkeys.TryParse("Shift+A", out _, out var a) && a == 0x41, $"vk={a}");

        Check("sin modificador no vale", !Hotkeys.TryParse("Espacio", out _, out _));
        Check("vacío no vale", !Hotkeys.TryParse("", out _, out _));
        Check("solo modificadores no vale", !Hotkeys.TryParse("Ctrl+Shift", out _, out _));
        Check("basura no vale", !Hotkeys.TryParse("Ctrl+NoExisteEstaTecla", out _, out _));

        CheckEqual("Format", Hotkeys.Format(Hotkeys.ModControl | Hotkeys.ModShift, 0x20), "Ctrl+Shift+Espacio");

        var roundTrip = Hotkeys.Format(Hotkeys.ModControl | Hotkeys.ModAlt, 0x41);
        Check("ida y vuelta",
            Hotkeys.TryParse(roundTrip, out var m2, out var v2) &&
            m2 == (Hotkeys.ModControl | Hotkeys.ModAlt) && v2 == 0x41,
            roundTrip);

        Check("hotkey por defecto válida", Hotkeys.IsValid(Hotkeys.DefaultHotkey));
        Check("Format sin tecla", Hotkeys.Format(Hotkeys.ModControl, 0) == "");
        Check("dos teclas ambiguo no vale", !Hotkeys.TryParse("Ctrl+A+B", out _, out _));
        Check("Ctrl+Ñ no vale (sin VK propio)", !Hotkeys.TryParse("Ctrl+Ñ", out _, out _));
        Check("Win+X vale", Hotkeys.TryParse("Win+X", out var wxm, out var wxv) && wxm == Hotkeys.ModWin && wxv == 0x58);
    }

    private static void IconTests()
    {
        var glyphs = ActionIcons.AllGlyphs;
        Check("hay iconos definidos", glyphs.Count > 0);
        Check("iconos de un solo carácter", glyphs.All(g => g.Length == 1));

        var missing = new List<string>();
        foreach (var glyph in glyphs.Select(g => g[0]).Distinct())
        {
            if (HasGlyph("Segoe Fluent Icons", glyph) || HasGlyph("Segoe MDL2 Assets", glyph)) continue;
            missing.Add($"U+{(int)glyph:X4}");
        }
        Check("todos los glifos existen en la fuente", missing.Count == 0, string.Join(", ", missing));

        // Las acciones con glifo deben devolver exactamente ese carácter.
        Check("For() devuelve el glifo",
            ActionIcons.For("copy") == "\uE8C8" && ActionIcons.For("accion.sin.icono") == "");
        Check("la familia incluye reserva para Windows 10", ActionIcons.Font.Contains("MDL2"));
        using var trayIcon = Selbar.Ui.TrayIcon.Create();
        Check("TrayIcon Selbar 64x64 válido", trayIcon.Width == 64 && trayIcon.Height == 64);
    }

    private static bool HasGlyph(string family, char character)
    {
        try
        {
            var typeface = new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily(family),
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);

            if (!typeface.TryGetGlyphTypeface(out var glyphTypeface)) return false;
            return glyphTypeface.CharacterToGlyphMap.ContainsKey(character);
        }
        catch { return false; }
    }

    private static void NativeTests()
    {
        if (IntPtr.Size == 8)
            CheckEqual("estructura INPUT de SendInput (x64)", NativeMethods.KeyboardInputSize, 40);

        Check("PointInWindow con HWND nulo", !NativeMethods.PointInWindow(IntPtr.Zero, 0, 0));
        Check("ForegroundTracker ignora HWND nulo", !ForegroundTracker.IsOwnWindow(IntPtr.Zero));
        CheckEqual("ClassNameOf nulo", NativeMethods.ClassNameOf(IntPtr.Zero), "");
        CheckEqual("WindowTitle nulo", NativeMethods.WindowTitle(IntPtr.Zero), "");
        Check("SendChord vacío falla", !NativeMethods.SendChord());
        Check("MakeNonActivating rechaza HWND nulo", !NativeMethods.MakeNonActivating(IntPtr.Zero));
        Check("IsSameOwner rechaza HWND nulo", !ForegroundTracker.IsSameOwner(1234, IntPtr.Zero));
        Check("IsSameOwner rechaza pid 0", !ForegroundTracker.IsSameOwner(0, new IntPtr(1234)));
        Check("Restore sin captura falla", !ForegroundTracker.Restore(0));
        Check("SelectionAnchor vacío", SelectionAnchor.Empty.IsEmpty);
        Check("SelectionAnchor con área", !new SelectionAnchor(10, 20, 100, 40).IsEmpty);
        Check("Clipboard rechaza null", !ClipboardService.TrySetText(null));
        Check("Clipboard rechaza vacío", !ClipboardService.TrySetText(""));
        Check("MethodFor desconocido vacío", TextExtractor.MethodFor("") == "");
        Check("WAV viejo es huérfano", MicRecorder.IsStaleDictationFile("powertools-dictado-abc123.wav", DateTime.UtcNow - TimeSpan.FromDays(2), DateTime.UtcNow));
        Check("WAV reciente no es huérfano", !MicRecorder.IsStaleDictationFile("powertools-dictado-abc123.wav", DateTime.UtcNow - TimeSpan.FromHours(1), DateTime.UtcNow));
        Check("otro WAV no es nuestro huérfano", !MicRecorder.IsStaleDictationFile("notas.wav", DateTime.UtcNow - TimeSpan.FromDays(2), DateTime.UtcNow));
        Check("Elapsed sin grabación es cero", DictationService.Elapsed == TimeSpan.Zero);
        Check("límite de dictado 3 min", DictationService.MaxDuration == TimeSpan.FromMinutes(3));
    }

    private static void RegistryTests()    {
        var registry = new ActionRegistry(null);
        var all = registry.All;

        Check("catálogo amplio", all.Count >= 30, $"count={all.Count}");
        Check("copiar visible por defecto", registry.GetVisibleActions("hola").Any(a => a.Id == "copy"));
        Check("resumir oculto en texto corto", !registry.GetVisibleActions("hola").Any(a => a.Id == "ai.summarize"));
        Check("resumir visible en texto largo", registry.GetVisibleActions(new string('x', 120)).Any(a => a.Id == "ai.summarize"));
        Check("acción desactivada no aparece", !registry.GetVisibleActions("hola").Any(a => a.Id == "ai.todos"));

        registry.SetEnabled("ai.define", true);
        Check("definir con una palabra", registry.GetVisibleActions("palabra").Any(a => a.Id == "ai.define"));
        Check("definir oculta con dos palabras", !registry.GetVisibleActions("dos palabras").Any(a => a.Id == "ai.define"));

        Check("abrir URL con URL", registry.GetVisibleActions("https://ejemplo.com/a?b=1").Any(a => a.Id == "sys.open-url"));
        Check("abrir URL con www", registry.GetVisibleActions("www.ejemplo.com").Any(a => a.Id == "sys.open-url"));
        Check("abrir URL oculta con texto normal", !registry.GetVisibleActions("esto no es una url").Any(a => a.Id == "sys.open-url"));
        Check("abrir URL oculta si es multilínea",
            !registry.GetVisibleActions("https://ejemplo.com\nsegunda línea").Any(a => a.Id == "sys.open-url"));
        Check("WhatsApp aparece con teléfono", registry.GetVisibleActions("+52 55 1234 5678").Any(a => a.Id == "search.whatsapp"));
        Check("WhatsApp oculto con texto", !registry.GetVisibleActions("hola mundo").Any(a => a.Id == "search.whatsapp"));
        Check("YouTube aparece con enlace", registry.GetVisibleActions("https://youtu.be/dQw4w9WgXcQ").Any(a => a.Id == "sys.youtube-video"));
        Check("YouTube oculto con texto", !registry.GetVisibleActions("un texto cualquiera").Any(a => a.Id == "sys.youtube-video"));
        Check("color aparece con HEX", registry.GetVisibleActions("#0078D4").Any(a => a.Id == "text.color"));
        Check("color oculto con palabra", !registry.GetVisibleActions("abc").Any(a => a.Id == "text.color"));
        Check("estilos aparece con texto", registry.GetVisibleActions("hola").Any(a => a.Id == "text.styles"));
        Check("QR aparece con enlace", registry.GetVisibleActions("https://ejemplo.com").Any(a => a.Id == "text.qr"));
        Check("Lens aparece con URL de imagen", registry.GetVisibleActions("https://ejemplo.com/foto.png").Any(a => a.Id == "search.lens-url"));
        Check("descarga aparece con TikTok", registry.GetVisibleActions("https://www.tiktok.com/@x/video/123").Any(a => a.Id == "sys.youtube-video"));

        var custom = new ActionDefinition
        {
            Id = "custom.test",
            Title = "Prueba",
            Kind = ActionKind.Ai,
            Output = OutputMode.Popup,
            PromptTemplate = "Haz algo con {text}"
        };
        registry.AddCustom(custom);
        Check("custom añadida", registry.All.Any(a => a.Id == "custom.test"));
        Check("custom visible", registry.GetVisibleActions("hola").Any(a => a.Id == "custom.test"));
        Check("custom es de categoría Custom", registry.Find("custom.test")!.Category == ActionCategory.Custom);
        Check("custom se elimina", registry.RemoveCustom("custom.test"));
        Check("las de serie no se borran", !registry.RemoveCustom("copy"));

        var badRegex = new ActionDefinition
        {
            Id = "custom.badregex",
            Title = "Regex rota",
            Kind = ActionKind.Ai,
            PromptTemplate = "{text}",
            MatchRegex = "("
        };
        registry.AddCustom(badRegex);
        Check("regex inválida no esconde la acción", registry.GetVisibleActions("hola").Any(a => a.Id == "custom.badregex"));

        var overrides = new List<ActionDefinition>
        {
            new() { Id = "copy", IsEnabled = false, Order = 999 },
            new() { Id = "ai.code-bugs", Title = "retirada", IsEnabled = true }
        };
        var withOverrides = new ActionRegistry(overrides);
        Check("override desactiva", !withOverrides.GetVisibleActions("hola").Any(a => a.Id == "copy"));
        Check("override cambia el orden", withOverrides.All.Last().Id == "copy");
        Check("override parcial no pierde el título",
            !string.IsNullOrWhiteSpace(new ActionRegistry(new[] { new ActionDefinition { Id = "text.upper" } })
                .All.First(a => a.Id == "text.upper").Title));
        Check("override parcial no lo manda al principio",
            new ActionRegistry(new[] { new ActionDefinition { Id = "text.upper" } })
                .All.First(a => a.Id == "text.upper").Order > 0);
        Check("acción retirada no resucita", !withOverrides.All.Any(a => a.Id == "ai.code-bugs"));

        Check("texto vacío no muestra nada", registry.GetVisibleActions("   ").Count == 0);
    }

    private static void LogTests()
    {
        string path = Logger.Path;
        // Se arman en tiempo de ejecución para que el propio código no parezca una credencial.
        string fakeKey = "sk-" + "prueba" + "1234567890";
        string fakeBearer = "tok_" + "987654321";
        Logger.Log("clave api_key=" + fakeKey + " activa");
        Logger.Warn("Authorization: Bearer " + fakeBearer);
        string body = File.Exists(path) ? string.Join("\n", File.ReadAllLines(path).TakeLast(2)) : "";
        Check("el log no filtra la api_key", !body.Contains(fakeKey));
        Check("el log no filtra el bearer", !body.Contains(fakeBearer));
        Check("el log escribe algo", body.Contains("INFO") || body.Contains("WARN"));

        string tmp = Path.Combine(Path.GetTempPath(), "pt-log-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(tmp, string.Concat(Enumerable.Repeat("2026-01-01 00:00:00.000 [INFO] línea de relleno\n", 200)));
            long antes = new FileInfo(tmp).Length;
            Logger.RotateFile(tmp, 4096);
            long despues = new FileInfo(tmp).Length;
            Check("rotación respeta el tope en bytes", despues <= 4096 || despues < antes, $"{antes}->{despues}");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>¿La cadena no tiene mitades de emoji sueltas?</summary>
    private static bool NoLoneSurrogates(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return false;
        }
        return true;
    }

    private static void DecisionTests()
    {
        var registry = new ActionRegistry(null);
        var all = registry.All;
        List<ActionDefinition> Off(params string[] ids) => ids.Select(i => all.First(a => a.Id == i)).ToList();

        static DecisionOutcome Out(double none, params (string Id, double P)[] probs) => new()
        {
            Probabilities = probs.ToDictionary(p => p.Id, p => p.P, StringComparer.Ordinal),
            NoAction = none
        };

        // La taxonomía debe apuntar a acciones que existen de verdad.
        Check("toda intención apunta a una acción real",
            ActionIntents.ToAction.Values.All(id => all.Any(a => a.Id == id)),
            string.Join(",", ActionIntents.ToAction.Where(kv => all.All(a => a.Id != kv.Value)).Select(kv => kv.Key)));
        Check("toda intención tiene descripción",
            ActionIntents.ToAction.Keys.All(k => ActionIntents.Criteria.Any(c => c.Key == k)));
        Check("la taxonomía incluye 'ninguna'", ActionIntents.Criteria.Any(c => c.Key == ActionIntents.None));

        // Promoción
        var promoted = ActionRanker.Rank(Off("date.info", "ai.summarize"), all, Out(0.05, ("responder", 0.85)));
        Check("promueve la intención elegida", promoted.Actions[0].Id == "ai.ask" && promoted.PromotedId == "ai.ask",
            promoted.Actions[0].Id);
        Check("promueve aunque no estuviera en la lista", promoted.Actions.Count == 3);
        Check("conserva el orden del resto", promoted.Actions[1].Id == "date.info" && promoted.Actions[2].Id == "ai.summarize");

        var already = ActionRanker.Rank(Off("ai.summarize", "date.info"), all, Out(0.1, ("resumir", 0.9)));
        Check("si ya era la primera, no reordena", already.Actions[0].Id == "ai.summarize" && already.Actions.Count == 2);

        var weak = ActionRanker.Rank(Off("date.info", "ai.summarize"), all, Out(0.2, ("responder", 0.4)));
        Check("confianza baja no mueve nada", weak.Actions[0].Id == "date.info" && weak.PromotedId == null);

        // Ocultar ruido
        var hide = ActionRanker.Rank(Off("date.info", "ai.summarize"), all, Out(0.9, ("responder", 0.2)));
        Check("'ninguna' oculta genéricas", hide.HidNoise && hide.Actions.Count == 1 && hide.Actions[0].Id == "date.info");

        var hideAll = ActionRanker.Rank(Off("ai.summarize", "ai.improve"), all, Out(0.95));
        Check("'ninguna' puede dejarlo vacío", hideAll.HidNoise && hideAll.Actions.Count == 0);

        var hard = ActionRanker.Rank(Off("text.calc"), all, Out(0.9, ("responder", 0.1)));
        Check("nunca oculta un acierto exacto", !hard.HidNoise && hard.Actions.Count == 1);

        Check("intención desconocida no rompe",
            ActionRanker.Rank(Off("ai.summarize"), all, Out(0.1, ("inventada", 0.99))).PromotedId == null);
        Check("sin decisión no cambia nada", ActionRanker.Rank(Off("ai.summarize"), all, null).PromotedId == null);

        // Credenciales: nunca se envían.
        Check("token parece credencial", ActionRanker.LooksSecret("aB3xK9mQ2pL7vR4tY8wZ1nC6"));
        Check("un enlace no es credencial",
            !ActionRanker.LooksSecret("https://www.tiktok.com/@u/video/7300000000?utm_source=copy&x=1"));
        Check("una ruta no es credencial", !ActionRanker.LooksSecret(@"C:\Users\cesar\Documents\contrato.pdf"));
        Check("un correo no es credencial", !ActionRanker.LooksSecret("juan.perez@gmail.com"));
        Check("una frase no es credencial", !ActionRanker.LooksSecret("Hola, necesito el pedido de balanzas"));

        // Recorte para el modelo
        var largo = string.Concat(Enumerable.Repeat("á🙂", 5000));
        var clip = ActionRanker.Clip(largo, 1000);
        Check("recorte respeta el tope", clip.Length <= 1000, clip.Length.ToString());
        Check("recorte no deja surrogates sueltos", NoLoneSurrogates(clip));

        // Petición: se pregunta por intención, no por el catálogo.
        string body = DecisionRouter.BuildRequestJson("hola", "gemini-decision-1");
        Check("petición con intenciones", body.Contains("\"intencion\"") && body.Contains("\"responder\""));
        Check("petición sin ids de acciones", !body.Contains("ai.summarize") && !body.Contains("date.info"));
        Check("petición es JSON válido", !string.IsNullOrEmpty(JsonDocument.Parse(body).RootElement.ToString()));

        // Respuesta del modelo
        var parsed = DecisionRouter.ParseResponse(
            "{\"answers\":{\"intencion\":{\"type\":\"choice\",\"probabilities\":{\"responder\":0.9,\"ninguna\":0.1}}," +
            "\"ninguna\":{\"type\":\"noul\",\"noul\":0.2}}}");
        Check("parsea probabilidades", parsed is not null && parsed.Probabilities["responder"] == 0.9);
        Check("parsea 'ninguna'", parsed is not null && Math.Abs(parsed.NoAction - 0.2) < 0.0001);
        Check("parsea sin depender del nombre de la pregunta",
            DecisionRouter.ParseResponse("{\"answers\":{\"x\":{\"type\":\"choice\",\"choice\":\"mapa\",\"confidence\":0.88}}}")
                ?.Probabilities.GetValueOrDefault("mapa") == 0.88);
        Check("basura devuelve null",
            DecisionRouter.ParseResponse("no es json") is null && DecisionRouter.ParseResponse("[]") is null);
    }

    private static void PresetTests()
    {
        var presets = ActionPresets.Defaults();

        CheckEqual("ids únicos", presets.Select(a => a.Id).Distinct().Count(), presets.Count);
        CheckEqual("órdenes únicos", presets.Select(a => a.Order).Distinct().Count(), presets.Count);
        Check("títulos rellenos", presets.All(a => !string.IsNullOrWhiteSpace(a.Title)));
        Check("acciones IA con prompt", presets.Where(a => a.Kind == ActionKind.Ai).All(a => !string.IsNullOrWhiteSpace(a.PromptTemplate)));
        Check("acciones IA con plantilla {text}", presets.Where(a => a.Kind == ActionKind.Ai).All(a => a.PromptTemplate!.Contains("{text}")));
        Check("acciones de navegador con URL", presets.Where(a => a.Output == OutputMode.Browser || a.Output == OutputMode.OpenUrl).All(a => a.Output == OutputMode.OpenUrl || a.Id is "contact.email" or "link.summarize" || !string.IsNullOrWhiteSpace(a.UrlTemplate)));
                int generic = presets.Count(a => a.IsEnabled && string.IsNullOrEmpty(a.MatchRegex)
            && !a.LongTextOnly && !a.SingleWordOnly && !a.MultiWordOnly);
        Check("pocas acciones genéricas activas", generic <= 12, $"genéricas={generic}");
        Check("contar está desactivado por defecto", presets.Any(a => a.Id == "text.count" && !a.IsEnabled));
        Check("quitar saltos está desactivado por defecto", presets.Any(a => a.Id == "text.nolines" && !a.IsEnabled));

        // La regex de cada preset debe coincidir con lo que la acción sabe hacer
        Regex Rx(string id) => new Regex(presets.First(a => a.Id == id).MatchRegex!, RegexOptions.CultureInvariant);
        var calcRx = Rx("text.calc");
        Check("chip calc no en fecha corta", !calcRx.IsMatch("12/03/2025"));
        Check("chip calc sí en resta", calcRx.IsMatch("100 - 45"));
        var dateRx = Rx("date.info");
        Check("chip fecha no en decimal", !dateRx.IsMatch("3.14") && !dateRx.IsMatch("1.5"));
        Check("chip fecha no en imposible", !dateRx.IsMatch("32/13/2025"));
        Check("chip maps no en frase con número", !Rx("place.maps").IsMatch("Tengo 3 gatos"));
        Check("chip maps sí en dirección", Rx("place.maps").IsMatch("Av. Larco 123"));
        Check("chip whatsapp no en fecha", !Rx("search.whatsapp").IsMatch("12/03/2025"));
        Check("chip estilos sí multilínea", Rx("text.styles").IsMatch("hola\nmundo"));

        // Sumas compactas que la guarda de teléfonos ocultaba
        foreach (var expr in new[] { "1234+5678", "100000+200", "1000+2000", "1234567+2", "100-10%-10%" })
            Check("chip calc en " + expr, calcRx.IsMatch(expr));
        Check("chip calc no en teléfono compacto", !calcRx.IsMatch("987-654-321") && !calcRx.IsMatch("+51 987 654 321"));

        // Formatos de fecha que DateTools entiende y el chip debe ofrecer
        foreach (var f in new[] { "12 de marzo", "12 de marzo del 2025", "12 mar. 2025", "12-mar-2025", "12/03/2025 a las 3pm", "29 de febrero" })
            Check("chip fecha en " + f, dateRx.IsMatch(f));
    }

    private static void ExecutorTests()
    {
        var settings = new AppSettings
        {
            Ai = new AiConfig { TargetLang = "francés", MaxInputChars = 1000 }
        };
        var executor = new ActionExecutor(settings);

        CheckEqual("sustituye {lang} y {text}",
            executor.BuildPrompt(new ActionDefinition { PromptTemplate = "Traduce a {lang}: {text}" }, "hola"),
            "Traduce a francés: hola");

        CheckEqual("el texto no se re-sustituye",
            executor.BuildPrompt(new ActionDefinition { PromptTemplate = "{text}" }, "usa {lang} aquí"),
            "usa {lang} aquí");

        Check("sustituye {date}", Regex.IsMatch(
            executor.BuildPrompt(new ActionDefinition { PromptTemplate = "{date}" }, "x"), @"^\d{4}-\d{2}-\d{2}$"));

        var truncated = executor.BuildPrompt(new ActionDefinition { PromptTemplate = "{text}" }, new string('a', 1500));
        Check("recorta la entrada larga", truncated.Length < 1300 && truncated.Contains("recortado"));

        CheckEqual("system prompt por defecto", executor.SystemPrompt, ActionExecutor.DefaultSystemPrompt);
        settings.Ai.SystemPrompt = "Sé breve";
        CheckEqual("system prompt propio", executor.SystemPrompt, "Sé breve");

        settings.Ai.MaxInputChars = 50;
        var clamped = executor.BuildPrompt(new ActionDefinition { PromptTemplate = "{text}" }, new string('b', 5000));
        Check("el recorte nunca baja de 1000", clamped.Length >= 1000);
    }

    private static void SettingsTests()
    {
        // Valores de serie discretos: nada aparece al seleccionar; el atajo manda.
        var defaults = new AppSettings();
        Check("sin aviso al seleccionar con ratón", !defaults.Triggers.AutoOnMouseSelection);
        Check("sin aviso al seleccionar con teclado", !defaults.Triggers.AutoOnKeyboardSelection);
        Check("atajo activo por defecto", defaults.Triggers.HotkeyEnabled);
        Check("migración marcada por defecto", !defaults.InvocationMigrated);

        // Se arma en runtime para no parecer una credencial en el código fuente.
        string fakeKey = "sk-" + "prueba" + "123";
        var cipher = SettingsManager.Protect(fakeKey);
        Check("DPAPI ida y vuelta", SettingsManager.Unprotect(cipher) == fakeKey);
        CheckEqual("DPAPI vacío", SettingsManager.Protect(""), "");
        CheckEqual("DPAPI basura", SettingsManager.Unprotect("no-es-base64"), "");

        var path = Path.Combine(AppPaths.DataDir, "settings-test.json");
        var manager = new SettingsManager(path);
        manager.Settings.Ai.Model = "modelo-x";
        manager.Settings.Ai.Temperature = 99;      // fuera de rango: se debe recortar
        manager.Settings.Triggers.MaxBarIcons = 99;
        manager.Settings.ExcludedApps = new List<string> { "app1.exe", "app1", "  " };
        manager.Save();

        var reloaded = new SettingsManager(path);
        reloaded.Load();

        CheckEqual("modelo persistido", reloaded.Settings.Ai.Model, "modelo-x");
        Check("temperatura recortada", reloaded.Settings.Ai.Temperature <= 2);
        Check("iconos recortados", reloaded.Settings.Triggers.MaxBarIcons <= 15);
        Check("excluidos limpios y sin duplicados",
            reloaded.Settings.ExcludedApps.Count(x => x.StartsWith("app1", StringComparison.OrdinalIgnoreCase)) == 1);
        Check("Selbar siempre excluido", reloaded.Settings.ExcludedApps.Contains("Selbar"));
        Check("session id generado", reloaded.Settings.Ai.GoSessionId.Length > 0);
        Check("versión de ajustes sellada", reloaded.Settings.SettingsVersion == AppSettings.CurrentVersion);

        // JSON editado a mano con nulls: no debe tumbar la barra (Migrate sanea).
        var dirtyPath = Path.Combine(AppPaths.DataDir, "settings-nulls.json");
        File.WriteAllText(dirtyPath, "{\"SettingsVersion\":1,\"Profiles\":null}");
        var dirty = new SettingsManager(dirtyPath);
        dirty.Load();
        Check("perfiles null saneados", dirty.Settings.Profiles != null && dirty.Settings.Profiles.Count == 0);

        File.WriteAllText(dirtyPath, "{\"SettingsVersion\":1,\"Profiles\":[null,{\"Process\":\"chrome\",\"HiddenActions\":null}]}");
        var dirty2 = new SettingsManager(dirtyPath);
        dirty2.Load();
        Check("perfil null eliminado", dirty2.Settings.Profiles.Count == 1);
        Check("ocultas null saneadas", dirty2.Settings.Profiles[0].HiddenActions != null);
        var reg = new ActionRegistry();
        reg.Profiles = dirty2.Settings.Profiles;
        Check("registro no lanza con perfiles sucios", reg.HiddenFor("chrome") != null);
        Check("existe copia .bak o el fichero", File.Exists(path));

        // JSON corrupto: no debe lanzar y debe dejar copia.
        File.WriteAllText(path, "{ esto no es json válido");
        var corrupted = new SettingsManager(path);
        corrupted.Load();
        Check("JSON corrupto → valores de serie", corrupted.Settings.Triggers.MaxBarIcons == 8);
        Check("copia del corrupto", Directory.GetFiles(AppPaths.DataDir, "settings-test.json.corrupto-*").Length > 0);

        // Ritmo de voz fuera de rango y campos de versiones futuras
        var ratePath = Path.Combine(AppPaths.DataDir, "settings-rate.json");
        File.WriteAllText(ratePath, "{\"SpeakRate\":999}");
        var rateMgr = new SettingsManager(ratePath); rateMgr.Load();
        Check("ritmo de voz recortado", rateMgr.Settings.SpeakRate >= 0.5 && rateMgr.Settings.SpeakRate <= 3.0);
        var fwdPath = Path.Combine(AppPaths.DataDir, "settings-future.json");
        File.WriteAllText(fwdPath, "{\"SettingsVersion\":99,\"CampoDelFuturo\":\"no-perder\"}");
        var fwd = new SettingsManager(fwdPath); fwd.Load(); fwd.Save();
        Check("campos futuros sobreviven", File.ReadAllText(fwdPath).Contains("CampoDelFuturo"));

        // Aplicar un borrador conserva las referencias vivas (monitor/ejecutor)
        var live = new SettingsManager(Path.Combine(AppPaths.DataDir, "settings-live.json"));
        live.Load();
        var liveRoot = live.Settings;
        var liveTriggers = live.Settings.Triggers;
        var liveAi = live.Settings.Ai;
        var draft = SettingsDraft.Clone(live.Settings);
        draft.Ai.Model = "modelo-borrador";
        draft.Triggers.Hotkey = "Ctrl+Alt+K";
        Check("aplicar borrador", live.TryApply(draft, out string applyError), applyError);
        Check("identidad de ajustes intacta", ReferenceEquals(liveRoot, live.Settings));
        Check("identidad de disparadores intacta", ReferenceEquals(liveTriggers, live.Settings.Triggers));
        Check("identidad de IA intacta", ReferenceEquals(liveAi, live.Settings.Ai));
        Check("borrador aplicado", live.Settings.Ai.Model == "modelo-borrador"
            && live.Settings.Triggers.Hotkey == "Ctrl+Alt+K");
        Check("borrador en disco", File.ReadAllText(Path.Combine(AppPaths.DataDir, "settings-live.json")).Contains("modelo-borrador"));
        var idRoot = live.Settings;
        live.Reset();
        Check("reset conserva identidad", ReferenceEquals(idRoot, live.Settings));
        Check("reset vuelve al modelo por defecto", live.Settings.Ai.Model == "deepseek-v4.1-flash");
    }

    private static void SearchTests()
    {
        var registry = new ActionRegistry(null);
        var all = registry.All;

        CheckEqual("prefijo primero", ActionSearch.Filter(all, "tra")[0].Id, "ai.translate");
        Check("por categoría", ActionSearch.Filter(all, "portapapeles").Any(a => a.Id == "copy"));
        Check("varias palabras", ActionSearch.Filter(all, "mejorar red").Any(a => a.Id == "ai.improve"));
        Check("fuzzy", ActionSearch.Filter(all, "trducr").Any(a => a.Id == "ai.translate"));
        CheckEqual("sin resultados", ActionSearch.Filter(all, "zzzz").Count, 0);
        CheckEqual("vacío devuelve todo", ActionSearch.Filter(all, "  ").Count, all.Count);
        Check("subsecuencia", ActionSearch.IsSubsequence("abc", "a1b2c3") && !ActionSearch.IsSubsequence("cba", "abc"));
        Check("búsqueda sin tildes", ActionSearch.Filter(all, "mayusculas").Any(a => a.Id == "text.upper")
            && ActionSearch.Filter(all, "minusculas").Count > 0);
    }

    private static void LibraryTests()
    {
        var recipes = PromptLibrary.All;
        Check("hay recetas", recipes.Count >= 20, $"recetas={recipes.Count}");
        CheckEqual("ids únicos", recipes.Select(r => r.Id).Distinct().Count(), recipes.Count);
        CheckEqual("títulos únicos", recipes.Select(r => r.Title).Distinct().Count(), recipes.Count);
        Check("prompts con {text}", recipes.All(r => r.Prompt.Contains("{text}")));
        Check("varios grupos", PromptLibrary.Groups.Count >= 4, string.Join(",", PromptLibrary.Groups));

        var action = PromptLibrary.ToAction(recipes[0]);
        Check("conversión a acción",
            action.Kind == ActionKind.Ai && action.PromptTemplate == recipes[0].Prompt && action.Title == recipes[0].Title);
        Check("duplicado detectado", PromptLibrary.IsAlreadyAdded(recipes[0].Title, new[] { action }));
        Check("no duplicado", !PromptLibrary.IsAlreadyAdded("otra cosa distinta", new[] { action }));
    }

    private static void DiffTests()
    {
        var same = DiffRenderer.Compute("hola mundo", "hola mundo");
        Check("sin cambios", same.All(s => s.Kind == DiffRenderer.DiffKind.Same));
        CheckEqual("texto conservado", string.Concat(same.Select(s => s.Text)), "hola mundo");

        var added = DiffRenderer.Compute("hola mundo", "hola mundo feliz");
        Check("detecta añadido", added.Any(s => s.Kind == DiffRenderer.DiffKind.Added && s.Text.Contains("feliz")));

        var removed = DiffRenderer.Compute("hola mundo cruel", "hola mundo");
        Check("detecta eliminado", removed.Any(s => s.Kind == DiffRenderer.DiffKind.Removed && s.Text.Contains("cruel")));

        var doc = DiffRenderer.ToFlowDocument("a b c", "a x c");
        CheckEqual("documento con leyenda y texto", doc.Blocks.Count, 2);

        var bigBefore = string.Join(" ", Enumerable.Repeat("palabra", 2000));
        var bigAfter = string.Join(" ", Enumerable.Repeat("palabra", 1999));
        var big = DiffRenderer.Compute(bigBefore, bigAfter);
        CheckEqual("texto enorme → sin resaltado fino", big.Count, 3);
    }

    private static void ExportImportTests()
    {
        var exportPath = Path.Combine(AppPaths.DataDir, "export-test.json");
        var settingsPath = Path.Combine(AppPaths.DataDir, "settings-export.json");
        var manager = new SettingsManager(settingsPath);
        manager.Load();
        manager.Settings.Ai.Model = "modelo-export";
        manager.Settings.Ai.ApiKeyEncrypted = SettingsManager.Protect("sk-secreto");
        manager.Settings.Ai.GroqApiKeyEncrypted = SettingsManager.Protect("gsk-secreto");
        manager.Settings.Triggers.MaxBarIcons = 9;

        manager.Export(exportPath);
        var raw = File.ReadAllText(exportPath);
        Check("exporta sin la key", !raw.Contains("sk-secreto") && !raw.Contains(manager.Settings.Ai.ApiKeyEncrypted));
        Check("exporta sin la key de Groq", !raw.Contains("gsk-secreto") && !raw.Contains(manager.Settings.Ai.GroqApiKeyEncrypted));

        var imported = SettingsManager.ReadSettingsFile(exportPath);
        CheckEqual("modelo importado", imported.Ai.Model, "modelo-export");
        CheckEqual("iconos importados", imported.Triggers.MaxBarIcons, 9);
        CheckEqual("key vacía al importar", imported.Ai.ApiKeyEncrypted, "");
        CheckEqual("key de Groq vacía al importar", imported.Ai.GroqApiKeyEncrypted, "");
        Check("session id generado al importar", imported.Ai.GoSessionId.Length > 0);

        manager.Adopt(imported);
        CheckEqual("adopt conserva la key", SettingsManager.Unprotect(manager.Settings.Ai.ApiKeyEncrypted), "sk-secreto");
        CheckEqual("adopt conserva la key de Groq", SettingsManager.Unprotect(manager.Settings.Ai.GroqApiKeyEncrypted), "gsk-secreto");
        CheckEqual("adopt aplica el modelo", manager.Settings.Ai.Model, "modelo-export");

        manager.Export(exportPath, includeApiKey: true);
        var withKey = SettingsManager.ReadSettingsFile(exportPath);
        CheckEqual("exporta con la key si se pide", withKey.Ai.ApiKeyEncrypted, manager.Settings.Ai.ApiKeyEncrypted);
        manager.Export(Path.Combine(AppPaths.DataDir, "sub-exp", "exp.json"));
        Check("export crea carpeta", File.Exists(Path.Combine(AppPaths.DataDir, "sub-exp", "exp.json")));
        manager.Adopt(new AppSettings { Ai = null! });
        Check("adopt tolera Ai null", manager.Settings.Ai != null);
    }

    private static void HistoryTests()
    {
        HistoryStore.Configure(new HistoryConfig());
        HistoryStore.Clear();
        Check("historial vacío", HistoryStore.All().Count == 0);

        HistoryStore.Add("ai.summarize", "Resumir", "modelo-1", "entrada\ncon salto", "salida uno");
        HistoryStore.Add("ai.translate", "Traducir", "modelo-2", "otra", "salida dos");

        var all = HistoryStore.All();
        CheckEqual("dos entradas", all.Count, 2);
        CheckEqual("más reciente primero", all[0].Action, "Traducir");
        CheckEqual("preview sin saltos de línea", all[1].InputPreview, "entrada con salto");
        CheckEqual("guarda el texto original", all[0].Input, "otra");
        Check("se puede repetir", all[0].CanRepeat && all[0].ActionId == "ai.translate");

        CheckEqual("filtro por acción", HistoryStore.All("resumir").Count, 1);
        CheckEqual("filtro por salida", HistoryStore.All("salida dos").Count, 1);
        CheckEqual("filtro sin coincidencias", HistoryStore.All("zzz").Count, 0);

        HistoryStore.Remove(all[0].Id);
        CheckEqual("borrar entrada", HistoryStore.All().Count, 1);

        var longOutput = new string('x', HistoryStore.MaxOutputChars + 500);
        HistoryStore.Add("custom.largo", "Largo", "m", "i", longOutput);
        Check("salida recortada", HistoryStore.All()[0].Output!.Length <= HistoryStore.MaxOutputChars + 1);

        // Cifrado DPAPI
        HistoryStore.Configure(new HistoryConfig { Encrypt = true });
        HistoryStore.RewriteAll();
        var rawFile = File.ReadAllText(HistoryStore.FilePath);
        Check("cifrado sin texto en claro", !rawFile.Contains("salida uno"));
        Check("marca de cifrado", rawFile.Contains("\"Encrypted\":true"));
        var decrypted = HistoryStore.All();
        Check("se descifra al leer", decrypted.Any(e => e.Output == "salida uno"));

        HistoryStore.Configure(new HistoryConfig { Encrypt = false });
        HistoryStore.RewriteAll();
        Check("vuelve a texto plano", File.ReadAllText(HistoryStore.FilePath).Contains("salida uno"));

        // Desactivado
        HistoryStore.Configure(new HistoryConfig { Enabled = false });
        HistoryStore.Clear();
        HistoryStore.Add("copy", "Copiar", "m", "x", "y");
        CheckEqual("desactivado no guarda", HistoryStore.All().Count, 0);

        HistoryStore.Configure(new HistoryConfig());
        HistoryStore.Clear();
        CheckEqual("vaciar historial", HistoryStore.All().Count, 0);

        // JSON editado a mano / cifrado de otro equipo: no perder nada
        File.WriteAllText(HistoryStore.FilePath, "[null,{\"Id\":null,\"Output\":null}]");
        HistoryStore.Configure(new HistoryConfig());
        Check("historial con nulls no tumba", HistoryStore.All().Count == 1);
        Check("historial sanea nulos", HistoryStore.All()[0].Id.Length > 0 && HistoryStore.All()[0].Output == "");
        File.WriteAllText(HistoryStore.FilePath, "[{\"Id\":\"abc\",\"When\":\"2026-01-01T00:00:00\",\"ActionId\":\"a\",\"Action\":\"A\",\"Model\":\"m\",\"InputPreview\":\"AQID\",\"Input\":\"AQID\",\"Output\":\"AQID\",\"Encrypted\":true}]");
        var ajeno = HistoryStore.All();
        Check("DPAPI ajeno no vacía", ajeno.Count == 1 && ajeno[0].Encrypted && ajeno[0].Input == "AQID");
        HistoryStore.Remove("id-que-no-existe");
        Check("reescritura conserva cifrado ajeno", File.ReadAllText(HistoryStore.FilePath).Contains("AQID"));
        File.WriteAllText(HistoryStore.FilePath, "{ esto no es json");
        HistoryStore.All();
        Check("historial corrupto deja copia", Directory.GetFiles(Path.GetDirectoryName(HistoryStore.FilePath)!, "history.json.corrupto-*").Length > 0);

        // Una entrada rota no puede arrastrar a las buenas
        File.WriteAllText(HistoryStore.FilePath,
            "[{\"Id\":\"a\",\"When\":\"2026-01-01T00:00:00\",\"ActionId\":\"x\",\"Action\":\"A\",\"Model\":\"m\",\"InputPreview\":\"p\",\"Input\":\"i\",\"Output\":\"uno\"}," +
            "{\"Id\":\"b\",\"When\":\"ayer\",\"ActionId\":\"x\",\"Action\":\"B\",\"Model\":\"m\",\"InputPreview\":\"p\",\"Input\":\"i\",\"Output\":\"dos\"}," +
            "{\"Id\":\"c\",\"When\":\"2026-01-02T00:00:00\",\"ActionId\":\"x\",\"Action\":\"C\",\"Model\":\"m\",\"InputPreview\":\"p\",\"Input\":\"i\",\"Output\":\"tres\"}]");
        var tolerante = HistoryStore.All();
        Check("entrada rota no borra las buenas", tolerante.Count == 2, tolerante.Count.ToString());
        Check("entradas buenas conservadas", tolerante.Any(e => e.Output == "uno") && tolerante.Any(e => e.Output == "tres"));
        HistoryStore.Configure(new HistoryConfig()); HistoryStore.Clear();
    }
    private static void ResultTurnTests()
    {
        // Streaming y error desactivan copiar/reemplazar/guardar/reutilizar
        var streaming = ResultCapabilities.For(true, false, true, true, true, true, true, true);
        Check("streaming no copia", !streaming.CanCopy && !streaming.CanReplace && !streaming.CanSave && !streaming.CanReuse);
        Check("streaming permite detener", streaming.CanStop && !streaming.CanRetry);
        var error = ResultCapabilities.For(false, true, true, true, true, true, true, true);
        Check("error no copia ni reemplaza", !error.CanCopy && !error.CanReplace && !error.CanReuse);
        Check("error con texto sí permite reintentar", error.CanRetry);
        var idle = ResultCapabilities.For(false, false, true, false, false, false, false, true);
        Check("resultado normal copia y guarda", idle.CanCopy && idle.CanSave && idle.CanReuse && !idle.CanReplace);
        Check("sin callbacks no hay reintentar/ajustar/preguntar", !idle.CanRetry && !idle.CanRefine && !idle.CanChat);
        var empty = ResultCapabilities.For(false, false, true, true, true, true, true, false);
        Check("sin texto no se copia", !empty.CanCopy && !empty.CanReplace && !empty.CanSave);

        // Un turno nuevo usa el texto visible y la versión, no la última respuesta
        var messages = ResultTurn.BuildMessages("sistema", "resume esto", "texto visible", 2, 3, "hazlo más corto");
        Check("turno con contexto de versión", messages[0].Content.Contains("versión 2 de 3"));
        Check("turno usa el texto visible", messages.Any(m => m.Role == "assistant" && m.Content == "texto visible"));
        Check("turno termina con la instrucción", messages[^1].Role == "user" && messages[^1].Content == "hazlo más corto");

        // Reutilizar no recorta en silencio: avisa
        var big = new string('x', ResultTurn.ReuseLimit + 5);
        var limited = ResultTurn.LimitForReuse(big);
        Check("reutilizar avisa del recorte", limited.Truncated && limited.Text.Length == ResultTurn.ReuseLimit);
        var small = ResultTurn.LimitForReuse("corto");
        Check("reutilizar texto corto intacto", !small.Truncated && small.Text == "corto");
    }

    private static void BuildVersionTests()
    {
        // La raíz del repo es donde vive build.ps1 (subir desde el bin de tests).
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "build.ps1"))) root = root.Parent;
        Check("raíz del repo hallada", root != null);
        if (root == null) return;
        string csproj = File.ReadAllText(Path.Combine(root.FullName, "src", "Selbar", "Selbar.csproj"));
        string iss = File.ReadAllText(Path.Combine(root.FullName, "tools", "installer.iss"));
        string v = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        string iv = Regex.Match(iss, "#define MyVersion \"([^\"]+)\"").Groups[1].Value;
        CheckEqual("versión csproj = installer.iss", iv, v);
        Check("iss fija icono del setup", iss.Contains("SetupIconFile"));
        Check("iss exige Win10 17763+", iss.Contains("MinVersion=10.0.17763"));
    }

    private static void MarkdownTests()
    {
        var plain = MarkdownRenderer.ToPlainText(
            "# Título\n\n**negrita** y `código`\n\n- uno\n- dos\n\n[enlace](https://ejemplo.com)\n\n> cita");
        Check("sin marcas de cabecera", !plain.Contains("# "));
        Check("sin negritas", !plain.Contains("**"));
        Check("conserva listas", plain.Contains("- uno") && plain.Contains("- dos"));
        Check("enlace legible", plain.Contains("enlace (https://ejemplo.com)"));
        Check("código sin backticks", plain.Contains("código") && !plain.Contains("`"));
        Check("cita sin >", !plain.Contains("> cita"));

        var document = MarkdownRenderer.ToFlowDocument(
            "# Hola\n\nTexto **fuerte**, *cursiva*, ~~tachado~~ y una [red](https://ejemplo.com).\n\n" +
            "- a\n  - b\n- c\n\n1. uno\n2. dos\n\n| A | B |\n|---|--:|\n| 1 | 2 |\n\n" +
            "```csharp\nvar x = 1;\n```\n\n> una cita\n\n---\n\n- [ ] pendiente\n- [x] hecha\n");
        Check("documento con varios bloques", document.Blocks.Count >= 8, $"bloques={document.Blocks.Count}");

        var unterminated = MarkdownRenderer.ToFlowDocument("```\nsin cerrar");
        CheckEqual("bloque de código sin cerrar", unterminated.Blocks.Count, 1);

        var huge = MarkdownRenderer.ToFlowDocument(new string('#', 450_000));
        CheckEqual("texto enorme → un solo bloque", huge.Blocks.Count, 1);

        var empty = MarkdownRenderer.ToFlowDocument("");
        CheckEqual("markdown vacío", empty.Blocks.Count, 0);

        CheckEqual("texto plano vacío", MarkdownRenderer.ToPlainText(null), "");
    }
}
