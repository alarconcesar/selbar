using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Selbar.Core.Actions;
using Selbar.Native;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Selbar.Ui;

/// <summary>
/// Menú vertical de acciones (icono + título). No roba el foco ni se activa.
/// La selección es lógica: el hook global reenvía flechas/Enter/dígitos a
/// <see cref="HandleKey"/> mientras este nivel está visible.
/// </summary>
public partial class OverflowWindow : Window
{
    private readonly string _selectedText;
    private readonly List<System.Windows.Controls.Button> _rows = new();
    private readonly List<Border> _rowHighlights = new();
    private readonly bool _grouped;
    private readonly bool _digitsEnabled;
    private int _selected = -1;
    // El hook de la barra lee este estado desde su hilo antes de cederle teclas.
    private volatile bool _closing;

    public OverflowWindow(string selectedText, IReadOnlyList<ActionDefinition> actions)
        : this(selectedText, actions, "Más acciones", grouped: true)
    {
    }

    public OverflowWindow(string selectedText, IReadOnlyList<ActionDefinition> actions, string title, bool grouped)
    {
        InitializeComponent();
        PopupWindowStyles.MakeNonActivating(this);
        _selectedText = selectedText ?? "";
        _grouped = grouped;
        // Los números de fila son atajos reales: si el usuario los desactivó no
        // deben mostrarse ni consumirse.
        _digitsEnabled = App.SettingsMgr?.Settings.Triggers.DigitShortcuts ?? true;

        var list = (actions ?? Array.Empty<ActionDefinition>())
            .Where(a => a != null)
            .ToList();

        Header.Text = list.Count > 0 ? $"{title} · {list.Count}" : title;
        SubHeader.Visibility = list.Any(a => a.Kind == ActionKind.Ai) ? Visibility.Visible : Visibility.Collapsed;

        BuildRows(list);
        if (_rows.Count > 0) SetSelected(0, scroll: false);

        // Si la cierran desde fuera (App.ClosePopups), que HandleKey deje de
        // tocar una ventana muerta aunque alguien conserve la referencia.
        Closed += (_, _) => _closing = true;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseImmediately();
                e.Handled = true;
            }
        };
    }

    /// <summary>Número de filas (para el hook y las pruebas).</summary>
    public int RowCount => _rows.Count;

    /// <summary>Fila seleccionada lógicamente (-1 si no hay).</summary>
    public int SelectedIndex => _selected;

    /// <summary>Alto máximo que la barra concede a la lista según el área útil.</summary>
    public void SetListMaxHeight(double value)
    {
        try { Scroll.MaxHeight = Math.Max(80, value); } catch { }
    }

    /// <summary>Alto actual de la lista (sin cabecera), para calcular la colocación.</summary>
    public double ListHeight
    {
        get
        {
            try { return Scroll.ActualHeight; } catch { return 0; }
        }
    }

    /// <summary>
    /// Navegación lógica reenviada desde el hook de la barra. Devuelve true si la
    /// tecla pertenece al menú. No usa foco ni activación.
    /// </summary>
    public bool HandleKey(int vk)
    {
        if (_closing) return false;
        // Un menú vacío no debe tragarse Enter: no hay nada que ejecutar.
        if (_rows.Count == 0) return false;

        switch (vk)
        {
            case 0x26: MoveSelection(-1); return true;                 // Arriba
            case 0x28: MoveSelection(1); return true;                  // Abajo
            case 0x21: MoveSelection(-PageSize); return true;          // Av Pág
            case 0x22: MoveSelection(PageSize); return true;           // Re Pág
            case 0x24: SetSelected(0, scroll: true); return true;      // Inicio
            case 0x23: SetSelected(_rows.Count - 1, scroll: true); return true; // Fin
            case 0x0D: ExecuteSelected(); return true;                 // Enter
        }

        if (!_digitsEnabled) return false;
        // Con Mayús se está escribiendo un símbolo (Shift+1 = !), no eligiendo fila.
        if (NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)) return false;
        if (vk >= 0x31 && vk <= 0x39) { SelectByNumber(vk - 0x30); return true; }
        if (vk >= 0x61 && vk <= 0x69) { SelectByNumber(vk - 0x61 + 1); return true; }
        return false;
    }

    private int PageSize => Math.Max(1, (int)((Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : 320) / 42));

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        int next = _selected < 0 ? (delta >= 0 ? 0 : _rows.Count - 1) : _selected + delta;
        SetSelected(next, scroll: true);
    }

    private void SelectByNumber(int number)
    {
        if (number < 1 || number > _rows.Count) return; // fuera de rango: no ejecuta nada
        SetSelected(number - 1, scroll: true);
        ExecuteSelected();
    }

    private void SetSelected(int index, bool scroll)
    {
        if (_rows.Count == 0) return;
        _selected = Math.Clamp(index, 0, _rows.Count - 1);
        // El template de BarMenuButton pinta su propio borde con Background fijo,
        // así que asignar Button.Background no se veía. El resaltado va en un
        // Border interno por fila.
        var brush = SelectionBrush();
        for (int i = 0; i < _rowHighlights.Count; i++)
            _rowHighlights[i].Background = i == _selected ? brush : Brushes.Transparent;
        if (!scroll) return;
        try { _rows[_selected].BringIntoView(); } catch { }
    }

    private void ExecuteSelected()
    {
        if (_selected < 0 || _selected >= _rows.Count) return;
        if (_rows[_selected].Tag is ActionDefinition action) Run(action);
    }

    private static Brush SelectionBrush()
    {
        try { return (Brush)System.Windows.Application.Current.FindResource("AccentSoft"); }
        catch { return new SolidColorBrush(Color.FromArgb(0x2E, 0x60, 0xCD, 0xFF)); }
    }

    /// <summary>Cierre inmediato (Esc, elección o cierre de la barra).</summary>
    public void CloseImmediately()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }

    private void BuildRows(IReadOnlyList<ActionDefinition> actions)
    {
        var buttonStyle = (Style)FindResource("BarMenuButton");
        ActionCategory? previousCategory = null;

        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action == null) continue;

            if (_grouped && previousCategory != null && action.Category != previousCategory)
                List.Children.Add(GroupHeader(action.CategoryLabel));
            previousCategory = action.Category;

            var content = BuildRow(action, i + 1);
            // El resaltado de selección vive aquí: el template del botón no usa
            // su propiedad Background.
            var highlight = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent
            };
            var button = new System.Windows.Controls.Button
            {
                Content = highlight,
                Tag = action,
                Style = buttonStyle,
                Focusable = false,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                ToolTip = Tooltip(action)
            };
            AutomationProperties.SetName(button, action.Title);
            AutomationProperties.SetHelpText(button, ActionLabels.Outcome(action));

            int rowIndex = i;
            button.Click += (_, _) => Run(action);
            button.MouseEnter += (_, _) =>
            {
                if (!_closing && rowIndex >= 0 && rowIndex < _rows.Count) SetSelected(rowIndex, scroll: false);
            };

            List.Children.Add(button);
            _rows.Add(button);
            _rowHighlights.Add(highlight);
        }
    }

    private static TextBlock GroupHeader(string label) => new()
    {
        Text = label,
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("TextTertiary"),
        Margin = new Thickness(10, 8, 4, 2)
    };

    private FrameworkElement BuildRow(ActionDefinition action, int number)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconElement(action);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = action.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var trailing = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 2, 0)
        };

        // El badge IA solo hace falta cuando la cabecera de grupo no lo dice ya.
        bool showAiBadge = action.Kind == ActionKind.Ai && (!_grouped || action.Category == ActionCategory.Custom);
        if (showAiBadge) trailing.Children.Add(AiBadge());

        if (number <= 9 && _digitsEnabled)
        {
            trailing.Children.Add(new TextBlock
            {
                Text = number.ToString(),
                FontSize = 10.5,
                Foreground = Res("TextTertiary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        }

        if (trailing.Children.Count > 0)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        return grid;
    }

    private static Border AiBadge() => new()
    {
        Background = Res("AccentSoft"),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 1, 5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "IA",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("AccentText")
        }
    };

    private static StackPanel Tooltip(ActionDefinition action)
    {
        var tip = new StackPanel { MaxWidth = 320 };
        tip.Children.Add(new TextBlock
        {
            Text = action.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        });

        string line = (action.Kind == ActionKind.Ai ? "IA · " : "") + ActionLabels.Outcome(action);
        if (!string.IsNullOrWhiteSpace(line))
        {
            tip.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                Foreground = Res("TextTertiary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        return tip;
    }

    private static Brush Res(string key)
    {
        try { return (Brush)System.Windows.Application.Current.FindResource(key); }
        catch { return Brushes.Gray; }
    }

    private void Run(ActionDefinition action)
    {
        if (action == null) return;
        _ = ActionRunner.ExecuteAsync(action, _selectedText);
    }

    private static FrameworkElement IconElement(ActionDefinition action)
    {
        if (!string.IsNullOrEmpty(action.IconGlyph))
        {
            return new TextBlock
            {
                Text = action.IconGlyph,
                FontFamily = new FontFamily(ActionIcons.Font),
                FontSize = 16,
                Width = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        var initial = new TextBlock
        {
            Text = string.IsNullOrEmpty(action.Title) ? "?" : action.Title[..1].ToUpperInvariant(),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Width = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        initial.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        return initial;
    }
}
