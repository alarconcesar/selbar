using System.IO;
using Microsoft.Win32;

namespace Selbar.Core.Platform;

/// <summary>Arranque con Windows vía HKCU\...\Run (no requiere admin).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultName = "Selbar";

    /// <summary>
    /// ¿Está activado el arranque automático? Solo cuenta como activado si la
    /// entrada apunta al ejecutable actual: tras mover la carpeta, actualizar el
    /// ZIP o desinstalar/reinstalar en otra ruta, Windows seguía lanzando una
    /// ruta muerta mientras Ajustes mostraba la casilla marcada.
    /// </summary>
    public static bool IsEnabled(string name = DefaultName, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key?.GetValue(name) is not string stored || stored.Length == 0) return false;

            string expected = Normalize(exePath ?? CurrentExePath());
            if (expected.Length == 0) return true; // sin ruta de referencia: vale cualquier entrada
            return string.Equals(Normalize(stored), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Activa o desactiva el arranque con Windows. Devuelve true solo si el
    /// registro quedó como se pidió: antes era void y un fallo (clave ilegible,
    /// política de grupo, ruta vacía) se veía en la UI como «guardado».
    /// </summary>
    public static bool Set(bool enabled, string? exePath = null, string name = DefaultName)
    {
        try
        {
            if (enabled)
            {
                string path = ExecutablePathToken(exePath ?? CurrentExePath());
                path = Environment.ExpandEnvironmentVariables(path);
                try { if (path.Length > 0 && !Path.IsPathRooted(path)) path = Path.GetFullPath(path); } catch { }
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    Diagnostics.Logger.Warn("autostart: no hay un .exe válido que registrar (" + path + ")");
                    return false;
                }
                // CreateSubKey: la clave Run puede no existir en un perfil recién
                // creado y OpenSubKey(..., true) devolvía null sin avisar.
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (run == null) return false;
                string value = "\"" + path + "\"";
                run.SetValue(name, value, RegistryValueKind.String);
                // SetValue no devuelve nada: releer es la única forma de saber que
                // el registro quedó como se pidió (políticas, vista WOW64, etc.).
                return string.Equals(run.GetValue(name) as string, value, StringComparison.Ordinal);
            }

            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (run?.GetValue(name) == null) return true; // ya estaba desactivado
                run.DeleteValue(name, false);
                // Igual que al activar: confirmar que la entrada desapareció.
                return run.GetValue(name) == null;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Logger.LogEx(ex, "autostart");
            return false;
        }
    }

    /// <summary>.exe que corresponde registrar (la app, no el host de `dotnet run`).</summary>
    private static string CurrentExePath()
    {
        try
        {
            var path = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(path) &&
                Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(asm)) path = Path.ChangeExtension(asm, ".exe");
            }
            return path;
        }
        catch { return ""; }
    }

    /// <summary>Quita comillas y variables de entorno para poder comparar rutas.</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            string path = Environment.ExpandEnvironmentVariables(ExecutablePathToken(raw));
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return ExecutablePathToken(raw); }
    }

    /// <summary>
    /// Extrae la ruta del ejecutable de un valor de la clave Run: quita las
    /// comillas y, si el valor lleva argumentos («"C:\...\Selbar.exe"
    /// --tray»), se queda solo con el .exe. Windows acepta argumentos ahí; antes
    /// esa entrada se veía como desactivada aunque apuntara a esta misma app.
    /// </summary>
    private static string ExecutablePathToken(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }

        // Sin comillas, lo que venga después del .exe son argumentos.
        // LastIndexOf para no cortar en una carpeta llamada «algo.exe».
        int exe = s.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 && exe + 4 < s.Length ? s[..(exe + 4)] : s;
    }
}
