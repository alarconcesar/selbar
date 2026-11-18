using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;
using Application = System.Windows.Application;

namespace Selbar.Ui;

/// <summary>
/// Tema de la app siguiendo Windows 11: paleta Fluent clara/oscura y color de
/// acento del sistema. Los pinceles se definen en Tokens.xaml y se MUTAN aquí,
/// así que todo lo que los consuma con DynamicResource se repinta al instante al
/// cambiar de tema o de acento. Las ventanas deben usar DynamicResource para los
/// tokens (los lookups hechos con StaticResource se quedan con la instancia
/// anterior, que es justo lo que queremos para estilos y plantillas).
/// </summary>
public static class Theming
{
    /// <summary>Modo pedido por el usuario: "system", "dark" o "light".</summary>
    public static string Mode { get; private set; } = "system";

    /// <summary>Tema realmente aplicado.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Acento original del sistema (sin adaptar).</summary>
    public static MediaColor Accent { get; private set; } = MediaColor.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Relleno de botones/indicadores (contraste con texto blanco).</summary>
    public static MediaColor AccentFill { get; private set; } = MediaColor.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Tono de acento para textos e iconos.</summary>
    public static MediaColor AccentText { get; private set; } = MediaColor.FromRgb(0x60, 0xCD, 0xFF);

    /// <summary>Se dispara tras aplicar una paleta (las ventanas refrescan su marco DWM).</summary>
    public static event Action? Changed;

    private static Application? _app;
    private static DispatcherTimer? _debounce;
    private static bool _listening;
    private static string _appliedSignature = "";

    /// <summary>
    /// Aplica modo de tema y acento. <paramref name="mode"/> acepta "system",
    /// "dark" o "light"; cualquier otra cosa equivale a "system".
    /// </summary>
    public static void Apply(Application? app = null, string? mode = null)
    {
        _app = app ?? Application.Current;
        Mode = Normalize(mode ?? Mode);
        IsDark = Mode switch
        {
            "dark" => true,
            "light" => false,
            _ => !SystemUsesLightTheme()
        };

        ApplyPalette(_app, IsDark);
        ApplyAccent(_app, IsDark);
        ListenForSystemChanges();

        string signature = Mode + "|" + IsDark + "|" + Accent.ToString();
        bool changed = signature != _appliedSignature;
        _appliedSignature = signature;
        if (changed) RaiseChanged();
    }

    /// <summary>Cambia el modo en caliente (Ajustes → Apariencia).</summary>
    public static void SetMode(string mode)
    {
        Apply(_app, mode);
    }

    /// <summary>Relee el tema y el acento del sistema (si el modo es "system").</summary>
    public static void RefreshFromSystem() => Apply(_app, Mode);

    /// <summary>Nombre legible del modo aplicado.</summary>
    public static string EffectiveName => IsDark ? "Oscuro" : "Claro";

    /// <summary>Deja de escuchar cambios del sistema (al salir).</summary>
    public static void Shutdown()
    {
        if (!_listening) return;
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
        _listening = false;
        _debounce?.Stop();
        _debounce = null;
    }

    /// <summary>Color del acento configurado en Windows (o el azul Fluent por defecto).</summary>
    public static MediaColor ReadSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", false);
            if (key?.GetValue("ColorizationColor") is int raw)
            {
                uint value = (uint)raw;
                // Sin alfa: el acento se usa como relleno opaco (con alfa se lavaba el color).
                var color = MediaColor.FromRgb(
                    (byte)(value >> 16), (byte)(value >> 8), (byte)value);

                // Algunos temas guardan negro o gris casi puro: no son "acento".
                if (color.R + color.G + color.B > 30) return color;
            }
        }
        catch { }

        return MediaColor.FromRgb(0x00, 0x78, 0xD4);
    }

    /// <summary>¿Windows está en modo claro? (Aplicaciones, no el sistema).</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            if (key?.GetValue("AppsUseLightTheme") is int value) return value != 0;
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------------- paleta

    private readonly record struct Entry(string Key, uint Dark, uint Light);

    /// <summary>
    /// Paleta Fluent 2 cálida y neutra. Los colores con alfa se componen sobre
    /// la superficie que haya debajo, igual que en WinUI.
    /// </summary>
    private static readonly Entry[] Palette =
    {
        // Superficies
        new("BgWindow",         0xFF202020, 0xFFF3F3F3),
        new("BgLayer",          0xFF272727, 0xFFEDEDED),
        new("BgCard",           0xFF2D2D2D, 0xFFFFFFFF),
        new("BgCardHover",      0xFF323232, 0xFFFBFBFB),
        new("BgInput",          0xFF2D2D2D, 0xFFFFFFFF),
        new("BgInputHover",     0xFF333333, 0xFFF7F7F7),
        new("BgControl",        0xFF2D2D2D, 0xFFFFFFFF),
        new("BgControlHover",   0xFF333333, 0xFFF5F5F5),
        new("BgControlPressed", 0xFF262626, 0xFFEDEDED),
        new("BgSubtleHover",    0xFF2E2E2E, 0xFFE9E9E9),
        new("BgSubtlePressed",  0xFF262626, 0xFFDEDEDE),
        new("BgSelected",       0xFF353535, 0xFFE5E5E5),
        new("BgOverlay",        0xF5202020, 0xF5F3F3F3),
        new("BgTooltip",        0xFF2C2C2C, 0xFFFFFFFF),
        new("BgFlyout",         0xFF2C2C2C, 0xFFFAFAFA),
        new("BgSegmentTrack",   0xFF1C1C1C, 0xFFE9E9E9),
        new("BgSegmentThumb",   0xFF3C3C3C, 0xFFFFFFFF),
        new("BgWell",           0xFF1C1C1C, 0xFFF7F7F7),
        new("BgBadge",          0xFF2A2A2A, 0xFFEDEDED),
        new("BgTitleBar",       0xFF202020, 0xFFF3F3F3),

        // Trazos
        new("Stroke",           0xFF3D3D3D, 0xFFD6D6D6),
        new("StrokeStrong",     0xFF4D4D4D, 0xFFB3B3B3),
        new("StrokeSubtle",     0xFF323232, 0xFFE5E5E5),

        // Texto
        new("TextPrimary",      0xFFFFFFFF, 0xFF1A1A1A),
        new("TextSecondary",    0xFFC7C7C7, 0xFF5A5A5A),
        new("TextTertiary",     0xFF9C9C9C, 0xFF767676),
        new("TextDisabled",     0xFF6E6E6E, 0xFFA0A0A0),
        new("TextOnAccent",     0xFFFFFFFF, 0xFFFFFFFF),

        // Estados
        new("Success",          0xFF6CCB5F, 0xFF0F7B0F),
        new("Warning",          0xFFF2C14E, 0xFF9D5D00),
        new("Danger",           0xFFFF99A4, 0xFFC42B1C),
        new("DangerFill",       0xFFC42B1C, 0xFFC42B1C),
        new("DangerSoft",       0xFF3A2020, 0xFFFDE7E9),
        new("DangerStroke",     0xFF6E3030, 0xFFE8A0A6),

        // Controles
        new("CheckFill",        0xFF2D2D2D, 0xFFFFFFFF),
        new("CheckStroke",      0xFF5A5A5A, 0xFF8A8A8A),
        new("ToggleOffFill",    0xFF2B2B2B, 0xFFE9E9E9),
        new("ToggleOffStroke",  0xFF5A5A5A, 0xFF9A9A9A),
        new("ToggleThumbOff",   0xFFC8C8C8, 0xFF5D5D5D),
        new("ToggleThumbOn",    0xFFFFFFFF, 0xFFFFFFFF),
        new("ScrollThumb",      0xFF4F4F4F, 0xFFC2C2C2),
        new("ScrollThumbHover", 0xFF6E6E6E, 0xFF9E9E9E),
        new("ScrollThumbPressed", 0xFF8A8A8A, 0xFF808080),

        // Marco de ventana (botones de título)
        new("CaptionHover",        0xFF383838, 0xFFE7E7E7),
        new("CaptionPressed",      0xFF2C2C2C, 0xFFDADADA),
        new("CaptionCloseHover",   0xFFC42B1C, 0xFFC42B1C),
        new("CaptionClosePressed", 0xFFB12A1F, 0xFFA62A19),
        new("CaptionGlyph",        0xFFEDEDED, 0xFF1A1A1A)
    };

    private static void ApplyPalette(Application? app, bool dark)
    {
        if (app == null) return;

        foreach (var entry in Palette)
        {
            var color = FromPacked(dark ? entry.Dark : entry.Light);
            SetBrush(app, entry.Key, color);
        }
    }

    private static void ApplyAccent(Application? app, bool dark)
    {
        var accent = ReadSystemAccent();
        Accent = accent;

        if (dark)
        {
            // El relleno debe aguantar texto blanco; el tono de texto, brillar sobre #202020.
            AccentFill = ColorMath.ClampLightness(accent, 0.28, 0.38);
            AccentText = ColorMath.ClampLightness(accent, 0.72, 0.84);
        }
        else
        {
            // En claro el acento se usa como relleno con texto blanco y como enlace sobre blanco.
            AccentFill = ColorMath.ClampLightness(accent, 0.22, 0.32);
            AccentText = ColorMath.ClampLightness(accent, 0.24, 0.34);
        }

        if (app == null) return;

        var hover = ColorMath.ClampLightness(
            ColorMath.Lighten(AccentFill, dark ? 0.08 : 0.06), 0, dark ? 0.40 : 0.34);

        SetBrush(app, "AccentFill", AccentFill);
        SetBrush(app, "AccentFillHover", hover);
        SetBrush(app, "AccentFillPressed", ColorMath.Darken(AccentFill, dark ? 0.10 : 0.08));
        SetBrush(app, "AccentText", AccentText);
        SetBrush(app, "AccentSoft", ColorMath.WithAlpha(dark ? AccentText : AccentFill, dark ? (byte)0x2E : (byte)0x1F));
        SetBrush(app, "AccentBorder", ColorMath.WithAlpha(dark ? AccentText : AccentFill, dark ? (byte)0x70 : (byte)0x59));
        SetBrush(app, "AccentSelection", ColorMath.WithAlpha(AccentText, dark ? (byte)0x55 : (byte)0x40));
        SetBrush(app, "AccentBrush", AccentText); // clave heredada de versiones anteriores
    }

    // ------------------------------------------------------------- utilidades

    private static void ListenForSystemChanges()
    {
        if (_listening) return;
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        catch { }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Desktop)) return;

        // El registro se actualiza un pelín después del aviso: esperamos un poco.
        var dispatcher = _app?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            // Un temporizador reutilizado: crear uno nuevo por cada aviso dejaba
            // decenas de DispatcherTimer vivos (con su manejador) en una sesión larga.
            _debounce ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, OnDebounceTick, dispatcher);
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private static void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        RefreshFromSystem();
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static string Normalize(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "dark" or "oscuro" => "dark",
        "light" or "claro" => "light",
        _ => "system"
    };

    private static MediaColor FromPacked(uint value) => MediaColor.FromArgb(
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static void SetBrush(Application app, string key, MediaColor color)
    {
        try
        {
            ApplyToDictionary(app.Resources, key, color);
        }
        catch { }
    }

    /// <summary>
    /// Recorre el árbol de diccionarios y repinta TODAS las copias del pincel.
    /// Tokens.xaml se mezcla en varios diccionarios, así que cada uno tiene su
    /// propia instancia y hay que actualizarlas todas para que el cambio se vea.
    /// </summary>
    private static void ApplyToDictionary(ResourceDictionary dictionary, string key, MediaColor color)
    {

        if (dictionary.Contains(key) && dictionary[key] is SolidColorBrush brush && brush.Color != color)
        {
            // WPF congela los pinceles de los diccionarios: se reemplaza la instancia
            // y los consumidores que usan DynamicResource se repintan solos.
            dictionary[key] = new SolidColorBrush(color);
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged != null) ApplyToDictionary(merged, key, color);
        }
    }
}
