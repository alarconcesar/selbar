# Revisión de UI · 0.13.0

Revisión ventana por ventana con un objetivo doble: **menos ruido para hacer lo de
siempre** y **correcciones de fondo** que la UI estaba escondiendo. No se quitó
ninguna función: lo experto se agrupa, no desaparece.

- **Alcance:** 16 ventanas (incluida la oculta de bandeja), 132 hallazgos
  documentados (2 P0, 48 P1, 82 P2).
- **Método:** lectura completa de cada XAML y su code-behind, contraste con las
  capturas de `docs/screenshots/` (históricas: solo composición) y verificación
  estática. En Linux **no se ejecutó WPF**: ver *Verificación*.
- **Invariantes respetados:** el indicador no captura teclado; barra y menú siguen
  sin activarse (`WS_EX_NOACTIVATE`); Mica desactivado; tests offline; sin
  dependencias nuevas; modelo por defecto intacto.

## Reglas de movimiento aplicadas

| Situación | Regla |
| --- | --- |
| Barra, menú, búsqueda, atajos, Esc | Sin animación: la utilidad se usa decenas de veces al día |
| Feedback de pulsación (ratón) | Contenido del botón a `0.98`, 100 ms; retorno 100 ms; solo `RenderTransform` |
| Revelado ocasional | `Motion.FadeIn` explícito, ≤150 ms, opacidad + desplazamiento ≤6 px |
| Ventana ocasional | `Motion.FadeInWindow`, 180 ms |
| Movimiento desactivado en Windows | Sin relojes; los bucles devuelven un `Storyboard` inerte y el contenido queda visible |

Nunca se animan `Width`, `Height`, `Margin` ni el layout; nunca se escala desde 0;
no hay cascadas de chips ni animación de tokens.

## Ventana por ventana

### ActionBarWindow (indicador + barra)
| Antes | Después | Por qué |
| --- | --- | --- |
| El catálogo se truncaba con `.Take(30)` antes de repartir barra y menú | Se conserva completo; solo se limita lo que se pinta | Una acción activa no puede quedar inencontrable |
| Hotkey con el mismo texto y barra visible: `return` sin hacer nada | `Promote()` convierte la instancia existente en barra de teclado | La intención explícita debe funcionar siempre |
| Entrada/salida con fundido y chips escalonados | Apertura, cierre y ejecución inmediatos | Uso de decenas de veces al día: la animación solo estorba |
| Dígitos 1-9 caducaban a los 4 s y se consumían aunque no hubiera chip | Sin caducidad; solo se consumen si el número existe | Ni teclas tragadas ni atajos muertos |
| La búsqueda se ensanchaba sin límite | Ancho acotado al área útil y `Ver todos (N)` para el resto | No salirse de la pantalla ni perder coincidencias |

### OverflowWindow (menú ···)
| Antes | Después | Por qué |
| --- | --- | --- |
| Las flechas seguían yendo a la barra con el menú abierto | El menú abierto recibe flechas, Inicio/Fin, Enter y Retroceso | Lo visible debe ser lo que responde |
| Filas escalonadas y altura fija que podía salirse del escritorio | Sin cascada; alto y posición limitados al área útil, con scroll | Que la última opción siempre sea alcanzable |
| Lista plana con un badge «IA» repetido en cada fila | Agrupación por categoría y una sola aclaración | Menos ruido para elegir |

### ResultWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Seis refinamientos permanentes + chat + pie + dos botones de acento | Barra compacta `Editar / Ajustar texto / Ver cambios / versión`; el contenido manda | El resultado es lo importante |
| **Reintentar borraba versiones** y `Ctrl+R` vaciaba incluso resultados locales | Regenerar crea una **versión nueva**; sin callback, `Ctrl+R` no hace nada | P0: se perdía trabajo del usuario |
| **Refinar desde edición concatenaba** el texto viejo con la respuesta | Antes de cada turno se guarda el borrador y la vista arranca limpia y sincronizada | P0: resultados mezclados |
| El foco saltaba al chat al terminar | Se habilita sin robar foco; el usuario decide cuándo escribir | No perder el punto de lectura |
| Un error se pegaba al texto y se podía copiar/reemplazar | Estado de error separado, matriz única de capacidades y guardas en atajos | No reemplazar una selección por un mensaje de error |
| `Enter` cerraba aunque fallara el portapapeles | Solo cierra si el copiado se confirmó | No perder el resultado |
| «Usar como selección» recortaba a 20 000 caracteres en silencio | Aviso explícito y confirmación | Nada silencioso |

### AskDialog
| Antes | Después | Por qué |
| --- | --- | --- |
| Diálogo fijo de 580×510 con 300 caracteres de contexto y sin alcance | 560×430 redimensionable; instrucción primero y `Texto seleccionado · N caracteres` | Una tarea de un campo no necesita una pantalla |
| «Preguntar» vacío no hacía nada | CTA deshabilitada y ayuda `Enter` / `Mayús+Enter` siempre visible | Que el estado explique el porqué |

### SettingsWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Siete acciones planas (Añadir, Editar, Eliminar, subir/bajar, Biblioteca, Restablecer) | `Buscar / Solo activas / Añadir` arriba; editar, ordenar y restablecer en menús | Lo diario primero |
| Parámetros técnicos al mismo nivel que proveedor, modelo y clave | Esenciales visibles; temperatura, tokens, timeout y prompt en avanzados plegados | Configurar IA no debería ser un formulario de ingeniería |
| **Guardar aplicaba cambios antes de validar** y cerrar no revertía acciones | Borrador de ajustes y de acciones; se valida todo, se escribe y recién entonces se aplica | La promesa «se aplican al guardar» debe ser cierta |
| Un fallo de disco cerraba igual | `TryApply` informa y conserva la ventana y el borrador | No fingir guardado |
| Navegar reiniciaba el scroll y animaba 190 ms | Se recuerda la posición por sección; teclado sin animación | Retomar donde estabas |
| La cabecera del `Expander` no estaba enlazada a `IsExpanded` | Clic en la cabecera expande (binding `TwoWay`) | Bug real de plantilla |

### ActionEditDialog
| Antes | Después | Por qué |
| --- | --- | --- |
| Campo vacío + sintaxis `{text}` obligatoria | Etiqueta por tipo, botón `Insertar {text}` y variables plegadas | Escribir la intención, no memorizar marcadores |
| Selector de salida deshabilitado que contradecía el efecto real | Resumen real («Se abrirá en tu navegador» / «Se ejecutará localmente») | Un control atenuado que miente es peor que no tenerlo |
| Formulario sin scroll y `Cancelar` sin `IsCancel` | Formulario desplazable y Escape cancela | Texto grande y pantallas pequeñas |

### PromptLibraryWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| 31 botones «Añadir» idénticos; el prompt solo en tooltip | Lista seleccionable + detalle con prompt completo y efecto real | Ver antes de añadir |
| El mensaje «Añadida» se sobrescribía al refrescar | Mensaje estable y estado de colisión veraz | Feedback que no desaparece |
| No se avisaba que algunas recetas **reemplazan** la selección | Etiqueta de resultado por receta | Evitar sorpresas al ejecutar |

### WelcomeWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Texto recortado dentro de un `StackPanel` horizontal | Grid con ancho real y tres pasos numerados | La primera impresión se lee completa |
| `Topmost` permanente | Ventana normal centrada | No secuestrar el escritorio |
| Anunciaba el atajo aunque estuviera desactivado | Estado real según `HotkeyEnabled` | No prometer lo que no funciona |

### HistoryWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| `Repetir` era la acción acentuada y el pie acumulaba acciones destructivas | `Copiar resultado` primaria; `Abrir` secundaria; repetir/eliminar/limpiar en menús | Reutilizar es local; repetir es otra petición |
| `Enter` en cualquier parte abría el resultado | Enter/Supr solo en la lista | No robar el teclado de botones y filtros |
| Una entrada cifrada ilegible se mostraba como texto y se podía repetir | Estado «cifrado no disponible» y acciones bloqueadas | No enviar un blob a la IA |
| Cada tecla releía y descifraba el historial | Instantánea en memoria, sin recrear el Markdown por carácter | Búsqueda fluida |

### ClipboardHistoryWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| `Pegar` era la primaria sin saber si hay destino | `Copiar y cerrar` primaria; pegar secundaria y de mejor esfuerzo | Copiar siempre es seguro |
| «Portapapeles vaciado» aunque solo se borraba el historial | «Vaciar historial de copias» + comprobación real | No mentir sobre privacidad |
| La lista no se refrescaba al reabrir | Refresco al activar y tras copiar sin cerrar | Lo que ves es lo que hay |

### TextStyleWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Un botón «Copiar» por cada estilo | Lista seleccionable y una CTA `Copiar [estilo]`; doble clic/Enter copia | Una acción, no doce |
| El preview cortaba a 70 unidades UTF-16 (rompía emojis) | Corte por grafemas (`StringInfo`) y copia del texto completo | No mostrar símbolos partidos |

### ColorPreviewWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| `Copiar HEX` duplicado y el alfa se perdía en silencio | Una primaria; con alfa copia HEX8 y deja HEX6 explícito | Un color transparente no puede volverse opaco |
| Entrada inválida dejaba copiar el texto como si fuera HEX | Estado inválido bloquea todas las copias | Coherencia |
| `rgba()`/`hsla()` usaban la cultura actual | Números con `InvariantCulture` | CSS válido siempre |

### QrWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Si fallaba el render, `Guardar PNG` seguía activo y no hacía nada | Ambas salidas dependen del mismo bitmap; error dentro del preview | No ofrecer acciones muertas |
| Imagen de 340 px fija | `Viewbox` uniforme con quiet zone | Escanea igual en cualquier tamaño |
| Copiar imagen con esperas en el hilo de UI | Copia asíncrona en STA con reintentos | No congelar la ventana |

### DownloadWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Consola de yt-dlp siempre visible y nota legal compitiendo | `Detalles técnicos` plegado; progreso, etapa y destino primero | Una descarga no es una terminal |
| Instalar yt-dlp no se podía cancelar y cerrar no la detenía | CTS ligada a Cancelar y al cierre | Poder abortar |
| Errores solo con `Cerrar` | `Reintentar` en el lugar de la primaria y detalle a demanda | Recuperación real |

### TranscribeWindow
| Antes | Después | Por qué |
| --- | --- | --- |
| Tarjeta de API key de Groq siempre visible | Solo si falta; si está, resumen y ajustes en `Más` | El objetivo es transcribir |
| Se podía cambiar de archivo mientras transcribía | Archivo y modelo capturados; selección bloqueada durante el envío | La UI no puede aparentar otro archivo |
| `Transcribir` se habilitaba con archivo inválido | Una sola validación gobierna la CTA | Estados coherentes |

### MainWindow (bandeja)
| Antes | Después | Por qué |
| --- | --- | --- |
| Mezclaba acceso diario, herramientas y mantenimiento | Frecuentes en la raíz; `Imagen` y `Más` como submenús | Menú corto sin perder nada |
| «Pausa» parecía pausar toda la app | «Pausar selección» con hora de fin y reanudar visible | Alcance explícito |
| El fallo al registrar el atajo de dictado solo iba al log | Estado visible del atajo | Que se note cuando no funciona |

## Correcciones de fondo (no visuales)

- **P0** Reintentar ya no destruye versiones; refinar desde edición no mezcla texto.
- **P0** Guardar ajustes valida antes de tocar el estado vivo, informa fallos de
  disco y `Descartar` revierte de verdad. El borrador conserva la identidad de
  `Ai`/`Triggers`/`History`, así que el monitor de selección y el ejecutor ven los
  cambios al instante (verificado con probe).
- `Expander`: la cabecera ahora expande (binding `TwoWay` a `IsExpanded`).
- Navegación del menú `···` y dígitos sin consumir teclas ajenas.
- Copiar/Reemplazar/Guardar/Reutilizar comparten una sola matriz de capacidades:
  nada se ejecuta durante streaming o error.
- Búsquedas de historial sin recargar ni descifrar en cada pulsación.

## Lo que no se hizo (a propósito)

- **No se quitó ninguna acción, receta, ajuste ni formato.** Lo experto se pliega.
- Fuera de esta ronda, documentado como pendiente: traducción de teclado para
  layout español/ñ en la búsqueda (AB-04) y prioridad selección vs imagen del
  portapapeles en el hotkey (MN-05).
- No se tocaron proveedores, claves, esquema de datos ni el modelo por defecto.

## Verificación

- `dotnet build tools/SelfTest -c Release -p:EnableWindowsTargeting=true`: **0 errores**.
- Probes en Linux con los mismos `.cs`: `TryApply`/identidad **20/20**, movimientos y
  helpers puros verificados por los ejecutores; XAML parseado (16/16) y handlers
  comprobados.
- **Pendiente en Windows:** ejecutar `--ui --shots` en claro/oscuro, recorrer con
  Tab/Enter/Esc, probar DPI 100–200 %, movimiento desactivado y el flujo
  guardar/descartar. Compilar no es ejecutar.
