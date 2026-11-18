# Arquitectura de Selbar

Documento para quien va a modificar el código. Para tareas concretas, ver
[COMO_EXTENDER.md](COMO_EXTENDER.md); para el porqué de las decisiones,
[DECISIONES.md](DECISIONES.md).

## Principios

1. **No interrumpir el uso normal de Windows.** El popup automático es un
   indicador que no captura teclado. La barra completa se abre de forma
   deliberada (hotkey, clic o hover).
2. **No robar el foco** a la app de origen. Todo popup es no-activable; cuando
   hace falta pegar o reemplazar, se devuelve el foco al HWND capturado.
3. **Todo local y opcional.** OCR usa Windows; las descargas usan yt-dlp; Lens y
   Groq solo se usan cuando el usuario lo pide y hay red/clave.
4. **Tests offline como contrato.** El harness nunca hace red. Las integraciones
   se prueban con validaciones, dobles y modo offline.
5. **Portable primero, instalador opcional.** Publicación portable con
   `portable.txt` y datos junto al ejecutable; también funciona desde
   `%AppData%`. `tools/installer.iss` (Inno Setup, por usuario y sin admin)
   instala el publish self-contained sin borrar datos al desinstalar.

## Stack y capas

- **.NET 8 / WPF**, `net8.0-windows10.0.19041.0`, mínimo Windows 10 17763.
- Proyecciones **WinRT** del targeting pack de Windows (vienen con el TFM, sin
  paquete extra) para OCR.
- **QRCoder 1.6.0** para QR (MIT).
- **WinForms interop** sólo para `NotifyIcon`/tray y alguna API de pantalla.
- Sin DI container, sin MVVM framework, sin paquetes de UI.

```
┌─────────────────────────────────────────────────────────────┐
│ UI (WPF)                                                    │
│  FluentWindow · ActionBarWindow · ResultWindow · Settings…  │
│  Tema (Tokens.xaml + Theming) · Motion · Chrome             │
├─────────────────────────────────────────────────────────────┤
│ Orquestación                                                │
│  App (arranque, hotkey, popups, tray)                       │
│  ActionRunner (UI + acciones especiales)                    │
├─────────────────────────────────────────────────────────────┤
│ Dominio                                                     │
│  ActionRegistry/Presets/Definition · ActionExecutor         │
│  SelectionMonitor/TextExtractor · Config · History          │
├─────────────────────────────────────────────────────────────┤
│ Servicios y plataforma                                      │
│  AI/OpenAiCompatibleProvider · GroqWhisper · OcrService     │
│  GoogleLens · YtDlp · ClipboardService · AppPaths           │
├─────────────────────────────────────────────────────────────┤
│ Nativo                                                      │
│  Hooks, P/Invoke, DPI, DWM, colocación y estilos de popup   │
└─────────────────────────────────────────────────────────────┘
```

### Mapa de carpetas

| Ruta | Contenido |
|---|---|
| `Core/Actions` | Modelo de acción, catálogo, registro contextual, ejecutor, prompts. |
| `Core/Ai` | Cliente OpenAI-compatible, parser SSE, Groq Whisper. |
| `Core/Config` | `AppSettings`, migraciones, DPAPI, export/import. |
| `Core/Selection` | Hooks de ratón/teclado, extracción UIA/portapapeles, anclaje. |
| `Core/Imaging` | OCR de Windows, Google Lens. |
| `Core/Downloads` | Dominios, argumentos, instalación y carpeta de yt-dlp. |
| `Core/Text` | Estilos Unicode, color HEX. |
| `Core/Platform` | Rutas de datos, autostart, portapapeles STA, foco. |
| `Core/History` | Historial local con cifrado opcional. |
| `Native` | Todo el P/Invoke; no debe depender de WPF salvo tipos de punto/rect. |
| `Ui` | Ventanas, controladores de UI, tema, animaciones, iconos. |
| `Ui/Theme` | `Tokens.xaml`, `Controls.xaml`, `WindowChrome.xaml`. |
| `tools/SelfTest` | Spec ejecutable de lógica y smoke de UI; genera capturas. |

## Arranque

`App.OnStartup` hace, en este orden:

1. Mutex de instancia única. Si ya hay una, envía el mensaje
   `Selbar.Activate` y sale.
2. Registra manejadores de excepciones y logger.
3. Carga `SettingsManager` desde `AppPaths.DataDir`.
4. Aplica tema/acento con `Theming.Apply` **antes** de crear ventanas.
5. Crea `ActionRegistry`, `ActionExecutor` y configura `HistoryStore`.
6. Crea `SelectionMonitor`, conecta `SelectionReady` a `ShowBar` y configura
   `KeyFilter` para el modo búsqueda.
7. Conecta `DismissRequested`, `EscapePressed` y `DigitPressed`.
8. Arranca hooks y registra la hotkey.
9. Si es la primera ejecución, muestra `WelcomeWindow`.

Las rutas de datos se resuelven en `AppPaths.Resolve`:
`SELBAR_DATA` → `portable.txt` + `.\data` → `%AppData%\Selbar` → temp.

## Flujo de selección

```
ratón/teclado
   │ hooks (SelectionMonitor)
   ▼
¿gesto válido? (drag, doble clic, long-press, Shift+flechas)
   │
   ├── UIA (rápido, no toca portapapeles)
   └── fallback: Ctrl+C en hilo STA (solo cuando hace falta)
   ▼
SelectionReady(text, anchor)
   ▼
App.ShowBar(text, anchor, peek, allowTyping)
   ├── pausa / app excluida / fullscreen / sin acciones → no mostrar
   ├── peek=true  → ActionBarWindow(peek, allowTyping:false)
   └── peek=false → ActionBarWindow(completa, allowTyping:true)
```

- El ancla es un rectángulo en píxeles físicos; `PopupPlacement` convierte a DIPs
  con el DPI del monitor y evita salirse de la pantalla.
- `SelectionMonitor` no puede bloquear: `FireAsync` espera 90–130 ms a que la app
  actualice la selección y extrae en segundo plano. El fallback de portapapeles
  usa un hilo STA y no se usa para cada selección.
- Las apps excluidas y la pantalla completa se comprueban en `App.ShowBar`.

## Teclado, indicador y barra

### Modo indicador (por defecto)

`SelectionPeek = true`:

- `ActionBarWindow(peek: true, allowTyping: false)` construye sólo el botón
  circular (`BuildPeek`). El teclado **no** se captura.
- `App.FilterKeyForBar` ve `AcceptsTyping == false` y, ante cualquier tecla que no
  sea un modificador suelto, cierra el popup y devuelve `false` para que la tecla
  llegue a la app.
- Hover: `MouseMove` real inicia un temporizador de 180 ms; al cumplirse,
  `Expand(enableTyping:false)` reconstruye la barra completa sin captura.
- Clic en el indicador: `Expand(enableTyping:true)`; la barra pasa a aceptar
  búsqueda.
- Auto-cierre a los 4,5 s; también `Esc`, clic fuera, rueda o clic derecho.
- `SelectionPeek = false` recupera la barra completa automática (comportamiento
  antiguo), útil como escape.

**Invariante:** si `AcceptsTyping` es `false`, ninguna tecla puede consumirse. Si
se cambia `Expand`/`BuildPeek`, hay que probar escribir para reemplazar.

### Barra completa

- `_chips` contiene todos los botones; `_numberedChips` solo los que llevan
  número/atajo. Los iconos de navegador (`IsBarIcon`) van primero y sin número.
- `BuildDefault` calcula cuántos chips caben (`MaxBarIcons`, ancho real) y manda
  el resto a `OverflowWindow`.
- Escribir filtra vía `OnCapturedKey`/`BuildFiltered`; `↑↓`, `Enter`,
  `Backspace`, `Esc` y `1–9` están descritos en `ActionBarWindow`.
- La búsqueda sólo se activa si la barra se abrió de forma deliberada. No añadas
  captura al modo hover.

## Acciones

`ActionDefinition` es el contrato. Campos importantes:

| Campo | Uso |
|---|---|
| `Id` | Clave estable para icono, etiqueta, overrides y ramas de UI. |
| `Category` / `Kind` | Agrupación y etiqueta IA/no-IA. |
| `Output` | `Popup`, `Copy`, `Replace`, `Browser`, `OpenUrl`, `SimulateKey`, etc. |
| `PromptTemplate` | `{text}`, `{lang}`, `{date}`, `{time}`, `{app}`. |
| `UrlTemplate` | URL de búsqueda con `{text}`. |
| `MatchRegex` | Regla de contexto; si no casa, la acción no aparece. |
| `MinLength` / `MaxLength` / `SingleWordOnly` / `MultiWordOnly` / `LongTextOnly` | Filtros rápidos. |
| `IsBarIcon` | Se pinta primero y sólo icono (lupa Google, abrir URL…). |
| `Order` | Orden dentro de la categoría. |

`ActionRegistry` combina presets + overrides del usuario (`IsEnabled`, `Order`).
`GetVisibleActions` filtra por enabled, contexto y reglas; los tests de
`RegistryTests` y `PresetTests` son la red de seguridad.

`ActionExecutor` resuelve los `Output` no-IA. `ActionRunner` intercepta antes las
acciones que necesitan una ventana propia:
`text.styles`, `text.color`, `text.qr`, `sys.youtube-video`,
`sys.youtube-audio`; después delega el resto. Si añades una acción con UI propia,
hazlo en `ActionRunner` y añade su ventana al smoke de `SelfTest`.

## Ventanas y chrome

- Todas las ventanas normales heredan de `FluentWindow`.
- `WindowChrome` dibuja la barra nativa, el arrastre, los botones y el borde.
  `TitleBar` se ocupa de minimizar/maximizar/cerrar y del doble clic.
- `FluentWindow` mantiene:
  - `Window.Background = Transparent` para que no haya un rectángulo de fondo.
  - `PART_Surface` (Border del template) con el color del tema.
  - `Clip` con `RectangleGeometry` radio 8 en Windows 11, 0 al maximizar.
  - `Padding = 8` al maximizar para compensar `WindowChrome`.
  - `SetRoundedCorners` y `ApplyDarkFrame` de DWM.
  - **Mica desactivado**: WPF lo renderizaba negro en esta configuración. No
    reactivar sin una prueba visual real; el `RenderTargetBitmap` no lo detecta.
- Los popups (`ActionBarWindow`, `OverflowWindow`) usan `AllowsTransparency`,
  borde redondeado y `WS_EX_NOACTIVATE`.

## Tema

- `Ui/Theme/Tokens.xaml` define las claves (`BgWindow`, `TextPrimary`,
  `AccentFill`, etc.).
- `Ui/Theming.cs` contiene la paleta clara/oscura y reemplaza las **instancias**
  de pincel en todos los diccionarios (`ApplyToDictionary`). WPF congela los
  pinceles de recursos; por eso no se mutan y se usan `DynamicResource`.
- El acento se lee de Windows y se ajusta con `ColorMath.ClampLightness` para
  garantizar contraste.
- `Motion` centraliza duraciones y respeta `ClientAreaAnimation`.
- Las animaciones de entrada de ventanas/popups usan opacidad/traslación, no
  cambios de layout.

## IA y servicios externos

- `OpenAiCompatibleProvider`: POST `/chat/completions`, streaming SSE con
  `SseParser`, errores mapeados (401/403, timeout, offline). `SELBAR_OFFLINE=1`
  bloquea toda llamada.
- `GroqWhisper`: POST multipart a
  `https://api.groq.com/openai/v1/audio/transcriptions`,
  `whisper-large-v3-turbo`, máximo 25 MB; la key va en Ajustes → IA y se cifra con
  DPAPI. No se exporta.
- `OcrService`: WinRT `Windows.Media.Ocr` + `BitmapDecoder`; requiere el paquete
  de idioma OCR de Windows. En tests se usa una imagen local.
- `GoogleLens`: subida multipart a `searchbyimage/upload` con
  `AllowAutoRedirect=false`; endpoint no oficial. Confirmación explícita antes de
  subir una imagen del portapapeles.
- `YtDlp`: resuelve dominios soportados, construye argumentos, busca el ejecutable
  y ofrece instalarlo. Sin ffmpeg usa formatos premezclados; si un sitio exige
  ffmpeg, se muestra el error de yt-dlp.

## Configuración, privacidad y datos

- `AppSettings.CurrentVersion` controla la migración; al añadir un campo, añade su
  validación/default en `SettingsManager.Migrate`.
- `ApiKeyEncrypted` y `GroqApiKeyEncrypted` se cifran con DPAPI (CurrentUser).
  `Export` las vacía salvo `includeApiKey: true`. `Adopt` conserva las locales si
  el archivo importado no las trae.
- El historial puede desactivarse/cifrarse/limpiarse; no se envía a ningún sitio.
- Nunca se extraen campos de contraseña (`TextExtractor`/UIA).

## Tests y capturas

`tools/SelfTest` es el contrato ejecutable:

- Fija `SELBAR_DATA` temporal, `SELBAR_OFFLINE=1` y
  `SELBAR_NO_PROMPTS=1`.
- `--ui`: crea cada ventana real, comprueba contraste de texto y que no haya
  excepciones; incluye OCR de extremo a extremo y el cierre del indicador al
  escribir.
- `--shots`: además guarda PNG en un directorio temporal (lo imprime al final).
  No los copia a `docs/`; el copiado es manual.
- `--light`: fuerza tema claro para las capturas gemelas.
- Las capturas usan `RenderTargetBitmap`: **no incluyen el marco de DWM**, así
  que las esquinas/chrome se verifican en una ventana real.

## Limitaciones conocidas

- Windows 10 no tiene esquinas redondeadas de DWM; el clip se desactiva.
- Sin paquete de idioma OCR, `OcrService.IsAvailable` es false y la acción no se
  ofrece.
- yt-dlp y ffmpeg no van en el ZIP. yt-dlp se puede instalar desde la app; ffmpeg
  no.
- Google Lens es un endpoint no oficial y puede cambiar.
- Groq exige key propia y conexión; no hay transcripción local.
- La CI en GitHub Actions ejecuta los auto-tests y publica ZIP self-contained
  para `win-x64` y `win-arm64`; con un tag `v*` crea la release.
- El instalador (`tools/installer.iss`, Inno Setup) genera el setup desde
  `dist\`; se distribuye también el ZIP portable.

## Al modificar

1. Lee `AGENTS.md` (reglas duras).
2. Sigue la receta de `COMO_EXTENDER.md`.
3. Añade/actualiza tests; ejecuta lógica y UI.
4. Actualiza README, capturas, versión y ZIP.
5. Si tocas chrome/esquinas/Mica, verifica en ventana real, no sólo en capturas.
