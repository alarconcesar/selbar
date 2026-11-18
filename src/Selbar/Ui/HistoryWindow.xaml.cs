using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Selbar.Core.Actions;
using Selbar.Core.History;
using Selbar.Core.Platform;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

namespace Selbar.Ui;

/// <summary>
/// Historial de resultados de IA: buscar y reutilizar el contenido local.
/// Enter/Supr solo actúan sobre la lista; los botones conservan su teclado nativo.
/// </summary>
public partial class HistoryWindow : FluentWindow
{
    private List<HistoryEntry> _all = new();
    private bool _loaded;
    private bool _updatingList;
    private string? _renderedEntryId;

    public HistoryWindow()
    {
        InitializeComponent();
        LoadSnapshot();

        EntryList.PreviewKeyDown += OnEntryListKeyDown;
        PreviewKeyDown += OnWindowKeyDown;
        Activated += (_, _) => { if (_loaded) LoadSnapshot(); };
        Loaded += (_, _) =>
        {
            _loaded = true;
            if (EntryList.SelectedItem == null && EntryList.Items.Count > 0) EntryList.SelectedIndex = 0;
            FilterBox.Focus();
        };

        // El documento Markdown se repinta con la paleta nueva al cambiar de tema.
        Theming.Changed += OnThemeChanged;
        Closed += (_, _) => Theming.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        try { RenderSelectedDocument(); } catch { }
    }

    /// <summary>Lee el almacén una vez; el filtro trabaja sobre esta instantánea.</summary>
    private void LoadSnapshot()
    {
        var selectedId = SelectedRow()?.Entry.Id;
        _all = HistoryStore.All();
        SubtitleText.Text = HistoryStore.Config.Enabled
            ? "Resultados guardados en este equipo."
            : "El historial está desactivado en Ajustes.";
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(string? selectedId = null)
    {
        string query = (FilterBox?.Text ?? "").Trim();
        IEnumerable<HistoryEntry> filtered = query.Length == 0
            ? _all
            : _all.Where(e => Matches(e, query));
        var rows = filtered.Select(e => new HistoryRow(e)).ToList();

        _updatingList = true;
        try
        {
            EntryList.ItemsSource = rows;
            UpdateEmpty(rows.Count);

            if (selectedId != null)
            {
                var again = rows.FirstOrDefault(r => r.Entry.Id == selectedId);
                if (again != null)
                {
                    EntryList.SelectedItem = again;
                    EntryList.ScrollIntoView(again);
                }
            }
            // Si la entrada seleccionada ya no está en la vista (filtro o cambio
            // externo), se elige la primera: nunca dejar filas visibles con el
            // panel de vista previa vacío.
            if (EntryList.SelectedItem == null && rows.Count > 0)
                EntryList.SelectedIndex = 0;
        }
        finally
        {
            _updatingList = false;
        }

        UpdatePreview();
    }

    private static bool Matches(HistoryEntry entry, string query) =>
        (entry.Action ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (entry.Model ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (!entry.Encrypted && (entry.InputPreview ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        (!entry.Encrypted && (entry.Output ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));

    private void UpdateEmpty(int shown)
    {
        bool disabled = !HistoryStore.Config.Enabled;
        bool noStore = _all.Count == 0;
        bool filtering = !string.IsNullOrWhiteSpace(FilterBox?.Text);

        EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistorySettingsBtn.Visibility = disabled ? Visibility.Visible : Visibility.Collapsed;
        ClearFilterBtn.Visibility = !disabled && filtering ? Visibility.Visible : Visibility.Collapsed;

        if (disabled)
            EmptyText.Text = "El historial está desactivado. Actívalo en Ajustes para guardar resultados.";
        else if (noStore)
            EmptyText.Text = "Aún no hay resultados de IA. Selecciona texto y usa una acción con IA.";
        else
            EmptyText.Text = "Ningún resultado coincide con el filtro.";

        CountText.Text = noStore
            ? ""
            : shown == _all.Count
                ? $"{shown} resultado{(shown == 1 ? "" : "s")}"
                : $"{shown} de {_all.Count}";
        if (ClearMenuItem != null)
            ClearMenuItem.IsEnabled = _all.Count > 0 || File.Exists(HistoryStore.FilePath);
    }

    private HistoryRow? SelectedRow() => EntryList.SelectedItem as HistoryRow;

    private HistoryEntry? Selected() => SelectedRow()?.Entry;

    private void OnFilter(object s, TextChangedEventArgs e)
    {
        var selectedId = SelectedRow()?.Entry.Id;
        ApplyFilter(selectedId);
    }

    /// <summary>Flecha abajo desde la búsqueda lleva a la lista sin cerrar la ventana.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || EntryList.Items.Count == 0) return;
        if (EntryList.SelectedIndex < 0) EntryList.SelectedIndex = 0;
        EntryList.Focus();
        e.Handled = true;
    }

    private void OnSelect(object s, SelectionChangedEventArgs e)
    {
        if (_updatingList) return;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var entry = Selected();
        bool hasEntry = entry != null;
        bool locked = entry?.Encrypted == true;
        bool canCopy = hasEntry && !locked && !string.IsNullOrEmpty(entry!.Output);

        CopyBtn.IsEnabled = canCopy;
        OpenBtn.IsEnabled = hasEntry && !locked && !string.IsNullOrEmpty(entry!.Output);
        if (DeleteMenuItem != null) DeleteMenuItem.IsEnabled = hasEntry;
        if (RepeatMenuItem != null) RepeatMenuItem.IsEnabled = hasEntry && !locked && entry!.CanRepeat;
        if (ShowOriginalMenuItem != null) ShowOriginalMenuItem.IsEnabled = hasEntry;
        PreviewPlaceholder.Visibility = hasEntry ? Visibility.Collapsed : Visibility.Visible;

        if (entry == null)
        {
            _renderedEntryId = null;
            PreviewTitle.Text = "Nada seleccionado";
            PreviewMeta.Text = "";
            PreviewView.Document = new System.Windows.Documents.FlowDocument();
            OriginalPanel.Visibility = Visibility.Collapsed;
            if (ShowOriginalMenuItem != null) ShowOriginalMenuItem.Header = "Ver original y datos completos";
            SetStatus(EntryList.Items.Count > 0 ? "Selecciona un resultado." : "");
            return;
        }

        string title = string.IsNullOrWhiteSpace(entry.Action) ? "Resultado" : entry.Action;
        PreviewTitle.Text = locked ? title + " · no disponible" : title;
        PreviewMeta.Text = locked
            ? "Contenido cifrado no disponible en este usuario o equipo."
            : $"{(string.IsNullOrWhiteSpace(entry.Model) ? "—" : entry.Model)} · {entry.When:g} · {entry.WordCount} palabras";

        RenderDocument(entry);
        UpdateOriginal(entry);

        if (locked)
            SetStatus("Entrada cifrada no disponible: no se copia ni se repite aquí.", error: true);
        else
            SetStatus("");
    }

    /// <summary>Solo se recrea el documento al cambiar de entrada o de tema, no al teclear el filtro.</summary>
    private void RenderSelectedDocument()
    {
        var entry = Selected();
        if (entry == null) return;
        _renderedEntryId = null;
        RenderDocument(entry);
    }

    private void RenderDocument(HistoryEntry entry)
    {
        // El filtro reemplaza ItemsSource y puede volver a seleccionar la misma fila:
        // no se recrea el FlowDocument si la entrada y el tema no cambiaron.
        if (entry.Id == _renderedEntryId && PreviewView.Document != null) return;
        _renderedEntryId = entry.Id;

        if (entry.Encrypted)
        {
            PreviewView.Document = new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(
                    new System.Windows.Documents.Run(
                        "Este resultado se guardó cifrado con otro usuario o equipo. " +
                        "No se puede mostrar, copiar ni repetir en esta sesión. " +
                        "Puedes eliminarlo o recuperarlo con la cuenta original.")));
            return;
        }

        try { PreviewView.Document = MarkdownRenderer.ToFlowDocument(entry.Output, 13.5); }
        catch
        {
            // Entrada corrupta o que el render no traga: texto plano antes que tumbar la UI.
            PreviewView.Document = new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(
                    new System.Windows.Documents.Run(entry.Output ?? "")));
        }
    }

    private void UpdateOriginal(HistoryEntry entry)
    {
        if (OriginalPanel.Visibility != Visibility.Visible) return;

        OriginalText.Text = entry.Encrypted
            ? "Contenido cifrado no disponible en este usuario o equipo."
            : entry.Input ?? "";
        // No contar el blob cifrado como si fueran caracteres del resultado.
        OriginalMeta.Text = entry.Encrypted
            ? $"{entry.ActionId} · {entry.Model} · {entry.When:g} · contenido cifrado no legible"
            : $"{entry.ActionId} · {entry.Model} · {entry.When:g} · " +
              $"{entry.Output?.Length ?? 0} caracteres de salida";
    }

    /// <summary>Escape y Ctrl+F son de la ventana; Enter/Supr/Ctrl+Enter solo de la lista.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && ctrl)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnEntryListKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        switch (e.Key)
        {
            case Key.Enter when ctrl:
                OnRepeat(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;

            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnDoubleClick(object s, MouseButtonEventArgs e)
    {
        // El doble clic en el hueco vacío de la lista no debe abrir la entrada
        // que quedó seleccionada antes.
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(EntryList, source) is not ListBoxItem) return;
        OpenSelected();
    }

    private void OpenSelected()
    {
        var entry = Selected();
        if (entry == null || entry.Encrypted) return;
        if (string.IsNullOrEmpty(entry.Output))
        {
            SetStatus("Esta entrada no tiene texto para abrir.", error: true);
            return;
        }
        try
        {
            new ResultWindow(entry.Action, entry.Output, OutputMode.Copy).Show();
            SetStatus("Abierto en una ventana de resultado.");
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo abrir: " + ex.Message, error: true);
        }
    }

    private void CopySelected()
    {
        var entry = Selected();
        if (entry == null || entry.Encrypted) return;
        if (string.IsNullOrEmpty(entry.Output))
        {
            SetStatus("Esta entrada no tiene texto para copiar.", error: true);
            return;
        }
        bool copied = ClipboardService.TrySetText(entry.Output);
        SetStatus(
            copied ? "Copiado al portapapeles." : "No se pudo copiar (portapapeles ocupado).",
            error: !copied);
    }

    private void OnCopy(object s, RoutedEventArgs e) => CopySelected();

    private void OnOpen(object s, RoutedEventArgs e) => OpenSelected();

    private void OnRepeat(object s, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry == null) return;
        if (entry.Encrypted)
        {
            SetStatus("La entrada cifrada no se puede repetir en este equipo.", error: true);
            return;
        }

        if (!entry.CanRepeat)
        {
            SetStatus("Esta entrada no guarda el texto o la acción original para repetirla.", error: true);
            return;
        }

        var action = App.Registry.Find(entry.ActionId);
        if (action == null)
        {
            // Compatibilidad con entradas antiguas: solo se acepta el título si
            // identifica una única acción activa. Dos acciones homónimas no deben
            // hacer que «Repetir» ejecute la que no era.
            var sameTitle = App.Registry.All
                .Where(a => a.IsEnabled && a.Title == entry.Action)
                .Take(2)
                .ToList();
            action = sameTitle.Count == 1 ? sameTitle[0] : null;
        }

        if (action == null || !action.IsEnabled)
        {
            SetStatus("La acción ya no existe o está desactivada.", error: true);
            return;
        }

        Close();
        _ = ActionRunner.ExecuteAsync(action, entry.Input);
    }

    private void OnDelete(object s, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var entry = Selected();
        if (entry == null) return;

        int index = EntryList.SelectedIndex;
        HistoryStore.Remove(entry.Id);

        if (HistoryStore.All().Any(e => e.Id == entry.Id))
        {
            SetStatus("No se pudo eliminar la entrada: revisa el archivo del historial.", error: true);
            return;
        }

        LoadSnapshot();
        if (EntryList.Items.Count > 0)
            EntryList.SelectedIndex = Math.Min(Math.Max(index, 0), EntryList.Items.Count - 1);
        SetStatus("Entrada eliminada.");
    }

    private void OnClear(object s, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Borra todo el historial de resultados de IA de este equipo. ¿Seguir?",
            "Selbar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        HistoryStore.Clear();
        // Comprobar también el archivo: si el historial era ilegible, All()
        // devuelve vacío aunque el borrado no haya podido eliminar nada.
        if (File.Exists(HistoryStore.FilePath) || HistoryStore.All().Count > 0)
        {
            SetStatus("No se pudo vaciar el historial: revisa el archivo o sus permisos.", error: true);
            return;
        }

        FilterBox.Text = "";
        LoadSnapshot();
        SetStatus("Historial vaciado.");
    }

    private void OnShowOriginal(object s, RoutedEventArgs e)
    {
        if (OriginalPanel.Visibility == Visibility.Visible)
        {
            OriginalPanel.Visibility = Visibility.Collapsed;
            if (ShowOriginalMenuItem != null) ShowOriginalMenuItem.Header = "Ver original y datos completos";
            return;
        }

        var entry = Selected();
        if (entry == null) return;
        OriginalPanel.Visibility = Visibility.Visible;
        if (ShowOriginalMenuItem != null) ShowOriginalMenuItem.Header = "Ocultar original y datos";
        UpdateOriginal(entry);
    }

    private void OnMore(object s, RoutedEventArgs e) => OpenMenu(MoreBtn);

    private void OnManage(object s, RoutedEventArgs e)
    {
        if (ClearMenuItem != null)
            ClearMenuItem.IsEnabled = _all.Count > 0 || File.Exists(HistoryStore.FilePath);
        OpenMenu(ManageBtn);
    }

    private static void OpenMenu(Button button)
    {
        try
        {
            if (button.ContextMenu is not { } menu) return;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch { }
    }

    private void OnClearFilter(object s, RoutedEventArgs e)
    {
        FilterBox.Text = "";
        FilterBox.Focus();
    }

    private void OnOpenHistorySettings(object s, RoutedEventArgs e)
    {
        try { App.OpenSettings("avanzado"); } catch { }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }
}

/// <summary>Fila de la lista: nunca muestra el blob cifrado como si fuera texto.</summary>
internal sealed class HistoryRow
{
    public HistoryRow(HistoryEntry entry) => Entry = entry;

    public HistoryEntry Entry { get; }

    public string Action => string.IsNullOrWhiteSpace(Entry.Action) ? "Resultado" : Entry.Action;

    public string WhenRelative => Entry.WhenRelative;

    public string Preview => Entry.Encrypted
        ? "Contenido cifrado no disponible en este equipo."
        : Entry.InputPreview;
}
