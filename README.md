# Selbar — menú IA al seleccionar texto (Windows 11/10)

Seleccionas texto en cualquier aplicación, pulsas el atajo y aparece una barra flotante
con acciones con IA y utilidades locales. Todo es **activable, desactivable y
reordenable** en Ajustes para no agobiar.

> Versión **0.13.6** · .NET 8 · WPF · OCR nativo · yt-dlp opcional · Google Lens · Whisper en Groq.
>
> **Gratis y open source (MIT).** Sin cuenta, sin telemetría y sin planes de pago.
> El código y las descargas están en GitHub:
> [github.com/alarconcesar/selbar](https://github.com/alarconcesar/selbar).
>
> ¿Vas a modificar el código? Empieza por **[AGENTS.md](AGENTS.md)** y la
> [documentación de desarrollo](docs/README.md).

---

## Novedades de la 0.13.6 · identidad oficial Selbar

- **Rebrand integral a Selbar**: nueva identidad visual **Dual-Bar S** en icono
  multirresolución (`app.ico` 16–256 px), bandeja del sistema, barra lateral de
  Ajustes y textos visibles en las 16 ventanas.
- **Compatibilidad total**: mantiene `Selbar.exe` y el arranque automático; si
  existen datos antiguos en `%AppData%\PowerTools`, se conservan ajustes, claves
  e historial al migrar a `%AppData%\Selbar`.

## Novedades de la 0.13.5 · discreta por defecto

La app deja de aparecer sola. **La barra se abre con el atajo** (`Ctrl+Shift+Espacio`),
no al seleccionar texto:

- **Sin círculo al seleccionar.** Ya no sale el indicador flotante cada vez que
  arrastras texto; la app permanece en la bandeja hasta que la llamas.
- **El atajo es la vía principal**: abre el menú completo con la selección actual.
- **Los avisos automáticos siguen existiendo**, pero desactivados: en
  Ajustes → Selección y atajos puedes volver a activarlos (el botón del indicador
  se habilita solo cuando hay algún aviso activo).
- **La migración es de una sola vez**: si tenías los avisos activados se apagan
  al actualizar, y si después los vuelves a encender no se revierten.
  Si además tenías el atajo apagado, se enciende para que la barra siempre tenga
  una forma de abrirse.
- **Bienvenida y textos** reescritos para el nuevo flujo (seleccionar → atajo), y
  la nota de privacidad: no lee nada hasta que la llamas.

## Novedades de la 0.13.4 · la IA elige la acción más probable

Nuevo, **opcional y desactivado por defecto**: la barra sigue apareciendo al
instante con las reglas locales y, en paralelo, Gemini decide cuál de las
acciones es la más útil para lo que seleccionaste (≈300 ms medidos).

- **No se pregunta por las 58 acciones, sino por la intención**: responder,
  resumir, corregir, traducir, calcular, convertir, fecha, mapa, archivo, enlace,
  correo, buscar o «ninguna». Una taxonomía fija que funciona con cualquier
  selección —un correo, un párrafo, código, un emoji o basura— y que no se rompe
  cuando el catálogo crece. Ver [docs/DECISION-IA.md](docs/DECISION-IA.md).
- **La intención elegida sube al primer puesto** con etiqueta «Elegida por IA»,
  solo si hay confianza suficiente (≥ 0,55). El resto del orden no se toca.
- **Si no hay nada útil, la barra no molesta**: se ocultan las sugerencias
  genéricas. Los aciertos exactos (correo, fecha, ruta, cálculo, enlace) nunca se
  ocultan: son hechos, no opiniones.
- **Nunca estorba**: sin key, sin red o si tarda más de 1,2 s, todo queda como
  siempre. Caché por selección y cortacircuitos tras 3 fallos.
- **Privacidad**: el texto sale del equipo solo si lo activas; si parece una
  contraseña o un token, no se envía nunca. La key se cifra con DPAPI.
- **Medido con el código real**: 14/16 aciertos en selecciones típicas (correo,
  enlace con `utm`, dirección, fecha, `2+3*4`, `5 usd a soles`, ruta, queja,
  texto largo, falta de ortografía, número suelto), 3/3 casos de basura ocultados
  y 281 ms de mediana. Cero promociones erróneas en la batería.

## Novedades de la 0.13.3 · cuarta pasada (fuzzing y análisis estático)

- **Una respuesta podía quedarse en blanco al final del streaming**: tras `[DONE]`
  se hacía una lectura más y, con la conexión abierta, se esperaba al timeout y
  se perdía todo el texto. Ahora corta en cuanto ve el final.
- **Un emoji partido entre dos chunks tumbaba el parser** (`ArgumentException` de
  UTF-16, no `JsonException`): se sanea el texto y la respuesta sigue. Lo
  encontró un fuzz de 60.000 cadenas; hoy 0 excepciones.
- **SSRF por nombre de dominio**: `localtest.me` resuelve a 127.0.0.1 y pasaba el
  filtro de texto. Ahora se valida la IP real de cada conexión.
- **El portapapeles ya no se pisa solo**: si el usuario copia algo mientras la app
  extrae la selección, se aborta en vez de devolver texto viejo; `Ctrl+C` se
  inyecta solo si el foco no cambió de ventana.
- **Guardar ajustes podía borrar tu configuración**: con `settings.json`
  bloqueado un instante (antivirus, OneDrive), la memoria tenía valores de serie
  y el guardado al salir la sobrescribía. Ahora se conserva el archivo y se avisa.
- **Sumas de 7–22 caracteres sin espacios** (`1234+5678`) habían dejado de
  ofrecer «Calcular» por la guarda de teléfonos. Corregido sin volver a mostrar el
  chip en `987-654-321`.
- **Fechas**: el chip ya aparece en `12-mar-2025`, `12 mar. 2025`,
  `12 de marzo del 2025` y `… a las 3pm`, formatos que el parser ya entendía.
- **Historial**: un archivo bloqueado ya no se interpreta como «historial vacío»
  (antes el siguiente guardado borraba todo).
- **Fugas GDI**: dos `StringFormat` por icono de bandeja sin disponer.
- **Dictado**: un arranque fallido dejaba el dictado muerto hasta reiniciar.
- **Instalador/CI**: el ZIP del CI no coincidía con el de `build.ps1`; la release
  no comprobaba tag contra `.csproj`.
- **+30 comprobaciones** (499 en el fuente) y 4 verificaciones ejecutadas fuera de
  Windows: fuzz de 60.000 entradas, XAML (21 pantallas), `settings.json`
  bloqueado con permisos reales y 27 casos de chips.

## Novedades de la 0.13.2 · tercera pasada, zonas nuevas

- **Arranque con Windows: el hotkey no funcionaba hasta abrir Ajustes.** Con
  `StartupUri` la ventana principal nace oculta y WPF nunca dispara `Loaded`;
  el registro del atajo corría antes de que existiera handle. Ahora se registra
  en el constructor y se reintenta al arrancar.
- **Fugas de credenciales al log**: `Authorization: Bearer <token>` se redactaba
  solo a medias (quedaba el token). Corregido y con prueba real.
- **El log rotaba en caracteres**, no en bytes: podía duplicar el tope de 512 KB.
- **Atajos**: registrar uno válido pero ocupado desregistraba el que ya
  funcionaba y te dejaba sin ninguno.
- **`settings.json` bloqueado** (antivirus, otro proceso) se trataba como
  corrupto y se sobrescribía con los valores de fábrica. Ahora se respeta.
- **Ajustes** ya no revierte `SeenWelcome` ni el tamaño de la ventana de
  Resultado al guardar; el grabador de atajos deja de capturar teclas tras
  guardar/descartar.
- **Calculadora**: `1000-100-50` y `100-10%-10%` se confundían con fechas;
  `1.000.000+1` no se interpretaba; un hex de más de 16 dígitos lanzaba
  `OverflowException`.
- **Fechas**: `12 de marzo`, `1 de enero`, `29 de febrero` (sin año), `del 2025`,
  `12-mar-2025` y `… a las 3pm` ya se entienden; en `DateTime.MaxValue` no lanza.
- **Chips**: «Analizar fecha» aparecía en `3.14`; «Buscar en WhatsApp» en
  fechas; «Ver en Maps» en cualquier frase con número («Tengo 3 gatos»).
- **Buscador de acciones** ignoraba tildes: «mayusculas» no encontraba
  «MAYÚSCULAS».
- **Ordenar texto** usaba orden ordinal: «Árbol» caía después de «Zorro».
- **Un override parcial** (solo `id` en el JSON) borraba el título del chip y lo
  mandaba al primer puesto de la barra.
- **Instalador**: copiaba `portable.txt`, así que la app instalada guardaba sus
  datos en `{app}` y los perdía al reinstalar en otra ruta; desinstalar dejaba
  el valor de arranque automático.
- **Resultado**: flechas de versión activas durante el streaming y el scroll
  saltaba al principio en cada token; «Zoom» se imprimía dos veces.
- **+38 comprobaciones** (496 en total) y ~50 bugs más de plataforma, iconos,
  voz y descarga.

## Novedades de la 0.13.1 · auditoría profunda de la 0.13.0

- **~120 bugs corregidos** en seis frentes: Resultado, Ajustes, barra/teclado,
  ventanas utilitarias, IA/red y núcleo del sistema.
- **Tu portapapeles deja de perderse**: al leer la selección se clonaba el
  contenido, pero el `Clear` invalidaba el proxy del proceso dueño y la
  restauración nunca ocurría. Ahora se materializa todo antes de copiar y solo se
  restaura si el portapapeles sigue siendo el nuestro.
- **Dictado**: el auto-stop ya no hacía perder los últimos 3 minutos; dos paradas
  simultáneas no borran el WAV a medio cerrar.
- **`INPUT` de 32 bits** medía 24 bytes (Win32 espera 28): en builds x86 no
  funcionaban copiar/pegar/reemplazar. Corregido con el union real.
- **Historial**: una sola entrada con fecha inválida vaciaba el archivo entero.
  Ahora se lee entrada a entrada y se conservan las buenas.
- **IA**: respuestas con raíz array, `output[]` o errores anidados ya se
  interpretan; el pensamiento (`thinking`) se filtra también dentro de bloques.
- **Red**: se bloquean loopback, link-local y metadatos en cualquier salto de
  redirección (SSRF), con LAN privada permitida; los cuerpos tienen tope.
- **Barra**: sin barra fantasma que se coma el teclado, sin cerrarse con el clic
  que la abrió, y el menú `···` por fin resalta la fila seleccionada.
- **+29 comprobaciones** nuevas en la suite (458 en total) y 18 verificadas
  ejecutando la lógica pura en Linux.

## Novedades de la 0.13.0 · interfaz ligera y honesta

Revisión de las **16 ventanas** (132 hallazgos) buscando lo mismo: hacer lo de
siempre en menos clics. El detalle está en **[docs/UI-REVIEW.md](docs/UI-REVIEW.md)**.

- **Resultado, primero el texto.** Barra compacta `Editar / Ajustar texto / Ver
  cambios`; los seis refinamientos siguen completos en el menú y el chat deja de
  robar el foco. Una sola acción acentuada según el estado.
- **Ajustes con calma.** `Guardar cambios` no cierra, `Descartar` revierte de
  verdad y los parámetros técnicos (temperatura, tokens, timeout, prompt) quedan
  plegados. Todo se valida antes de aplicar nada.
- **Sin relleno:** la bandeja agrupa `Imagen` y `Más`; las historias ponen
  `Copiar` como primaria y dejan lo destructivo en menús; estilos y color usan
  una sola acción en lugar de repetir botones.
- **Rápida de verdad:** fuera animaciones en la barra, el menú y la búsqueda
  (se usan decenas de veces al día); feedback de pulsación de 100 ms, solo con
  ratón, y respeto total a «Efectos de animación» de Windows.
- **Bugs de fondo corregidos:** `Reintentar` ya no borra versiones, refinar desde
  el editor no mezcla texto viejo, un error no se puede reemplazar sobre tu
  selección, la cabecera de los desplegables abre, y la navegación del menú `···`
  responde a las flechas.
- **+20 comprobaciones** nuevas en la suite (429 en total): capacidades de
  resultado según estado, turnos con la versión visible y guardado de ajustes
  con identidad conservada.

## Novedades de la 0.12.3 · no mostrar el pensamiento

- **Fix**: con proveedores que razonan (opencode y similares), el pensamiento
  interno del modelo (`reasoning_content`) se colaba como texto en la respuesta.
  Ahora se ignora siempre y solo se muestra el contenido real.

## Novedades de la 0.12.2 · segunda auditoría (5 frentes en paralelo)

- **~50 bugs más**, corregidos por área: ventanas que tocaban UI muerta al cerrar,
  timers/handles/CTS fugados, foco devuelto a ventanas recicladas, portapapeles
  restaurado que destruía lo copiado, hooks con doble `Start`.
- **IA/red blindada**: respuestas vacías o en array ya no dan ventana vacía,
  `file://` y `javascript:` no llegan a la red, Lens solo abre google,
  yt-dlp verifica SHA-256 en temporal antes de instalar, OCR e imágenes con
  topes de tamaño y dimensiones, HTML con timeout.
- **Datos que no se pierden**: nulls y DPAPI ajeno ya no vacían historiales,
  copia del corrupto, escritura atómica, Obsidian con escritura exclusiva
  (no pisa notas ajenas, no sale del vault), perfiles `!` para desactivar.
- **56 comprobaciones nuevas** en la suite (351 en total, + grupo de versiones
  csproj = installer.iss) + instalador por usuario con icono y Win10 mínimo.

## Novedades de la 0.12.1 · auditoría de bugs a fondo

- **54 bugs corregidos** tras revisar cada parser, regex y rama nueva caso por caso:
  sin crashes en uso normal, sin falsos chips, sin pegar a ciegas.
- **Correo**: detecta `Juan Pérez <…>`, ignora `mailto:` con basura, valida puntos
  y guiones en bordes, arma `mailto:` incluso desde el nombre.
- **Fecha**: años de 2 dígitos con pivote (99→1999), `29/02` busca el próximo
  bisiesto, acepta hora al final (`mañana 15:30`, `12/03/2025 5:30pm`).
- **Cálculo**: hexadecimales (`0xFF+2`), potencias con negativo (`-2^2`) y
  conversiones con número pegado (`3km a millas`, `30°C a F`).
- **Dictado**: se detiene solo al llegar al máximo sin perder lo grabado.
- **yt-dlp**: el binario se verifica con SHA-256 oficial antes de instalarlo.
- **Foco**: la barra nunca pega si la app de origen ya se cerró (avisa y copia).
- **64-bit**: llamadas de ventana separadas para x64/ARM64.
- **23 comprobaciones nuevas** en la suite (337 en total) + build y empaquetado
  sanos (`build.ps1`, instalador por usuario, CI con tags `v*`).

## Novedades de la 0.12.0 · la barra que adivina y el loop que se cierra

- **Sugerencia inteligente (★)**: según lo seleccionado, la acción más probable
  va primera con su motivo ("Parece un correo", "Parece una fecha"…). Si no hay
  señal clara, no adivina y la barra va normal. Se apaga en Ajustes → Disparadores.
- **Redactar correo**: con un email seleccionado abre tu app de correo. Vale
  `juan@x.com`, `Juan <juan@x.com>` y `mailto:`.
- **Analizar fecha**: qué día cae (`12/03/2025`, `12 de marzo`, `mañana`) y
  cuántos días faltan o pasaron, más el formato ISO para copiar.
- **Abrir ubicación**: con una ruta (`C:\…`, `\\equipo\…`, `%VAR%\…`, `~/…`) abre
  la carpeta o selecciona el archivo en el Explorador. Si no existe en tu equipo,
  copia la ruta normalizada.
- **Usar como selección**: en el menú `···` de la ventana de resultado, cierra y
  reabre la barra con el resultado como texto. Encadena sin reseleccionar
  (traducir una traducción, resumir un resumen).
- **Historial del portapapeles**: **tray → Portapapeles…** guarda los últimos 20
  textos copiados, con búsqueda, copiar, pegar, eliminar y vaciado.
- **44 comprobaciones nuevas** de lógica (correo, fecha, ruta, sugerencia,
  portapapeles) + la ventana nueva en el smoke de UI.

## Novedades de la 0.11.0 · enlaces inteligentes y mapas

- **Limpiar enlace**: quita `utm_*`, `fbclid`, `gclid`, `igshid`… y copia la URL limpia.
  Solo aparece cuando el enlace trae parámetros de seguimiento.
- **Ver destino real**: sigue las redirecciones de un acortador (`bit.ly`, `t.co`,
  `amzn.to`, `lnkd.in`…) y dice cuántos saltos dio. El destino se copia al portapapeles.
- **Resumir enlace**: lee la página y la resume en 5 puntos. Si es un **PDF**, lo descarga
  y le pasa el OCR local a las 3 primeras páginas antes de resumir.
- **Abrir en Maps**: al seleccionar una dirección (`Av. Larco 123`, `Calle 50 1200`) abre
  Google Maps con la búsqueda.
- **Perfiles sociales**: con `@usuario` seleccionado aparecen chips para abrir el perfil
  en **X**, **Instagram** y **Facebook**.

## Novedades de la 0.10.0 · voz, visión y cálculo

- **Dictado**: un atajo (`Ctrl+Shift+D` por defecto) graba del micrófono; el segundo
  toque transcribe con Groq Whisper y escribe el texto donde tengas el cursor. También
  desde **tray → Dictar**.
- **Leer en voz alta**: nuevo chip que lee la selección con las voces de Windows
  (WinRT `SpeechSynthesizer`), sin red ni dependencias. Velocidad configurable.
- **Guardar en Obsidian**: escribe una nota Markdown con frontmatter (`creado`,
  `origen`, `tags`) dentro de tu vault. Si el nombre existe, añade `(2)` en vez de
  sobrescribir. Se configura la carpeta en Ajustes → Avanzado → Integraciones.
- **Visión**: **tray → Analizar imagen con IA** envía la imagen del portapapeles al
  modelo (formato `image_url` base64) y devuelve la descripción o el texto transcrito.
- **Perfiles por aplicación**: oculta acciones según el proceso en primer plano
  (`chrome = ai.todos, text.qr`). Se edita como texto en Ajustes → Integraciones.
- **Calcular y Convertir**: chips contextuales que aparecen solo con una operación
  (`12*8`, `350+20%`, `20% de 350`) o una conversión (`3 km a millas`, `15 usd a pen`,
  `30 c a f`). La aritmética y las unidades son locales; la moneda consulta tipos de
  cambio y falla con un aviso claro si no hay red.
- **CI en GitHub Actions**: `.github/workflows/build.yml` ejecuta los auto-tests y
  publica ZIP self-contained para `win-x64` y `win-arm64`; con un tag `v*` crea release.

## Novedades de la 0.9.0 · no interrumpir al escribir

- **Al seleccionar ya no aparece el menú completo**: aparece un botón circular de acento con
  el icono de Selbar. **No captura el teclado**: si empiezas a escribir para
  reemplazar el texto, la app recibe la pulsación como siempre y el indicador se
  oculta. Se acabó el “se puso a buscar solo”.
- **El menú se abre al interactuar**: mueve el ratón hasta el indicador (se abre solo
  tras 180 ms y con animación) o haz clic en él. El clic abre el menú en modo
  búsqueda.
- **El atajo global sigue abriendo el menú completo** y ahí sí puedes escribir para
  filtrar. `Esc`, clic fuera, rueda, clic derecho o 4,5 s sin tocar el indicador lo
  cierran.
- **Nuevo ajuste** en Disparadores: “Mostrar solo un indicador…”. Si lo desactivas,
  vuelve el comportamiento anterior (barra completa al seleccionar).
- **Ventanas sin fondos negros en las esquinas**: las superficies son sólidas del tema
  y se recortan al radio de Windows 11; la barra de título ya no queda negra en claro.
- **209 auto-tests + 46 comprobaciones de UI**, incluyendo que el indicador **no
  consume la tecla** al escribir y se cierra.

## Novedades de la 0.8.0 · redes, Lens, QR y Whisper

- **Descargas de redes sociales**: las acciones “Descargar vídeo” y “Descargar
  audio” aparecen para enlaces de **YouTube, TikTok, Instagram, Facebook,
  X/Twitter, Vimeo, Twitch, Reddit, SoundCloud, Dailymotion, Pinterest, LinkedIn,
  Rumble, Odysee, Bitchute, Kick, VK, OK.ru, Bilibili, Niconico, Bandcamp,
  Archive.org, Snapchat y TED** (siempre que yt-dlp soporte el enlace concreto).
  En la ventana de descarga hay un botón **Sitios compatibles** con la lista.
- **Google Lens**: si seleccionas una **URL de imagen**, aparece “Buscar imagen en
  Google Lens”. Si tienes una **imagen copiada**, usa **tray → Buscar imagen en
  Google Lens**: la app pide confirmación y la sube a Lens para la búsqueda inversa.
  Nada se envía hasta que lo pides; la imagen no se guarda.
- **Código QR**: al seleccionar un enlace aparece **Generar código QR**. Muestra el
  QR en una ventana y permite **copiar la imagen** o **guardarla como PNG**.
  Usa QRCoder (MIT), incluido en el ZIP; no necesita internet.
- **Audio a texto con Whisper en Groq**: configura tu **API key de Groq** en
  Ajustes → IA y usa **tray → Transcribir audio con Groq…**. Elige un archivo
  (mp3, m4a, wav, ogg, webm, flac, mp4 o aac; máximo 25 MB), transcribe con
  `whisper-large-v3-turbo` y abre el texto en la ventana de resultado. La key se
  cifra con DPAPI y no se exporta.
- **209 auto-tests de lógica + 41 comprobaciones de UI** (41 con capturas; incluye
  OCR real, Lens, QR, Groq offline y los nuevos dominios).

## Novedades de la 0.7.0 · OCR, YouTube y más acciones

- **OCR local**: copia una imagen (o usa Win+Shift+S para recortar una zona) y pulsa
  el atajo global o **tray → Leer texto de una imagen (OCR)**. El texto se reconoce
  con la API de Windows, sin subir nada a internet, y se abre en la ventana de
  resultado para copiarlo, traducirlo o resumirlo. Respeta el idioma OCR instalado
  en Windows.
- **WhatsApp contextual**: si seleccionas un teléfono, aparece “Enviar por WhatsApp”
  y abre `wa.me` con el número limpio (incluye el código de país).
- **Descargar de YouTube**: para enlaces de YouTube aparecen “Descargar vídeo” y
  “Descargar audio”. Usa **yt-dlp** (se busca en el PATH, junto a la app o en su
  carpeta de datos; si falta, la ventana ofrece instalarlo desde la release oficial).
  Sin ffmpeg descarga un formato ya mezclado para no depender de nada más.
- **Estilos de letra**: 12 “tipografías” Unicode (negrita, cursiva, negrita cursiva,
  caligráfica, doble trazo, monoespaciada, versalitas, círculos…) con vista previa y
  copia para redes, anuncios o documentos.
- **Ver color HEX**: al seleccionar `#0078D4` (o `#RGB`, `#RRGGBBAA`) aparece una
  ventana con la muestra y los valores HEX/RGB/HSL listos para copiar.
- **Contar palabras y caracteres** y **Quitar saltos de línea**: desactivadas por
  defecto, se activan en Ajustes → Acciones.
- **191 auto-tests de lógica + 37 comprobaciones de UI** (incluye OCR real, estilos,
  color y seguridad de los enlaces de YouTube).

## Novedades de la 0.6.0 · interfaz nativa de Windows 11

- **Marco Fluent de verdad**: barra de título integrada (icono + título, 44 px) con
  minimizar/maximizar/cerrar como en Windows 11 (hover, cerrar en rojo),
  esquinas redondeadas, borde y sombra de DWM, arrastre y doble clic nativos. La
  ventana de resultado usa su cabecera como asa de arrastre.
- **Tema claro y oscuro que sigue a Windows**: Ajustes → Apariencia (Sistema / Claro /
  Oscuro) con cambio en caliente, sin reiniciar. Si cambias el tema o el color de
  acento en Windows con la app abierta, también se actualiza.
- **Paleta Fluent 2 completa en ambos temas**: superficies, texto, trazos, estados,
  controles y marco. El acento del sistema se adapta para que los botones con texto
  blanco y los enlaces mantengan contraste, aunque tu acento sea claro.
- **Controles al detalle de WinUI**: 32 px de alto, radios 4 (controles) y 8 (tarjetas
  y flyouts), foco con borde de acento, campo de texto con línea inferior de acento,
  combos con chevrón, interruptores 40×20, scrollbars finas, menús contextuales
  Fluent y tarjetas de ajustes con filas separadas.
- **Tipografía nativa**: Segoe UI Variable Display para títulos (28) y Text para el
  resto (14/12), con `UseLayoutRounding` y `SnapsToDevicePixels`.
- **Menús contextuales**: en el resultado (copiar, guardar, reemplazar) y en el texto
  editable (cortar, copiar, pegar, seleccionar todo).
- **Paneles flotantes como flyouts**: barra y menú `···` con radio 8, borde y sombra
  suaves, chips de 36 px y glifos de Segoe Fluent Icons.
- **Bandeja coherente**: el menú del tray cambia con el tema y el icono se redibuja
  con el acento actual.
- **184 comprobaciones**: 154 de lógica + 30 de UI. La smoke incluye capturas en
  claro y oscuro, contraste de texto en ambas paletas y cambio de tema en caliente.

## Novedades de la 0.5.0 · más rápido de usar

- **Escribe para buscar en la barra** (modo paleta): con la barra abierta, teclea y las
  acciones se filtran al instante (título, categoría, descripción y coincidencia difusa).
  `↑↓` mueve, `Enter` ejecuta, `Backspace` borra. Ya no hace falta el menú `···` ni
  recordar el número: 35 acciones caben en la cabeza. Se puede desactivar en Ajustes.
- **La barra aparece junto a la selección**, no sobre el cursor (usa el rectángulo de UIA
  y cae al cursor si la app no lo expone).
- **Extracción adaptativa por proceso**: si en una app UIA no devuelve nada y el
  portapapeles sí, la segunda vez va directa al `Ctrl+C` con un UIA corto. Menos espera
  en Word, Firefox y compañía.
- **Refinamiento en un clic**: tras un resultado, chips "Más corto", "Más formal",
  "Más claro", "En viñetas", "Explicar" y "Traducir".
- **Versiones**: cada refinamiento o pregunta crea una versión nueva; `‹ 2/3 ›` para
  volver a cualquier respuesta anterior sin perderla. "Copiar" copia la versión activa como texto plano.
- **Diff antes/después**: en correcciones, reescrituras y traducciones, "Ver cambios" resalta en rojo lo que
  se quita y en verde lo que se añade. "Reemplazar" solo cuando te convence.
- **Atajos en el resultado**: `Enter` copia y cierra, `Ctrl+Enter` reemplaza y cierra,
  `Ctrl+R` reintenta. El contador de palabras pasa a la línea de estado.
- **Copiar claro**: "Copiar" copia **texto plano** (lo que se ve) y "Copiar como
  Markdown" (menú `···`) copia el original con marcas. "Reemplazar" también pega
  texto plano, sin `**` ni `#`.
- **Barra más segura**: escribir, espacio o retroceso nunca modifican el texto de la app
  de origen mientras la barra está abierta; `Esc` primero limpia la búsqueda.
- **Diálogos prácticos**: `Enter` pregunta en "Preguntar a la IA" (`Mayús+Enter` salto) y el
  historial se maneja con teclado (abrir, repetir, eliminar, buscar).
- **Pausa temporal desde el tray** (15/30/60/120 min) y aviso al cerrar Ajustes con
  cambios sin guardar.
- **Biblioteca de 31 prompts** (escritura, trabajo, código, estudio e idiomas) que se
  añaden como acciones personalizadas con un clic.
- **Historial privado**: se puede desactivar, cifrar con DPAPI o borrar al cerrar.
  Guarda también el texto original, así que cada entrada tiene botón **Repetir**.
- **Exportar/importar ajustes** en JSON (sin la API key salvo que se pida), con
  confirmación al reemplazar.
- **Salvaguarda offline** (`SELBAR_OFFLINE=1`): los tests no pueden hacer ninguna
  llamada de red. Modelo por defecto: **deepseek-v4.1-flash**.
- **154 auto-tests de lógica** (buscador, biblioteca, diff, cifrado del historial, export/import) + **30 comprobaciones de UI**.

### Base visual de la 0.4.0 (superada por la 0.6.0)

- **Sistema de diseño propio** (`Ui/Theme/`): tokens de color, tipografía, radios y
  espaciado + controles reestilizados (botones, entradas, combos, scrollbars,
  interruptores, listas, tooltips, expanders). Todo en oscuro Fluent, sin partes
  "sin vestir".
- **Color de acento del sistema**: se lee de Windows y se adapta para mantener
  contraste (relleno oscuro para texto blanco, tono claro para textos e iconos).
  El icono de la bandeja también usa tu acento.
- **Animaciones con criterio**: entrada de ventanas (fundido + desplazamiento),
  chips escalonados, control segmentado deslizante, interruptores animados,
  menú `···` que crece desde su esquina, barra de progreso indeterminada al
  escribir y fundido al cerrar. **Respeta "Efectos de animación" de Windows**:
  si lo desactivas, todo es instantáneo.
- **Barra flotante**: píldora con degradado y sombra suave, separadores con
  desvanecido, iconos redibujados, número de atajo en un distintivo y tooltips
  con descripción real.
- **Ventana de resultado**: control segmentado Vista/Texto animado, tarjeta de
  contenido, progreso de streaming, chat con placeholder y botón de envío que se
  habilita al escribir, jerarquía clara de acciones (la principal en acento),
  **bloques de código con botón Copiar**, zoom con aviso temporal.
- **Ajustes**: navegación lateral estilo Windows 11 (con iconos y sección activa),
  interruptores en vez de casillas, tarjetas por bloque, buscador con icono,
  distintivos de color por tipo de resultado, grabador de hotkey en caja
  monoespaciada y "zona de riesgo" diferenciada.
- **Historial**: dos paneles (lista + vista previa markdown en vivo), filtro con
  icono, fechas relativas ("hace 5 min"), estado vacío con mensaje y acciones
  Copiar/Abrir/Eliminar con estado de habilitado.
- Ventanas de **bienvenida**, **preguntar** y **editar acción** rediseñadas con
  cabecera, tarjetas y pies de diálogo consistentes.
- **Menú del tray oscuro con iconos** y barra lateral de Ajustes con el mismo
  lenguaje visual.
- **Ajustes de accesibilidad**: foco visible en controles, contraste alto en
  textos secundarios y objetivos de clic de 32 px.

### Lo esencial de la 0.3.0

- La barra no roba el foco: Pegar / Cortar / Reemplazar actúan sobre la app de origen.
- "Reemplazar" recuerda la ventana de origen y le devuelve el foco antes del `Ctrl+V`.
- Hooks sin bloqueos, portapapeles a prueba de balas y proveedor de IA reescrito
  (un `HttpClient`, reintentos, parser SSE tolerante, más proveedores).
- Grabador de hotkey, ajustes atómicos con `.bak`, modo portable real.
- Acciones nuevas: Abrir URL, Formatear JSON, Quitar líneas repetidas, comandos.
- **117 auto-tests** y prueba de humo de UI (ver [Tests](#tests)).

---

## Catálogo (todo toggleable en Ajustes → Acciones)

| Grupo | Acciones | Por defecto |
|---|---|---|
| IA Traducir | Traducir, Traducir al inglés | Traducir ON |
| IA Reescribir | Mejorar redacción, Corregir ortografía, Hacer conciso, Tono formal | 2 primeras ON |
| IA Explicar | Resumir, Explicar, Puntos clave, Definir término, Preguntar a la IA, Resumir en 1 línea, Extraer tareas | Resumir+Explicar+Preguntar ON |
| Portapapeles (sin IA) | Copiar, Cortar, Pegar, Pegar sin formato, Seleccionar todo, Eliminar | Solo Copiar ON |
| Buscar (sin IA) | Google, Wikipedia, YouTube, StackOverflow, WhatsApp (teléfono), Google Lens (URL de imagen), Redactar correo, Limpiar enlace, Ver destino, Abrir en Maps, Perfil en X/IG/FB | Google, WhatsApp, Lens, Limpiar, Destino, Maps, correo y sociales ON |
| Sistema (sin IA) | Abrir URL, Pulsar Enter, Pulsar Tab, Descargar vídeo/audio de redes (con yt-dlp), Leer en voz alta, Guardar en Obsidian, Abrir ubicación | Abrir URL, Descargas, Leer y ubicación ON |
| Texto (sin IA) | MAYÚSCULAS, minúsculas, Alternar, Capitalizar, Limpiar espacios, Quitar saltos, Ordenar líneas, Quitar repetidas, Formatear JSON, Contar palabras y caracteres, Ver color HEX, Estilos de letra, Generar QR, Calcular, Convertir, Analizar fecha | Color/estilos/QR/calc/convertir/fecha ON |
| IA Explicar (+web) | Resumir enlace (lee la página o el PDF con OCR y lo resume) | OFF |

La acción con ★ va primera cuando el texto parece un correo, fecha, ruta o enlace
con seguimiento (se apaga en Ajustes → Disparadores).

Reglas de contexto: `Definir` solo con 1 palabra, `Resumir` solo con texto largo (>60),
`Ordenar`/`Quitar repetidas` solo multivía, `Abrir URL` solo si la selección es una URL
(regex). El popup muestra un máximo configurable de chips (8 por defecto) y el resto va
al menú `···`.

---

## Requisitos

- Windows 10/11 x64 (probado en Windows 11 24H2/25H2). El OCR usa el paquete de
  idioma OCR de Windows. QRCoder va incluido; Groq es opcional y necesita su propia API key.
- **yt-dlp** solo si quieres descargar de YouTube: el ZIP no lo incluye. Si falta,
  la app ofrece instalarlo.
- Para compilar: **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8`.
- Para el zip normal: **.NET 8 Desktop Runtime** en el equipo destino.
  Usa `.\build.ps1 -SelfContained` si no quieres depender de nada.

## Compilar, probar y empaquetar

```powershell
# Todo en uno: tests + publicación + zip portable
.\build.ps1

# Solo compilar / ejecutar en desarrollo
dotnet build src\Selbar\Selbar.csproj -c Release
dotnet run --project src\Selbar\Selbar.csproj

# Variantes
.\build.ps1 -SkipTests            # sin auto-tests
.\build.ps1 -NoZip                # publica en dist\ pero no comprime
.\build.ps1 -SelfContained        # .exe único autocontenido (no requiere .NET)
.\build.ps1 -Configuration Debug
```

Salidas:

- `dist\Selbar-<versión>\` — carpeta publicada (con `portable.txt` y `LEEME.md`).
- `Selbar-<versión>-portable.zip` — zip listo para descomprimir y ejecutar.

El icono (`src\Selbar\Assets\app.ico`) se genera con `tools\make-icon.ps1` y se
embebe en el `.exe`.

## Instalar

1. Descomprime el zip en una carpeta normal (Escritorio, `C:\Tools\Selbar`...).
2. Ejecuta `Selbar.exe`. Aparece en la **bandeja del sistema**; no abre ventana.
3. Primer arranque: ventana de bienvenida + opción de abrir Ajustes.
4. Opcional: *Iniciar con Windows* en Ajustes → Disparadores (registro `HKCU\...\Run`,
   sin permisos de administrador).

> Como el zip trae `portable.txt`, los ajustes se guardan en `.\data` junto al `.exe`.
> Si lo pones en `Program Files` (sin permiso de escritura), cae automáticamente a
> `%AppData%\Selbar`.

### Instalador (opcional)

`tools/installer.iss` (Inno Setup 6) genera un instalador **por usuario, sin
administrador** a partir del publicado self-contained:

```powershell
.\build.ps1 -SelfContained
iscc tools\installer.iss /DMyVersion=<la del .csproj>
```

Instala en `%LocalAppData%\Programs\Selbar` con accesos directos, y al
desinstalar conserva tus ajustes (`%AppData%\Selbar` no se toca). La
versión del instalador se verifica contra el `.csproj` en CI.

## Configurar la IA

1. Clic derecho en el icono del tray → **Ajustes** (o doble clic).
2. Pestaña **IA** → elige proveedor. La Base URL y el modelo se rellenan solos.
3. Pega tu **API key** y pulsa **Probar conexión** (debe salir `OK: ...`).
4. **Guardar**. La key se cifra con **DPAPI** en `settings.json` (solo tu usuario de
   Windows puede descifrarla).

Proveedores incluidos: **OpenCode GO**, OpenAI, OpenRouter, DeepSeek, Groq, Mistral,
xAI, Gemini (endpoint compatible), Ollama y LM Studio (locales, sin key), y
**Personalizado** (cualquier endpoint `/chat/completions`).

- OpenCode GO: `https://opencode.ai/zen/go/v1` y un modelo de
  `https://opencode.ai/zen/go/v1/models` (solo los que usan `/chat/completions`).
  El valor por defecto es **`deepseek-v4.1-flash`**.
- OpenRouter gratis: modelo `openrouter/free`.
- Ollama: `http://localhost:11434/v1`, modelo `llama3.1`, sin key.
- La app envía `x-opencode-session` automáticamente cuando detecta OpenCode GO.

Opciones avanzadas (dentro de la pestaña IA): instrucción de sistema, temperatura,
máximo de tokens, timeout y máximo de caracteres enviados (recorte de seguridad).

## Hotkey global

Por defecto **Ctrl+Shift+Espacio**. En Ajustes → Disparadores pulsa **Grabar** y
teclea la combinación (necesita Ctrl/Alt/Shift/Win + una tecla). Funciona aunque
desactives el popup automático: selecciona texto y púlsala. Si Windows avisa de
conflicto, elige otra.

## Interfaz

Todo comparte un mismo sistema de diseño (`src/Selbar/Ui/Theme/`) y un marco
propio (`FluentWindow`) que envuelve cada ventana con el de Windows 11:

- **Marco**: `WindowChrome` + barra de título integrada (`TitleBar`) con icono, título,
  botones de sistema, esquinas redondeadas y borde de DWM. Al maximizar se
  compensa el área de cliente para que nada quede fuera de pantalla.
- **Tema**: `Theming` lee `AppsUseLightTheme` (o el modo elegido en Ajustes →
  Apariencia), repinta los pinceles `DynamicResource` de la paleta Fluent y avisa a
  las ventanas para actualizar el marco. `SystemEvents.UserPreferenceChanged` lo
  mantiene sincronizado si cambias Windows con la app abierta.
- **Tokens**: superficies, texto, trazos, estado (éxito/aviso/peligro), controles,
  marcos, acento, tipografía (`Segoe UI Variable`), radios (2/4/8/12/16) y espaciado.
- **Controles**: botón estándar, principal, sutil, icono y destructivo; campos de
  texto y contraseña con línea de acento; combo con chevrón; casilla; interruptor
  40×20; listas; scrollbars finas; menús contextuales; tooltips; expander; tarjetas,
  badges y filas de ajustes con separadores.
- **Acento**: `Theming` lee `HKCU\...\DWM\ColorizationColor`, quita el alfa y deriva
  `AccentFill` (relleno con texto blanco) y `AccentText` (textos e iconos) con
  contraste garantizado en ambos temas.
- **Movimiento**: `Motion` centraliza duraciones y curvas y consulta
  `SystemParameters.ClientAreaAnimation`; si Windows tiene desactivados los
  efectos, las animaciones no se ejecutan.

Por defecto la app **sigue el tema de Windows**; en Ajustes → Apariencia puedes
forzar Claro u Oscuro y el cambio se aplica al instante.

Ventanas:

- **Indicador al seleccionar**: botón circular pequeño que no roba el teclado. Al pasar el
  ratón se convierte en la barra con una animación; al hacer clic se abre en modo
  búsqueda.

  ![Indicador](docs/screenshots/indicador.png)

- **Barra**: flyout Fluent con borde y sombra, chips de 36 px con icono + etiqueta +
  número, separadores por grupo y menú `···` (glifo nativo).

  ![Barra flotante](docs/screenshots/barra.png)

  Con la barra abierta por el atajo o haciendo clic en el indicador, escribir filtra al instante:

  ![Barra con búsqueda](docs/screenshots/barra-busqueda.png)

- **Resultado**: cabecera arrastrable con estado, botón **Editar / Vista previa**,
  tarjeta de contenido, menú contextual, chat de seguimiento, acción principal en
  acento y menú `···` con "Copiar como Markdown", "Guardar como…" y ajustes de IA.
  Si la IA falla, el estado se vuelve rojo y el pie cambia a **Reintentar** y
  **Abrir ajustes de IA** (con una pista si es un 401/403).

  ![Ventana de resultado](docs/screenshots/resultado.png)

  Con versiones (refinamientos) y diff antes/después:

  | Versiones | Diff |
  |---|---|
  | ![Versiones](docs/screenshots/resultado-versiones.png) | ![Diff](docs/screenshots/resultado-diff.png) |

  Si algo falla (por ejemplo, una API key que falta), no se disfraza de resultado:

  ![Error de IA](docs/screenshots/resultado-error.png)

- **Biblioteca de prompts**: 31 recetas listas para añadir con un clic.

  ![Biblioteca](docs/screenshots/biblioteca.png)

- **Ajustes**: barra lateral con secciones (Acciones, IA, Disparadores, Apariencia,
  Avanzado), tarjetas, interruptores, buscador y diagnóstico.

  | Acciones | Inteligencia artificial |
  |---|---|
  | ![Ajustes · Acciones](docs/screenshots/ajustes-acciones.png) | ![Ajustes · IA](docs/screenshots/ajustes-ia.png) |
  | **Apariencia** | **Avanzado** |
  | ![Ajustes · Apariencia](docs/screenshots/ajustes-apariencia.png) | ![Ajustes · Avanzado](docs/screenshots/ajustes-avanzado.png) |
  | **Disparadores** | |
  | ![Ajustes · Disparadores](docs/screenshots/ajustes-disparadores.png) | |

- **Estilos de letra**, **color HEX**, **código QR**, **audio a texto** y **descargas**:

  | Estilos de letra | Color HEX |
  |---|---|
  | ![Estilos de letra](docs/screenshots/estilos.png) | ![Color HEX](docs/screenshots/color.png) |
  | **Código QR** | **Audio a texto (Groq)** |
  | ![Código QR](docs/screenshots/qr.png) | ![Transcribir audio](docs/screenshots/transcribir.png) |

  ![Descarga de YouTube y otras redes](docs/screenshots/descarga.png)

- **Historial**: lista + vista previa markdown, filtro, fechas relativas y estado vacío.

  ![Historial](docs/screenshots/historial.png)

- **Diálogos**: bienvenida, preguntar a la IA y nueva acción con el mismo marco.

  | Bienvenida | Preguntar a la IA |
  |---|---|
  | ![Bienvenida](docs/screenshots/bienvenida.png) | ![Preguntar a la IA](docs/screenshots/preguntar.png) |
  | **Nueva acción** | **Menú ···** |
  | ![Nueva acción](docs/screenshots/accion-nueva.png) | ![Menú ···](docs/screenshots/menu.png) |

- **Tema claro** (sigue el ajuste de Windows):

  | Ajustes en claro | Resultado en claro |
  |---|---|
  | ![Ajustes en claro](docs/screenshots/tema-claro-ajustes.png) | ![Resultado en claro](docs/screenshots/tema-claro-resultado.png) |

## OCR, Lens, QR y YouTube

**OCR de una imagen**

1. Copia una imagen (o haz `Win` `Shift` `S` y recorta una zona; el recorte queda en el portapapeles).
2. Pulsa tu atajo global o usa **tray → Leer texto de una imagen (OCR)**.
3. El texto aparece en la ventana de resultado. Desde ahí puedes copiarlo, refinarlo o
   usar los chips de traducción/resumen.

Todo el reconocimiento ocurre en tu equipo con la API de Windows; la imagen no se envía
a ningún servidor. Necesita el paquete OCR del idioma en Windows.

![Resultado de OCR](docs/screenshots/resultado-ocr.png)

**Descargar de redes sociales**

1. Selecciona un enlace de YouTube, TikTok, Instagram, Facebook, X/Twitter, Vimeo,
   Twitch, Reddit, SoundCloud, Dailymotion, Pinterest, LinkedIn, Rumble, Odysee,
   Bitchute, Kick, VK, OK.ru, Bilibili, Niconico, Bandcamp, Archive.org, Snapchat o TED.
2. Elige **Descargar vídeo** o **Descargar audio** en la barra.
3. La ventana muestra el progreso y permite cancelar. Al terminar, “Abrir carpeta”.
   El botón **Sitios compatibles** muestra la lista desde la propia ventana.

Se usa [yt-dlp](https://github.com/yt-dlp/yt-dlp), que no va incluido en el ZIP. Si no
lo encuentra en el PATH, junto a la app o en su carpeta de datos, ofrece instalarlo
desde la release oficial. La lista completa y actualizada está en
[supportedsites.md](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md).
Respeta los derechos de autor y las condiciones de cada plataforma.

**Google Lens**

- Con una **URL de imagen** seleccionada: acción **Buscar imagen en Google Lens**.
- Con una **imagen copiada**: **tray → Buscar imagen en Google Lens**. La app pide
  confirmación y sube la imagen a Google para la búsqueda inversa. No se guarda en
  disco ni se envía nada si cancelas.

**Código QR**

Selecciona cualquier enlace y elige **Generar código QR**. La ventana permite
**Copiar imagen** (para pegarla en un chat o documento) o **Guardar PNG**. Funciona
sin conexión.

**Audio a texto con Whisper (Groq)**

1. Crea una API key en [console.groq.com/keys](https://console.groq.com/keys).
2. Pégala en Ajustes → Inteligencia artificial → **Transcripción de audio (Groq)** y
   guarda (se cifra con DPAPI).
3. **tray → Transcribir audio con Groq…**, elige el archivo y pulsa **Transcribir**.
   Límite de Groq: 25 MB por archivo. El texto se abre en la ventana de resultado con
   las mismas acciones de copiar/refinar.

## Cómo funciona

```
hooks de ratón/teclado (SelectionMonitor)
        │  drag / doble clic / long-press / Shift+flechas / hotkey
        ▼
TextExtractor      UIA (hilo MTA con timeout) → Ctrl+C con restauración del portapapeles
        ▼
ActionRegistry.GetVisibleActions(texto)   toggles + reglas de contexto (+ regex)
        ▼
ActionBarWindow (no activable)  chips medidos, separadores, ···, atajos 1-9
        ▼
ActionExecutor   local (texto, portapapeles, URL, comando) o IA (streaming SSE)
        ▼
ResultWindow   markdown en vivo, detener/reintentar, chat, copiar, guardar, reemplazar
```

Detalles que importan:

- **No se roban pulsaciones**: al seleccionar solo aparece un indicador que no captura
  el teclado. El menú se abre al pasar el ratón, al hacer clic o con el atajo; la
  búsqueda al escribir solo se activa de forma deliberada (atajo o clic en el indicador).
  Se cierra con clic fuera, rueda, clic derecho, `Esc` o temporizador.
- **Apps excluidas**: por defecto `Selbar` (no reacciona a sí misma).
- **Campos de contraseña**: nunca se extraen ni se envían.
- **Pantalla completa** (juegos/vídeo): no se muestra la barra.
- **Atajos 1-9** para los 9 primeros chips, válidos 4 s (se muestran en el chip si
  activas la opción) y **búsqueda escribiendo** (desactivable).
- **Historial** con filtro, vista previa markdown, copiar, **repetir** la acción y
  controles de privacidad (desactivar, cifrar con DPAPI, borrar al salir).
- **Instancia única**: si la app ya está abierta, el segundo arranque trae Ajustes al
  frente en vez de duplicar iconos.
- Las entradas de IA se recortan a 12.000 caracteres (configurable).
- El historial guarda los últimos 50 resultados (60.000 caracteres por entrada).

## Atajos de teclado

**Barra**

| Atajo | Acción |
|---|---|
| Escribir (atajo o clic) | Filtrar acciones al instante |
| `↑` `↓` | Mover la selección del filtro |
| `Enter` | Ejecutar la primera coincidencia |
| `Backspace` | Borrar una letra del filtro |
| `Esc` | Borrar el filtro; si ya está vacío, cerrar |
| `1`–`9` | Ejecutar esa acción (sin filtro activo) |

**Ventana de resultado**

| Atajo | Acción |
|---|---|
| `Esc` | Cerrar |
| `Enter` | Copiar como texto y cerrar |
| `Ctrl` `Enter` | Reemplazar la selección y cerrar |
| `Ctrl` `R` | Reintentar |
| `Ctrl` `+` / `Ctrl` `−` | Zoom (persistente) |
| `Ctrl` `0` | Zoom al 100 % |
| `Ctrl` `S` | Guardar `.md` / `.txt` |
| `Ctrl` `Shift` `C` | Copiar como texto |
| `Enter` (en el chat) | Enviar seguimiento (`Mayús+Enter` = salto de línea) |

**Historial**

| Atajo | Acción |
|---|---|
| `Enter` | Abrir el resultado con formato |
| `Ctrl` `Enter` | Repetir la acción con el mismo texto |
| `Supr` | Eliminar la entrada |
| `Ctrl` `F` | Buscar |
| `Esc` | Cerrar |

**Ajustes**: `Ctrl` `S` guarda. Si cierras con cambios sin guardar, te pregunta.
**Preguntar a la IA**: `Enter` pregunta, `Mayús+Enter` salto de línea.

## Acciones personalizadas

Ajustes → Acciones → **+ Añadir**. Tres tipos:

1. **Prompt de IA** — plantilla con `{text}`; eliges si muestra ventana, copia o
   reemplaza. Placeholders: `{text}`, `{lang}`, `{date}`, `{time}`, `{app}`.
2. **Abrir URL** — `https://ejemplo.com/?q={text}` (la selección se codifica sola).
3. **Comando** — se ejecuta con `cmd.exe` y su salida se muestra en una ventana.
   Se sustituye `{text}`. *Se ejecuta con tus permisos: úsalo solo con comandos de
   confianza.*

## Datos y modo portable

| Qué | Dónde |
|---|---|
| Ajustes | `settings.json` (+ `settings.json.bak`) |
| API key | dentro de `settings.json`, cifrada con DPAPI |
| Historial | `history.json` |
| Log | `log.txt` (rotación automática, ~512 KB) |

Carpeta de datos, por orden: `SELBAR_DATA` (variable de entorno) →
`portable.txt` junto al `.exe` (`.\data`) → `%AppData%\Selbar`.

Ajustes → Avanzado permite abrir la carpeta, abrir el log, copiar un diagnóstico y
restablecer todo de fábrica.

## Tests

```powershell
# Lógica (parser SSE, hotkeys, registro, prompts, markdown, ajustes, historial, iconos)
dotnet run --project tools\SelfTest -c Release

# Prueba de humo de UI: crea/muestra/cierra todas las ventanas (valida XAML y recursos)
dotnet run --project tools\SelfTest -c Release -- --ui

# Además, guarda capturas PNG de cada ventana y de cada pestaña de Ajustes
dotnet run --project tools\SelfTest -c Release -- --ui --shots

# Las mismas comprobaciones con el tema claro
dotnet run --project tools\SelfTest -c Release -- --ui --shots --light
```

La smoke de UI incluye una comprobación de **contraste de texto** sobre cada ventana
(texto sobre fondo real, componiendo capas translúcidas): detecta títulos o distintivos
ilegibles en cualquiera de los dos temas. También comprueba el **cambio de tema en
caliente**: Ajustes → Apariencia repinta la paleta sin reiniciar y las ventanas abiertas
adoptan el marco claro u oscuro. En total son 48 comprobaciones de UI (42 de ventanas y
contraste + 2 de tema + 1 de OCR de extremo a extremo + 3 del indicador: no captura
el teclado, no consume la tecla y se cierra al escribir).

`.\build.ps1` ejecuta los tests de lógica antes de publicar (`-SkipTests` para saltarlos).

Los tests **no hacen ninguna llamada de red**: activan `SELBAR_OFFLINE=1`, que hace que el
cliente de IA falle al instante si algo intentara conectar. La prueba de humo de UI configura
el proveedor con Base URL vacía. No se consume ningún modelo ni ninguna API de pago.

## Desarrollo

Si vas a modificar el proyecto (persona o agente), empieza por
[AGENTS.md](AGENTS.md):

- [ARQUITECTURA.md](docs/ARQUITECTURA.md) — capas, flujos, threading, ventanas,
  tema, IA, tests y limitaciones.
- [COMO_EXTENDER.md](docs/COMO_EXTENDER.md) — recetas para añadir acciones,
  ventanas, ajustes, colores e integraciones.
- [DECISIONES.md](docs/DECISIONES.md) — el porqué del indicador, las superficies
  sólidas, DPAPI, los tests offline, etc.
- [docs/README.md](docs/README.md) — índice de la documentación.

```powershell
# Compilar y ejecutar la suite de lógica (offline)
dotnet build tools\SelfTest\SelfTest.csproj -c Release
dotnet run --project tools\SelfTest -c Release --no-build

# Smoke de UI y capturas (no hace red)
dotnet run --project tools\SelfTest -c Release --no-build -- --ui
dotnet run --project tools\SelfTest -c Release --no-build -- --ui --shots

# Publicar: tests + dist + LEEME + ZIP portable
.\build.ps1
```

`build.ps1` no ejecuta el smoke de UI ni copia capturas a `docs/screenshots/`; el
copiado es manual. Antes de entregar, revisa el checklist de `AGENTS.md`.

## Créditos

- [QRCoder](https://github.com/codebude/QRCoder) (MIT) para los códigos QR.
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) para las descargas; no va incluido en el ZIP.
- OCR y Google Lens usan servicios/API de Windows y Google respectivamente.

## Solución de problemas

- **No aparece la barra**: revisa que no estés en pausa (tray → Reanudar), que la app
  actual no esté excluida, que el popup automático esté activado y que la selección
  supere la longitud mínima. Prueba la hotkey: si tampoco, el hook está bloqueado por
  seguridad (algunas apps de juegos/anticheat).
- **La hotkey no se registra**: otro programa la usa. Cambia de combinación.
- **`HTTP 400 ... MissingSessionID`**: es OpenCode GO sin session ID; la app lo genera
  sola al cargar los ajustes. Si lo borraste a mano, guarda Ajustes de nuevo.
- **`HTTP 401/403`**: key incorrecta o caducada. Vuelve a pegarla y prueba.
- **`HTTP 404`**: Base URL mal (suele sobrar `/chat/completions` o el modelo no existe).
- **El modelo no aparece en la lista**: algunos endpoints exigen la key para `/models`;
  escribe el id a mano.
- **"No se pudo copiar"**: otra app tiene el portapapeles bloqueado; reintenta.
- **Fondos negros en las esquinas**: corregido en 0.9.0. Las ventanas usan superficies
  sólidas del tema recortadas al radio del marco; no dependen de Mica.
- **SmartScreen** en el `.exe`: es un binario sin firmar. *Más información → Ejecutar*.
- **Arranque lento la primera vez**: se carga JIT y se genera el icono; normal.

Log en `log.txt` (Ajustes → Avanzado → Abrir log). Los errores nunca tumban la app:
se registran y se muestra el mensaje en la ventana de resultado.

## Estructura

```
src/Selbar/
  App.xaml(.cs)              arranque, instancia única, popups, hooks
  Native/                    Win32 (hooks, SendInput, foco), hotkeys, colocación, DWM
  Core/Ai/                   proveedor OpenAI-compatible, parser SSE, presets
  Core/Imaging/              OCR local con Windows.Media.Ocr
  Core/Text/                 estilos Unicode y códigos de color
  Core/Downloads/            integración con yt-dlp
  Core/Actions/              modelo, presets, registro con contexto, ejecutor
  Core/Config/               ajustes + migración, guardado atómico, DPAPI
  Core/Selection/            hooks globales y extracción de texto (UIA/Ctrl+C)
  Core/Platform/             rutas de datos, portapapeles seguro, foco recordado
  Core/History/              historial de resultados
  Ui/                        barra, menú ···, resultado, ajustes, historial, diálogos
  Ui/Theme/                  tokens Fluent (claro/oscuro), controles y marco de ventana
  Ui/Motion.cs               animaciones (respeta los efectos de Windows)
docs/                        arquitectura, recetas, decisiones y capturas
tools/
  SelfTest/                  auto-tests + prueba de humo de UI
  make-icon.ps1              genera el .ico
  installer.iss              instalador opcional por usuario (Inno Setup)
build.ps1                    tests + publish + zip
AGENTS.md                    reglas y checklist para agentes
```

## Limitaciones conocidas

- UIA no cubre todas las apps; en algunas el texto sale por el fallback `Ctrl+C`
  (con restauración del portapapeles).
- Los atajos 1-9 ejecutan esa acción mientras la barra acepta teclado y no hay
  filtro activo (abierta por atajo o clic): si escribes un número justo entonces,
  se ejecuta esa acción.
- El zoom y el tamaño de la ventana de resultado son globales (no por acción).
- No hay firma de código: Windows puede avisar al ejecutar el `.exe`.
