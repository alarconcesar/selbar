using Selbar.Core.Text;
using Selbar.Core.Web;

namespace Selbar.Core.Actions;

/// <summary>
/// Qué tipo de texto es y qué acción conviene primero. Todo local y rápido:
/// si no hay una señal clara, no adivina (devuelve null y la barra va normal).
/// Prioridad: correo > ruta > fecha > acortador > enlace con seguimiento.
/// </summary>
public static class TextSuggest
{
    public sealed record Suggestion(string ActionId, string Reason);

    /// <summary>
    /// Devuelve la acción sugerida con su motivo, o null si nada destaca.
    /// No lanza nunca: ante la duda, null.
    /// </summary>
    public static Suggestion? Suggest(string? text)
    {
        try
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0 || t.Length > 2000 || t.Contains('\n')) return null;

            if (EmailTools.IsEmail(t))
                return new Suggestion("contact.email", "Parece un correo");

            if (FilePathTools.IsFilePath(t))
                return new Suggestion("file.open", "Parece una ruta de archivo");

            if (DateTools.LooksLikeDate(t))
                return new Suggestion("date.info", "Parece una fecha");

            if (LinkTools.IsShortener(t))
                return new Suggestion("link.expand", "Enlace acortado");

            if (LinkTools.HasTracking(t))
                return new Suggestion("link.clean", "Tiene seguimiento");

            return null;
        }
        catch
        {
            return null;
        }
    }
}
