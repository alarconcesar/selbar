# Decisiones de diseño (ADR breves)

Registro de decisiones que no son obvias al leer el código. Si vas a cambiar una,
lee el contexto y actualiza este archivo.

---

## ADR-001 · Indicador en lugar de barra automática

**Contexto:** al seleccionar texto, una barra completa que capturaba la escritura
impedía reemplazar la selección con naturalidad.

**Decisión (0.9.0):** el flujo automático muestra un indicador (`peek`) que no
captura el teclado. La barra completa se abre con hover (180 ms, movimiento real),
clic o hotkey. La captura de teclado sólo existe cuando `AcceptsTyping` es true.

**Consecuencias:** hay dos modos de la misma ventana (`BuildPeek`/`Expand`); el
modo búsqueda se activa deliberadamente. Cualquier tecla normal con el indicador
visible se devuelve a la app y cierra el popup. No romper esta regla.

---

## ADR-002 · Popups no activables

**Contexto:** activar la barra quitaba el foco a la app y rompía pegar/reemplazar.

**Decisión:** `WS_EX_NOACTIVATE` para barra, overflow y popups. El foco de origen
se captura con `ForegroundTracker` y se restaura antes de pegar/reemplazar.

**Consecuencias:** los popups no reciben teclado por WPF; el modo búsqueda necesita
un hook global. No usar `Activate()` ni `Focus()` para “arreglar” el foco.

---

## ADR-003 · Superficies sólidas y Mica desactivado

**Contexto:** con `WindowChrome` + WPF, Mica se veía negro en la barra de título y
en las esquinas; el contenido transparente no compone alfa de forma fiable.

**Decisión:** `Window.Background = Transparent`; `PART_Surface` pinta el color del
tema y se recorta con radio 8; DWM dibuja el marco, el borde y las esquinas. Mica
no se aplica. `WindowChrome` no usa `CornerRadius` como sustituto del clip.

**Consecuencias:** se pierde el material Mica, pero las ventanas son consistentes
en claro/oscuro y no muestran fondos negros. No reactivar Mica sin probar en una
ventana real; `RenderTargetBitmap` no captura DWM.

---

## ADR-004 · Tests offline obligatorios

**Contexto:** los tests no deben gastar tokens, depender de red ni filtrar claves.

**Decisión:** `tools/SelfTest` fija `SELBAR_OFFLINE=1`; los proveedores y Groq
lanzan error si se intenta red. El smoke de UI no hace peticiones.

**Consecuencias:** las integraciones se prueban con validaciones, dobles y modo
offline. Un test que necesite red no es aceptable en la suite.

---

## ADR-005 · Claves con DPAPI y exportación sin secretos

**Contexto:** las claves de IA/Groq no deben quedar legibles ni viajar al exportar
ajustes.

**Decisión:** `ApiKeyEncrypted` y `GroqApiKeyEncrypted` se cifran con DPAPI
(CurrentUser). `Export` las vacía por defecto; `Adopt` conserva las locales si el
archivo importado no las trae.

**Consecuencias:** el archivo exportado se puede compartir sin secretos. Si se
añade otro secreto, aplicar el mismo patrón y actualizar tests.

---

## ADR-006 · QRCoder como única dependencia funcional

**Contexto:** el QR debía funcionar offline y sin servicios.

**Decisión:** QRCoder 1.6.0 (MIT) incluido en el ZIP; no se añaden frameworks de
UI ni paquetes de iconos.

**Consecuencias:** el ZIP incluye `QRCoder.dll`; el resto de iconos son Segoe
Fluent Icons. Cualquier dependencia nueva debe justificarse y actualizar tamaño,
créditos y pruebas.

---

## ADR-007 · yt-dlp y ffmpeg no se empaquetan

**Contexto:** yt-dlp cambia con frecuencia y ffmpeg es grande; ambos tienen
licencias y binarios propios.

**Decisión:** se detecta yt-dlp en PATH/junto a la app/carpeta de datos y se
ofrece instalar la release oficial. No se empaqueta ffmpeg; se usan formatos
premezclados y se muestra el error de yt-dlp si un sitio lo requiere.

**Consecuencias:** la descarga depende de una herramienta externa; la lista de
sitios y argumentos vive en `YtDlp.cs`. No prometer compatibilidad total con todos
los sitios.

---

## ADR-008 · WPF propio en lugar de un framework de UI

**Contexto:** se buscaba un look Windows 11 consistente sin introducir un
framework grande.

**Decisión:** `FluentWindow` + `WindowChrome` + estilos/tokens propios;
WinForms sólo para `NotifyIcon` y APIs de pantalla.

**Consecuencias:** control total del chrome y de las animaciones, pero más
responsabilidad: recursos dinámicos, DPI, foco, maximizar y capturas se prueban a
mano. No mezclar `Window` con `FluentWindow` sin motivo.

---

## ADR-009 · Acciones de navegador como iconos al inicio

**Contexto:** Google y otras búsquedas quedaban ocultas en “más acciones” y
ocupaban sitio con texto.

**Decisión:** `ActionDefinition.IsBarIcon` marca búsquedas/abrir URL; se ordenan
primero y se pintan sólo con glifo (`BuildIconChip`). No consumen numeración de
atajos; los chips de texto siguen numerados desde 1.

**Consecuencias:** la barra gana claridad, pero los iconos dependen del tooltip.
Si se añade otra acción de navegador, decide explícitamente si es `IsBarIcon`.

---

## ADR-010 · Modelo por defecto DeepSeek V4.1 Flash

**Contexto:** el usuario pidió usar únicamente ese modelo en el proyecto.

**Decisión:** defaults, ejemplos y pruebas usan `deepseek-v4.1-flash`.

**Consecuencias:** no cambiar el default ni los ejemplos a modelos caros sin
petición expresa. Los proveedores siguen siendo configurables por el usuario.

---

## ADR-011 · Sugerencia en vez de más acciones

**Contexto:** con ~50 acciones, cada chip nuevo empeora la barra (más ruido).
El valor está en acertar a la primera, no en acumular comandos.

**Decisión:** `TextSuggest` detecta el tipo (correo, ruta, fecha, acortador,
seguimiento) y `ActionRegistry` mueve esa acción al frente con marca ★ y motivo
en el tooltip. Si no hay señal clara, devuelve null y la barra va normal.
`TriggerConfig.SmartSuggest` lo apaga; los perfiles por app y los toggles
siguen mandando (la sugerida no resucita acciones ocultas).

**Consecuencias:** las acciones nuevas de contexto (correo, fecha, ruta) solo
tienen sentido con la sugerencia: sin ella serían ruido. Al añadir un detector,
añadir su preset, su icono, su etiqueta y sus tests en la misma entrega.
(Las acciones-icono de navegador —lupa, abrir URL— siguen yendo delante por
ADR-009; la ★ queda primera entre los chips de texto.)
