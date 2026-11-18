# Decisión inteligente de la acción más probable

La barra flotante ya sabe qué acciones *pueden* aplicar (reglas locales). Este
documento describe la capa opcional que decide **cuál de ellas es la más
probable** para lo que el usuario acaba de seleccionar, usando
`gemini-decision-1` (modelo de decisiones: devuelve probabilidades, no texto).

## Por qué no se pregunta por acciones

El catálogo tiene 58 acciones y cambia con el usuario: preguntar «¿cuál de estas
58?» da confianza baja, tarda más y se rompe en cuanto se añade una acción.

Se pregunta por **intención**, una taxonomía fija de 15 categorías:

| Intención | Acción | Intención | Acción |
| :--- | :--- | :--- | :--- |
| responder | `ai.ask` | calcular | `text.calc` |
| resumir | `ai.summarize` | convertir | `text.convert` |
| mejorar | `ai.improve` | fecha | `date.info` |
| corregir | `ai.fix` | mapa | `place.maps` |
| traducir | `ai.translate` | archivo | `file.open` |
| explicar | `ai.explain` | enlace | `link.clean` |
| correo | `contact.email` | buscar | `search.google` |
| ninguna | — (no se muestra nada) | | |

Cualquier selección cae en una de ellas (o en «ninguna»), así que el sistema
funciona igual con un correo, un párrafo, código, un emoji o basura. La
traducción intención → acción es un diccionario determinista y testeable.

## Flujo

1. El usuario suelta el texto. La barra se pinta **al instante** con las reglas
   locales (0 ms). Nada espera a la red.
2. En paralelo se envía la selección (`Primero…Último`, máx. 8.000 caracteres) con
   dos preguntas en **una sola petición**:
   - `choice` sobre las 15 intenciones;
   - `noul` «¿no necesita ninguna acción?».
3. Cuando llega (≈300 ms medidos), se aplica:
   - **intención clara** (P ≥ 0,55) → su acción sube al primer puesto. Si no
     estaba en la lista local, se antepone. El resto conserva su orden.
   - **nada útil** (P(ninguna) ≥ 0,75 y mejor intención < 0,75) → se ocultan las
     sugerencias genéricas. Los **aciertos exactos** (correo, fecha, ruta,
     cálculo, enlace: acciones sin IA con regex que casó) **nunca** se ocultan:
     son hechos, no opiniones.
   - si tras ocultar no queda nada, la barra se cierra sola.
4. Si no hay key, no hay red, el modelo tarda más de 1,2 s o responde mal, **no
   cambia nada**: se queda el orden local.

## Garantías

- **Nunca bloquea ni rompe**: todo el camino está en `try/catch` y devuelve
  `null` ante cualquier problema.
- **Nunca envía credenciales**: si la selección parece un token (sin espacios,
  con mayúsculas/símbolos y dígitos) no sale del equipo. Enlaces, rutas y correos
  sí se consideran texto normal.
- **Caché**: misma selección + mismo modelo → misma respuesta, sin gastar cuota
  (64 entradas, 10 minutos).
- **Cortacircuitos**: 3 fallos seguidos → 5 minutos sin llamar.
- **Tests sin red**: la suite usa decisiones simuladas; el parser y el ranking son
  funciones puras.

## Coste

Free tier de Google AI Studio: 10.000 peticiones/día por key, sin coste de salida
(el modelo no genera texto). Cada decisión ≈ 250-350 tokens de entrada.

## Ajustes

`Ajustes → Inteligencia artificial → Decisión inteligente`: activar, ocultar
ruido, y la API key (cifrada con DPAPI). Desactivado por defecto: el texto
seleccionado sale del equipo solo si el usuario lo activa.
