using System.IO;

namespace Selbar.Core.Platform;

/// <summary>
/// Dónde viven los datos de la app. Por orden:
/// 1) variable de entorno <c>SELBAR_DATA</c> (tests/perfiles aislados),
/// 2) modo portable: si junto al .exe hay un <c>portable.txt</c>, se usa <c>.\data</c>,
/// 3) lo normal: <c>%AppData%\Selbar</c>.
/// Si el sitio elegido no es escribible (p. ej. Program Files), se cae a AppData.
/// </summary>
public static class AppPaths
{
    /// <summary>Marca que activa el modo portable (se incluye en el zip portable).</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>Carpeta de datos ya creada, absoluta y escribible.</summary>
    public static string DataDir { get; } = Resolve();

    /// <summary>Ruta completa de un fichero dentro de la carpeta de datos.</summary>
    public static string DataFile(string fileName)
    {
        string name = (fileName ?? "").Trim();
        if (name.Length == 0) name = "datos";
        try
        {
            string full = Path.GetFullPath(Path.Combine(DataDir, name));
            // Nunca se devuelve una ruta que se salga de la carpeta de datos: un
            // nombre con «..» escribía fuera (y el log acabaría en cualquier sitio).
            if (full.StartsWith(DataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
            return InsideFallback(name);
        }
        catch { return InsideFallback(name); }
    }

    /// <summary>
    /// Último recurso para un nombre rechazado: solo la hoja, dentro de DataDir.
    /// «.» y «..» no son hojas válidas (Path.Combine(DataDir, "..") apunta al
    /// padre aunque el texto empiece por DataDir).
    /// </summary>
    private static string InsideFallback(string name)
    {
        string leaf = Path.GetFileName(name);
        if (leaf.Length == 0 || leaf == "." || leaf == "..") leaf = "datos";
        return Path.Combine(DataDir, leaf);
    }

    /// <summary>Abre la carpeta de datos en el Explorador.</summary>
    public static void OpenInExplorer()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static string Resolve()
    {
        // 1) Anulación explícita (útil para tests y perfiles aislados).
        var env = Environment.GetEnvironmentVariable("SELBAR_DATA")
            ?? Environment.GetEnvironmentVariable("POWERTOOLS_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            // Una ruta relativa se resolvía contra el directorio de trabajo, que en
            // una app de bandeja es cualquiera (System32 si arranca con Windows).
            string candidate = env.Trim();
            try
            {
                if (!Path.IsPathRooted(candidate))
                    candidate = Path.Combine(AppContext.BaseDirectory, candidate);
            }
            catch { }
            if (TryPrepare(candidate, out var fromEnv)) return fromEnv;
        }

        // 2) A la vera del ejecutable: portable.
        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, PortableMarker)) &&
                TryPrepare(Path.Combine(exeDir, "data"), out var portable))
                return portable;
        }
        catch { }

        // 3) Perfil del usuario.
        // Compatibilidad con instalaciones antiguas de PowerTools: si no existe
        // la carpeta nueva pero sí la vieja, se conservan ajustes, claves e historial.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Selbar");
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerTools");
        if (!Directory.Exists(appData) && Directory.Exists(legacy)) appData = legacy;
        if (TryPrepare(appData, out var fromAppData)) return fromAppData;

        // 4) Último recurso: temp (nunca debe impedir arrancar). Se usa la
        // subcarpeta propia incluso si la prueba de escritura falló: dejar los
        // datos en la raíz de %TEMP% los mezclaba con los de cualquier programa.
        var temp = Path.Combine(Path.GetTempPath(), "Selbar");
        if (TryPrepare(temp, out var fromTemp)) return fromTemp;

        return temp;
    }

    /// <summary>Crea la carpeta y comprueba que se puede escribir de verdad.</summary>
    private static bool TryPrepare(string dir, out string full)
    {
        full = "";
        try
        {
            full = Path.GetFullPath(dir);
            Directory.CreateDirectory(full);

            // Nombre único por intento: con un ".write-test" fijo, dos instancias
            // arrancando a la vez (o un antivirus leyendo el archivo) se peleaban
            // por él y la perdedora caía a %TEMP%, con ajustes e historial distintos.
            var probe = Path.Combine(full, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.WriteByte(0);
            }
            try { File.Delete(probe); } catch { /* si no se puede borrar, la carpeta ya se validó */ }
            return true;
        }
        catch
        {
            full = "";
            return false;
        }
    }
}
