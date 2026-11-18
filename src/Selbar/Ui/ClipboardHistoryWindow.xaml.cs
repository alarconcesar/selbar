using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Selbar.Core.History;
using Selbar.Core.Platform;
using Selbar.Native;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

namespace Selbar.Ui;

/// <summary>
/// Últimos textos copiados con Selbar: buscar, copiar y pegar de forma segura.
/// Enter/Supr solo actúan sobre la lista; Pegar es de mejor esfuerzo y no se anuncia como verificado.
/// </summary>
public partial class ClipboardHistoryWindow : FluentWindow
{
    private List<ClipboardEntry> _all = new();
    private bool _loaded;
    private bool _updatingList;
    private string? _previewEntryId;

    public ClipboardHistoryWindow()
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
        };
    }

    /// <summary>Lee el almacén una vez; el filtro no vuelve a leer ni a descifrar por tecla.</summary>
    private void LoadSnapshot()
    {
        var selectedId = (EntryList.SelectedItem as ClipboardEntry)?.Id;
        _all = ClipboardHistoryStore.All();
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(string? selectedId = null)
    {
        string query = (FilterBox?.Text ?? "").Trim();
        var filtered = query.Length == 0
            ? _all
            : _all.Where(e => (e.Text ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _updatingList = true;
        try
        {
            EntryList.ItemsSource = filtered;
            UpdateEmpty(filtered.Count);

            if (selectedId != null)
            {
                var again = filtered.FirstOrDefault(e => e.Id == selectedId);
                if (again != null)
                {
                    EntryList.SelectedItem = again;
                    EntryList.ScrollIntoView(again);
                }
            }
            // Copiar sin cerrar devuelve el texto al principio del almacén (con
            // Id nuevo): si la entrada anterior ya no está, se selecciona la
            // primera para no dejar la lista sin selección tras refrescar.
            if (EntryList.SelectedItem == null && filtered.Count > 0)
                EntryList.SelectedIndex = 0;
        }
        finally
        {
            _updatingList = false;
        }

        UpdatePreview();
    }

    private void UpdateEmpty(int shown)
    {
        bool noStore = _all.Count == 0;
        bool filtering = !string.IsNullOrWhiteSpace(FilterBox?.Text);

        EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFilterBtn.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = noStore
            ? "Aún no copiaste texto con Selbar. No es el historial de Windows (Win+V)."
            : "Ningún texto coincide con el filtro.";

        CountText.Text = noStore
            ? ""
            : shown == _all.Count
                ? $"{shown} texto{(shown == 1 ? "" : "s")}"
                : $"{shown} de {_all.Count}";
        if (ClearMenuItem != null)
            ClearMenuItem.IsEnabled = _all.Count > 0 || File.Exists(ClipboardHistoryStore.FilePath);
    }

    private ClipboardEntry? Selected() => EntryList.SelectedItem as ClipboardEntry;

    private void OnFilter(object s, TextChangedEventArgs e)
    {
        var selectedId = (EntryList.SelectedItem as ClipboardEntry)?.Id;
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
        bool hasText = hasEntry && !string.IsNullOrEmpty(entry!.Text);

        CopyBtn.IsEnabled = hasText;
        PasteBtn.IsEnabled = hasText;
        if (DeleteMenuItem != null) DeleteMenuItem.IsEnabled = hasEntry;
        PreviewPlaceholder.Visibility = hasEntry ? Visibility.Collapsed : Visibility.Visible;

        if (entry == null)
        {
            _previewEntryId = null;
            PreviewBox.Text = "";
            PreviewMeta.Text = "";
            SetStatus(EntryList.Items.Count > 0 ? "Selecciona un texto." : "");
            return;
        }

        string text = entry.Text ?? "";
        // Solo se reescribe al cambiar de entrada: refrescar al activar (o tras
        // copiar sin cerrar) no debe perder la selección/scroll de la vista previa.
        if (!string.Equals(_previewEntryId, entry.Id, StringComparison.Ordinal))
        {
            _previewEntryId = entry.Id;
            PreviewBox.Text = text;
        }
        PreviewMeta.Text = $"{entry.When:g} · {TextElementCount(text)} caracteres";
        SetStatus("");
    }

    /// <summary>Cuenta grafemas, no unidades UTF-16: un emoji es un carácter.</summary>
    private static int TextElementCount(string text)
    {
        int count = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) count++;
        return count;
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
                OnPaste(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Enter:
                CopySelected(closeAfter: true);
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
        // El doble clic en el hueco de la lista no debe copiar y cerrar la
        // entrada que quedó seleccionada antes.
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(EntryList, source) is not ListBoxItem) return;
        CopySelected(closeAfter: true);
    }

    private void CopySelected(bool closeAfter = false)
    {
        var entry = Selected();
        if (entry == null) return;

        string text = entry.Text ?? "";
        if (text.Length == 0)
        {
            SetStatus("Esta entrada no tiene texto para copiar.", error: true);
            return;
        }

        if (!ClipboardService.TrySetText(text))
        {
            SetStatus("No se pudo copiar (portapapeles ocupado).", error: true);
            return;
        }

        if (closeAfter)
        {
            Close();
            return;
        }

        // Copiar sin cerrar reordena el almacén (el texto vuelve al principio).
        LoadSnapshot();
        SetStatus("Copiado al portapapeles. La ventana sigue abierta.");
    }

    private void OnCopy(object s, RoutedEventArgs e) => CopySelected(closeAfter: true);

    private void OnCopyKeep(object s, RoutedEventArgs e) => CopySelected(closeAfter: false);

    /// <summary>Pega el texto en la app anterior (mejor esfuerzo; no se anuncia como verificado).</summary>
    private void OnPaste(object s, RoutedEventArgs e)
    {
        var entry = Selected();
        if (entry == null) return;

        string text = entry.Text ?? "";
        if (text.Length == 0)
        {
            SetStatus("Esta entrada no tiene texto para pegar.", error: true);
            return;
        }

        if (!ClipboardService.TrySetText(text))
        {
            SetStatus("No se pudo copiar (portapapeles ocupado).", error: true);
            return;
        }

        if (!ForegroundTracker.Restore(70))
        {
            LoadSnapshot();
            SetStatus("Copiado, pero no se pudo devolver el foco. Pega con Ctrl+V.", error: true);
            return;
        }

        try
        {
            Close();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { NativeMethods.SendCtrlV(); }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch { }
    }

    private void OnDelete(object s, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var entry = Selected();
        if (entry == null) return;

        int index = EntryList.SelectedIndex;
        ClipboardHistoryStore.Remove(entry.Id);

        if (ClipboardHistoryStore.All().Any(e => e.Id == entry.Id))
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
            "Borra los textos copiados que guardó Selbar. No borra el portapapeles actual de Windows. ¿Seguir?",
            "Selbar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        ClipboardHistoryStore.Clear();
        bool gone = !File.Exists(ClipboardHistoryStore.FilePath) && ClipboardHistoryStore.All().Count == 0;
        if (!gone)
        {
            SetStatus("No se pudo vaciar el historial de copias: revisa el archivo o sus permisos.", error: true);
            return;
        }

        FilterBox.Text = "";
        LoadSnapshot();
        SetStatus("Historial de copias vaciado. El portapapeles de Windows no cambia.");
    }

    private void OnShowInfo(object s, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Selbar guarda hasta 20 textos copiados desde aquí, sin cifrar, en clipboard-history.json. " +
            "Los textos de más de 4000 caracteres no se guardan. No es el historial de Windows (Win+V) ni incluye imágenes.",
            "Alcance del historial de copias", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnMore(object s, RoutedEventArgs e) => OpenMenu(MoreBtn);

    private void OnManage(object s, RoutedEventArgs e)
    {
        if (ClearMenuItem != null)
            ClearMenuItem.IsEnabled = _all.Count > 0 || File.Exists(ClipboardHistoryStore.FilePath);
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

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextTertiary");
    }
}
