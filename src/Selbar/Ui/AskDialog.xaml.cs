using System.Windows;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Selbar.Ui;

/// <summary>
/// Pregunta libre: la instrucción es lo primero, la selección se resume y se
/// puede inspeccionar completa. Preguntar solo se habilita con texto útil.
/// </summary>
public partial class AskDialog : FluentWindow
{
    private const int PreviewLimit = 300;

    // El texto se envía completo, pero ActionExecutor.BuildPrompt recorta en
    // MaxInputChars (12 000 por defecto): la ayuda no promete más que eso.
    private const string PreviewHint = "Se envía el texto completo (máx. de caracteres en Ajustes).";
    private const string FullHint = "Texto completo que se enviará.";

    private readonly string _fullSelection;

    public string Prompt => PromptBox.Text.Trim();

    public AskDialog(string selectedText)
    {
        InitializeComponent();

        _fullSelection = (selectedText ?? "").Trim();
        string normalized = _fullSelection.Replace("\r\n", "\n").Replace('\r', '\n');

        // El corte de la vista previa no puede caer entre los dos chars de un
        // emoji: quedaría un sustituto suelto («�») en la primera línea.
        int cut = Math.Min(PreviewLimit, normalized.Length);
        if (cut > 0 && cut < normalized.Length && char.IsHighSurrogate(normalized[cut - 1])) cut--;

        SelectionFullBox.Text = normalized;
        SelectionBox.Text = cut < normalized.Length
            ? normalized[..cut].TrimEnd() + "…"
            : normalized;

        SelectionSummaryText.Text = $"Texto seleccionado · {_fullSelection.Length:N0} caracteres";
        SelectionSummaryText.ToolTip = _fullSelection.Length > PreviewLimit
            ? $"La vista previa muestra los primeros {PreviewLimit} caracteres; se envía el texto completo (hasta el máximo de Ajustes)."
            : null;

        bool truncated = normalized.Length > PreviewLimit;
        SelectionToggleBtn.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
        SelectionHint.Text = truncated ? PreviewHint : FullHint;

        UpdateAskState();

        Loaded += (_, _) =>
        {
            PromptBox.Focus();
            PromptBox.CaretIndex = PromptBox.Text.Length;
        };
    }

    private void UpdateAskState()
    {
        try
        {
            bool ready = !string.IsNullOrWhiteSpace(PromptBox.Text);
            AskBtn.IsEnabled = ready;
            AskBtn.ToolTip = ready
                ? "Preguntar sobre el texto seleccionado (Enter)"
                : "Escribe una instrucción para continuar";
        }
        catch { }
    }

    private void OnPromptChanged(object s, System.Windows.Controls.TextChangedEventArgs e) => UpdateAskState();

    /// <summary>Enter pregunta; Mayús+Enter inserta un salto de línea.</summary>
    private void OnPromptKey(object s, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        if (AskBtn.IsEnabled) OnOk(this, new RoutedEventArgs());
    }

    private void OnToggleSelection(object s, RoutedEventArgs e)
    {
        bool showFull = SelectionFullBox.Visibility != Visibility.Visible;
        SelectionFullBox.Visibility = showFull ? Visibility.Visible : Visibility.Collapsed;
        SelectionBox.Visibility = showFull ? Visibility.Collapsed : Visibility.Visible;
        SelectionToggleBtn.Content = showFull ? "Ver vista previa" : "Ver texto completo";
        SelectionHint.Text = showFull ? FullHint : PreviewHint;
    }

    private void OnOk(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text)) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object s, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
