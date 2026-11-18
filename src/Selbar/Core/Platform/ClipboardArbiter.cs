namespace Selbar.Core.Platform;

/// <summary>
/// Coordina las copias que hace el usuario desde la interfaz (ClipboardService)
/// con el ciclo automático Clear → Ctrl+C → restaurar de TextExtractor.
///
/// Sin esto, si el usuario pulsaba «Copiar» mientras la extracción esperaba a que
/// la app de origen respondiera al Ctrl+C, la extracción leía el texto recién
/// copiado por el usuario como si fuera la selección y después lo pisaba al
/// restaurar el portapapeles anterior.
/// </summary>
public static class ClipboardArbiter
{
    private static long _userWrites;

    /// <summary>Número de copias solicitadas por el usuario. Monótono.</summary>
    public static long UserWriteCount => Interlocked.Read(ref _userWrites);

    /// <summary>Marca que el usuario quiere copiar algo AHORA.</summary>
    public static void NotifyUserWrite() => Interlocked.Increment(ref _userWrites);
}
