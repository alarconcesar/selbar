namespace Selbar.Core.Actions;

/// <summary>Receta de prompt lista para añadir como acción personalizada.</summary>
public sealed record PromptRecipe(
    string Id,
    string Title,
    string Group,
    string Description,
    string Prompt,
    OutputMode Output = OutputMode.Popup);

/// <summary>
/// Biblioteca de prompts: recetas listas para usar sin escribir una plantilla.
/// Se añaden como acciones personalizadas normales (y se pueden editar después).
/// </summary>
public static class PromptLibrary
{
    public static IReadOnlyList<PromptRecipe> All { get; } = new List<PromptRecipe>
    {
        // ---------------- Escritura ----------------
        new("lib.improve-short", "Mejorar y acortar", "Escritura",
            "Claro, natural y más breve",
            "Reescribe el siguiente texto para que sea claro, natural y más breve sin perder información. Devuelve SOLO el texto final:\n\n{text}",
            OutputMode.Replace),
        new("lib.tone-close", "Tono cercano", "Escritura",
            "Informal pero cuidado",
            "Reescribe el siguiente texto en tono cercano e informal, manteniendo el significado. SOLO el resultado:\n\n{text}"),
        new("lib.tone-pro", "Tono profesional", "Escritura",
            "Formal y directo",
            "Reescribe el siguiente texto con tono profesional, claro y directo. SOLO el resultado:\n\n{text}"),
        new("lib.bullets", "Convertir en viñetas", "Escritura",
            "Lista de puntos claros",
            "Convierte el siguiente texto en una lista de viñetas claras y concisas (máximo 8 puntos). SOLO la lista:\n\n{text}"),
        new("lib.table", "Convertir en tabla", "Escritura",
            "Tabla markdown con columnas",
            "Convierte el siguiente texto en una tabla markdown con columnas sensatas. SOLO la tabla:\n\n{text}"),
        new("lib.remove-fluff", "Quitar paja", "Escritura",
            "Sin redundancias ni relleno",
            "Elimina redundancias, relleno y frases hechas del siguiente texto. SOLO el texto limpio:\n\n{text}"),
        new("lib.headlines", "Proponer titulares", "Escritura",
            "5 titulares para este texto",
            "Propón 5 titulares breves y atractivos (máximo 10 palabras) para el siguiente texto, en lista numerada:\n\n{text}"),

        // ---------------- Trabajo ----------------
        new("lib.reply", "Responder mensaje", "Trabajo",
            "Respuesta breve y educada",
            "Redacta una respuesta breve, educada y útil a este mensaje. Devuelve solo la respuesta:\n\n{text}"),
        new("lib.email", "Redactar email", "Trabajo",
            "Con saludo y cierre",
            "Convierte el siguiente contenido en un email profesional, con saludo y cierre, sin inventar datos. SOLO el email:\n\n{text}"),
        new("lib.followup", "Email de seguimiento", "Trabajo",
            "Recordatorio amable",
            "Escribe un email de seguimiento breve y amable sobre lo siguiente, sin sonar insistente:\n\n{text}"),
        new("lib.decline", "Rechazar amablemente", "Trabajo",
            "Decir que no sin ofender",
            "Redacta un mensaje breve para rechazar amablemente lo siguiente, ofreciendo una alternativa si tiene sentido:\n\n{text}"),
        new("lib.executive", "Resumen ejecutivo", "Trabajo",
            "Un párrafo para dirección",
            "Resume el siguiente texto en un único párrafo (máximo 80 palabras) para alguien con poco tiempo. SOLO el resumen:\n\n{text}"),
        new("lib.minutes", "Acta de reunión", "Trabajo",
            "Decisiones, riesgos y tareas",
            "Extrae de la siguiente transcripción: (1) decisiones tomadas, (2) riesgos o bloqueos, (3) tareas con responsable si se menciona. Formato markdown:\n\n{text}"),
        new("lib.todos", "Extraer tareas", "Trabajo",
            "Checklist accionable",
            "Extrae las tareas accionables del siguiente texto como lista de verificación (- [ ] tarea, con responsable y fecha si aparecen):\n\n{text}"),
        new("lib.json-data", "Extraer datos (JSON)", "Trabajo",
            "Fechas, importes, personas",
            "Extrae del texto los datos estructurados que encuentres (fechas, importes, personas, empresas, referencias). Devuelve SOLO JSON válido, sin explicación:\n\n{text}"),
        new("lib.questions", "Preguntas pendientes", "Trabajo",
            "Qué habría que aclarar",
            "Enumera las preguntas clave que habría que aclarar antes de actuar sobre el siguiente texto (máximo 8):\n\n{text}"),

        // ---------------- Código ----------------
        new("lib.code-explain", "Explicar código", "Código",
            "Qué hace y por qué",
            "Explica qué hace el siguiente código paso a paso, con lenguaje sencillo. Si detectas algo extraño, dilo al final:\n\n{text}"),
        new("lib.code-review", "Revisar código", "Código",
            "Bugs, casos límite y mejoras",
            "Revisa el siguiente código: busca bugs, casos límite y mejoras concretas. Devuelve una lista priorizada con la línea o el fragmento afectado:\n\n{text}"),
        new("lib.commit", "Comentario de commit", "Código",
            "Conventional Commits",
            "Escribe un mensaje de commit en formato Conventional Commits para este cambio. SOLO el mensaje, en una línea de asunto y cuerpo corto si hace falta:\n\n{text}"),
        new("lib.tests", "Tests unitarios", "Código",
            "Casos felices y bordes",
            "Escribe tests unitarios para el siguiente código, cubriendo casos normales y bordes. Usa el framework más habitual del lenguaje:\n\n{text}"),
        new("lib.explain-error", "Explicar error", "Código",
            "Causa y solución",
            "Explica este error: qué significa, por qué ocurre y cómo solucionarlo. Si hay varias causas posibles, ordénalas por probabilidad:\n\n{text}"),
        new("lib.regex", "Explicar regex", "Código",
            "Trozo a trozo",
            "Explica esta expresión regular trozo a trozo y pon un ejemplo de lo que casa y de lo que no:\n\n{text}"),
        new("lib.sql", "Generar SQL", "Código",
            "Consulta a partir del texto",
            "Convierte la siguiente descripción en una consulta SQL estándar y explica brevemente lo que hace:\n\n{text}"),

        // ---------------- Estudio ----------------
        new("lib.eli5", "Explicar como a un niño", "Estudio",
            "Sin jerga, con ejemplos",
            "Explica el siguiente texto como si tuviera 10 años: frases cortas, sin jerga y con un ejemplo. SOLO la explicación:\n\n{text}"),
        new("lib.quiz", "Preguntas de examen", "Estudio",
            "5 preguntas con respuesta",
            "Genera 5 preguntas de examen sobre el siguiente texto, con su respuesta justificada. Formato markdown:\n\n{text}"),
        new("lib.keypoints", "Ideas clave", "Estudio",
            "5 ideas imprescindibles",
            "Extrae las 5 ideas más importantes del siguiente texto, en frases cortas:\n\n{text}"),
        new("lib.pros-cons", "A favor y en contra", "Estudio",
            "Argumentos equilibrados",
            "Expón argumentos a favor y en contra de lo siguiente, en dos listas equilibradas:\n\n{text}"),
        new("lib.jargon", "Simplificar jerga", "Estudio",
            "Sin tecnicismos",
            "Reescribe el siguiente texto sin jerga técnica, manteniendo el significado. SOLO el resultado:\n\n{text}"),

        // ---------------- Idiomas ----------------
        new("lib.translate-format", "Traducir manteniendo formato", "Idiomas",
            "Conserva listas y tablas",
            "Traduce al {lang} manteniendo el formato original (listas, tablas, negritas). Devuelve SOLO la traducción:\n\n{text}"),
        new("lib.translate-en", "Traducir al inglés", "Idiomas",
            "Copia el resultado",
            "Translate the following text to English, keeping the original formatting. Return ONLY the translation:\n\n{text}",
            OutputMode.Copy),
        new("lib.english-fix", "Mejorar inglés", "Idiomas",
            "Corrige y suena natural",
            "Improve the following English text: fix grammar and make it sound natural. Return ONLY the improved text:\n\n{text}"),
    };

    public static IReadOnlyList<string> Groups { get; } =
        All.Select(r => r.Group).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Convierte una receta en una acción personalizada lista para el registro.</summary>
    public static ActionDefinition ToAction(PromptRecipe recipe) => new()
    {
        Id = "custom." + Guid.NewGuid().ToString("N")[..8],
        Title = recipe.Title,
        Category = ActionCategory.Custom,
        Kind = ActionKind.Ai,
        Output = recipe.Output,
        IsEnabled = true,
        Description = recipe.Description,
        PromptTemplate = recipe.Prompt
    };

    /// <summary>¿Ya existe una acción personalizada con ese título?</summary>
    public static bool IsAlreadyAdded(string title, IEnumerable<ActionDefinition> actions) =>
        actions.Any(a => a.Category == ActionCategory.Custom &&
                         string.Equals(a.Title?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase));
}
