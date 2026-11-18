using System.Diagnostics;
using System.IO;
using System.Windows;
using Selbar.Core.Actions;
using Selbar.Core.Ai;
using Selbar.Core.Diagnostics;
using Selbar.Core.History;
using Selbar.Core.Integrations;
using Selbar.Core.Downloads;
using Selbar.Core.Platform;
using Selbar.Core.Speech;
using Selbar.Core.Text;
using Selbar.Core.Web;
using Selbar.Native;

namespace Selbar.Ui;

/// <summary>
/// Ejecución compartida barra/overflow. Las acciones IA abren la ventana de
/// resultado al instante y la rellenan por streaming; el resto es inmediato.
/// Los refinamientos y las preguntas de seguimiento crean una versión nueva.
/// </summary>
public static class ActionRunner
{
    public static async Task ExecuteAsync(ActionDefinition action, string text)
    {
        App.ClosePopups();

        // Los invocadores hacen `_ = ExecuteAsync(...)`: una excepción aquí no la
        // observa nadie y la acción moriría en silencio, sin ventana ni log.
        try
        {
            if (action.Kind == ActionKind.NoAi)
                await RunLocalAsync(action, text);
            else
                await RunAiActionAsync(action, text);
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "acción " + action.Id);
            ShowError(action.Title, ex.Message);
        }
    }

    private static async Task RunLocalAsync(ActionDefinition action, string text)
    {
        // Ventanas propias para acciones que no son "texto → texto".
        if (action.Id == "text.styles")
        {
            new TextStyleWindow(text).Show();
            return;
        }

        if (action.Id == "text.qr")
        {
            string link = (text ?? "").Trim();
            if (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowError(action.Title, "La selección no es un enlace válido.");
                return;
            }
            new QrWindow(uri.AbsoluteUri).Show();
            return;
        }

        if (action.Id == "text.color")
        {
            if (!ColorCode.TryParse(text, out _))
            {
                ShowError(action.Title, "El texto no es un color HEX válido (#RGB, #RRGGBB o #RRGGBBAA).");
                return;
            }
            new ColorPreviewWindow(text).Show();
            return;
        }

        // Conversión de unidades (offline) o moneda (consulta tipos de cambio).
        if (action.Id == "text.convert")
        {
            string? converted = await CalcEngine.ConvertAsync(text);
            if (converted == null)
            {
                ShowError(action.Title, "No reconozco esa conversión. Prueba con «3 km a millas» o «15 usd a pen».");
                return;
            }
            new ResultWindow(action.Title, converted, OutputMode.Popup).Show();
            return;
        }

        // Leer en voz alta con las voces de Windows (no bloquea la UI).
        if (action.Id == "sys.speak")
        {
            string body = (text ?? "").Trim();
            if (body.Length == 0)
            {
                ShowError(action.Title, "No hay texto seleccionado.");
                return;
            }
            try
            {
                double rate = App.SettingsMgr.Settings.SpeakRate;
                await Task.Run(() => SpeechService.SpeakAsync(body, rate));
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "leer en voz alta");
                ShowError(action.Title, ex.Message);
            }
            return;
        }

        // Guardar una nota en el vault de Obsidian (local, sin red).
        if (action.Id == "sys.obsidian")
        {
            var cfg = App.SettingsMgr.Settings;
            string app = "";
            try { app = NativeMethods.ForegroundProcessName(); } catch { }
            var save = ObsidianSink.Save(cfg.ObsidianVault, cfg.ObsidianFolder, text, app);
            if (!save.Ok)
            {
                ShowError(action.Title, save.Message);
                return;
            }
            new ResultWindow(action.Title, save.Message, OutputMode.Popup).Show();
            return;
        }

        if (action.Id is "sys.youtube-video" or "sys.youtube-audio")
        {
            bool audio = action.Id.EndsWith("-audio", StringComparison.Ordinal);
            if (!YtDlp.TryNormalizeUrl(text, out _))
            {
                ShowError(action.Title, "La selección no es un enlace de YouTube válido.");
                return;
            }
            new DownloadWindow(text, audio).Show();
            return;
        }

        // Limpiar parámetros de seguimiento de un enlace (utm_*, fbclid, gclid…).
        if (action.Id == "link.clean")
        {
            string original = (text ?? "").Trim();
            string cleaned = LinkTools.CleanTracking(original);
            if (string.Equals(cleaned, original, StringComparison.Ordinal))
            {
                new ResultWindow(action.Title, "Ese enlace no tenía parámetros de seguimiento.", OutputMode.Popup).Show();
                return;
            }
            if (!ClipboardService.TrySetText(cleaned))
            {
                new ResultWindow(action.Title,
                    $"Enlace limpio (no se pudo copiar):\n\n{cleaned}",
                    OutputMode.Popup).Show();
                return;
            }
            new ResultWindow(action.Title,
                $"Enlace limpio (copiado):\n\n{cleaned}\n\nAntes: {original.Length} caracteres · ahora: {cleaned.Length}",
                OutputMode.Copy).Show();
            return;
        }

        // Ver a dónde lleva de verdad un enlace acortado.
        if (action.Id == "link.expand")
        {
            try
            {
                var (finalUrl, hops) = await LinkTools.ExpandAsync(text);
                ClipboardService.TrySetText(finalUrl);
                string detalle = hops switch
                {
                    0 => "Destino real",
                    1 => "Destino real (1 redirección)",
                    _ => "Destino real (" + hops + " redirecciones)"
                };
                new ResultWindow(action.Title, detalle + ":\n\n" + finalUrl, OutputMode.Copy).Show();
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "expandir enlace");
                ShowError(action.Title, ex.Message);
            }
            return;
        }

        // Abrir ubicación: archivo (lo selecciona) o carpeta en el Explorador.
        if (action.Id == "file.open")
        {
            string path = FilePathTools.Normalize(text);
            if (path.Length == 0)
            {
                ShowError(action.Title, "No hay ninguna ruta que abrir.");
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
                ClipboardService.TrySetText(path);
                new ResultWindow(action.Title,
                    $"Esa ruta no existe en este equipo (copiada igual):\n\n{path}",
                    OutputMode.Copy).Show();
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "abrir ubicación");
                ShowError(action.Title, "No se pudo abrir: " + ex.Message);
            }
            return;
        }

        ActionExecutor.Result result;
        try
        {
            result = await App.Executor.ExecuteAsync(action, text);
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "acción " + action.Id);
            result = new ActionExecutor.Result(false, action.Title, "", action.Output, ex.Message);
        }

        if (!result.Ok)
        {
            ShowError(action.Title, result.Error);
            return;
        }

        // Popup/Copy necesitan ventana para que se vea el resultado.
        // Replace, Cut, Paste, Browser... ya han hecho su trabajo sin molestar.
        if (result.Output is OutputMode.Popup or OutputMode.Copy)
            new ResultWindow(result.Title, result.Text, result.Output).Show();
    }

    private static async Task RunAiActionAsync(ActionDefinition action, string text)
    {
        text ??= string.Empty;
        // Resumir un enlace: se extrae el texto (HTML o PDF con OCR) y luego se resume.
        if (action.Id == "link.summarize")
        {
            string pageTitle;
            string pageText;
            try
            {
                var page = await WebPageReader.ReadAsync((text ?? "").Trim());
                pageTitle = string.IsNullOrWhiteSpace(page.Title)
                    ? (Uri.TryCreate(page.Url, UriKind.Absolute, out var u) ? u.Host : "enlace")
                    : page.Title!.Trim();
                pageText = page.Text ?? "";
            }
            catch (Exception ex)
            {
                Logger.LogEx(ex, "leer enlace");
                ShowError(action.Title, ex.Message);
                return;
            }

            if (pageText.Length < 200)
            {
                ShowError(action.Title,
                    "No pude extraer texto suficiente de esa página. Si es un PDF escaneado sin texto legible, " +
                    "descárgalo y usa tray → Leer texto de una imagen (OCR).");
                return;
            }

            action = new ActionDefinition
            {
                Id = "link.summarize.run",
                Title = "Resumen de " + pageTitle,
                Category = ActionCategory.AiExplain,
                Kind = ActionKind.Ai,
                Output = OutputMode.Popup,
                IsEnabled = true,
                PromptTemplate = $"Resume el siguiente contenido de «{pageTitle}» en 5 puntos clave en {{lang}}. " +
                                 "Al final añade una línea con la idea principal.\n\n{text}"
            };
        }

        // "Preguntar a la IA": prompt libre antes de ejecutar.
        if (action.Id == "ai.ask")
        {
            string? prompt = AskUser(text!);
            if (string.IsNullOrWhiteSpace(prompt)) return;

            action = new ActionDefinition
            {
                Id = "ai.ask.run",
                Title = "Pregunta",
                Category = ActionCategory.Custom,
                Kind = ActionKind.Ai,
                Output = action.Output,
                IsEnabled = true,
                PromptTemplate = prompt.Trim() + "\n\n{text}"
            };
        }

        // El diff tiene sentido en reescrituras y traducciones (comparar con el original),
        // no en resúmenes o explicaciones donde todo el texto cambia.
        bool canDiff = !string.IsNullOrWhiteSpace(text) &&
                       (action.Output == OutputMode.Replace ||
                        action.Category is ActionCategory.AiRewrite or ActionCategory.AiTranslate);
        var win = new ResultWindow(action.Title, "", action.Output, streaming: true,
            sourceText: canDiff ? text : null);
        win.SetRefinable(true);
        win.Show();

        var turns = new TurnCancellation();

        // Último turno, para reintentar exactamente lo mismo como versión nueva.
        string? lastInstruction = null;
        string? lastInput = null;
        string lastLabel = "";
        int lastVersion = 1;
        int lastTotal = 1;

        win.Closed += (_, _) => turns.Cancel();
        win.StopRequested += (_, _) => turns.Cancel();

        win.RetryRequested += async () =>
        {
            turns.Cancel();

            // Una sola BeginVersion, aquí; el streaming no vuelve a limpiar.
            string label = lastInstruction == null
                ? "Reintento"
                : "Reintento · " + (lastLabel.Length == 0 ? "Pregunta" : lastLabel);
            win.BeginVersion(label);

            if (lastInstruction == null)
            {
                await StreamFirstAsync(win, action, text!, turns);
                win.EnableChat();
                return;
            }

            await StreamReplyAsync(win, action, text!, lastInput ?? "", lastVersion, lastTotal,
                lastInstruction, turns);
            win.EnableChat();
        };

        win.ChatSendRequested += async userText =>
        {
            if (string.IsNullOrWhiteSpace(userText)) return;

            // Contexto = texto que el usuario está viendo (edición o versión elegida),
            // no la última respuesta guardada en el historial.
            string visible = win.CurrentText;
            int version = win.VersionNumber;
            int total = win.VersionCount;
            lastInstruction = userText;
            lastInput = visible;
            lastLabel = "Pregunta";
            lastVersion = version;
            lastTotal = total;

            win.BeginVersion("Pregunta");
            var turn = await StreamReplyAsync(win, action, text!, visible, version, total, userText, turns);
            // La pregunta solo vuelve al campo si el turno no produjo texto: con una
            // respuesta parcial reenviarla duplicaría el turno.
            if (!turn.Ok && !turn.ProducedText) win.RestoreChatDraft(userText);
        };

        win.RefineRequested += async instruction =>
        {
            if (string.IsNullOrWhiteSpace(instruction)) return;

            string visible = win.CurrentText;
            int version = win.VersionNumber;
            int total = win.VersionCount;
            lastInstruction = instruction;
            lastInput = visible;
            lastLabel = ResultTurn.ShortLabel(instruction);
            lastVersion = version;
            lastTotal = total;

            win.BeginVersion(lastLabel);
            await StreamReplyAsync(win, action, text!, visible, version, total, instruction, turns);
        };

        await StreamFirstAsync(win, action, text!, turns);
        win.EnableChat();
    }

    /// <summary>Un turno de ajuste/pregunta sobre el texto visible, sin respuestas obsoletas.</summary>
    private static async Task<TurnResult> StreamReplyAsync(ResultWindow win, ActionDefinition action, string originalText,
        string visibleText, int version, int total, string instruction, TurnCancellation turns)
    {
        var token = turns.BeginNew();
        win.BeginStreaming(App.SettingsMgr.Settings.Ai.Model);

        // Se cuenta lo recibido para saber si el turno llegó a producir texto
        // (restaurar la pregunta o no depende de eso).
        int produced = 0;
        void OnToken(string t)
        {
            if (!string.IsNullOrEmpty(t)) produced += t.Length;
            win.AppendToken(t);
        }

        try
        {
            var messages = ResultTurn.BuildMessages(
                App.Executor.SystemPrompt,
                App.Executor.BuildPrompt(action, originalText),
                visibleText,
                version,
                total,
                instruction)
                .Select(m => new ChatMessage(m.Role, m.Content))
                .ToList();

            await App.Executor.RunChatStreamingAsync(messages, OnToken, token);
            win.FinishSuccess();
            return new TurnResult(true, produced > 0);
        }
        catch (OperationCanceledException)
        {
            win.FinishCancelled();
            return new TurnResult(false, produced > 0);
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "IA chat");
            win.FinishError(ex.Message);
            return new TurnResult(false, produced > 0);
        }
    }

    /// <summary>Un turno: si terminó bien y si llegó a producir texto.</summary>
    private readonly record struct TurnResult(bool Ok, bool ProducedText);

    /// <summary>Pregunta libre con el hilo UI garantizado (el hook llama desde fondo).</summary>
    private static string? AskUser(string selection)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return null;

        if (dispatcher.CheckAccess()) return ShowAskDialog(selection);
        return dispatcher.Invoke(() => ShowAskDialog(selection));
    }

    private static string? ShowAskDialog(string selection)
    {
        try
        {
            var dialog = new AskDialog(selection);
            try
            {
                var owner = System.Windows.Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible && owner != dialog) dialog.Owner = owner;
            }
            catch { }
            return dialog.ShowDialog() == true ? dialog.Prompt : null;
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "preguntar a la IA");
            return null;
        }
    }

    private static async Task<string> StreamFirstAsync(ResultWindow win, ActionDefinition action, string text,
        TurnCancellation turns)
    {
        var token = turns.BeginNew();
        win.BeginStreaming(App.SettingsMgr.Settings.Ai.Model);
        try
        {
            string final = await App.Executor.RunAiStreamingAsync(
                action, text, win.AppendToken, token);

            string note = "";
            if (action.Output is OutputMode.Copy or OutputMode.Replace)
            {
                // Texto plano, como «Copiar» y «Reemplazar» en la ventana: pegar
                // markdown (**negritas**, #, listas) en la app de origen se ve literal.
                string plain = MarkdownRenderer.ToPlainText(final);
                if (plain.Length == 0) plain = final;
                if (ClipboardService.TrySetText(plain))
                    note = action.Output == OutputMode.Copy ? "copiado" : "listo para reemplazar";
                else
                    note = "no se pudo copiar al portapapeles";
            }

            try
            {
                // Leer y reescribir history.json (con DPAPI si el historial está
                // cifrado) puede tardar con 50 entradas: fuera del hilo UI para que
                // «Listo» no llegue con la ventana congelada.
                string model = App.SettingsMgr.Settings.Ai.Model;
                await Task.Run(() => HistoryStore.Add(action.Id, action.Title, model, text, final));
            }
            catch { }

            win.FinishSuccess(note);
            return final;
        }
        catch (OperationCanceledException)
        {
            win.FinishCancelled();
            return "";
        }
        catch (Exception ex)
        {
            Logger.LogEx(ex, "IA " + action.Id);
            win.FinishError(ex.Message);
            return "";
        }
    }

    public static void ShowError(string title, string? message)
    {
        try
        {
            var win = new ResultWindow("Error en " + title, "", OutputMode.Popup);
            win.Show();
            // Estado de error real: sin Reintentar ni Ajustes de IA si no hay
            // operación conectada. El detalle se copia desde el propio aviso.
            win.SetLocalError(string.IsNullOrWhiteSpace(message) ? "Error desconocido" : message!);
        }
        catch (Exception ex) { Logger.LogEx(ex, "mostrar error"); }
    }

    /// <summary>Un turno de IA a la vez: cancelar el anterior al empezar uno nuevo.</summary>
    private sealed class TurnCancellation
    {
        private CancellationTokenSource _source = new();

        public void Cancel()
        {
            try { _source.Cancel(); } catch { }
        }

        public CancellationToken BeginNew()
        {
            Cancel();
            _source = new CancellationTokenSource();
            return _source.Token;
        }
    }
}
