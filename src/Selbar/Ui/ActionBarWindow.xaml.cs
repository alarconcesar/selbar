using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Selbar.Core.Actions;
using Selbar.Core.Diagnostics;
using Selbar.Core.Selection;
using Selbar.Native;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Size = System.Windows.Size;

namespace Selbar.Ui;

/// <summary>
/// Barra flotante: chips (icono + etiqueta corta), separadores por grupo y menú
/// `···` para lo que no quepa. Escribiendo se filtra (modo paleta): `↑↓` mueve,
/// `Enter` ejecuta, `Backspace` borra y `Esc` cierra.
///
/// No roba el foco (WS_EX_NOACTIVATE), así que Pegar/Cortar/Reemplazar siguen
/// actuando sobre la app de origen.
/// </summary>
public partial class ActionBarWindow : Window
{
    private readonly string _selectedText;
    private readonly List<ActionDefinition> _allActions;
    private SelectionAnchor _anchor;

    private readonly List<System.Windows.Controls.Button> _chips = new();
    private readonly List<System.Windows.Controls.Button> _numberedChips = new();
    private readonly DispatcherTimer? _peekTimer;
    private readonly bool _searchConfigured;
    private readonly bool _showNumbers;
    private readonly Brush? _pillBackground;
    private readonly Brush? _pillBorder;
    private readonly Thickness _pillBorderThickness;
    private readonly CornerRadius _pillCorner;
    private readonly System.Windows.Media.Effects.Effect? _pillEffect;
    private DispatcherTimer? _hideTimer;
    // El hook de teclado lee estas banderas desde su hilo: deben ser volatile.
    private volatile bool _allowTyping;
    private volatile bool _peekMode;
    private bool _expanded;
    private DispatcherTimer? _hoverTimer;
    private DispatcherTimer? _leaveTimer;

    private List<ActionDefinition> _inBar = new();
    private OverflowWindow? _overflowWin;
    private volatile bool _overflowOpen;
    private bool _hasOverflow;
    private System.Windows.Controls.Button? _moreButton;
    // La referencia se reemplaza (nunca se muta): lectura segura desde el hook.
    private volatile string _filter = "";
    // Instantánea del número de atajos: el hook no puede leer _numberedChips.Count
    // mientras la UI la reconstruye.
    private volatile int _digitCount;
    private int _selected = -1;
    // Volatile: el hook de teclado lo lee desde su hilo para decidir si la barra
    // sigue siendo dueña del teclado; sin esto una lectura obsoleta podía consumir
    // teclas de una barra ya cerrada.
    private volatile bool _closing;
    private string? _suggestedId;
    private string? _suggestedReason;
    /// <summary>La decisión con IA se pide una sola vez por barra.</summary>
    private bool _rankStarted;

    /// <summary>Pincel del tema (las instancias se mutan al cambiar de tema).</summary>
    private static Brush Res(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public ActionBarWindow(string selectedText, IReadOnlyList<ActionDefinition> actions,
        SelectionAnchor anchor = default, bool peek = false, bool allowTyping = true)
    {
        InitializeComponent();
        PopupWindowStyles.MakeNonActivating(this);
        Icon = AppIcon.Get();
        _pillBackground = Pill.Background;
        _pillBorder = Pill.BorderBrush;
        _pillBorderThickness = Pill.BorderThickness;
        _pillCorner = Pill.CornerRadius;
        _pillEffect = Pill.Effect;

        _selectedText = selectedText ?? "";
        _anchor = anchor;
        // Catálogo completo: el límite se aplica a lo que cabe en la fila, nunca a
        // lo que existe. Más y la búsqueda deben poder encontrar todo.
        _allActions = (actions ?? Array.Empty<ActionDefinition>()).ToList();

        // Marca de sugerida: se resuelve una vez (barato y offline).
        try
        {
            if (App.Registry.SmartSuggestEnabled)
            {
                var sug = TextSuggest.Suggest(_selectedText);
                if (sug != null && _allActions.Any(a => a.Id == sug.ActionId))
                {
                    _suggestedId = sug.ActionId;
                    _suggestedReason = sug.Reason;
                }
            }
        }
        catch { }

        var triggers = App.SettingsMgr.Settings.Triggers;
        _searchConfigured = triggers.SearchOnType;
        _allowTyping = allowTyping;
        _peekMode = peek;
        _expanded = !peek;
        _showNumbers = triggers.ShowNumbers;

        if (peek)
        {
            BuildPeek();
            Pill.MouseEnter += OnPillMouseEnter;
            Pill.MouseLeave += OnPillMouseLeave;
            Pill.MouseMove += OnPillMouseMove;

            // El indicador desaparece solo si no interactúas con él.
            _peekTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _peekTimer.Tick += (_, _) =>
            {
                _peekTimer?.Stop();
                // Si el puntero está encima no se cierra: el usuario puede estar
                // a punto de hacer clic (OnPillMouseEnter ya lo detiene; esto cubre
                // la carrera de un tick ya encolado).
                if (!_expanded && !Pill.IsMouseOver) FadeOutAndClose();
            };
            _peekTimer.Start();
        }
        else
        {
            Rebuild();
        }

        EnsureAutoHideTimer();

        Place();

        // Autoocultado por inactividad: cualquier movimiento sobre la barra lo rearma.
        if (!peek)
        {
            Pill.MouseMove += (_, _) => ArmAutoHide();
            // Un clic en el hueco de la barra (no en un chip, que ya se maneja)
            // habilita el teclado sin activar la ventana.
            Pill.MouseLeftButtonDown += OnPillMouseDown;
            Pill.ToolTip = "Escribe para buscar · flechas para elegir · Enter ejecuta · Esc cierra";
        }

        Loaded += (_, _) => Place();

        Closed += (_, _) =>
        {
            _closing = true;
            try { _hideTimer?.Stop(); } catch { }
            try { _peekTimer?.Stop(); } catch { }
            try { _hoverTimer?.Stop(); } catch { }
            try { _leaveTimer?.Stop(); } catch { }
            try { Pill.MouseEnter -= OnPillMouseEnter; } catch { }
            try { Pill.MouseLeave -= OnPillMouseLeave; } catch { }
            try { Pill.MouseMove -= OnPillMouseMove; } catch { }
            try { _overflowWin?.Close(); } catch { }
            _overflowWin = null;
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            HandleEscape();
            e.Handled = true;
        };
    }

    /// <summary>True mientras solo se muestra el indicador (no captura teclado).</summary>
    public bool IsIndicator => _peekMode;

    /// <summary>¿Hay texto de búsqueda? (la App decide si captura las teclas).</summary>
    public bool IsFiltering => _filter.Length > 0;

    /// <summary>
    /// Autorización deliberada de teclado (hotkey o clic). No depende de la
    /// preferencia SearchOnType: los atajos numéricos y el enrutado del menú
    /// siguen funcionando aunque escribir-para-buscar esté apagado.
    /// </summary>
    public bool AcceptsTyping => _allowTyping && !_peekMode && !_closing;

    /// <summary>¿La ventana se está cerrando? (para no promover una instancia muerta).</summary>
    public bool IsClosing => _closing;

    /// <summary>¿Hay menú abierto ahora mismo? El hook le cede la navegación.</summary>
    public bool OverflowOpen => _overflowOpen;

    /// <summary>¿La fila tiene acciones fuera de la barra? (para pruebas/diagnóstico).</summary>
    public bool HasOverflowActions => _hasOverflow;

    /// <summary>Cuántos chips tienen número de atajo disponible (1-9).</summary>
    public int DigitCount => _digitCount;

    /// <summary>
    /// Promueve la instancia existente a modo teclado deliberado sin recrearla ni
    /// animar: es la hotkey o un clic sobre una barra ya visible.
    /// </summary>
    public void Promote(bool enableTyping)
    {
        if (_closing) return;
        if (_peekMode)
        {
            Expand(enableTyping);
            return;
        }
        if (enableTyping) _allowTyping = true;
        _expanded = true;
        // Si el autoocultado se activó después de abrir la barra, aún no hay reloj.
        EnsureAutoHideTimer();
        ArmAutoHide();
    }

    /// <summary>
    /// Reancla la barra visible a una selección nueva con el mismo texto: sin
    /// esto, seleccionar la misma palabra en otro sitio dejaba la barra en la
    /// posición anterior.
    /// </summary>
    public void RepositionTo(SelectionAnchor anchor)
    {
        if (_closing || anchor.IsEmpty) return;
        _anchor = anchor;
        Place();
    }

    /// <summary>Esc: cierra el nivel superior (menú → búsqueda → barra) sin demora.</summary>
    public void HandleEscape()
    {
        if (_closing) return;
        if (_overflowOpen)
        {
            CloseOverflow();
            return;
        }
        if (IsFiltering)
        {
            ClearFilter();
            return;
        }
        FadeOutAndClose();
    }

    /// <summary>Esc: borra la búsqueda y vuelve a la fila de chips.</summary>
    public void ClearFilter()
    {
        if (_filter.Length == 0) return;
        _filter = "";
        _selected = -1;
        Rebuild();
        Reposition();
    }

    // ---------------- Construcción de la fila ----------------

    // ---------------- Indicador (peek) ----------------

    /// <summary>Pequeña píldora con el icono: no captura teclado ni tapa la selección.</summary>
    private void BuildPeek()
    {
        Bar.Children.Clear();
        _chips.Clear();
        _numberedChips.Clear();
        _digitCount = 0;

        Pill.Background = Brushes.Transparent;
        Pill.BorderThickness = new Thickness(0);
        Pill.Effect = null;
        Pill.CornerRadius = new CornerRadius(0);
        Pill.Padding = new Thickness(6);

        var button = new System.Windows.Controls.Button
        {
            Content = new TextBlock
            {
                Text = "\uE945",
                FontFamily = new FontFamily(ActionIcons.Font),
                FontSize = 15,
                Foreground = Res("TextOnAccent"),
                VerticalAlignment = VerticalAlignment.Center
            },
            Style = (Style)FindResource("PeekButton"),
            ToolTip = "Acciones de Selbar · pasa el ratón o haz clic"
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Acciones de Selbar");
        System.Windows.Automation.AutomationProperties.SetHelpText(button,
            "Pasa el ratón para abrir la barra; haz clic o pulsa el atajo para buscar con el teclado.");
        button.Click += (_, _) => Expand(enableTyping: true);
        Bar.Children.Add(button);
    }
    /// <summary>
    /// Convierte el indicador en la barra completa. Al pasar el ratón no se
    /// activa la captura de teclado; al hacer clic sí (acción deliberada).
    /// </summary>
    private void Expand(bool enableTyping)
    {
        // Autorizar teclado manda aunque la barra ya se haya expandido por hover:
        // un clic/hotkey sobre esa instancia no debe quedar sin efecto.
        if (enableTyping) _allowTyping = true;
        if (_expanded || _closing) return;

        _expanded = true;
        _peekMode = false;
        try { _peekTimer?.Stop(); } catch { }
        try { _hoverTimer?.Stop(); } catch { }
        try { _leaveTimer?.Stop(); } catch { }

        Pill.MouseEnter -= OnPillMouseEnter;
        Pill.MouseLeave -= OnPillMouseLeave;
        Pill.MouseMove -= OnPillMouseMove;

        Pill.Background = _pillBackground ?? Res("BgFlyout");
        Pill.BorderBrush = _pillBorder ?? Res("Stroke");
        Pill.BorderThickness = _pillBorderThickness;
        Pill.CornerRadius = _pillCorner;
        Pill.Effect = _pillEffect;
        Pill.Padding = new Thickness(4);

        Rebuild();
        Reposition();
        EnsureAutoHideTimer();
        ArmAutoHide();
        Pill.MouseMove += (_, _) => ArmAutoHide();
        // En el indicador el clic solo se escuchaba en el botón; al expandir por
        // hover, un clic en el hueco no autorizaba el teclado y la primera tecla
        // cerraba la barra. Se engancha una sola vez (Expand es idempotente).
        Pill.MouseLeftButtonDown += OnPillMouseDown;
        if (Pill.ToolTip == null)
            Pill.ToolTip = "Escribe para buscar · flechas para elegir · Enter ejecuta · Esc cierra";
    }

    /// <summary>
    /// Clic en el hueco de la barra: cierra el menú si estaba abierto y autoriza
    /// el teclado. Los chips y el botón ··· son Buttons y ya marcan el evento
    /// como atendido, así que aquí solo llega el fondo.
    /// </summary>
    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (_overflowOpen) CloseOverflow();
        Promote(enableTyping: true);
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        // No abrimos nada solo con que el indicador aparezca debajo del cursor.
        // El temporizador de hover se inicia con un movimiento real del ratón.
        try { _leaveTimer?.Stop(); } catch { }
        // Con el puntero encima el indicador no debe desvanecerse: si no, el
        // usuario no llega a hacer clic en él.
        try { _peekTimer?.Stop(); } catch { }
    }

    private void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (_expanded || _closing) return;

        try { _leaveTimer?.Stop(); } catch { }
        if (_hoverTimer is { IsEnabled: true }) return;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _hoverTimer.Tick += (_, _) =>
        {
            try { _hoverTimer?.Stop(); } catch { }
            if (!_expanded && Pill.IsMouseOver) Expand(enableTyping: false);
        };
        _hoverTimer.Start();
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        try { _hoverTimer?.Stop(); } catch { }
        if (_expanded) return;

        try { _leaveTimer?.Stop(); } catch { }
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _leaveTimer.Tick += (_, _) =>
        {
            try { _leaveTimer?.Stop(); } catch { }
            if (!_expanded && !Pill.IsMouseOver) FadeOutAndClose();
        };
        _leaveTimer.Start();
    }

    private void Rebuild()
    {
        Bar.Children.Clear();
        _chips.Clear();
        _numberedChips.Clear();
        _digitCount = 0;

        var chipStyle = (Style)FindResource("BarChipButton");

        if (IsFiltering)
            BuildFiltered(chipStyle);
        else
            BuildDefault(chipStyle);

        MaybeStartRanking();
    }

    /// <summary>
    /// Pide en segundo plano la acción más probable. La barra ya está pintada con
    /// el orden local: si el modelo tarda, falla o no hay key, no cambia nada.
    /// </summary>
    private void MaybeStartRanking()
    {
        if (_rankStarted || _closing || _peekMode || IsFiltering) return;
        if (_selectedText.Trim().Length < 2) return;
        try
        {
            if (!App.Decisions.IsConfigured) return;
            _rankStarted = true;
            _ = ApplyDecisionAsync(_selectedText);
        }
        catch (Exception ex) { Logger.LogEx(ex, "barra: preparar decisión"); }
    }

    private async System.Threading.Tasks.Task ApplyDecisionAsync(string text)
    {
        try
        {
            var outcome = await App.Decisions.DecideAsync(text);
            if (outcome is null || _closing) return;
            if (!string.Equals(_selectedText, text, StringComparison.Ordinal)) return;

            var ranked = ActionRanker.Rank(
                _allActions, App.Registry.All, outcome,
                App.SettingsMgr.Settings.Ai.DecisionHideNoise);

            // Sin nada útil: se cierra sola, salvo que el usuario ya esté encima.
            if (ranked.Actions.Count == 0)
            {
                if (!Pill.IsMouseOver) FadeOutAndClose();
                return;
            }
            if (ranked.PromotedId is null && !ranked.HidNoise) return;

            _allActions.Clear();
            _allActions.AddRange(ranked.Actions);
            if (ranked.PromotedId is { } id)
            {
                _suggestedId = id;
                _suggestedReason = "Elegida por IA · pulsa Enter";
            }

            int keep = _selected;
            Rebuild();
            _selected = Math.Clamp(keep, -1, _chips.Count - 1);
        }
        catch (Exception ex) { Logger.LogEx(ex, "barra: decisión inteligente"); }
    }

    private void BuildDefault(Style chipStyle)
    {
        _selected = -1;
        _hasOverflow = false;
        _moreButton = null;

        var triggers = App.SettingsMgr.Settings.Triggers;
        int maxChips = Math.Clamp(triggers.MaxBarIcons, 3, 15);

        // Las acciones de navegador (lupa, abrir URL…) van primero y solo con icono.
        var barActions = _allActions
            .Select((action, index) => (action, index))
            .OrderBy(x => x.action.IsBarIcon ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.action)
            .ToList();

        var chips = barActions
            .Select(a => a.IsBarIcon ? BuildIconChip(a) : BuildChip(a, chipStyle))
            .ToList();

        var moreStyle = (Style)FindResource("BarIconButton");
        var moreBtn = new System.Windows.Controls.Button
        {
            Content = new TextBlock
            {
                Text = "\uE712",
                FontFamily = new FontFamily(ActionIcons.Font),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            },
            ToolTip = "Más acciones",
            Style = moreStyle,
            Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(moreBtn, "Más acciones");
        System.Windows.Automation.AutomationProperties.SetHelpText(moreBtn,
            "Muestra el resto del catálogo; las flechas y Enter lo recorren sin quitar el foco.");
        moreBtn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double moreW = moreBtn.DesiredSize.Width + 2;

        int take = Math.Min(barActions.Count, maxChips);
        double available = ComputeAvailWidth();
        while (take > 1 && RowWidth(chips, barActions, take, moreW, barActions.Count) > available)
            take--;

        _inBar = barActions.Take(take).ToList();
        var overflow = barActions.Skip(take).ToList();

        string? lastCategory = null;
        for (int i = 0; i < _inBar.Count; i++)
        {
            string category = _inBar[i].Category.ToString();
            if (lastCategory != null && category != lastCategory) Bar.Children.Add(Separator());
            lastCategory = category;

            var chip = chips[i];
            if (!_inBar[i].IsBarIcon)
            {
                _numberedChips.Add(chip);
                if (_showNumbers && _numberedChips.Count <= 9 && chip.Content is StackPanel content)
                    content.Children.Add(NumberBadge(_numberedChips.Count));
            }

            Bar.Children.Add(chip);
            _chips.Add(chip);

            int number = _inBar[i].IsBarIcon || !_showNumbers
                ? 0
                : _numberedChips.IndexOf(chip) + 1;
            chip.ToolTip = RichTooltip(_inBar[i], number is > 0 and <= 9 ? number : null,
                _inBar[i].Id == _suggestedId ? _suggestedReason : null);
        }

        if (overflow.Count > 0)
        {
            _hasOverflow = true;
            _moreButton = moreBtn;
            moreBtn.Tag = overflow;
            moreBtn.Click += (_, _) => OpenOverflow(overflow, "Más acciones", grouped: true);
            Bar.Children.Add(moreBtn);
            _chips.Add(moreBtn);
        }

        _digitCount = Math.Min(9, _numberedChips.Count);
    }

    /// <summary>Chip compacto para acciones de navegador: solo el icono.</summary>
    private System.Windows.Controls.Button BuildIconChip(ActionDefinition action)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = IconElement(action, 15),
            Tag = action,
            Style = (Style)FindResource("BarIconButton"),
            Focusable = false,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, action.Title);
        button.Click += (_, _) => Run(action);
        return button;
    }
    private void BuildFiltered(Style chipStyle)
    {
        _hasOverflow = false;
        _moreButton = null;

        var filter = FilterChip();
        filter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Bar.Children.Add(filter);

        var matches = ActionSearch.Filter(_allActions, _filter);
        const int MaxResults = 6;

        if (matches.Count == 0)
        {
            Bar.Children.Add(new TextBlock
            {
                Text = "Sin coincidencias",
                Foreground = Res("TextTertiary"),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 10, 0)
            });
            _selected = -1;
            return;
        }

        // Solo se construyen los chips que podrían caber (máximo MaxResults); el
        // resto vive en la lista vertical de "Ver todos".
        var allChips = matches.Take(MaxResults).Select(a => BuildChip(a, chipStyle)).ToList();

        System.Windows.Controls.Button? seeAll = null;
        double seeAllW = 0;
        if (matches.Count > 1)
        {
            // Se construye siempre que haya más de una coincidencia: el ancho puede
            // reducir los chips visibles por debajo de MaxResults.
            seeAll = BuildSeeAllButton(matches);
            seeAll.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            seeAllW = seeAll.DesiredSize.Width + 2;
        }

        // La búsqueda conserva el presupuesto de ancho de la fila normal: el resto
        // se abre en la lista vertical, no en una fila horizontal sin límite.
        double available = Math.Max(240, ComputeAvailWidth() - filter.DesiredSize.Width - 10);
        int take = Math.Min(matches.Count, MaxResults);
        while (take > 1 && RowWidth(allChips, matches, take, seeAllW, matches.Count, separators: false) > available)
            take--;

        _inBar = matches.Take(take).ToList();
        for (int i = 0; i < take; i++)
        {
            Bar.Children.Add(allChips[i]);
            _chips.Add(allChips[i]);
        }

        if (matches.Count > take && seeAll != null)
        {
            Bar.Children.Add(seeAll);
            _chips.Add(seeAll);
            _hasOverflow = true;
        }

        _selected = Math.Clamp(_selected, 0, _chips.Count - 1);
        UpdateSelectionVisuals();
    }

    /// <summary>Acceso explícito al resto de coincidencias (lista vertical completa).</summary>
    private System.Windows.Controls.Button BuildSeeAllButton(IReadOnlyList<ActionDefinition> matches)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = new TextBlock
            {
                Text = $"Ver todos ({matches.Count})",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            Tag = matches,
            ToolTip = $"Ver las {matches.Count} coincidencias en una lista",
            Style = (Style)FindResource("BarChipButton"),
            Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(button,
            $"Ver todas las coincidencias ({matches.Count})");
        button.Click += (_, _) => OpenOverflow(matches, "Resultados", grouped: false);
        return button;
    }

    /// <summary>Chip de búsqueda: lupa + texto + cursor.</summary>
    private FrameworkElement FilterChip()
    {
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        content.Children.Add(new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily(ActionIcons.Font),
            FontSize = 12,
            Foreground = TryBrush("AccentText", Color.FromRgb(0x60, 0xCD, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });

        content.Children.Add(new TextBlock
        {
            Text = _filter,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        content.Children.Add(new TextBlock
        {
            Text = "\u258F",
            FontSize = 12.5,
            Foreground = TryBrush("AccentText", Color.FromRgb(0x60, 0xCD, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 2, 0),
            Opacity = 0.85
        });

        var border = new Border
        {
            Background = TryBrush("AccentSoft", Color.FromArgb(0x2E, 0x60, 0xCD, 0xFF)),
            BorderBrush = TryBrush("AccentBorder", Color.FromArgb(0x70, 0x60, 0xCD, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 0, 8, 0),
            Height = 28,
            Margin = new Thickness(1, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
            ToolTip = string.IsNullOrEmpty(_filter) ? null : "Buscar: " + _filter
        };
        System.Windows.Automation.AutomationProperties.SetName(border, "Buscar acciones: " + _filter);
        return border;
    }

    private static Border NumberBadge(int number) => new()
    {
        CornerRadius = new CornerRadius(3),
        Background = Res("BgSubtleHover"),
        Padding = new Thickness(4, 1, 4, 1),
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = number.ToString(),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextSecondary")
        }
    };

    private System.Windows.Controls.Button BuildChip(ActionDefinition action, Style style)
    {
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(IconElement(action, 14));
        content.Children.Add(new TextBlock
        {
            Text = action.DisplayTitle,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        });
        if (action.Id == _suggestedId)
            content.Children.Add(SuggestedBadge());

        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Tag = action,
            ToolTip = RichTooltip(action, chipNumber: null,
                action.Id == _suggestedId ? _suggestedReason : null),
            Style = style,
            Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(button, action.Title);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, ActionLabels.Outcome(action));
        button.Click += (_, _) => Run(action);
        return button;
    }

    /// <summary>Estrella discreta que marca el chip sugerido.</summary>
    private static FrameworkElement SuggestedBadge() => new TextBlock
    {
        Text = "★",
        FontSize = 10,
        Foreground = Res("AccentText"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(5, 0, 0, 0)
    };

    private static FrameworkElement IconElement(ActionDefinition action, double size)
    {
        if (!string.IsNullOrEmpty(action.IconGlyph))
        {
            return new TextBlock
            {
                Text = action.IconGlyph,
                FontFamily = new FontFamily(ActionIcons.Font),
                FontSize = size + 1,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        return new TextBlock
        {
            Text = action.Initial,
            FontSize = size - 1,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("AccentText"),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static StackPanel RichTooltip(ActionDefinition action, int? chipNumber, string? suggestedReason = null)
    {
        var tip = new StackPanel { MaxWidth = 300 };
        tip.Children.Add(new TextBlock
        {
            Text = action.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        });

        if (!string.IsNullOrWhiteSpace(suggestedReason))
        {
            tip.Children.Add(new TextBlock
            {
                Text = "★ Sugerida · " + suggestedReason,
                FontSize = 11,
                Foreground = Res("AccentText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        string line = (action.Kind == ActionKind.Ai ? "IA · " : "") + ActionLabels.Outcome(action);
        if (chipNumber != null) line += $" · tecla {chipNumber}";
        if (!string.IsNullOrEmpty(line))
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

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 18,
        Background = Res("StrokeSubtle"),
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---------------- Entrada de teclado (modo deliberado) ----------------

    /// <summary>Tecla capturada por el hook global (se llama en el hilo de UI).</summary>
    public void OnCapturedKey(int vk)
    {
        if (_closing) return;
        Touch();

        if (vk == 0x1B) // Esc
        {
            HandleEscape();
            return;
        }

        // El menú visible manda: flechas, Enter y dígitos se resuelven ahí.
        if (_overflowWin is { } menu)
        {
            if (menu.HandleKey(vk)) return;
            if (vk == 0x08)
            {
                // Retroceso solo vuelve a la búsqueda: no debe borrar además un
                // carácter del filtro (antes hacía las dos cosas).
                CloseOverflow();
                return;
            }
            if (!IsPrintable(vk)) return;
            CloseOverflow();                           // Escribir cierra el menú y filtra
        }

        switch (vk)
        {
            case 0x0D: // Enter
                ExecuteSelected();
                return;

            case 0x08: // Backspace
                if (_filter.Length > 0)
                {
                    _filter = _filter[..^1];
                    _selected = 0;
                    Rebuild();
                    Reposition();
                }
                return;

            case 0x26: // Arriba
            case 0x25: // Izquierda
                MoveSelection(-1);
                return;

            case 0x28: // Abajo
            case 0x27: // Derecha
                MoveSelection(1);
                return;

            case 0x24: // Inicio
                SelectEdge(first: true);
                return;

            case 0x23: // Fin
                SelectEdge(first: false);
                return;

            case 0x20: // Espacio (solo si ya se está filtrando)
                if (_filter.Length > 0) AppendChar(' ');
                return;
        }

        var ch = CharFor(vk);
        if (ch is null || !_searchConfigured) return;
        AppendChar(ch.Value);
    }

    private static bool IsPrintable(int vk) => CharFor(vk) != null;

    private void AppendChar(char ch)
    {
        if (_filter.Length >= 32) return;
        _filter += char.ToLowerInvariant(ch);
        _selected = 0;
        Rebuild();
        Reposition();
    }

    private void ExecuteSelected()
    {
        if (_selected < 0 || _selected >= _chips.Count) return;

        if (ReferenceEquals(_chips[_selected], _moreButton) &&
            _moreButton?.Tag is IReadOnlyList<ActionDefinition> overflow)
        {
            OpenOverflow(overflow, "Más acciones", grouped: true);
            return;
        }

        switch (_chips[_selected].Tag)
        {
            case ActionDefinition action:
                Run(action);
                break;
            case IReadOnlyList<ActionDefinition> list:
                OpenOverflow(list, "Resultados", grouped: false);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_chips.Count == 0) return;
        int count = _chips.Count;
        if (_selected < 0) _selected = delta >= 0 ? 0 : count - 1;
        else _selected = ((_selected + delta) % count + count) % count;
        UpdateSelectionVisuals();
    }

    private void SelectEdge(bool first)
    {
        if (_chips.Count == 0) return;
        _selected = first ? 0 : _chips.Count - 1;
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        for (int i = 0; i < _chips.Count; i++)
        {
            _chips[i].Background = i == _selected
                ? TryBrush("AccentSoft", Color.FromArgb(0x2E, 0x60, 0xCD, 0xFF))
                : System.Windows.Media.Brushes.Transparent;
        }
    }

    private static char? CharFor(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return (char)vk;
        if (vk >= 0x41 && vk <= 0x5A) return char.ToLowerInvariant((char)vk);
        // Teclado numérico: mismas teclas que la fila superior para el filtro.
        if (vk >= 0x60 && vk <= 0x69) return (char)('0' + (vk - 0x60));
        return vk switch
        {
            0x20 => ' ',
            0xBD => '-',
            0xBE => '.',
            0xBC => ',',
            0xBF => '/',
            0xBA => ';',
            0xBB => '=',
            0xDE => '\'',
            0xDB => '[',
            0xDD => ']',
            0xDC => '\\',
            0xC0 => '`',
            _ => null
        };
    }

    private void Run(ActionDefinition action)
    {
        if (action == null) return;
        _ = ActionRunner.ExecuteAsync(action, _selectedText);
    }

    private void Reposition()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try { Place(); } catch { }
        });
    }

    private void Place()
    {
        if (_anchor.IsEmpty)
            PopupPlacement.PlaceAboveCursor(this);
        else
            PopupPlacement.PlaceAboveAnchor(this, _anchor.Left, _anchor.Top, _anchor.Width, _anchor.Height);
    }

    // ---------------- Cierre / atajos ----------------

    /// <summary>
    /// Cierre inmediato. Conserva el nombre histórico porque App y ActionRunner
    /// lo llaman; ya no espera a ningún fundido.
    /// </summary>
    public void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;

        try { _hideTimer?.Stop(); } catch { }
        try { _peekTimer?.Stop(); } catch { }
        try { _hoverTimer?.Stop(); } catch { }
        try { _leaveTimer?.Stop(); } catch { }
        CloseOverflow();
        try { Close(); } catch { }
    }

    /// <summary>
    /// Atajo 1-9 del hook global. Sin caducidad silenciosa: mientras la barra
    /// deliberada siga abierta, sus números visibles siguen siendo válidos.
    /// </summary>
    public void InvokeIndex(int digit)
    {
        try
        {
            if (_closing || _peekMode || IsFiltering || _overflowOpen) return;
            if (digit < 1 || digit > _numberedChips.Count) return;
            if (_numberedChips[digit - 1].Tag is ActionDefinition action)
                Run(action);
        }
        catch (Exception ex) { Logger.LogEx(ex, "atajo de barra"); }
    }

    /// <summary>Reinicia el autoocultado por inactividad (si está configurado).</summary>
    private void Touch() => ArmAutoHide();

    /// <summary>Crea el temporizador de autoocultado una sola vez, ya en modo explícito.</summary>
    private void EnsureAutoHideTimer()
    {
        if (_hideTimer != null || _peekMode) return;
        int autoHide = Math.Clamp(App.SettingsMgr.Settings.Triggers.AutoHideSeconds, 0, 600);
        if (autoHide <= 0) return;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHide) };
        _hideTimer.Tick += (_, _) =>
        {
            // La barra explícita dura mientras se opera: búsqueda y menú la suspenden.
            if (IsFiltering || _overflowOpen)
            {
                try { _hideTimer?.Stop(); _hideTimer?.Start(); } catch { }
                return;
            }
            FadeOutAndClose();
        };
        _hideTimer.Start();
    }

    private void ArmAutoHide()
    {
        if (_peekMode || _hideTimer == null) return;
        try { _hideTimer.Stop(); _hideTimer.Start(); } catch { }
    }

    private void CloseOverflow()
    {
        var menu = _overflowWin;
        _overflowOpen = false;
        _overflowWin = null;
        if (menu != null)
        {
            try { menu.CloseImmediately(); } catch { }
        }
        if (!_closing) ArmAutoHide();
    }

    // ---------------- Utilidades ----------------

    private Brush TryBrush(string key, Color fallback)
    {
        try { return (Brush)FindResource(key); }
        catch { return new SolidColorBrush(fallback); }
    }

    /// <summary>Ancho máximo útil de la barra antes de mandar al ··· (se adapta a pantallas estrechas).</summary>
    private static double ComputeAvailWidth()
    {
        try
        {
            var (cx, cy) = NativeMethods.Cursor();
            double scale = PopupPlacement.DpiScaleAt(cx, cy);
            var area = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(cx, cy)).WorkingArea;
            return Math.Min(620, Math.Max(320, (area.Width / scale) * 0.72));
        }
        catch { return 580; }
    }

    private static double RowWidth(List<System.Windows.Controls.Button> chips,
        List<ActionDefinition> candidates, int take, double moreW, int total, bool separators = true)
    {
        double w = 0;
        string? last = null;
        for (int i = 0; i < take; i++)
        {
            chips[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w += chips[i].DesiredSize.Width + 2;
            string category = candidates[i].Category.ToString();
            if (separators && last != null && category != last) w += 12;
            last = category;
        }
        if (take < total) w += moreW;
        return w + 14;
    }


    private void OpenOverflow(IReadOnlyList<ActionDefinition> overflow, string title, bool grouped)
    {
        if (_closing) return;

        // Abrir el menú es una acción deliberada: sus flechas, Enter y dígitos
        // deben funcionar aunque la barra se hubiera expandido solo con hover.
        _allowTyping = true;

        try { _overflowWin?.CloseImmediately(); } catch { }
        _overflowWin = null;
        _overflowOpen = false;

        OverflowWindow? menu = null;
        try
        {
            var created = new OverflowWindow(_selectedText, overflow, title, grouped);
            menu = created;
            _overflowWin = created;
            _overflowOpen = true;
            try { _hideTimer?.Stop(); } catch { }

            created.Closed += (_, _) =>
            {
                if (_overflowWin == created)
                {
                    _overflowWin = null;
                    _overflowOpen = false;
                }
                if (!_closing) ArmAutoHide();
            };
            App.TrackPopup(created);

            created.Loaded += (_, _) =>
            {
                try { PlaceOverflowMenu(created); } catch { }
            };

            // Posición aproximada antes de mostrar: evita el salto desde (0,0).
            try
            {
                created.Left = Math.Max(0, Left + ActualWidth - 300);
                created.Top = Top + ActualHeight + 6;
            }
            catch { }

            created.Show();
        }
        catch (Exception ex)
        {
            // Sin esto la bandera quedaba en true con una ventana que nunca se vio:
            // el hook enviaba flechas/Enter a un menú fantasma y las teclas se perdían.
            Logger.LogEx(ex, "abrir menú de acciones");
            if (_overflowWin == menu) { _overflowWin = null; _overflowOpen = false; }
            if (menu != null)
            {
                try { menu.CloseImmediately(); } catch { }
            }
            if (!_closing) ArmAutoHide();
        }
    }

    /// <summary>
    /// Coloca el menú dentro del área útil: elige el lado con más espacio, limita
    /// el alto de la lista y recorta el rectángulo por los cuatro bordes.
    /// </summary>
    private void PlaceOverflowMenu(OverflowWindow menu)
    {
        menu.UpdateLayout();

        var (cx, cy) = NativeMethods.Cursor();
        double scale = PopupPlacement.DpiScaleAt(cx, cy);
        if (scale <= 0.1) scale = 1;

        var area = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(cx, cy)).WorkingArea;

        double left = area.Left / scale, top = area.Top / scale;
        double right = area.Right / scale, bottom = area.Bottom / scale;
        const double margin = 8;
        const double gap = 6;

        double desiredX = Left + ActualWidth - menu.ActualWidth;
        double below = Top + ActualHeight + gap;
        double aboveTop = Top - gap;
        double spaceBelow = bottom - margin - below;
        double spaceAbove = aboveTop - (top + margin);

        // Cabecera, borde y padding que no son lista desplazable.
        double chrome = Math.Max(0, menu.ActualHeight - menu.ListHeight);
        double listSpace = Math.Max(spaceBelow, spaceAbove) - chrome - margin;
        menu.SetListMaxHeight(Math.Clamp(listSpace, 96, 380));
        menu.UpdateLayout();

        double x = Math.Clamp(desiredX, left + margin,
            Math.Max(left + margin, right - margin - menu.ActualWidth));

        double y = below;
        if (y + menu.ActualHeight > bottom - margin) y = aboveTop - menu.ActualHeight;
        if (y < top + margin) y = top + margin;
        if (y + menu.ActualHeight > bottom - margin)
            y = Math.Max(top + margin, bottom - margin - menu.ActualHeight);

        menu.Left = x;
        menu.Top = y;
    }
}
