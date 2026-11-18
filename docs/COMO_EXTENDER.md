# Cómo extender Selbar

Recetas para los cambios más habituales. Antes de tocar nada, lee
[AGENTS.md](../AGENTS.md) y [ARQUITECTURA.md](ARQUITECTURA.md).

## Antes de empezar

```powershell
dotnet build tools\SelfTest\SelfTest.csproj -c Release
dotnet run --project tools\SelfTest -c Release --no-build
```

Si el cambio es visual, ejecuta también:

```powershell
dotnet run --project tools\SelfTest -c Release --no-build -- --ui
```

Los tests son offline. No añadas llamadas reales a APIs ni descargas.

---

## Añadir una acción

### 1. Definirla en el catálogo

Edita `src/Selbar/Core/Actions/ActionPresets.cs` y añade una entrada a
`Defaults()`:

```csharp
new()
{
    Id = "search.github",
    Title = "Buscar en GitHub",
    Category = ActionCategory.Search,
    Kind = ActionKind.NoAi,
    Output = OutputMode.Browser,
    Order = 214,
    IsEnabled = false,
    Description = "Busca el texto en GitHub",
    UrlTemplate = "https://github.com/search?q={text}"
},
```

Convenciones:

- `Id` es estable y único; se usa en iconos, etiquetas, overrides y ramas de UI.
- `Order` no debe coincidir con otra acción de la misma zona.
- `MatchRegex` si sólo debe aparecer con cierto contenido (URL, email, color…).
- `PromptTemplate` para IA, `UrlTemplate` para búsquedas, `Script`/`KeyName` para
  sistema.
- No actives por defecto acciones que puedan sorprender o saturar la barra.

### 2. Icono y etiqueta corta

- `src/Selbar/Core/Actions/ActionIcons.cs`: `ActionIcons.For(id)` con un glifo
  de Segoe Fluent Icons.
- `src/Selbar/Core/Actions/ActionLabels.cs`: etiqueta corta para el chip (si no
  hay, se usa `Title`).
- Si es una acción de navegador que debe ir al principio **sólo con icono**,
  añádela a `ActionDefinition.IsBarIcon`.

No inventes un glifo sin comprobar que la fuente lo tiene; si no, el chip cae a la
inicial.

### 3. Comportamiento

- Si usa un `OutputMode` existente, no toques nada más.
- Si necesita lógica no-IA, añade el caso en
  `src/Selbar/Core/Actions/ActionExecutor.cs`.
- Si necesita su propia ventana, intercepta el `Id` en
  `src/Selbar/Ui/ActionRunner.cs` antes de delegar en el executor.
- Si es una acción de sistema sensible (simular teclas, ejecutar comando), revisa
  las reglas de seguridad y el contexto.

### 4. Tests y documentación

- `tools/SelfTest/Program.cs`:
  - `PresetTests`: que existe, id único y valores esperados.
  - `RegistryTests`: visibilidad con una selección adecuada.
  - `ExecutorTests`: salida/argumentos si añade lógica.
- README: catálogo de acciones y novedades de la versión.
- Si tiene ventana, añádela al smoke de UI (ver abajo).

---

## Añadir una ventana

### 1. Crear XAML + code-behind

Usa una ventana existente como plantilla (`TextStyleWindow`, `ColorPreviewWindow`,
`QrWindow`) y hereda de `FluentWindow`:

```xml
<ui:FluentWindow x:Class="Selbar.Ui.MiWindow"
                 xmlns:ui="clr-namespace:Selbar.Ui"
                 TitleBarTitle="Mi herramienta"
                 Width="720" Height="520"
                 WindowStartupLocation="CenterScreen">
    <Grid Background="{DynamicResource BgWindow}">
        <!-- contenido -->
    </Grid>
</ui:FluentWindow>
```

Reglas:

- No pongas `Style="{StaticResource FluentWindowStyle}"`: `FluentWindow` ya lo
  aplica.
- Usa `{DynamicResource ...}` para colores; no uses pinceles locales congelados.
- Controles de 32 px, radios 4/8, tipografía y espaciado de los tokens existentes.
- Si es un popup que no debe quitar el foco, usa el patrón de
  `PopupWindowStyles.MakeNonActivating` y `WS_EX_NOACTIVATE`.
- No añadas `Background` blanco/negro fijo: usa `BgWindow`, `BgCard`, etc.
- Para animaciones, usa `Motion`; respeta `ClientAreaAnimation`.

### 2. Registrarla en el smoke de UI

En `tools/SelfTest/Program.cs`, añade una entrada a la lista `windows`:

```csharp
("MiWindow", () => new MiWindow()),
```

El harness abrirá la ventana, comprobará contraste y la cerrará. Si necesitas
interactuar antes de la captura, sigue el patrón de `SettingsWindow`/`HistoryWindow`
en la misma lista.

### 3. Captura para docs

Con `--ui --shots` se genera un PNG en el directorio temporal. Cópialo a
`docs/screenshots/` con un nombre estable y enlázalo desde README. Actualiza el
contador de comprobaciones de UI si añadiste una ventana.

### 4. Enlazarla a una acción

En `ActionRunner.cs`, intercepta su `Id` y abre la ventana. Si la acción no debe
ejecutarse sin ventana, no la dejes caer al executor.

---

## Añadir un ajuste

1. **Modelo:** añade la propiedad a `Core/Config/AppSettings.cs` con un default
   seguro. Si es un secreto, no lo guardes en claro: usa `SettingsManager.Protect`.
2. **Migración:** si el campo necesita validación o cambia el significado de una
   versión anterior, actualiza `SettingsManager.Migrate` y sube
   `AppSettings.CurrentVersion`.
3. **UI:** en `Ui/SettingsWindow.xaml`, añade el control dentro de la tarjeta
   correcta; en `SettingsWindow.xaml.cs`:
   - carga el valor en `LoadSettingsIntoUi`;
   - guárdalo en `TrySave`/`OnSave`;
   - si es interactivo, llama a `MarkDirty()` en su evento.
4. **Persistencia:** `SettingsManager.Save` ya serializa; comprueba que
   `Export` no filtra secretos.
5. **Tests:** añade el caso a `SettingsTests` y, si aplica, a
   `ExportImportTests`.
6. **Docs:** README → sección de Ajustes y novedades.

Nunca cambies el modelo por defecto (`deepseek-v4.1-flash`) sin petición expresa.

---

## Perfiles por app (ocultar acciones según la ventana)

- Formato en Ajustes → Perfiles: `proceso = id1, id2` (una línea por app).
- Una línea que empieza con `!` **desactiva** ese perfil sin borrarlo:
  `!chrome = ai.todos`. Ojo: `Format` no escribe perfiles sin ids (Parse los
  descartaría y se perderían al guardar).

---

## Añadir una entrada de tray o hotkey

- Tray: `Ui/MainWindow.xaml.cs`, en la construcción del `ContextMenuStrip`. Usa
  `TrayIcon.GlyphBitmap("\uXXXX")` y asigna `Tag` al glifo para el tema.
- Hotkey: `Native/HotkeyManager` + `App.RefreshHotkey`; la acción debe respetar
  pausa, apps excluidas y fullscreen.
- Si abre una ventana, comprueba que no se queda en segundo plano ni roba el foco
  sin querer.

---

## Añadir un color o estilo visual

1. Añade la clave a `Ui/Theme/Tokens.xaml` y a la paleta de `Ui/Theming.cs` para
   claro y oscuro.
2. Úsala con `{DynamicResource Clave}`.
3. No guardes una instancia de pincel en un campo estático: WPF la congela y el
   cambio de tema no se refleja. `Theming.ApplyToDictionary` reemplaza instancias.
4. Comprueba contraste con el smoke (`CheckTextContrast`) y en claro/oscuro.
5. Actualiza capturas si el cambio es visible.

---

## Añadir una integración externa

- Comprueba `SELBAR_OFFLINE` **antes** de cualquier red.
- No pongas claves en el repo ni en logs.
- Define límites (tamaño, timeout, formatos) y errores accionables.
- Añade validaciones unitarias y un test de que offline bloquea la llamada.
- Si requiere una herramienta externa, no la empaquetes: detecta, explica y ofrece
  instalación (patrón `YtDlp`).
- Documenta en README: requisito, privacidad y qué se envía.

---

## Añadir/actualizar tests

- Los tests de lógica viven en `tools/SelfTest/Program.cs` y se agrupan con
  `Run("Nombre", Metodo)`.
- Usa `Check(nombre, condición)` y `CheckEqual`.
- No dependas de orden de tests ni de estado global sin limpiarlo.
- Para UI, añade la ventana a `windows`; para capturas, usa `--shots`.
- Los tests deben seguir funcionando sin red y sin claves.

---

## Checklist de una contribución

- [ ] Acción/ajuste/ventana registrada en todos los sitios (preset, icono,
      etiqueta, executor/runner, settings, smoke).
- [ ] Tests de lógica añadidos/actualizados.
- [ ] `--ui` en verde en oscuro y claro si hay UI.
- [ ] Capturas actualizadas si cambió lo visual.
- [ ] README y `docs/` actualizados.
- [ ] `.\build.ps1` genera ZIP y `dist/Selbar-<version>/`.
- [ ] Smoke del `.exe` publicado con datos temporales.
- [ ] Si tocaste chrome/esquinas/Mica, verifica en ventana real.
