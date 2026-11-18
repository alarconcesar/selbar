using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Selbar.Core.Actions;
using Selbar.Core.Ai;
using Selbar.Core.Config;
using Selbar.Core.Diagnostics;
using Selbar.Core.History;
using Selbar.Core.Platform;
using Selbar.Native;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Selbar.Ui;

/// <summary>
/// Ajustes: navegación lateral con cinco secciones sobre un borrador descartable.
/// Nada de lo que se edita aquí (incluidas las acciones) toca el estado vivo hasta
/// que «Guardar cambios» confirma que settings.json se escribió de verdad.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    /// <summary>Caché de modelos por endpoint (5 min) para no pegar a la red en cada apertura.</summary>
    private static readonly Dictionary<string, (DateTime At, List<string> Models)> ModelsCache = new();

    private readonly double[] _scrollOffsets = new double[5];
    private int _currentSection = -1;

    private AppSettings _draft = new();
    private ActionRegistry _draftRegistry = null!;

    private CancellationTokenSource? _modelsCts;
    private CancellationTokenSource? _testCts;
    private bool _initialized;
    private bool _closed;
    private bool _recording;
    private bool _clearKey;
    private bool _clearGroqKey;
    private bool _clearDecisionKey;
    private bool _dirty;
    private bool _dirtyEnabled;
    private bool _modelsLoadedOnce;
    private bool _navFromPointer;
    private bool _advancedExpanded;
    private bool _groqExpanded;

    public SettingsWindow(string? section = null)
    {
        InitializeComponent();
        try { BrandIcon.Source = AppIcon.Get(); } catch { }

        foreach (var provider in Providers.All) ProviderBox.Items.Add(provider);
        VersionText.Text = "v" + App.Version;

        BeginDraft();
        LoadSettingsIntoUi();
        RefreshActions();

        HotkeyBox.TextChanged += (_, _) => HotkeyDisplay.Text = HotkeyBox.Text;
        HotkeyDisplay.Text = HotkeyBox.Text;
        Deactivated += (_, _) => { if (_recording) StopRecording(); };

        _initialized = true;
        SelectSection(section ?? "acciones");

        // Seguimiento de cambios sin guardar (avisa al cerrar).
        _dirtyEnabled = true;
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OnAnyTextChanged), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler(OnAnyToggle), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler(OnAnyToggle), true);
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnAnySelection), true);
        ApiKeyBox.PasswordChanged += (_, _) =>
        {
            // Vaciar la caja no borra la key guardada: solo «Borrar» lo pide. El
            // aviso y el estado deben reflejar lo que se guardaría en cada momento.
            if (ApiKeyBox.Password.Length > 0) _clearKey = false;
            UpdateKeyHints();
            MarkDirty();
        };
        GroqKeyBox.PasswordChanged += (_, _) =>
        {
            if (GroqKeyBox.Password.Length > 0) _clearGroqKey = false;
            UpdateKeyHints();
            MarkDirty();
        };
        DecisionKeyBox.PasswordChanged += (_, _) =>
        {
            if (DecisionKeyBox.Password.Length > 0) _clearDecisionKey = false;
            UpdateKeyHints();
            MarkDirty();
        };
    }

    private AppSettings Draft => _draft;

    // ---------------- Borrador ----------------

    /// <summary>Reinicia el borrador desde los ajustes guardados (sin tocar el estado vivo).</summary>
    private void BeginDraft(bool cancelModelLoad = false)
    {
        // Descartar, importar o restablecer pueden cambiar de endpoint/modelo: una
        // carga de modelos en vuelo no debe repintar el combo al terminar.
        if (cancelModelLoad) CancelModelLoad();
        _draft = SettingsDraft.Clone(App.SettingsMgr.Settings);
        _draftRegistry = new ActionRegistry(_draft.ActionOverrides)
        {
            Profiles = _draft.Profiles,
            SmartSuggestEnabled = _draft.Triggers.SmartSuggest
        };
    }

    // ---------------- Navegación ----------------

    private void OnNavPointer(object sender, System.Windows.Input.MouseButtonEventArgs e) => _navFromPointer = true;

    private void OnNavKey(object sender, System.Windows.Input.KeyEventArgs e) => _navFromPointer = false;

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        // Cambiar de sección no debe dejar el grabador capturando teclas.
        if (_recording) StopRecording();
        ShowSection(Math.Max(0, NavList.SelectedIndex));
    }

    /// <summary>Va a una sección concreta (la usa el resultado al fallar la IA y la bienvenida).</summary>
    public void SelectSection(string section)
    {
        // Abrir Ajustes en una sección mientras se graba un atajo dejaría al
        // grabador capturando la primera tecla que pulse el usuario.
        if (_recording) StopRecording();
        int index = (section ?? "").Trim().ToLowerInvariant() switch
        {
            "ia" or "inteligencia" or "inteligencia artificial" => 1,
            "disparadores" or "seleccion" or "selección" or "seleccion y atajos" or "selección y atajos"
                or "barra y atajos" => 2,
            "apariencia" or "tema" => 3,
            "avanzado" or "datos" or "datos e integraciones" => 4,
            _ => 0
        };

        if (NavList.SelectedIndex != index)
        {
            NavList.SelectedIndex = index; // dispara OnNavChanged → ShowSection
            return;
        }
        ShowSection(index);
    }

    private void ShowSection(int index)
    {
        var panels = new FrameworkElement[] { PanelAcciones, PanelIA, PanelDisparadores, PanelApariencia, PanelAvanzado };
        if (index < 0 || index >= panels.Length) index = 0;

        if (_currentSection >= 0 && _currentSection < panels.Length)
            _scrollOffsets[_currentSection] = ContentScroll.VerticalOffset;

        // Solo el ratón puede permitirse un fundido breve; el teclado cambia al instante.
        bool animate = _navFromPointer;
        _navFromPointer = false;

        for (int i = 0; i < panels.Length; i++)
        {
            bool active = i == index;
            if (active)
            {
                if (panels[i].Visibility != Visibility.Visible)
                {
                    panels[i].Visibility = Visibility.Visible;
                    if (animate) Motion.FadeIn(panels[i], 150, offsetY: 4);
                }
            }
            else
            {
                panels[i].Visibility = Visibility.Collapsed;
            }
        }

        _currentSection = index;
        double offset = _scrollOffsets[index];
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { ContentScroll.ScrollToVerticalOffset(offset); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // La lista de modelos solo se pide al entrar de forma deliberada en IA.
        if (index == 1 && !_modelsLoadedOnce && _initialized)
        {
            _modelsLoadedOnce = true;
            _ = LoadModelsAsync();
        }
    }

    // ---------------- Carga / guardado ----------------

    private void LoadSettingsIntoUi(bool resetDisclosures = true)
    {
        // Rebase del formulario (guardar, descartar, importar o restablecer): el
        // grabador de atajos no puede seguir capturando sobre los valores nuevos.
        if (_recording) StopRecording();
        var ai = Draft.Ai;
        ProviderBox.SelectedItem = Providers.All.Contains(ai.Provider) ? ai.Provider : "Personalizado";
        BaseUrlBox.Text = ai.BaseUrl;
        ModelBox.Text = ai.Model;
        LangBox.Text = ai.TargetLang;
        SystemPromptBox.Text = ai.SystemPrompt;
        TemperatureBox.Text = ai.Temperature.ToString("0.##", CultureInfo.CurrentCulture);
        MaxTokensBox.Text = ai.MaxTokens.ToString(CultureInfo.CurrentCulture);
        TimeoutBox.Text = ai.TimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        MaxInputBox.Text = ai.MaxInputChars.ToString(CultureInfo.CurrentCulture);
        GroqModelBox.Text = ai.TranscriptionModel;
        DecisionToggle.IsChecked = ai.DecisionEnabled;
        DecisionHide.IsChecked = ai.DecisionHideNoise;
        UpdateKeyHints();

        var t = Draft.Triggers;
        AutoMouse.IsChecked = t.AutoOnMouseSelection;
        AutoKb.IsChecked = t.AutoOnKeyboardSelection;
        SelectionPeek.IsChecked = t.SelectionPeek;
        UpdatePeekState();
        HotkeyEnabled.IsChecked = t.HotkeyEnabled;
        HotkeyBox.Text = string.IsNullOrWhiteSpace(t.Hotkey) ? Hotkeys.DefaultHotkey : t.Hotkey;
        HotkeyDisplay.Text = HotkeyBox.Text;
        HotkeyHint.Text = "Funciona aunque desactives el aviso automático: selecciona texto y púlsala.";
        HotkeyHint.Foreground = Theme("TextTertiary");
        MaxIconsBox.Text = t.MaxBarIcons.ToString(CultureInfo.CurrentCulture);
        ShowNumbers.IsChecked = t.ShowNumbers;
        SearchOnType.IsChecked = t.SearchOnType;
        SmartSuggest.IsChecked = t.SmartSuggest;
        DigitShortcuts.IsChecked = t.DigitShortcuts;
        AutoHideBox.Text = t.AutoHideSeconds.ToString(CultureInfo.CurrentCulture);
        MinLengthBox.Text = t.MinSelectionLength.ToString(CultureInfo.CurrentCulture);
        DictationHotkeyBox.Text = Draft.DictationHotkey;

        var history = Draft.History;
        HistoryEnabled.IsChecked = history.Enabled;
        HistoryEncrypt.IsChecked = history.Encrypt;
        HistoryClearOnExit.IsChecked = history.ClearOnExit;
        HistoryStatus.Text = "";

        ExcludedBox.Text = string.Join("\n", Draft.ExcludedApps);
        StartWindows.IsChecked = Autostart.IsEnabled() || Draft.StartWithWindows;

        DataPathBox.Text = AppPaths.DataDir;

        // Integraciones y voz
        ObsidianVaultBox.Text = Draft.ObsidianVault;
        ObsidianFolderBox.Text = Draft.ObsidianFolder;
        SpeakRateBox.Text = Draft.SpeakRate.ToString("0.##", CultureInfo.CurrentCulture);
        VoiceInfoText.Text = "Voz del sistema: " + Core.Speech.SpeechService.VoiceName;
        ProfilesBox.Text = ProfileRules.Format(Draft.Profiles);
        AboutText.Text =
            $"Selbar {App.Version} · .NET {Environment.Version}\n" +
            $"Ajustes: {App.SettingsMgr.FilePath}";

        // Apariencia: tema guardado y estado real del sistema.
        ThemeBox.SelectedIndex = (Draft.Theme ?? "system").ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        AnimationsHint.Text = Motion.Enabled
            ? "Activadas. La app respeta el ajuste de efectos de animación de Windows."
            : "Desactivadas en este equipo (efectos de animación de Windows o del sistema).";
        ThemeAppliedText.Text = "Aplicado: " + Theming.EffectiveName;

        // Los grupos técnicos empiezan plegados; Groq se abre solo si ya está configurado.
        if (resetDisclosures)
        {
            _advancedExpanded = false;
            _groqExpanded = !string.IsNullOrEmpty(ai.GroqApiKeyEncrypted);
        }
        ApplyAdvancedState();
        ApplyGroqState();
    }

    private Brush Theme(string key)
    {
        try { return (Brush)FindResource(key); }
        catch { return Brushes.Gray; }
    }

    /// <summary>
    /// Avisos de las dos claves. Se recalculan siempre desde la caja, la intención
    /// de borrado y lo guardado: escribir y luego vaciar no puede dejar un
    /// «pendiente de guardar» que ya no existe.
    /// </summary>
    private void UpdateKeyHints()
    {
        bool typed = ApiKeyBox.Password.Length > 0;
        KeyHint.Text = typed
            ? "Nueva key pendiente de guardar."
            : _clearKey
                ? "La key se borrará al guardar. Escribe una nueva para sustituirla."
                : string.IsNullOrEmpty(Draft.Ai.ApiKeyEncrypted)
                    ? "Sin key: solo funcionará un servidor local (Ollama/LM Studio)."
                    : "Hay una key guardada (escribe para cambiarla).";

        bool groqTyped = GroqKeyBox.Password.Length > 0;
        GroqKeyHint.Text = groqTyped
            ? "Nueva key de Groq pendiente de guardar."
            : _clearGroqKey
                ? "La key de Groq se borrará al guardar."
                : string.IsNullOrEmpty(Draft.Ai.GroqApiKeyEncrypted)
                    ? "Sin key de Groq: la transcripción no estará disponible."
                    : "Hay una key de Groq guardada (escribe para cambiarla).";
        UpdateGroqState();

        bool decisionTyped = DecisionKeyBox.Password.Length > 0;
        DecisionHint.Text = decisionTyped
            ? "Nueva key de Gemini pendiente de guardar."
            : _clearDecisionKey
                ? "La key de Gemini se borrará al guardar."
                : string.IsNullOrEmpty(Draft.Ai.DecisionApiKeyEncrypted)
                    ? "Sin key: la barra usa solo las reglas locales (instantáneo)."
                    : "Hay una key de Gemini guardada (escribe para cambiarla).";
        DecisionToggle.IsEnabled = decisionTyped || (!_clearDecisionKey && !string.IsNullOrEmpty(Draft.Ai.DecisionApiKeyEncrypted));
        DecisionHide.IsEnabled = DecisionToggle.IsEnabled;
    }

    /// <summary>Estado visible de la key de Groq (se reutiliza al borrarla, sin esperar a recargar).</summary>
    private void UpdateGroqState()
    {
        bool typed = GroqKeyBox.Password.Length > 0;
        bool hasKey = !string.IsNullOrEmpty(Draft.Ai.GroqApiKeyEncrypted);
        GroqStateText.Text = typed
            ? "Nueva key pendiente"
            : _clearGroqKey ? "Se borrará al guardar" : hasKey ? "Configurada" : "Sin configurar";
        GroqStateText.Foreground = Theme(
            _clearGroqKey && !typed ? "Warning" : typed || hasKey ? "Success" : "TextTertiary");
    }

    /// <summary>Anula una carga de modelos en vuelo para que no repinte el combo.</summary>
    private void CancelModelLoad()
    {
        try { _modelsCts?.Cancel(); } catch { }
        _modelsCts = null;
        if (ModelsStatus != null) ModelsStatus.Text = "";
        if (ModelsRefreshBtn != null) ModelsRefreshBtn.IsEnabled = true;
    }

    // ---------------- Divulgación progresiva ----------------

    private static void SetChevron(TextBlock chevron, bool expanded) =>
        chevron.Text = expanded ? "\uE70E" : "\uE70D";

    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        AdvancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedLabel.Text = expanded ? "Ocultar opciones avanzadas" : "Opciones avanzadas";
        SetChevron(AdvancedChevron, expanded);
    }

    private void ApplyAdvancedState() => SetAdvancedExpanded(_advancedExpanded);

    private void OnToggleAdvanced(object s, RoutedEventArgs e) => SetAdvancedExpanded(!_advancedExpanded);

    private void SetGroqExpanded(bool expanded)
    {
        _groqExpanded = expanded;
        GroqPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SetChevron(GroqChevron, expanded);
    }

    private void ApplyGroqState() => SetGroqExpanded(_groqExpanded);

    private void OnToggleGroq(object s, RoutedEventArgs e) => SetGroqExpanded(!_groqExpanded);

    private void OnToggleAppearanceHelp(object s, RoutedEventArgs e)
    {
        bool show = AppearanceHelpText.Visibility != Visibility.Visible;
        AppearanceHelpText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetChevron(AppearanceHelpChevron, show);
    }

    // ---------------- Apariencia ----------------

    private static string ThemeTag(object? item) =>
        item is ComboBoxItem { Tag: string tag } ? tag : "system";

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;

        // Vista previa inmediata: al descartar o cerrar se vuelve al tema guardado.
        Theming.SetMode(ThemeTag(ThemeBox.SelectedItem));
        ThemeAppliedText.Text = "Aplicado: " + Theming.EffectiveName;
        MarkDirty();
    }

    private void OnOpenWindowsColors(object s, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:personalization-colors") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowSaveError("No se pudo abrir Configuración de Windows: " + ex.Message); }
    }

    // ---------------- Acciones ----------------

    private void OnAddMenu(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void OnMoreMenu(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private static void OpenMenu(object sender)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RefreshActions()
    {
        if (ActionList == null) return;

        bool wasEnabled = _dirtyEnabled;
        _dirtyEnabled = false;
        try
        {
            var selectedId = (ActionList.SelectedItem as ActionDefinition)?.Id;
            var query = (FilterBox?.Text ?? "").Trim();
            bool activeOnly = ActiveOnly?.IsChecked == true;

            var all = _draftRegistry.All.OrderBy(a => a.Order).ToList();
            IEnumerable<ActionDefinition> filtered = all;

            if (activeOnly) filtered = filtered.Where(a => a.IsEnabled);
            if (query.Length > 0)
            {
                filtered = filtered.Where(a =>
                    a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (a.Description ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();
            ActionList.ItemsSource = list;
            if (selectedId != null)
                ActionList.SelectedItem = list.FirstOrDefault(a => a.Id == selectedId);
            else if (list.Count > 0)
                ActionList.SelectedIndex = 0;

            if (EmptyActions != null)
                EmptyActions.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateActionCount(all, list.Count);
            UpdateActionCommands();
        }
        finally { _dirtyEnabled = wasEnabled; }
    }

    private void UpdateActionCount(List<ActionDefinition>? all = null, int? visible = null)
    {
        var actions = all ?? _draftRegistry.All.OrderBy(a => a.Order).ToList();
        int active = actions.Count(a => a.IsEnabled);
        int total = actions.Count;
        int shown = visible ?? total;
        ActiveCountText.Text = shown == total
            ? $"{active} de {total} acciones activas"
            : $"{shown} visibles · {active} de {total} activas";
    }

    private void UpdateActionCommands()
    {
        if (EditMenuItem == null) return;

        var action = SelectedAction();
        bool custom = action?.Category == ActionCategory.Custom;
        // Solo las personalizadas se editan, eliminan y ordenan (las de serie se
        // activan u ocultan): el texto de la sección y el hint dicen lo mismo.
        EditMenuItem.IsEnabled = custom;
        DeleteMenuItem.IsEnabled = custom;
        MoveUpItem.IsEnabled = custom;
        MoveDownItem.IsEnabled = custom;

        ActionHintText.Text = action == null
            ? ""
            : custom
                ? "Personalizada · puedes editarla, eliminarla u ordenarla."
                : "De serie · puedes activarla u ocultarla.";
    }

    private void OnActionSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionCommands();

    private void OnActionListRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ActionList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item == null) return;
        item.IsSelected = true;
        item.Focus();
    }

    /// <summary>Editar/Eliminar/Subir/Bajar solo aplican a acciones propias; en las de serie se deshabilitan.</summary>
    private void OnActionContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        bool custom = (element.DataContext as ActionDefinition)?.Category == ActionCategory.Custom;
        // Por cabecera y no por índice: el menú tiene un separador en medio.
        SetMenuItemEnabled(menu, "Editar", custom);
        SetMenuItemEnabled(menu, "Eliminar", custom);
        SetMenuItemEnabled(menu, "Subir", custom);
        SetMenuItemEnabled(menu, "Bajar", custom);
    }

    private static void SetMenuItemEnabled(ItemsControl menu, string header, bool enabled)
    {
        foreach (object item in menu.Items)
            if (item is MenuItem menuItem && string.Equals(menuItem.Header as string, header, StringComparison.Ordinal))
                menuItem.IsEnabled = enabled;
    }

    /// <summary>
    /// Un interruptor de fila cambia el borrador (no el registro vivo). El binding
    /// es OneWay porque la lista virtualiza: al reciclar contenedores WPF reaplica
    /// IsChecked y dispara Checked/Unchecked sin intervención del usuario. Solo se
    /// cuenta un cambio real comparando con el modelo antes de escribirlo.
    /// </summary>
    private void OnActionToggle(object sender, RoutedEventArgs e)
    {
        if (!_initialized || !_dirtyEnabled) return;
        if (sender is not System.Windows.Controls.CheckBox toggle || toggle.DataContext is not ActionDefinition action) return;

        bool enabled = toggle.IsChecked == true;
        if (action.IsEnabled == enabled) return; // evento de generación/reciclado, no del usuario
        if (!toggle.IsKeyboardFocusWithin && !toggle.IsMouseOver) return;

        action.IsEnabled = enabled;
        MarkDirty();
        UpdateActionCount(null, ActionList.Items.Count);
        if (ActiveOnly?.IsChecked == true)
            Dispatcher.BeginInvoke(new Action(RefreshActions), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------- Cambios sin guardar ----------------

    private void MarkDirty()
    {
        if (!_dirtyEnabled) return;
        _dirty = true;
        // «Guardado.» o un error anterior ya no describen el formulario actual.
        if (SaveStatus != null) SaveStatus.Visibility = Visibility.Collapsed;
        UpdateDirtyUi();
    }

    private void UpdateDirtyUi()
    {
        if (DirtyHint != null)
            DirtyHint.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        if (DiscardButton != null)
            DiscardButton.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAnyTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_dirtyEnabled) return;
        if (e.OriginalSource is System.Windows.Controls.TextBox box && (box == FilterBox || box.IsReadOnly)) return;
        MarkDirty();
    }

    private void OnAnyToggle(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, AutoMouse) || ReferenceEquals(e.OriginalSource, AutoKb))
            UpdatePeekState();
        // Los interruptores de la lista llevan su propio handler (OnActionToggle).
        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton toggle &&
            toggle.DataContext is ActionDefinition) return;
        // «Solo activas» es un filtro de la vista, no un ajuste que se guarde.
        if (ReferenceEquals(e.OriginalSource, ActiveOnly)) return;
        MarkDirty();
    }

    /// <summary>El indicador solo tiene sentido si hay algún aviso automático activo.</summary>
    private void UpdatePeekState()
    {
        bool cualquierAviso = AutoMouse.IsChecked == true || AutoKb.IsChecked == true;
        SelectionPeek.IsEnabled = cualquierAviso;
        SelectionPeek.Opacity = cualquierAviso ? 1 : 0.55;
    }

    private void OnAnySelection(object? sender, SelectionChangedEventArgs e)
    {
        if (!_dirtyEnabled) return;
        // Navegar por secciones o seleccionar una acción no es un cambio de ajustes.
        if (e.OriginalSource is System.Windows.Controls.Primitives.Selector selector &&
            (selector == NavList || selector == ActionList)) return;
        MarkDirty();
    }

    /// <summary>Los tests desactivan los diálogos modales (SELBAR_NO_PROMPTS=1).</summary>
    private static bool PromptsEnabled =>
        Environment.GetEnvironmentVariable("SELBAR_NO_PROMPTS") != "1";

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recording) StopRecording();

        if (_dirty && PromptsEnabled)
        {
            var answer = MessageBox.Show(this,
                "Hay cambios sin guardar.\n\n" +
                "Sí: guardar y cerrar · No: descartar los cambios · Cancelar: seguir editando.",
                "Selbar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes && !TryApplyDraft()) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.No) DiscardDraft();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Si se cierra descartando cambios, el tema vuelve al que está guardado.
        try { if (_dirty) Theming.SetMode(App.SettingsMgr.Settings.Theme); } catch { }
        _closed = true;
        // Ninguna continuación tardía (modelos/test) debe tocar la ventana muerta.
        // Solo se cancela: disponer aquí el CTS mientras HttpClient lo usa puede
        // lanzar ObjectDisposedException. Cada operación lo dispone en su finally.
        try { _modelsCts?.Cancel(); } catch { }
        _modelsCts = null;
        try { _testCts?.Cancel(); } catch { }
        _testCts = null;
        base.OnClosed(e);
    }

    private void OnFilterToggle(object sender, RoutedEventArgs e) => RefreshActions();

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => RefreshActions();

    private ActionDefinition? ActionFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as ActionDefinition ?? SelectedAction();

    private ActionDefinition? SelectedAction() => ActionList.SelectedItem as ActionDefinition;

    private void OnAdd(object s, RoutedEventArgs e)
    {
        var dialog = new ActionEditDialog(null) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _draftRegistry.AddCustom(dialog.Result);
        MarkDirty();
        RefreshActions();
        ActionList.SelectedItem = _draftRegistry.Find(dialog.Result.Id);
        ShowInfo($"«{dialog.Result.Title}» añadida al borrador. Guarda para conservarla.");
    }

    private void OnEdit(object s, RoutedEventArgs e)
    {
        var action = ActionFrom(s);
        if (action == null) return;
        if (action.Category != ActionCategory.Custom)
        {
            ShowInfo("Las acciones de serie solo se activan o desactivan. Para cambiarlas, crea una propia desde «Añadir».");
            return;
        }

        var dialog = new ActionEditDialog(action) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        ApplyEdited(action, dialog.Result);
        MarkDirty();
        RefreshActions();
        ActionList.SelectedItem = action;
        ShowInfo($"«{action.Title}» actualizada en el borrador.");
    }

    /// <summary>Copia solo los campos editables; conserva Id, Order, categoría y reglas importadas.</summary>
    private static void ApplyEdited(ActionDefinition target, ActionDefinition source)
    {
        target.Title = source.Title;
        target.IsEnabled = source.IsEnabled;
        target.Kind = source.Kind;
        target.Output = source.Output;
        target.PromptTemplate = source.PromptTemplate;
        target.UrlTemplate = source.UrlTemplate;
        target.Script = source.Script;
    }

    private void OnDelete(object s, RoutedEventArgs e)
    {
        var action = ActionFrom(s);
        if (action == null) return;
        if (!_draftRegistry.RemoveCustom(action.Id))
        {
            ShowInfo("Solo se pueden eliminar acciones personalizadas. Las de serie se desactivan.");
            return;
        }
        MarkDirty();
        RefreshActions();
        ShowInfo($"«{action.Title}» se quitará al guardar. Si cierras sin guardar, no se aplicará.");
    }

    private void MoveSelected(int delta, ActionDefinition? action)
    {
        action ??= SelectedAction();
        if (action == null) return;

        if (action.Category != ActionCategory.Custom)
        {
            ShowInfo("Las acciones de serie no se reordenan: actívalas u ocúltalas, o crea una propia.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(FilterBox?.Text) || ActiveOnly?.IsChecked == true)
        {
            ShowInfo("Quita la búsqueda y «Solo activas» para cambiar el orden.");
            return;
        }

        var sorted = _draftRegistry.All.OrderBy(a => a.Order).ToList();
        int index = sorted.FindIndex(x => x.Id == action.Id);
        if (index < 0) return;
        int target = index + delta;
        if (target < 0 || target >= sorted.Count) return;

        (sorted[index].Order, sorted[target].Order) = (sorted[target].Order, sorted[index].Order);
        MarkDirty();
        RefreshActions();
        ActionList.SelectedItem = action;
    }

    private void OnMoveUp(object s, RoutedEventArgs e) => MoveSelected(-1, ActionFrom(s));
    private void OnMoveDown(object s, RoutedEventArgs e) => MoveSelected(1, ActionFrom(s));

    private void OnReset(object s, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Vuelve a las acciones de serie en el borrador: se perderán tus prompts y el orden personalizado.\n\n" +
            "Todavía no se aplica hasta que pulses «Guardar cambios».",
            "Restablecer acciones", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _draftRegistry = new ActionRegistry(null)
        {
            Profiles = _draft.Profiles,
            SmartSuggestEnabled = _draft.Triggers.SmartSuggest
        };
        MarkDirty();
        RefreshActions();
        ShowInfo("Acciones de serie restablecidas en el borrador. Guarda para aplicarlas.");
    }

    // ---------------- IA ----------------

    private string CurrentApiKey()
    {
        if (!string.IsNullOrEmpty(ApiKeyBox.Password)) return ApiKeyBox.Password;
        if (_clearKey) return "";
        return SettingsManager.Unprotect(Draft.Ai.ApiKeyEncrypted);
    }

    private void OnProviderChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_initialized || !_dirtyEnabled) return;
        var provider = ProviderBox.SelectedItem as string;
        if (string.IsNullOrEmpty(provider) || provider == "Personalizado") return;

        var knownUrls = Providers.All.Where(p => p != "Personalizado")
            .Select(Providers.BaseUrlFor)
            .Where(u => u.Length > 0)
            .ToList();
        var currentUrl = BaseUrlBox.Text.Trim();
        if (currentUrl.Length == 0 || knownUrls.Contains(currentUrl, StringComparer.OrdinalIgnoreCase))
            BaseUrlBox.Text = Providers.BaseUrlFor(provider);

        var defaults = Providers.All.Select(Providers.DefaultModelFor).ToList();
        if (string.IsNullOrWhiteSpace(ModelBox.Text) || defaults.Contains(ModelBox.Text.Trim()))
            ModelBox.Text = Providers.DefaultModelFor(provider);

        _ = LoadModelsAsync();
    }

    private void OnModelsRefresh(object s, RoutedEventArgs e)
    {
        _modelsLoadedOnce = true;
        _ = LoadModelsAsync();
    }

    private void OnResetSystemPrompt(object s, RoutedEventArgs e) => SystemPromptBox.Text = "";

    private void OnClearGroqKey(object s, RoutedEventArgs e)
    {
        GroqKeyBox.Clear();
        _clearGroqKey = true;
        UpdateKeyHints();
        MarkDirty();
    }

    private void OnOpenGroqKeys(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://console.groq.com/keys") { UseShellExecute = true }); }
        catch (Exception ex) { ShowSaveError("No se pudo abrir el navegador: " + ex.Message); }
    }

    private void OnClearKey(object s, RoutedEventArgs e)
    {
        ApiKeyBox.Clear();
        _clearKey = true;
        UpdateKeyHints();
        MarkDirty();
    }

    private void OnClearDecisionKey(object s, RoutedEventArgs e)
    {
        DecisionKeyBox.Clear();
        _clearDecisionKey = true;
        UpdateKeyHints();
        MarkDirty();
    }

    /// <summary>Trae GET {baseUrl}/models y rellena el combo (editable: se puede escribir a mano).</summary>
    private async Task LoadModelsAsync()
    {
        CancellationTokenSource? cts = null;
        try
        {
            // Cancelar la carga anterior sin disponerla aquí: su token puede
            // seguir en uso en su propio FetchModels; se dispone sola al terminar.
            _modelsCts?.Cancel();
            var fresh = new CancellationTokenSource();
            _modelsCts = cts = fresh;
            var ct = fresh.Token;

            var provider = ProviderBox.SelectedItem as string ?? "Personalizado";
            var baseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text)
                ? Providers.BaseUrlFor(provider)
                : BaseUrlBox.Text.Trim();
            var key = CurrentApiKey();
            var current = (ModelBox.Text ?? "").Trim();

            ModelsRefreshBtn.IsEnabled = false;
            ModelsStatus.Text = "Cargando modelos…";
            ModelsStatus.Foreground = Theme("TextTertiary");

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ModelsStatus.Text = "Indica la Base URL para cargar los modelos.";
                return;
            }

            string cacheKey = baseUrl + "|" + (string.IsNullOrEmpty(key) ? "" : "#");
            lock (ModelsCache)
            {
                if (ModelsCache.TryGetValue(cacheKey, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 5)
                {
                    ApplyModels(hit.Models, current);
                    ModelsStatus.Text = $"{hit.Models.Count} modelos disponibles (en caché).";
                    return;
                }
            }

            var models = await OpenAiCompatibleProvider.FetchModelsAsync(baseUrl, key, 15, ct);
            if (_closed || ct.IsCancellationRequested) return;

            lock (ModelsCache) { ModelsCache[cacheKey] = (DateTime.UtcNow, models); }
            ApplyModels(models, current);
            ModelsStatus.Text = $"{models.Count} modelos disponibles.";
            ModelsStatus.Foreground = Theme("TextTertiary");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed || (cts?.IsCancellationRequested == true)) return;
            ModelsStatus.Text = "No se pudo cargar la lista: " + ex.Message + " (escríbelo a mano)";
            ModelsStatus.Foreground = Theme("Danger");
        }
        finally
        {
            // El CTS de la carga que termina se dispone siempre: antes, si seguía
            // siendo el actual, quedaba vivo hasta que otra carga lo reemplazaba
            // (una fuga por cada apertura de Ajustes → IA). Si ya lo sustituyó
            // otra carga, se suelta aquí igualmente.
            if (cts != null)
            {
                if (ReferenceEquals(_modelsCts, cts)) _modelsCts = null;
                try { cts.Dispose(); } catch { }
            }
            if (!_closed) ModelsRefreshBtn.IsEnabled = true;
        }
    }

    private void ApplyModels(List<string> models, string current)
    {
        var wasEnabled = _dirtyEnabled;
        _dirtyEnabled = false; // rellenar la lista no es un cambio del usuario
        try
        {
            ModelBox.ItemsSource = models;
            // La petición puede tardar: si el usuario ya escribió otro modelo
            // mientras llegaba la lista, no pisar lo escrito con el valor viejo.
            string live = (ModelBox.Text ?? "").Trim();
            if (!string.Equals(live, current, StringComparison.Ordinal)) return;
            if (!string.IsNullOrEmpty(current) && models.Contains(current, StringComparer.Ordinal))
                ModelBox.SelectedItem = current;
            else
                ModelBox.Text = current; // conserva lo guardado/escrito aunque no esté en la lista
            ModelsStatus.Foreground = Theme("TextTertiary");
        }
        finally { _dirtyEnabled = wasEnabled; }
    }

    private void OnTest(object s, RoutedEventArgs e) => _ = TestConnectionAsync();

    private async Task TestConnectionAsync()
    {
        // Un «Probar» anterior no debe seguir vivo si este lo reemplaza; y al
        // cerrar la ventana OnClosed lo cancela (antes seguía hasta 45 s).
        try { _testCts?.Cancel(); } catch { }
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        TestBtn.IsEnabled = false;
        TestStatus.Text = "Probando…";
        TestStatus.Foreground = Theme("TextTertiary");

        try
        {
            var provider = ProviderBox.SelectedItem as string ?? "Personalizado";
            var baseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text)
                ? Providers.BaseUrlFor(provider)
                : BaseUrlBox.Text.Trim();
            var model = (ModelBox.Text ?? "").Trim();
            var key = CurrentApiKey();

            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Indica un modelo (p.ej. deepseek-v4.1-flash).");
            if (string.IsNullOrWhiteSpace(key) && !Providers.IsLocal(baseUrl))
                throw new InvalidOperationException("Falta la API key. Pégala arriba y pulsa Probar.");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Falta la Base URL.");

            using var client = new OpenAiCompatibleProvider(
                baseUrl, model, key, 30, Draft.Ai.GoSessionId, Draft.Ai.Temperature, 64);

            var answer = await client.CompleteAsync("Responde exactamente con: OK", "ping", cts.Token);
            if (_closed) return;
            var preview = answer.Length > 160 ? answer[..160] + "…" : answer;
            TestStatus.Text = $"OK · {preview}";
            TestStatus.Foreground = Theme("Success");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            TestStatus.Text = "Error: " + ex.Message;
            TestStatus.Foreground = Theme("Danger");
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts)) _testCts = null;
            try { cts.Dispose(); } catch { }
            if (!_closed) TestBtn.IsEnabled = true;
        }
    }

    // ---------------- Hotkey ----------------

    private void OnRecordHotkey(object s, RoutedEventArgs e)
    {
        _recording = true;
        HotkeyRecordBtn.Content = "Pulsa…";
        HotkeyDisplay.Text = "Pulsa la combinación…";
        HotkeyHint.Text = "Pulsa Ctrl/Alt/Shift/Win + una tecla. Esc para cancelar.";
        HotkeyHint.Foreground = Theme("AccentText");
        try { Focus(); System.Windows.Input.Keyboard.Focus(this); } catch { }
    }

    private void StopRecording()
    {
        _recording = false;
        HotkeyRecordBtn.Content = "Grabar";
        HotkeyDisplay.Text = HotkeyBox.Text;
        HotkeyHint.Text = "Funciona aunque desactives el aviso automático: selecciona texto y púlsala.";
        HotkeyHint.Foreground = Theme("TextTertiary");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            // Ctrl+S guarda sin cerrar (convención de Windows).
            if (e.Key == System.Windows.Input.Key.S &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                OnSave(this, new RoutedEventArgs());
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key == System.Windows.Input.Key.Escape)
        {
            StopRecording();
            return;
        }

        // Pulsaciones sueltas de modificador: seguimos esperando la tecla real.
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
            return;

        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (modifiers == System.Windows.Input.ModifierKeys.None)
        {
            HotkeyHint.Text = "Añade Ctrl, Alt, Shift o Win: si no, no es una hotkey global.";
            return;
        }

        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        uint mods = 0;
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0) mods |= Hotkeys.ModControl;
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) mods |= Hotkeys.ModShift;
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0) mods |= Hotkeys.ModAlt;
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0) mods |= Hotkeys.ModWin;

        var text = Hotkeys.Format(mods, (uint)vk);
        if (!Hotkeys.TryParse(text, out _, out _))
        {
            HotkeyHint.Text = "Esa tecla no vale como hotkey.";
            return;
        }

        HotkeyBox.Text = text;
        StopRecording();
    }

    private void OnResetHotkey(object s, RoutedEventArgs e)
    {
        HotkeyBox.Text = Hotkeys.DefaultHotkey;
        StopRecording();
    }

    // ---------------- Guardar ----------------

    private void OnSave(object s, RoutedEventArgs e) => TryApplyDraft();

    /// <summary>
    /// Valida todo el formulario, aplica el borrador y solo anuncia «Guardado» si
    /// settings.json se escribió. Ante cualquier fallo, la ventana sigue abierta y
    /// el estado vivo queda como estaba.
    /// </summary>
    private bool TryApplyDraft()
    {
        if (!TryApplyUiToDraft()) return false;

        bool encryptionChanged = App.SettingsMgr.Settings.History.Encrypt != _draft.History.Encrypt;
        if (!App.SettingsMgr.TryApply(_draft, out string saveError))
        {
            ShowSaveError("No se pudo guardar en disco: " + saveError +
                          " · Revisa los permisos de la carpeta de datos y vuelve a intentarlo.");
            return false;
        }

        bool autostartOk = true;
        bool applied = true;
        try
        {
            HistoryStore.Configure(App.SettingsMgr.Settings.History);
            if (encryptionChanged) HistoryStore.RewriteAll();
            Autostart.Set(_draft.StartWithWindows);
            autostartOk = Autostart.IsEnabled() == _draft.StartWithWindows;
            App.ReloadRuntime();
            Theming.SetMode(_draft.Theme);
            App.RefreshHotkey();
        }
        catch (Exception ex)
        {
            applied = false;
            Logger.LogEx(ex, "aplicar ajustes");
        }

        Logger.Log($"ajustes guardados · proveedor={_draft.Ai.Provider} modelo={_draft.Ai.Model} hotkey={_draft.Triggers.Hotkey}");

        // Rebase: el borrador vuelve a nacer de lo que acaba de quedar guardado.
        _clearKey = false;
        _clearGroqKey = false;
        _clearDecisionKey = false;
        ApiKeyBox.Clear();
        GroqKeyBox.Clear();
        DecisionKeyBox.Clear();
        BeginDraft();
        _dirty = false;
        _dirtyEnabled = false;
        LoadSettingsIntoUi(resetDisclosures: false);
        RefreshActions();
        _dirtyEnabled = true;
        UpdateDirtyUi();

        if (!autostartOk)
            ShowWarning("Guardado. Windows no confirmó el inicio automático; revísalo en Configuración → Aplicaciones de inicio.");
        else if (!applied)
            ShowWarning("Guardado en disco, pero algún cambio no se pudo aplicar en caliente. Reinicia Selbar si no lo ves.");
        else
            ShowSuccess("Guardado.");

        return true;
    }

    /// <summary>Lee la UI en el borrador tras validar TODO. Devuelve false sin tocar nada si algo no es válido.</summary>
    private bool TryApplyUiToDraft()
    {
        // ---- Validación de IA ----
        if (!SettingsDraft.TryParseDouble(TemperatureBox.Text, 0, 2, out double temperature))
            return Invalid("Temperatura: escribe un número entre 0 y 2 (por ejemplo 0,3).", TemperatureBox, 1, advanced: true);
        if (!SettingsDraft.TryParseInt(MaxTokensBox.Text, 0, 200_000, out int maxTokens))
            return Invalid("Máx. tokens: escribe un número entre 0 y 200000 (0 = el límite del proveedor).", MaxTokensBox, 1, advanced: true);
        if (!SettingsDraft.TryParseInt(TimeoutBox.Text, 5, 300, out int timeout))
            return Invalid("Timeout: escribe un número de segundos entre 5 y 300.", TimeoutBox, 1, advanced: true);
        if (!SettingsDraft.TryParseInt(MaxInputBox.Text, 1000, 200_000, out int maxInput))
            return Invalid("Máx. caracteres enviados: escribe un número entre 1000 y 200000.", MaxInputBox, 1, advanced: true);

        string provider = ProviderBox.SelectedItem as string ?? "Personalizado";
        string baseUrl = (BaseUrlBox.Text ?? "").Trim();
        if (baseUrl.Length == 0) baseUrl = Providers.BaseUrlFor(provider);
        string model = (ModelBox.Text ?? "").Trim();
        string lang = (LangBox.Text ?? "").Trim();
        string systemPrompt = (SystemPromptBox.Text ?? "").Trim();
        string transcriptionModel = (GroqModelBox.Text ?? "").Trim();
        if (transcriptionModel.Length == 0) transcriptionModel = "whisper-large-v3-turbo";

        // ---- Validación de atajos ----
        string hotkey = (HotkeyBox.Text ?? "").Trim();
        bool hotkeyEnabled = HotkeyEnabled.IsChecked == true;
        if ((hotkeyEnabled || hotkey.Length > 0) && !Hotkeys.TryParse(hotkey, out _, out _))
            return Invalid($"El atajo «{hotkey}» no es válido. Pulsa Grabar y elige otra combinación.", HotkeyRecordBtn, 2);

        string dictation = (DictationHotkeyBox.Text ?? "").Trim();
        if (dictation.Length > 0 && !Hotkeys.IsValid(dictation))
            return Invalid($"El atajo de dictado «{dictation}» no es válido. Usa Ctrl/Alt/Shift/Win + una tecla.", DictationHotkeyBox, 2);
        if (hotkeyEnabled && dictation.Length > 0 && string.Equals(dictation, hotkey, StringComparison.OrdinalIgnoreCase))
            return Invalid("El atajo de dictado es igual al de la barra. Elige otro para que no se peleen.", DictationHotkeyBox, 2);

        // ---- Validación de la barra ----
        if (!SettingsDraft.TryParseInt(MaxIconsBox.Text, 3, 15, out int maxIcons))
            return Invalid("Acciones visibles: escribe un número entre 3 y 15.", MaxIconsBox, 2);
        if (!SettingsDraft.TryParseInt(AutoHideBox.Text, 0, 600, out int autoHide))
            return Invalid("Ocultar tras X s: escribe entre 0 y 600 (0 = nunca).", AutoHideBox, 2);
        if (!SettingsDraft.TryParseInt(MinLengthBox.Text, 1, 500, out int minLength))
            return Invalid("Longitud mínima: escribe un número entre 1 y 500.", MinLengthBox, 2);
        if (!SettingsDraft.TryNormalizeExcluded(ExcludedBox.Text, out var excluded, out string excludedError))
            return Invalid(excludedError, ExcludedBox, 2);

        // ---- Validación de integraciones ----
        if (!SettingsDraft.TryParseDouble(SpeakRateBox.Text, 0.5, 3.0, out double speakRate))
            return Invalid("Velocidad de la voz: escribe un número entre 0,5 y 3 (1 = normal).", SpeakRateBox, 4);
        if (!SettingsDraft.TryValidateProfiles(ProfilesBox.Text, out string profileError))
            return Invalid(profileError, ProfilesBox, 4);

        // ---- Claves (DPAPI) antes de tocar nada ----
        string? newApiKey = null;
        if (!string.IsNullOrEmpty(ApiKeyBox.Password))
        {
            newApiKey = SettingsManager.Protect(ApiKeyBox.Password);
            if (newApiKey.Length == 0)
                return Invalid("No se pudo cifrar la API key con DPAPI. No se ha cambiado nada; vuelve a intentarlo.", ApiKeyBox, 1);
        }

        string? newGroqKey = null;
        if (!string.IsNullOrEmpty(GroqKeyBox.Password))
        {
            newGroqKey = SettingsManager.Protect(GroqKeyBox.Password);
            if (newGroqKey.Length == 0)
            {
                SetGroqExpanded(true);
                return Invalid("No se pudo cifrar la API key de Groq con DPAPI. No se ha cambiado nada; vuelve a intentarlo.", GroqKeyBox, 1);
            }
        }

        string? newDecisionKey = null;
        if (!string.IsNullOrEmpty(DecisionKeyBox.Password))
        {
            newDecisionKey = SettingsManager.Protect(DecisionKeyBox.Password);
            if (newDecisionKey.Length == 0)
                return Invalid("No se pudo cifrar la API key de Gemini con DPAPI. No se ha cambiado nada; vuelve a intentarlo.", DecisionKeyBox, 1);
        }

        // ---- Aplicación al borrador (ya no puede fallar) ----
        // Campos que este formulario no edita y que otras ventanas guardan por su
        // cuenta (ResultWindow persiste tamaño y fuente al redimensionar o hacer
        // zoom): se toman de lo vivo para que guardar Ajustes no los revierta.
        var live = App.SettingsMgr.Settings;
        _draft.ResultFontSize = live.ResultFontSize;
        _draft.ResultWidth = live.ResultWidth;
        _draft.ResultHeight = live.ResultHeight;
        _draft.Triggers.LongPressMs = live.Triggers.LongPressMs;
        _draft.History.MaxEntries = live.History.MaxEntries;
        // La marca de bienvenida la escribe App al cerrar esa ventana, no este
        // formulario: si se abrió Ajustes antes de cerrarla, el borrador la trae
        // en false y guardar haría que reapareciera.
        _draft.SeenWelcome = live.SeenWelcome;

        _draft.Ai.Provider = provider;
        _draft.Ai.BaseUrl = baseUrl;
        _draft.Ai.Model = model;
        _draft.Ai.TargetLang = lang.Length == 0 ? "español" : lang;
        _draft.Ai.SystemPrompt = systemPrompt;
        _draft.Ai.Temperature = temperature;
        _draft.Ai.MaxTokens = maxTokens;
        _draft.Ai.TimeoutSeconds = timeout;
        _draft.Ai.MaxInputChars = maxInput;
        _draft.Ai.TranscriptionModel = transcriptionModel;
        if (newApiKey != null) _draft.Ai.ApiKeyEncrypted = newApiKey;
        else if (_clearKey) _draft.Ai.ApiKeyEncrypted = "";
        if (newGroqKey != null) _draft.Ai.GroqApiKeyEncrypted = newGroqKey;
        else if (_clearGroqKey) _draft.Ai.GroqApiKeyEncrypted = "";
        if (newDecisionKey != null) _draft.Ai.DecisionApiKeyEncrypted = newDecisionKey;
        else if (_clearDecisionKey) _draft.Ai.DecisionApiKeyEncrypted = "";
        _draft.Ai.DecisionEnabled = DecisionToggle.IsChecked == true
            && !string.IsNullOrEmpty(_draft.Ai.DecisionApiKeyEncrypted);
        _draft.Ai.DecisionHideNoise = DecisionHide.IsChecked != false;

        var triggers = _draft.Triggers;
        triggers.AutoOnMouseSelection = AutoMouse.IsChecked == true;
        triggers.AutoOnKeyboardSelection = AutoKb.IsChecked == true;
        triggers.SelectionPeek = SelectionPeek.IsChecked != false;
        triggers.HotkeyEnabled = hotkeyEnabled;
        triggers.Hotkey = hotkey;
        triggers.MaxBarIcons = maxIcons;
        triggers.ShowNumbers = ShowNumbers.IsChecked == true;
        triggers.SearchOnType = SearchOnType.IsChecked == true;
        triggers.SmartSuggest = SmartSuggest.IsChecked == true;
        triggers.DigitShortcuts = DigitShortcuts.IsChecked == true;
        triggers.AutoHideSeconds = autoHide;
        triggers.MinSelectionLength = minLength;

        var history = _draft.History;
        history.Enabled = HistoryEnabled.IsChecked == true;
        history.Encrypt = HistoryEncrypt.IsChecked == true;
        history.ClearOnExit = HistoryClearOnExit.IsChecked == true;

        _draft.ExcludedApps = excluded;
        _draft.StartWithWindows = StartWindows.IsChecked == true;
        _draft.SpeakRate = speakRate;
        _draft.DictationHotkey = dictation;
        _draft.Profiles = ProfileRules.Parse(ProfilesBox.Text);
        _draft.ObsidianVault = (ObsidianVaultBox.Text ?? "").Trim();
        _draft.ObsidianFolder = (ObsidianFolderBox.Text ?? "").Trim();

        _draft.Theme = ThemeTag(ThemeBox.SelectedItem);
        _draft.DarkMode = _draft.Theme != "light";

        // Primer vault: activar la acción existente en el propio borrador, para que
        // el guardado la conserve (no se finge un efecto que no llega a settings.json).
        bool hadVault = !string.IsNullOrWhiteSpace(App.SettingsMgr.Settings.ObsidianVault);
        if (!hadVault && _draft.ObsidianVault.Length > 0)
            _draftRegistry.SetEnabled("sys.obsidian", true);

        _draftRegistry.Profiles = _draft.Profiles;
        _draftRegistry.SmartSuggestEnabled = triggers.SmartSuggest;
        _draft.ActionOverrides = _draftRegistry.All.ToList();

        SaveStatus.Visibility = Visibility.Collapsed;
        return true;
    }

    /// <summary>Muestra el error, abre la sección y enfoca el campo culpable.</summary>
    private bool Invalid(string message, System.Windows.Controls.Control? focus, int section, bool advanced = false)
    {
        SelectSectionByIndex(section);
        if (advanced && !_advancedExpanded) SetAdvancedExpanded(true);
        if (focus != null)
        {
            focus.Focus();
            if (focus is System.Windows.Controls.TextBox textBox) textBox.SelectAll();
        }
        ShowSaveError(message);
        return false;
    }

    private void SelectSectionByIndex(int index)
    {
        if (NavList.SelectedIndex != index) NavList.SelectedIndex = index;
        else ShowSection(index);
    }

    private void DiscardDraft()
    {
        _clearKey = false;
        _clearGroqKey = false;
        ApiKeyBox.Clear();
        GroqKeyBox.Clear();
        BeginDraft(cancelModelLoad: true);
        _dirtyEnabled = false;
        LoadSettingsIntoUi();
        RefreshActions();
        _dirtyEnabled = true;
        _dirty = false;
        UpdateDirtyUi();
        try { Theming.SetMode(App.SettingsMgr.Settings.Theme); } catch { }
        ShowInfo("Cambios descartados.");
    }

    private void ShowSaveError(string message)
    {
        SaveStatus.Text = message;
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.Foreground = Theme("Danger");
    }

    private void ShowWarning(string message)
    {
        SaveStatus.Text = message;
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.Foreground = Theme("Warning");
    }

    // ---------------- Datos e integraciones ----------------

    /// <summary>Selector de carpeta para el vault de Obsidian.</summary>
    private void OnPickObsidianVault(object s, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Elige la carpeta de tu vault de Obsidian",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            string current = (ObsidianVaultBox.Text ?? "").Trim();
            if (Directory.Exists(current)) dialog.SelectedPath = current;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            ObsidianVaultBox.Text = dialog.SelectedPath;
            MarkDirty();
        }
        catch (Exception ex)
        {
            ShowSaveError("No se pudo abrir el selector de carpetas: " + ex.Message);
        }
    }

    private void OnOpenDataFolder(object s, RoutedEventArgs e) => AppPaths.OpenInExplorer();

    private void OnOpenLog(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Logger.Path) { UseShellExecute = true }); }
        catch (Exception ex) { AdvancedStatus.Text = "No se pudo abrir el log: " + ex.Message; }
    }

    private void OnCopyDiagnostics(object s, RoutedEventArgs e)
    {
        var ai = Draft.Ai;
        var triggers = Draft.Triggers;
        var text = new StringBuilder()
            .AppendLine($"Selbar {App.Version}")
            .AppendLine($"SO: {Environment.OSVersion.VersionString} · .NET {Environment.Version}")
            .AppendLine($"Datos: {AppPaths.DataDir}")
            .AppendLine($"Proveedor: {ai.Provider} · Modelo: {ai.Model}")
            .AppendLine($"Base URL: {ai.BaseUrl}")
            .AppendLine($"Hotkey: {triggers.Hotkey} (activa: {triggers.HotkeyEnabled})")
            .AppendLine($"Acciones activas: {_draftRegistry.All.Count(a => a.IsEnabled)} de {_draftRegistry.All.Count}")
            .ToString();

        if (ClipboardService.TrySetText(text))
            AdvancedStatus.Text = "Diagnóstico copiado al portapapeles (sin claves).";
        else
            AdvancedStatus.Text = "No se pudo copiar al portapapeles.";
    }

    private void OnResetAll(object s, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Se borran TODOS los ajustes y las acciones personalizadas:\n" +
            "· claves de IA y Groq\n· prompts y orden de acciones\n· atajos, tema e integraciones\n\n" +
            "Se conserva el historial y no se repetirá la bienvenida. También se desactiva el inicio con Windows.\n\n¿Seguir?",
            "Restablecer Selbar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // Se construye el estado de fábrica y se persiste ANTES de aplicarlo:
        // si el disco falla, la app sigue con la configuración anterior.
        var factory = new AppSettings { SeenWelcome = true };
        if (!App.SettingsMgr.TryApply(factory, out string resetError))
        {
            ShowSaveError("No se pudo restablecer en disco: " + resetError);
            return;
        }

        bool applied = true;
        try
        {
            Theming.SetMode(App.SettingsMgr.Settings.Theme);
            Autostart.Set(false);
            App.ReloadRuntime();
            App.RefreshHotkey();
        }
        catch (Exception ex)
        {
            applied = false;
            Logger.LogEx(ex, "restablecer ajustes");
        }
        Logger.Log("ajustes restablecidos de fábrica");

        _clearKey = false;
        _clearGroqKey = false;
        ApiKeyBox.Clear();
        GroqKeyBox.Clear();
        BeginDraft(cancelModelLoad: true);
        _dirty = false;
        _dirtyEnabled = false;
        LoadSettingsIntoUi();
        RefreshActions();
        _dirtyEnabled = true;
        UpdateDirtyUi();
        if (applied) ShowSuccess("Ajustes restablecidos. El historial se conserva.");
        else ShowWarning("Ajustes restablecidos en disco, pero algún cambio no se pudo aplicar en caliente. Reinicia Selbar.");
    }

    // ---------------- Biblioteca / importar / exportar / historial ----------------

    private void OnOpenLibrary(object s, RoutedEventArgs e)
    {
        try
        {
            int before = _draftRegistry.All.Count;
            var dialog = new PromptLibraryWindow(_draftRegistry) { Owner = this };
            dialog.ShowDialog();
            RefreshActions();
            if (_draftRegistry.All.Count > before)
            {
                MarkDirty();
                ShowInfo("Biblioteca: acciones añadidas al borrador. Guarda para conservarlas.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "abrir biblioteca de prompts");
            ShowSaveError("No se pudo abrir la biblioteca: " + ex.Message);
        }
    }

    private void OnExportSettings(object s, RoutedEventArgs e)
    {
        if (_dirty && PromptsEnabled)
        {
            var answer = MessageBox.Show(this,
                "Se exportará la última versión guardada, no los cambios que aún no has guardado.\n\n¿Exportar de todos modos?",
                "Exportar ajustes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Ajustes de Selbar (*.json)|*.json",
            FileName = "selbar-ajustes.json",
            DefaultExt = ".json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            App.SettingsMgr.Export(dialog.FileName);
            AdvancedStatus.Text = "Ajustes guardados exportados (sin claves de IA ni Groq).";
        }
        catch (Exception ex) { AdvancedStatus.Text = "No se pudo exportar: " + ex.Message; }
    }

    private void OnImportSettings(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Ajustes de Selbar (*.json)|*.json|Todos los archivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var answer = MessageBox.Show(this,
            "Se reemplazarán todos los ajustes actuales y se descartarán los cambios sin guardar de esta ventana.\n" +
            "Las claves locales se conservan si el archivo no trae otras utilizables en este equipo.\n\n¿Seguir?",
            "Importar ajustes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var imported = SettingsManager.ReadSettingsFile(dialog.FileName);
            bool encryptionChanged = App.SettingsMgr.Settings.History.Encrypt != imported.History.Encrypt;
            // Persiste y solo entonces aplica: si el disco falla, la configuración
            // viva y el formulario se quedan como estaban (sin doble escritura).
            if (!App.SettingsMgr.TryAdopt(imported, out string saveError))
            {
                ShowSaveError("No se pudo escribir la importación: " + saveError + " · Se conserva la configuración anterior.");
                return;
            }

            bool applied = true;
            try
            {
                Theming.SetMode(App.SettingsMgr.Settings.Theme);
                Autostart.Set(App.SettingsMgr.Settings.StartWithWindows);
                App.ReloadRuntime();
                // Importar puede activar el cifrado del historial: las entradas que
                // ya existen no pueden quedarse en claro solo porque no se reescriban.
                HistoryStore.Configure(App.SettingsMgr.Settings.History);
                if (encryptionChanged) HistoryStore.RewriteAll();
                App.RefreshHotkey();
            }
            catch (Exception ex)
            {
                applied = false;
                Logger.LogEx(ex, "aplicar importación");
            }

            // Nada del formulario anterior (claves escritas, intenciones de borrado)
            // puede sobrevivir a la importación y pisar lo importado.
            _clearKey = false;
            _clearGroqKey = false;
            ApiKeyBox.Clear();
            GroqKeyBox.Clear();
            BeginDraft(cancelModelLoad: true);
            _dirty = false;
            _dirtyEnabled = false;
            LoadSettingsIntoUi();
            RefreshActions();
            _dirtyEnabled = true;
            UpdateDirtyUi();

            if (applied) ShowSuccess("Ajustes importados.");
            else ShowWarning("Ajustes importados en disco, pero algún cambio no se pudo aplicar en caliente. Reinicia Selbar.");
        }
        catch (Exception ex) { ShowSaveError("No se pudo importar: " + ex.Message); }
    }

    private void OnClearHistory(object s, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Borra todo el historial de resultados de forma irreversible. ¿Seguir?",
            "Historial", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        HistoryStore.Clear();
        bool cleared = !File.Exists(HistoryStore.FilePath);
        HistoryStatus.Text = cleared
            ? "Historial vaciado."
            : "No se pudo confirmar el borrado del historial. Revisa permisos y vuelve a intentarlo.";
        HistoryStatus.Foreground = Theme(cleared ? "Success" : "Danger");
    }

    private void ShowInfo(string message)
    {
        SaveStatus.Text = message;
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.Foreground = Theme("TextSecondary");
    }

    /// <summary>Solo para operaciones que ya quedaron aplicadas y persistidas.</summary>
    private void ShowSuccess(string message)
    {
        SaveStatus.Text = message;
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.Foreground = Theme("Success");
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();

    private void OnDiscard(object s, RoutedEventArgs e)
    {
        if (!_dirty) return;
        if (PromptsEnabled)
        {
            var answer = MessageBox.Show(this,
                "Se perderán los cambios que aún no has guardado. ¿Descartar?",
                "Selbar", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        DiscardDraft();
    }
}
