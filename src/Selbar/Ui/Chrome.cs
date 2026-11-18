using System.Windows;
using System.Windows.Input;

namespace Selbar.Ui;

/// <summary>
/// Comportamientos de marco de ventana al estilo Windows 11.
/// </summary>
public static class Chrome
{
    /// <summary>
    /// Marca un elemento como "zona de arrastre" en ventanas sin barra de título
    /// (por ejemplo la cabecera de la ventana de resultado). Arrastra la ventana
    /// con el comportamiento nativo de Windows (Aero Snap incluido) y maximiza
    /// o restaura con doble clic.
    /// </summary>
    public static readonly DependencyProperty IsDragHandleProperty = DependencyProperty.RegisterAttached(
        "IsDragHandle", typeof(bool), typeof(Chrome), new PropertyMetadata(false, OnIsDragHandleChanged));

    public static void SetIsDragHandle(DependencyObject element, bool value)
        => element.SetValue(IsDragHandleProperty, value);

    public static bool GetIsDragHandle(DependencyObject element)
        => (bool)element.GetValue(IsDragHandleProperty);

    private static void OnIsDragHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        if (e.NewValue is true) element.MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not DependencyObject source) return;

        var window = Window.GetWindow(source);
        if (window == null) return;

        if (e.ClickCount == 2 && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try { window.DragMove(); } catch { }
    }
}
