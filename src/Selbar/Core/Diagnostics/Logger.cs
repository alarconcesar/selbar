using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Selbar.Core.Platform;

namespace Selbar.Core.Diagnostics;

/// <summary>
/// Log en la carpeta de datos (log.txt). Se rota solo al pasar de tamaño,
/// tanto al arrancar como durante la sesión, para que no crezca sin control.
/// Nunca lanza: un fallo de log no puede tumbar la app. Además tapa claves
/// (API keys, tokens, Authorization) antes de escribir: el log se comparte en
/// los informes de error y no debe filtrar credenciales.
/// </summary>
public static class Logger
{
    private const long MaxBytes = 512 * 1024;

    /// <summary>Cada cuántos bytes escritos se comprueba el tamaño real del fichero.</summary>
    private const long CheckEveryBytes = MaxBytes / 8;

    /// <summary>Tope de una sola línea; un volcado enorme no debe inundar el log.</summary>
    private const int MaxMessageChars = 8000;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static long _bytesSinceCheck;

    /// <summary>Ruta del log.</summary>
    public static string Path => AppPaths.DataFile("log.txt");

    public static void Log(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Error con contexto (acción, módulo...). Incluye las excepciones internas.</summary>
    public static void LogEx(Exception ex, string context)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(context).Append("] ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            var inner = ex.InnerException;
            for (int depth = 0; inner != null && depth < 3; depth++, inner = inner.InnerException)
                sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            Write("ERROR", sb.ToString());
        }
        catch { }
    }

    /// <summary>Recorta el fichero a los últimos <paramref name="maxBytes"/> (conserva líneas enteras).</summary>
    public static void RotateFile(string path, long maxBytes = MaxBytes)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= maxBytes) return;

                // Se trabaja en BYTES, no en caracteres: con acentos y emoji un
                // recorte «de maxBytes caracteres» dejaba el fichero al doble
                // del tamaño prometido.
                byte[] all = File.ReadAllBytes(path);
                int keep = (int)Math.Min(all.Length, Math.Max(4096, maxBytes));
                int start = all.Length - keep;
                while (start < all.Length && all[start] != (byte)'\n') start++;
                if (start >= all.Length) start = all.Length - keep; // una sola línea gigante
                File.WriteAllBytes(path, all[start..]);
                _bytesSinceCheck = 0;
            }
        }
        catch { }
    }

    private static void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}{Environment.NewLine}";
            int bytes = Utf8NoBom.GetByteCount(line);

            lock (Gate)
            {
                // Comprobación por bytes acumulados, no «cada 128 líneas»: una
                // excepción con un volcado grande pasaba entre dos comprobaciones
                // y el log se quedaba muy por encima del tope hasta la siguiente.
                if (_bytesSinceCheck + bytes >= CheckEveryBytes)
                {
                    RotateFile(Path, MaxBytes);
                    _bytesSinceCheck = 0;
                }
                File.AppendAllText(Path, line, Utf8NoBom);
                _bytesSinceCheck += bytes;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ privacidad

    /// <summary>«clave = valor» y cabeceras Authorization.</summary>
    private static readonly Regex SecretValue = new(
        @"(?<prefix>\b(?:api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|token|authorization|password|passwd|secret)\b\s*[=:]\s*(?:Bearer\s+)?|\bBearer\s+)(?<secret>[^\s""',;]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Claves con forma de credencial aunque no lleven etiqueta.</summary>
    private static readonly Regex KeyLike = new(
        @"\b(?:sk|gsk|xai|pk|rk|hf)[-_][A-Za-z0-9_\-]{10,}\b",
        RegexOptions.Compiled);

    private static string Redact(string message)
    {
        try
        {
            if (message.Length > MaxMessageChars)
                message = message[..MaxMessageChars] + "… (recortado)";
            message = SecretValue.Replace(message, m => m.Groups["prefix"].Value + "***");
            message = KeyLike.Replace(message, "***");
            return message;
        }
        catch { return message; }
    }
}
