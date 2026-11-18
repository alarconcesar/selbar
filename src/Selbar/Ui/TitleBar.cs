using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Selbar.Ui;

/// <summary>
/// Barra de título integrada en la ventana. Imita el marco de Windows 11:
/// icono + título a la izquierda, botones de sistema a la derecha, altura 44 px.
/// </summary>
public sealed class TitleBar : System.Windows.Controls.Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("Selbar"));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(ImageSource), typeof(TitleBar), new PropertyMetadata(null));

    public static readonly DependencyProperty IsDialogProperty = DependencyProperty.Register(
        nameof(IsDialog), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    public static readonly DependencyProperty IsRestoreProperty = DependencyProperty.Register(
        nameof(IsRestore), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, (d, _) => ((TitleBar)d).SyncButtons()));

    private CaptionButton? _minimize;
    private CaptionButton? _maximize;
    private CaptionButton? _close;
    private Window? _window;

    public TitleBar()
    {
        SetResourceReference(StyleProperty, "TitleBarStyle");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ImageSource? Icon
    {
        get => (ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsDialog
    {
        get => (bool)GetValue(IsDialogProperty);
        set => SetValue(IsDialogProperty, value);
    }

    public bool IsRestore
    {
        get => (bool)GetValue(IsRestoreProperty);
        set => SetValue(IsRestoreProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _minimize = GetTemplateChild("PART_Minimize") as CaptionButton;
        _maximize = GetTemplateChild("PART_Maximize") as CaptionButton;
        _close = GetTemplateChild("PART_Close") as CaptionButton;
        SyncButtons();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;

        _window.StateChanged += OnWindowStateChanged;
        IsRestore = _window.WindowState == WindowState.Maximized;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window != null) IsRestore = _window.WindowState == WindowState.Maximized;
    }

    private void SyncButtons()
    {
        if (_maximize != null)
        {
            _maximize.IsRestore = IsRestore;
            _maximize.ToolTip = IsRestore ? "Restaurar" : "Maximizar";
        }

        if (_minimize != null)
        {
            _minimize.ToolTip = "Minimizar";
            AutomationProperties.SetName(_minimize, "Minimizar");
        }
        if (_close != null)
        {
            _close.ToolTip = "Cerrar";
            AutomationProperties.SetName(_close, "Cerrar");
        }
        if (_maximize != null) AutomationProperties.SetName(_maximize, IsRestore ? "Restaurar" : "Maximizar");
    }
}
