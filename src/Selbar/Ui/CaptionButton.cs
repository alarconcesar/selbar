using System.Windows;

namespace Selbar.Ui;

/// <summary>
/// Botón de la barra de título (minimizar, maximizar/restaurar, cerrar) con el
/// aspecto y el comportamiento nativos de Windows 11.
/// </summary>
public sealed class CaptionButton : System.Windows.Controls.Button
{
    public enum CaptionKind { Minimize, MaximizeRestore, Close }

    /// <summary>Tipo de botón; al cambiarlo se actualiza el glifo.</summary>
    public CaptionKind Kind
    {
        get => (CaptionKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(CaptionKind), typeof(CaptionButton),
        new PropertyMetadata(CaptionKind.Minimize, (d, _) => ((CaptionButton)d).UpdateGlyph()));

    /// <summary>True cuando la ventana está maximizada (el glifo pasa a "restaurar").</summary>
    public bool IsRestore
    {
        get => (bool)GetValue(IsRestoreProperty);
        set => SetValue(IsRestoreProperty, value);
    }

    public static readonly DependencyProperty IsRestoreProperty = DependencyProperty.Register(
        nameof(IsRestore), typeof(bool), typeof(CaptionButton),
        new PropertyMetadata(false, (d, _) => ((CaptionButton)d).UpdateGlyph()));

    public CaptionButton()
    {
        SetResourceReference(StyleProperty, "CaptionButtonStyle");
        UpdateGlyph();
    }

    protected override void OnClick()
    {
        base.OnClick();

        var window = Window.GetWindow(this);
        if (window == null) return;

        switch (Kind)
        {
            case CaptionKind.Minimize:
                SystemCommands.MinimizeWindow(window);
                break;

            case CaptionKind.MaximizeRestore:
                if (window.WindowState == WindowState.Maximized)
                    SystemCommands.RestoreWindow(window);
                else
                    SystemCommands.MaximizeWindow(window);
                break;

            case CaptionKind.Close:
                SystemCommands.CloseWindow(window);
                break;
        }
    }

    private void UpdateGlyph()
    {
        // Glifos de Segoe Fluent Icons: los mismos que usa el marco de Windows 11.
        Content = Kind switch
        {
            CaptionKind.Minimize => "\uE921",
            CaptionKind.MaximizeRestore => IsRestore ? "\uE923" : "\uE922",
            _ => "\uE8BB"
        };
    }
}
