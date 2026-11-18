using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Selbar.Core.Actions;

namespace Selbar.Ui;

/// <summary>
/// Catálogo de recetas de prompts. Trabaja sobre el registro que recibe (el
/// borrador de Ajustes cuando se abre desde allí) y conserva la selección al
/// refrescar; añadir no ejecuta IA ni escribe la selección.
/// </summary>
public partial class PromptLibraryWindow : FluentWindow
{
    private const string AllGroups = "Todos los grupos";

    private sealed class RecipeRow
    {
        public required PromptRecipe Recipe { get; init; }
        public bool NameTaken { get; init; }
        public bool AddedThisSession { get; init; }
        public string StateLabel => AddedThisSession ? "Añadida" : NameTaken ? "Nombre repetido" : "";
        public bool HasState => StateLabel.Length > 0;
    }

    private readonly List<PromptRecipe> _all = PromptLibrary.All.ToList();
    private readonly ActionRegistry? _target;
    private readonly HashSet<string> _addedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedId;
    private bool _ready;

    public PromptLibraryWindow(ActionRegistry? target = null)
    {
        // Sin destino explícito se abre en modo consulta: no se escribe en el
        // registro vivo (sus altas no pasarían por el borrador ni por Guardar).
        _target = target;

        InitializeComponent();

        GroupFilter.Items.Add(AllGroups);
        foreach (string group in PromptLibrary.Groups) GroupFilter.Items.Add(group);
        GroupFilter.SelectedIndex = 0;
        _ready = true;

        Refresh();

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Acciones del destino; vacío si la ventana se abrió sin registro.</summary>
    private IReadOnlyList<ActionDefinition> KnownActions =>
        _target?.All ?? Array.Empty<ActionDefinition>();

    private static string OutputEffect(OutputMode output) => output switch
    {
        OutputMode.Replace => "Reemplaza la selección con el resultado.",
        OutputMode.Copy => "Copia el resultado al portapapeles.",
        OutputMode.Popup => "Muestra el resultado en una ventana.",
        OutputMode.Browser => "Abre el resultado en el navegador.",
        OutputMode.RunCommand => "Ejecuta un comando local.",
        _ => "Aplica el resultado al terminar."
    };

    private void Refresh()
    {
        if (!_ready || RecipeList == null) return;

        double scrollOffset = FindScrollViewer(RecipeList)?.VerticalOffset ?? 0;
        string query = (FilterBox?.Text ?? "").Trim();
        string group = GroupFilter?.SelectedItem as string ?? AllGroups;

        IEnumerable<PromptRecipe> filtered = _all;
        if (group != AllGroups)
            filtered = filtered.Where(r => string.Equals(r.Group, group, StringComparison.Ordinal));
        if (query.Length > 0)
        {
            filtered = filtered.Where(r =>
                r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var known = KnownActions;
        var rows = filtered
            .Select(r => new RecipeRow
            {
                Recipe = r,
                NameTaken = PromptLibrary.IsAlreadyAdded(r.Title, known),
                AddedThisSession = _addedThisSession.Contains(r.Id)
            })
            .ToList();

        string? keep = (RecipeList.SelectedItem as RecipeRow)?.Recipe.Id ?? _selectedId;
        RecipeList.ItemsSource = rows;
        var restore = rows.FirstOrDefault(r => r.Recipe.Id == keep) ?? rows.FirstOrDefault();
        if (restore != null) RecipeList.SelectedItem = restore;

        // Reconstruir ItemsSource no debe devolver al usuario al principio de la lista.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { FindScrollViewer(RecipeList)?.ScrollToVerticalOffset(scrollOffset); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int added = rows.Count(r => r.AddedThisSession);
        int taken = rows.Count(r => r.NameTaken && !r.AddedThisSession);
        string scope = rows.Count == _all.Count ? $"{_all.Count} recetas" : $"{rows.Count} de {_all.Count} recetas";
        CountText.Text = scope
                         + (added > 0 ? $" · {added} añadidas en esta sesión" : "")
                         + (taken > 0 ? $" · {taken} con nombre repetido" : "");

        UpdateDetail();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer direct) return direct;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnFilter(object s, TextChangedEventArgs e) => Refresh();

    private void OnGroupFilter(object s, SelectionChangedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void OnRecipeSelected(object s, SelectionChangedEventArgs e) => UpdateDetail();

    private void UpdateDetail()
    {
        var row = RecipeList.SelectedItem as RecipeRow;
        if (row == null)
        {
            _selectedId = null;
            DetailEmpty.Visibility = Visibility.Visible;
            DetailBody.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = false;
            AddButton.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedId = row.Recipe.Id;
        AddButton.Visibility = Visibility.Visible;
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailBody.Visibility = Visibility.Visible;

        DetailTitle.Text = row.Recipe.Title;
        DetailGroup.Text = row.Recipe.Group;
        DetailDescription.Text = row.Recipe.Description;
        DetailOutput.Text = OutputEffect(row.Recipe.Output);
        PromptPreviewBox.Text = row.Recipe.Prompt;

        if (_target == null)
        {
            AddButton.Content = "Biblioteca sin destino";
            AddButton.IsEnabled = false;
            DetailState.Text = "No hay un registro de acciones disponible. Abre la biblioteca desde Ajustes → Acciones.";
            DetailState.Foreground = (System.Windows.Media.Brush)FindResource("TextTertiary");
            return;
        }

        bool addedNow = _addedThisSession.Contains(row.Recipe.Id);
        if (row.NameTaken)
        {
            AddButton.Content = addedNow ? "Añadida al borrador" : "Ya hay una acción con este nombre";
            AddButton.IsEnabled = false;
            DetailState.Text = addedNow
                ? "Añadida al borrador de Ajustes. Guarda en Ajustes para conservarla."
                : "Ya existe una acción con este título. Para no duplicarla, edítala en Ajustes → Acciones.";
            DetailState.Foreground = (System.Windows.Media.Brush)FindResource(
                addedNow ? "Success" : "TextTertiary");
        }
        else
        {
            AddButton.Content = "Añadir a mis acciones";
            AddButton.IsEnabled = true;
            DetailState.Text = row.Recipe.Output == OutputMode.Replace
                ? "Se añadirá activada y reemplazará la selección al ejecutarla. Todavía no se guarda hasta pulsar Guardar en Ajustes."
                : "Se añadirá activada al borrador de Ajustes. Todavía no se guarda hasta pulsar Guardar en Ajustes.";
            DetailState.Foreground = (System.Windows.Media.Brush)FindResource("TextTertiary");
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (RecipeList.SelectedItem is not RecipeRow row) return;

        if (_target == null)
        {
            StatusText.Text = "No hay un registro de acciones disponible: abre la biblioteca desde Ajustes → Acciones.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            return;
        }

        if (PromptLibrary.IsAlreadyAdded(row.Recipe.Title, KnownActions))
        {
            StatusText.Text = $"«{row.Recipe.Title}» ya tiene una acción con ese nombre. No se duplica.";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextTertiary");
            Refresh();
            return;
        }

        _target.AddCustom(PromptLibrary.ToAction(row.Recipe));
        _addedThisSession.Add(row.Recipe.Id);
        _selectedId = row.Recipe.Id;
        StatusText.Text = $"Añadida «{row.Recipe.Title}» al borrador. Guarda en Ajustes para conservarla.";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Success");
        Refresh();
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
