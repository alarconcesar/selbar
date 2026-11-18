# AGENTS.md — trabajar en Selbar

Guía corta para agentes de código. El **README.md es para usuarios**; este archivo
y `docs/` son para quien modifica el proyecto.

## Qué es

Selbar es una app de escritorio **Windows 10/11** en **.NET 8 + WPF**. Al
seleccionar texto (o con una hotkey) ofrece acciones contextuales: IA, portapapeles,
búsquedas, descargas con yt-dlp, OCR, QR, Google Lens y transcripción con Groq.

- Proyecto: `src/Selbar/Selbar.csproj`
- TFM: `net8.0-windows10.0.19041.0`; mínimo `10.0.17763.0`
- UI: WPF, ventanas propias sobre `FluentWindow`; sin frameworks de UI externos.
- Dependencias: **QRCoder 1.6.0** y la proyección WinRT de Windows. No añadas
  paquetes sin justificarlo.
- Datos de usuario: `%AppData%\Selbar`; portable si hay `portable.txt`
  (entonces `.\data`); `SELBAR_DATA` tiene prioridad.
- Versión actual: leer `<Version>` del `.csproj`; no la dupliques a mano.

## Reglas que no se negocian

1. **No interferir con la escritura normal.** Al seleccionar aparece un indicador
   (`peek`), no la barra completa. El indicador **no captura el teclado**. Solo se
   captura cuando la barra se abrió deliberadamente (hotkey, clic en el indicador)
   y `ActionBarWindow.AcceptsTyping` es `true`. Si no acepta teclado, cualquier
   pulsación normal debe **cerrar el popup y devolverse a la app sin consumirla**.
2. **No robar el foco.** La barra, el overflow y los popups usan
   `WS_EX_NOACTIVATE`. Para pegar/reemplazar se devuelve el foco con
   `ForegroundTracker`; no uses `Activate()` en popups.
3. **Los tests son offline.** `tools/SelfTest` pone `SELBAR_OFFLINE=1`. No
   hagas llamadas reales a IA, Groq, Lens, yt-dlp ni descargas en tests.
4. **No cambies el modelo por defecto** (`deepseek-v4.1-flash`) ni las claves sin
   que lo pida el usuario.
5. **WPF y tema:** los pinceles se resuelven con `DynamicResource`; no captures
   pinceles congelados. Las ventanas normales heredan de `FluentWindow`.
   El fondo de ventana es transparente y la superficie sólida (`PART_Surface`) se
   recorta a las esquinas. **Mica está desactivado a propósito** porque en esta
   configuración WPF lo pintaba negro; no lo reactives sin probar el resultado real.
6. **Clipboard en STA.** Cualquier acceso al portapapeles debe ir en un hilo STA
   con reintentos (`ClipboardService`). No lo llames desde el hilo del hook.
7. **Hooks rápidos.** El hook de teclado/ratón no puede tocar WPF ni bloquearse:
   usa `App.PostToUi`/`Dispatcher.BeginInvoke`.
8. **Documenta y empaqueta.** Si cambias comportamiento visible, actualiza README,
   contadores de tests, capturas y versión; luego ejecuta `build.ps1`.

## Comandos

```powershell
# Compilar todo (incluye el harness de tests)
dotnet build tools\SelfTest\SelfTest.csproj -c Release

# Tests de lógica (rápidos, offline)
dotnet run --project tools\SelfTest -c Release --no-build

# Smoke de UI (abre ventanas reales, sin red)
dotnet run --project tools\SelfTest -c Release --no-build -- --ui

# UI + capturas para docs (directorio temporal que imprime al final)
dotnet run --project tools\SelfTest -c Release --no-build -- --ui --shots

# Igual, forzando tema claro (para las capturas de tema claro)
dotnet run --project tools\SelfTest -c Release --no-build -- --ui --shots --light

# Empaquetar: tests de lógica + publish + LEEME + ZIP
.\build.ps1

# Variantes útiles
.\build.ps1 -SkipTests -NoZip
.\build.ps1 -SelfContained
```

`build.ps1` **no ejecuta** el smoke de UI ni copia capturas a `docs/`; eso es
manual. El ZIP sale en la raíz como `Selbar-<version>-portable.zip`.

Smoke del ejecutable publicado (perfil aislado, sin tocar tus datos):

```powershell
$data = Join-Path $env:TEMP ("pt-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $data | Out-Null
$env:SELBAR_DATA = $data
$env:SELBAR_OFFLINE = "1"
$env:SELBAR_OPEN_SETTINGS = "1"
Start-Process ".\dist\Selbar-<version>\Selbar.exe"
# Espera 4 s, comprueba que sigue vivo, revisa "$data\log.txt" y ciérralo.
```

## Mapa del código

| Ruta | Responsabilidad |
|---|---|
| `src/Selbar/App.xaml.cs` | Arranque, instancia única, tema, monitor, hotkey, ciclo de popups, `ShowBar`, filtro de teclado, OCR/Lens desde tray. |
| `Core/Selection/` | `SelectionMonitor` (hooks), `TextExtractor` (UIA/portapapeles), anclaje. |
| `Core/Actions/` | `ActionDefinition`, `ActionRegistry` (visibilidad/contexto), `ActionPresets`, `ActionExecutor`, iconos/etiquetas, prompts. |
| `Ui/ActionBarWindow.*` | Indicador + barra + búsqueda + chips + overflow. Es la pieza más sensible. |
| `Ui/ActionRunner.cs` | Puente UI: ejecuta, abre ventanas especiales, resultado, reemplazo, errores. |
| `Ui/ResultWindow.*` | Resultado, streaming, editar/preview, diff, versiones, chat, errores. |
| `Ui/FluentWindow.cs` + `Ui/Theme/` | Marco, título, superficie recortada, tokens, estilos, animaciones. |
| `Ui/SettingsWindow.*` | Ajustes y persistencia visual. |
| `Core/Ai/` | Proveedor OpenAI-compatible, SSE; `GroqWhisper` para audio. |
| `Core/Imaging/` | OCR WinRT y subida a Google Lens. |
| `Core/Downloads/YtDlp.cs` | Dominios, argumentos, instalación y apertura de carpeta. |
| `Core/Text/` | Estilos Unicode y color HEX. |
| `Core/Config/` | `AppSettings`, migraciones, DPAPI, export/import. |
| `Native/` | P/Invoke, hooks, DPI, colocación, estilos de popup, DWM. |
| `tools/SelfTest/` | Spec ejecutable: lógica + smoke visual + capturas. |
| `docs/` | Arquitectura, recetas, decisiones y capturas. |

## Flujos que hay que entender

### Selección → indicador → barra

1. `SelectionMonitor` detecta el gesto y llama a `TextExtractor`.
2. `App.ShowBar(...)` consulta `Registry.GetVisibleActions(text)`.
3. Si `SelectionPeek` está activo, crea `ActionBarWindow(peek: true,
   allowTyping: false)`.
4. El indicador se auto-oculta a los 4,5 s. Con movimiento real del ratón durante
   ~180 ms se expande; con clic se expande y habilita búsqueda.
5. Si la barra no acepta teclado, `App.FilterKeyForBar` la cierra y devuelve
   `false` para que la tecla llegue a la app.
6. Al elegir una acción: `ActionRunner.ExecuteAsync` → `ActionExecutor` → resultado,
   portapapeles, navegador o ventana especial.

### Acción

`ActionDefinition` describe la acción. La visibilidad la decide
`ActionRegistry.GetVisibleActions` (enabled, longitud, regex, contexto). La
ejecución no-AI sale de `ActionExecutor`; las acciones con ventana propia se
interceptan en `ActionRunner` por `Id` (`text.styles`, `text.color`, `text.qr`,
`sys.youtube-*`, etc.). La IA usa `OpenAiCompatibleProvider`.

### Ventanas y tema

- `FluentWindow` pone `WindowChrome`, barra de título, `PART_Surface`, arrastre,
  DPI y maximizar. El `Window.Background` es transparente; `PART_Surface` pinta el
  color del tema y se recorta con radio 8.
- Los colores viven en `Ui/Theme/Tokens.xaml` y en la paleta de `Ui/Theming.cs`.
  `Theming` reemplaza las instancias de pincel en todos los diccionarios, porque
  WPF las congela.
- `Motion` respeta `SystemParameters.ClientAreaAnimation`.

## Dónde añadir cosas

- **Acción nueva:** `docs/COMO_EXTENDER.md#añadir-una-acción`.
- **Ventana nueva:** `docs/COMO_EXTENDER.md#añadir-una-ventana`.
- **Ajuste nuevo:** `docs/COMO_EXTENDER.md#añadir-un-ajuste`.
- **Contexto/flujo:** `docs/ARQUITECTURA.md`.
- **Por qué está así:** `docs/DECISIONES.md`.

## Checklist antes de entregar

- [ ] `dotnet build tools\SelfTest\SelfTest.csproj -c Release` sin errores.
- [ ] Tests de lógica en verde; si tocaste UI, `--ui` en verde con tema oscuro y claro.
- [ ] Sin llamadas de red en tests (no metas claves ni descargas).
- [ ] Capturas actualizadas en `docs/screenshots/` si cambió lo visual.
- [ ] README actualizado (comportamiento, catálogo, contadores, versión).
- [ ] `.\build.ps1` genera `dist/Selbar-<version>/` y el ZIP.
- [ ] Smoke del `.exe` publicado con `SELBAR_DATA` temporal; revisar `log.txt`.
- [ ] Si cambió el chrome o las esquinas, verificar en una **ventana real**: el
      harness con `RenderTargetBitmap` no captura el marco de DWM.
