using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using Selbar.Core.Platform;
using Selbar.Core.Text;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Selbar.Ui;

/// <summary>
/// Muestra el texto seleccionado con las “tipografías” Unicode más usadas.
/// Una sola primaria copia el estilo elegido; el texto completo nunca se sustituye por la vista previa.
/// </summary>
public partial class TextStyleWindow : FluentWindow
{
    private readonly string _source;

    public TextStyleWindow(string text)
    {
        InitializeComponent();
        _source = text ?? "";

        string previewBase = PreviewOf(_source);
        var rows = UnicodeStyles.All
            .Select(style => new StyleRow(style.Id, style.Name, style.Apply(previewBase)))
            .ToList();
        StyleList.ItemsSource = rows;

        bool hasSource = _source.Length > 0;
        StyleList.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasSource ? Visibility.Collapsed : Visibility.Visible;

        if (rows.Count > 0 && hasSource) StyleList.SelectedIndex = 0;
        UpdateAction();

        Loaded += (_, _) =>
        {
            if (hasSource && rows.Count > 0) StyleList.Focus();
            else CopyStyleBtn.IsEnabled = false;
        };

        PreviewKeyDown += OnWindowKeyDown;
    }

    private StyleRow? SelectedStyle() => StyleList.SelectedItem as StyleRow;

    private void OnSelectStyle(object s, SelectionChangedEventArgs e) => UpdateAction();

    private void UpdateAction()
    {
        var row = SelectedStyle();
        bool enabled = _source.Length > 0 && row != null;
        CopyStyleBtn.IsEnabled = enabled;
        CopyStyleBtn.Content = row == null ? "Copiar estilo" : $"Copiar {row.Name}";
        if (row != null) AutomationProperties.SetName(CopyStyleBtn, $"Copiar el estilo {row.Name}");
    }

    /// <summary>Enter solo copia cuando el foco está en la lista; los botones conservan su teclado.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && StyleList.IsKeyboardFocusWithin)
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void OnCopyStyle(object sender, RoutedEventArgs e) => CopySelected();

    private void OnStyleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Solo copia si el doble clic cayó en una fila, no en el hueco de la lista.
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(StyleList, source) is not ListBoxItem) return;
        if (SelectedStyle() == null) return;
        CopySelected();
        e.Handled = true;
    }

    private void CopySelected()
    {
        var row = SelectedStyle();
        if (row == null || _source.Length == 0) return;

        string styled = UnicodeStyles.Apply(row.Id, _source);
        bool copied = ClipboardService.TrySetText(styled);
        SetStatus(
            copied
                ? $"Copiado: {row.Name}. La ventana sigue abierta para comparar otra variante."
                : "No se pudo copiar: el portapapeles está ocupado.",
            error: !copied);
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }

    /// <summary>
    /// Corta por elementos de texto (grafemas), nunca por unidades UTF-16, para no partir
    /// emojis, banderas ni acentos combinados en la vista previa.
    /// </summary>
    private static string PreviewOf(string text, int maxElements = 70)
    {
        string flat = (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (flat.Length == 0) return "";

        int elements = 0;
        int end = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(flat);
        while (enumerator.MoveNext())
        {
            elements++;
            end = enumerator.ElementIndex + enumerator.GetTextElement().Length;
            if (elements >= maxElements) break;
        }

        return elements >= maxElements && end < flat.Length ? flat[..end] + "…" : flat;
    }
}

/// <summary>Fila de estilo con nombre legible y vista previa ya transformada.</summary>
internal sealed record StyleRow(string Id, string Name, string Preview);
