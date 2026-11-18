using System.Windows;
using System.Windows.Controls;
using Selbar.Core.Actions;

namespace Selbar.Ui;

/// <summary>
/// Editor de acciones personalizadas: prompt de IA, URL o comando. Trabaja sobre
/// una copia de la acción existente, así Cancelar nunca muta la acción original.
/// </summary>
public partial class ActionEditDialog : FluentWindow
{
    public ActionDefinition Result { get; }

    private const string TypeAi = "Ai";
    private const string TypeUrl = "Url";
    private const string TypeCommand = "Command";

    private bool _ready;
    private bool _variablesOpen;

    /// <summary>Acción importada con una salida que este editor no sabe expresar.</summary>
    private bool _preserveType;

    public ActionEditDialog(ActionDefinition? existing)
    {
        InitializeComponent();

        OutputBox.SelectedIndex = 0;
        TypeBox.SelectedIndex = 0;

        if (existing != null)
        {
            Result = Clone(existing);
            Title = "Editar acción";
            TitleBarTitle = "Editar acción";
            HeaderTitle.Text = "Editar acción";
            SaveButton.Content = "Guardar cambios";
            TitleBox.Text = existing.Title;
            EnabledBox.IsChecked = existing.IsEnabled;

            bool outputMatched = false;
            foreach (ComboBoxItem item in OutputBox.Items)
                if ((string)item.Tag == existing.Output.ToString())
                {
                    OutputBox.SelectedItem = item;
                    outputMatched = true;
                }

            // Un ajuste importado puede traer una salida que el editor no ofrece:
            // se conserva como opción propia en lugar de cambiarla en silencio.
            if (!outputMatched)
            {
                var preserved = new ComboBoxItem
                {
                    Content = existing.Output.ToString(),
                    Tag = existing.Output.ToString()
                };
                OutputBox.Items.Add(preserved);
                OutputBox.SelectedItem = preserved;
            }

            if (existing.Kind == ActionKind.NoAi && existing.Output == OutputMode.RunCommand)
            {
                TypeBox.SelectedIndex = 2;
                PromptBox.Text = existing.Script ?? "";
            }
            else if (existing.Kind == ActionKind.NoAi && existing.Output == OutputMode.Browser)
            {
                TypeBox.SelectedIndex = 1;
                PromptBox.Text = existing.UrlTemplate ?? "";
            }
            else
            {
                TypeBox.SelectedIndex = 0;
                PromptBox.Text = existing.PromptTemplate ?? "";

                // El editor solo sabe expresar prompt de IA, URL o comando. Una
                // acción importada con otra salida (copiar, simular tecla, abrir la
                // selección…) se conserva tal cual: sin esto, guardar la convertiría
                // en una llamada a la IA y enviaría el texto seleccionado.
                if (existing.Kind != ActionKind.Ai)
                {
                    _preserveType = true;
                    TypeBox.IsEnabled = false;
                    PromptBox.IsEnabled = false;
                    InsertTextBtn.IsEnabled = false;
                }
            }
        }
        else
        {
            Result = new ActionDefinition
            {
                Id = "custom." + Guid.NewGuid().ToString("N")[..8],
                Kind = ActionKind.Ai,
                Output = OutputMode.Popup,
                IsEnabled = true
            };
            EnabledBox.IsChecked = true;
            SaveButton.Content = "Crear acción";
        }

        _ready = true;
        ApplyType();

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>Copia todos los campos para no tocar la acción viva hasta aceptar.</summary>
    private static ActionDefinition Clone(ActionDefinition source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Category = source.Category,
        Kind = source.Kind,
        Output = source.Output,
        IsEnabled = source.IsEnabled,
        Order = source.Order,
        PromptTemplate = source.PromptTemplate,
        UrlTemplate = source.UrlTemplate,
        Script = source.Script,
        KeyName = source.KeyName,
        MinLength = source.MinLength,
        MaxLength = source.MaxLength,
        SingleWordOnly = source.SingleWordOnly,
        MultiWordOnly = source.MultiWordOnly,
        LongTextOnly = source.LongTextOnly,
        MatchRegex = source.MatchRegex,
        UrlEncode = source.UrlEncode,
        Description = source.Description
    };

    private string SelectedType =>
        (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? TypeAi;

    private void OnTypeChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // Si el usuario elige un tipo, el editor vuelve a mandar sobre la acción.
        _preserveType = false;
        TypeBox.IsEnabled = true;
        PromptBox.IsEnabled = true;
        InsertTextBtn.IsEnabled = true;
        ApplyType();
        HideError();
    }

    private void ApplyType()
    {
        if (_preserveType)
        {
            PromptLabel.Text = "Contenido de la acción";
            PromptBox.SpellCheck.IsEnabled = false;
            PromptBox.FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono");
            PromptHint.Text = "Esta acción importada no usa prompt de IA, URL ni comando: aquí solo cambias el título y si está activa.";
            WarningText.Visibility = Visibility.Collapsed;
            OutputSection.Visibility = Visibility.Collapsed;
            OutputSummaryText.Visibility = Visibility.Collapsed;
            VariablesToggle.Visibility = Visibility.Collapsed;
            VariablesText.Visibility = Visibility.Collapsed;
            return;
        }

        VariablesToggle.Visibility = Visibility.Visible;
        VariablesText.Visibility = _variablesOpen ? Visibility.Visible : Visibility.Collapsed;
        PromptBox.SpellCheck.IsEnabled = SelectedType == TypeAi;
        PromptBox.FontFamily = (System.Windows.Media.FontFamily)FindResource(
            SelectedType == TypeAi ? "FontText" : "FontMono");

        switch (SelectedType)
        {
            case TypeUrl:
                PromptLabel.Text = "URL del enlace";
                PromptHint.Text = "Dirección completa; si incluye {text}, se sustituye por la selección codificada. Ejemplo: https://ejemplo.com/buscar?q={text}";
                WarningText.Visibility = Visibility.Collapsed;
                OutputSection.Visibility = Visibility.Collapsed;
                OutputSummaryText.Visibility = Visibility.Visible;
                OutputSummaryText.Text = "Se abrirá en tu navegador. No se muestra ni se copia nada.";
                VariablesText.Text = "En una URL solo se sustituye {text}. {lang}, {date}, {time} y {app} no se aplican a este tipo.";
                break;

            case TypeCommand:
                PromptLabel.Text = "Comando local";
                PromptHint.Text = "Se ejecuta con cmd.exe y se muestra su salida. Si incluye {text}, se inserta la selección tal cual.";
                WarningText.Visibility = Visibility.Visible;
                OutputSection.Visibility = Visibility.Collapsed;
                OutputSummaryText.Visibility = Visibility.Visible;
                OutputSummaryText.Text = "Se ejecutará en tu equipo y se mostrará su salida. No hay botón de prueba: guardar no ejecuta nada.";
                VariablesText.Text = "En un comando solo se sustituye {text}, sin escapar comillas ni símbolos del shell. No se aplican {lang}, {date}, {time} ni {app}.";
                break;

            default:
                PromptLabel.Text = "Prompt para la IA";
                PromptHint.Text = "Escribe la instrucción e incluye {text} donde quieras la selección. Ejemplo: «Reescribe con tono cercano: {text}».";
                WarningText.Visibility = Visibility.Collapsed;
                OutputSection.Visibility = Visibility.Visible;
                OutputSummaryText.Visibility = Visibility.Collapsed;
                VariablesText.Text = "Variables disponibles: {text} (selección), {lang} (idioma destino de Ajustes), {date}, {time} y {app} (proceso en primer plano).";
                break;
        }
    }

    private void OnToggleVariables(object s, RoutedEventArgs e)
    {
        _variablesOpen = !_variablesOpen;
        VariablesText.Visibility = _variablesOpen ? Visibility.Visible : Visibility.Collapsed;
        VariablesChevron.Text = _variablesOpen ? "\uE70E" : "\uE70D";
    }

    private void OnInsertText(object s, RoutedEventArgs e)
    {
        string text = PromptBox.Text ?? "";
        // Si hay texto seleccionado, «Insertar {text}» lo sustituye en vez de
        // duplicarlo dejando la selección y añadiendo el token al lado.
        int start = Math.Clamp(PromptBox.SelectionStart, 0, text.Length);
        int length = Math.Clamp(PromptBox.SelectionLength, 0, text.Length - start);
        PromptBox.Text = text.Remove(start, length).Insert(start, "{text}");
        PromptBox.CaretIndex = start + "{text}".Length;
        PromptBox.Focus();
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        string type = SelectedType;
        string title = (TitleBox.Text ?? "").Trim();
        string body = (PromptBox.Text ?? "").Trim();

        if (title.Length == 0)
        {
            ShowError("Ponle un título para reconocerla en la barra.", TitleBox);
            return;
        }

        if (_preserveType)
        {
            // Título y visibilidad nada más: Kind, Output y plantillas se quedan
            // como estaban en la acción importada.
            Result.Title = title;
            Result.IsEnabled = EnabledBox.IsChecked == true;
            DialogResult = true;
            Close();
            return;
        }

        if (type == TypeAi && body.Length == 0)
        {
            ShowError("Escribe el prompt. Puedes usar «Insertar {text}» para colocar la selección.", PromptBox);
            return;
        }
        if (type == TypeAi && !body.Contains("{text}"))
        {
            ShowError("El prompt debe incluir {text} (la selección). Usa «Insertar {text}» si lo necesitas.", PromptBox);
            return;
        }
        if (type == TypeUrl)
        {
            if (body.Length == 0)
            {
                ShowError("Escribe la URL del enlace.", PromptBox);
                return;
            }
            if (!IsValidUrlTemplate(body))
            {
                ShowError("La URL no es válida: debe ser una dirección completa (https://…) sin espacios ni saltos de línea.", PromptBox);
                return;
            }
        }
        if (type == TypeCommand && body.Length == 0)
        {
            ShowError("Escribe el comando que se ejecutará.", PromptBox);
            return;
        }

        Result.Title = title;
        Result.IsEnabled = EnabledBox.IsChecked == true;

        switch (type)
        {
            case TypeUrl:
                Result.Kind = ActionKind.NoAi;
                Result.Output = OutputMode.Browser;
                Result.UrlTemplate = body;
                Result.PromptTemplate = null;
                Result.Script = null;
                break;

            case TypeCommand:
                Result.Kind = ActionKind.NoAi;
                Result.Output = OutputMode.RunCommand;
                Result.Script = body;
                Result.PromptTemplate = null;
                Result.UrlTemplate = null;
                break;

            default:
                Result.Kind = ActionKind.Ai;
                Result.Output = (OutputBox.SelectedItem as ComboBoxItem)?.Tag is string tag &&
                                Enum.TryParse<OutputMode>(tag, out var mode)
                    ? mode
                    : OutputMode.Popup;
                Result.PromptTemplate = body;
                Result.UrlTemplate = null;
                Result.Script = null;
                break;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>Comprueba la plantilla con un dato ficticio, sin abrir el navegador.</summary>
    private static bool IsValidUrlTemplate(string body)
    {
        if (body.Contains('\n') || body.Contains('\r') || body.Contains(' ')) return false;
        string sample = body.Replace("{text}", "texto-de-ejemplo", StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme);
    }

    private void ShowError(string message, System.Windows.Controls.Control? focus)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (focus == null) return;
        focus.Focus();
        if (focus is System.Windows.Controls.TextBox textBox) textBox.SelectAll();
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
    }

    private void OnCancel(object s, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            OnCancel(this, new RoutedEventArgs());
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
