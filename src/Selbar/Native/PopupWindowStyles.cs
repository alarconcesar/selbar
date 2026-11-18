using System.Windows;
using System.Windows.Interop;
using Selbar.Core.Diagnostics;

namespace Selbar.Native;

/// <summary>Estilos de los popups flotantes (barra, menú ···).</summary>
public static class PopupWindowStyles
{
    /// <summary>
    /// Convierte la ventana en "no activable": aparece por encima sin quitarle el
    /// foco a la app donde estabas escribiendo. Imprescindible para que Pegar /
    /// Cortar / Reemplazar actúen sobre la app de origen y no sobre el popup.
    /// (El resultado se aplica en SourceInitialized, cuando ya hay HWND.)
    /// </summary>
    public static void MakeNonActivating(Window window)
    {
        if (window == null) return;
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                // Si falla, el popup roba el foco y «Pegar» acaba en nuestro propio
                // popup: antes se descartaba el resultado sin dejar rastro.
                if (!NativeMethods.MakeNonActivating(hwnd))
                    Logger.Warn("popup: no se pudo marcar la ventana como no activable");
            }
            catch (Exception ex) { Logger.LogEx(ex, "popup no activable"); }
        };

        // Si la ventana ya tiene HWND (llamada después de Show), SourceInitialized
        // no volverá a dispararse y el popup se quedaría activable: robaría el foco.
        try
        {
            var existing = new WindowInteropHelper(window).Handle;
            if (existing != IntPtr.Zero && !NativeMethods.MakeNonActivating(existing))
                Logger.Warn("popup: no se pudo marcar la ventana como no activable (ya visible)");
        }
        catch (Exception ex) { Logger.LogEx(ex, "popup no activable"); }
    }
}
