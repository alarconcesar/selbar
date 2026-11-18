using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using System.Windows.Media;
using System.Windows.Shell;
using Selbar.Native;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Selbar.Ui;

/// <summary>
/// Ventana con el marco nativo de Windows 11: barra de título integrada,
/// botones de minimizar/maximizar/cerrar, esquinas redondeadas, borde y sombra
/// de DWM, arrastre y doble clic nativos. Superficies sólidas del tema.
///
/// Las ventanas de la app heredan de aquí en lugar de <see cref="Window"/>.
/// El contenido que se escribe en el XAML se presenta debajo de la barra.
/// </summary>
public class FluentWindow : Window
{
    /// <summary>Título que se muestra en la barra (por defecto, el de la app).</summary>
    public static readonly DependencyProperty TitleBarTitleProperty = DependencyProperty.Register(
        nameof(TitleBarTitle), typeof(string), typeof(FluentWindow),
        new PropertyMetadata("Selbar"));

    /// <summary>Diálogo: sin minimizar/maximizar y con el marco en color sólido.</summary>
    public static readonly DependencyProperty IsDialogProperty = DependencyProperty.Register(
        nameof(IsDialog), typeof(bool), typeof(FluentWindow),
        new PropertyMetadata(false, (d, _) => ((FluentWindow)d).UpdateChrome()));

    /// <summary>False en ventanas que dibujan su propia cabecera (ResultWindow).</summary>
    public static readonly DependencyProperty ShowTitleBarProperty = DependencyProperty.Register(
        nameof(ShowTitleBar), typeof(bool), typeof(FluentWindow),
        new PropertyMetadata(true, (d, _) => ((FluentWindow)d).UpdateChrome()));

    /// <summary>Icono de la barra de título.</summary>
    public static readonly DependencyProperty TitleBarIconProperty = DependencyProperty.Register(
        nameof(TitleBarIcon), typeof(ImageSource), typeof(FluentWindow), new PropertyMetadata(null));

    /// <summary>Contenido opcional a la derecha del título (indicadores, etc.).</summary>
    public static readonly DependencyProperty TitleBarContentProperty = DependencyProperty.Register(
        nameof(TitleBarContent), typeof(object), typeof(FluentWindow), new PropertyMetadata(null));

    private readonly WindowChrome _chrome;
    private TitleBar? _titleBar;
    private Border? _surface;
    private bool _backdrop;

    public FluentWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize;

        _chrome = new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, _chrome);

        SetResourceReference(StyleProperty, "FluentWindowStyle");

        try { Icon = AppIcon.Get(); } catch { }
        try { TitleBarIcon = AppIcon.Get(); } catch { }

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Theming.Changed += OnThemeChanged;
        Closed += OnClosed;
    }

    public string TitleBarTitle
    {
        get => (string)GetValue(TitleBarTitleProperty);
        set => SetValue(TitleBarTitleProperty, value);
    }

    public bool IsDialog
    {
        get => (bool)GetValue(IsDialogProperty);
        set => SetValue(IsDialogProperty, value);
    }

    public bool ShowTitleBar
    {
        get => (bool)GetValue(ShowTitleBarProperty);
        set => SetValue(ShowTitleBarProperty, value);
    }

    public ImageSource? TitleBarIcon
    {
        get => (ImageSource?)GetValue(TitleBarIconProperty);
        set => SetValue(TitleBarIconProperty, value);
    }

    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _titleBar = GetTemplateChild("PART_TitleBar") as TitleBar;
        _surface = GetTemplateChild("PART_Surface") as Border;
        if (_surface != null)
        {
            _surface.SizeChanged -= OnSurfaceSizeChanged;
            _surface.SizeChanged += OnSurfaceSizeChanged;
        }
        UpdateSurfaceClip();
        ApplyBackdropToVisuals();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Superficies sólidas: el contenido de WPF no compone alfa de forma
            // fiable sobre Mica en todas las configuraciones (se veía negro).
            _backdrop = false;
            DesktopEffects.ApplyDarkFrame(hwnd, Theming.IsDark);
            DesktopEffects.SetRoundedCorners(hwnd, true);
            ApplyBackdropToVisuals();
            ApplyCompositionBackground(hwnd);
        }
        catch { }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;

        // Con WindowChrome el área de cliente se sale del monitor al maximizar:
        // este margen devuelve el contenido dentro de la pantalla.
        Padding = maximized ? new Thickness(8) : new Thickness(0);

        if (_titleBar != null) _titleBar.IsRestore = maximized;
        UpdateSurfaceClip();

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) DesktopEffects.SetRoundedCorners(hwnd, !maximized);
        }
        catch { }
    }

    /// <summary>
    /// WPF pinta de negro las zonas no cubiertas del cliente; fijamos el fondo
    /// de composición al color base para que no aparezcan bandas negras.
    /// </summary>
    /// </summary>
    private void ApplyCompositionBackground(IntPtr hwnd)
    {
        try
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is HwndTarget target)
                target.BackgroundColor = (TryFindResource("BgWindow") as SolidColorBrush)?.Color ?? Colors.Black;
        }
        catch { }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceClip();

    /// <summary>
    /// Windows 11 redondea el marco con DWM, pero el contenido WPF sigue siendo
    /// cuadrado. Recortamos la superficie para que las esquinas no muestren el
    /// fondo cuadrado por debajo del marco redondeado.
    /// </summary>
    private void UpdateSurfaceClip()
    {
        if (_surface == null) return;

        try
        {
            bool rounded = Environment.OSVersion.Version.Build >= 22000
                           && WindowState != WindowState.Maximized
                           && _surface.ActualWidth > 1 && _surface.ActualHeight > 1;

            if (!rounded)
            {
                _surface.Clip = null;
                return;
            }

            _surface.Clip = new RectangleGeometry(
                new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight), 8, 8);
        }
        catch { }
    }

    private void OnThemeChanged()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                _backdrop = false;
                DesktopEffects.ApplyDarkFrame(hwnd, Theming.IsDark);
                DesktopEffects.SetRoundedCorners(hwnd, true);
                ApplyCompositionBackground(hwnd);
            }
            ApplyBackdropToVisuals();
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e) => Theming.Changed -= OnThemeChanged;

    private void UpdateChrome()
    {
        bool caption = ShowTitleBar;
        _chrome.CaptionHeight = caption ? 44 : 0;
        _chrome.ResizeBorderThickness = IsDialog ? new Thickness(0) : new Thickness(6);
        if (IsDialog && ResizeMode == ResizeMode.CanResize) ResizeMode = ResizeMode.NoResize;
        if (_titleBar != null) _titleBar.IsDialog = IsDialog;
    }

    /// <summary>
    /// Pinta la superficie sólida del tema y deja el fondo de ventana transparente;
    /// el recorte redondeado se aplica en UpdateSurfaceClip.
    /// </summary>
    private void ApplyBackdropToVisuals()
    {
        try
        {
            var baseBrush = TryFindResource("BgWindow") as Brush;
            var titleBrush = TryFindResource("BgTitleBar") as Brush;

            // El fondo de WPF se pinta en un rectángulo cuadrado por debajo del
            // contenido; lo dejamos transparente y pintamos la superficie ya
            // recortada con las esquinas redondeadas.
            Background = Brushes.Transparent;

            if (_surface != null)
                _surface.Background = _backdrop ? Brushes.Transparent : baseBrush ?? Brushes.Black;

            if (_titleBar != null)
                _titleBar.Background = _backdrop ? Brushes.Transparent : titleBrush ?? baseBrush;
        }
        catch { }
    }
}
