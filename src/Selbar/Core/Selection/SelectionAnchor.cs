namespace Selbar.Core.Selection;

/// <summary>
/// Rectángulo de la selección en píxeles físicos de pantalla. Sirve para colocar
/// la barra justo encima del texto seleccionado en vez de sobre el cursor.
/// </summary>
public readonly record struct SelectionAnchor(double Left, double Top, double Width, double Height)
{
    public bool IsEmpty => Width <= 1 || Height <= 1;
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
    public double Bottom => Top + Height;
    public double Right => Left + Width;

    public static SelectionAnchor Empty => default;

    public override string ToString() => IsEmpty
        ? "sin ancla"
        : $"{Left:F0},{Top:F0} {Width:F0}x{Height:F0}";
}
