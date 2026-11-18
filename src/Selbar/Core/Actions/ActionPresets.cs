using Selbar.Core.Downloads;

namespace Selbar.Core.Actions;

/// <summary>
/// Catálogo inspirado en SnipDo + Orbital + PopTool.
/// Todo se puede activar/desactivar y reordenar en Ajustes.
/// Los "IsEnabled=false" por defecto son para no agobiar al usuario nuevo.
/// </summary>
public static class ActionPresets
{
    public static List<ActionDefinition> Defaults() => new()
    {
        // ---------- IA: traducir ----------
        new() { Id="ai.translate", Title="Traducir", Category=ActionCategory.AiTranslate, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=10,
            Description="Detecta el idioma y traduce al idioma configurado",
            PromptTemplate="Detecta el idioma y traduce al {lang}. Devuelve SOLO la traducción, sin comillas ni explicación:\n\n{text}" },
        new() { Id="ai.translate-en", Title="Traducir al inglés", Category=ActionCategory.AiTranslate, Kind=ActionKind.Ai, Output=OutputMode.Copy, Order=11, IsEnabled=false,
            Description="Traduce al inglés y lo copia",
            PromptTemplate="Translate the following text to English. Return ONLY the translation:\n\n{text}" },

        // ---------- IA: reescribir ----------
        new() { Id="ai.improve", Title="Mejorar redacción", Category=ActionCategory.AiRewrite, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=20,
            Description="Claro, natural y profesional",
            PromptTemplate="Mejora la redacción del siguiente texto (claro, natural, profesional). Devuelve SOLO el texto mejorado:\n\n{text}" },
        new() { Id="ai.fix", Title="Corregir ortografía", Category=ActionCategory.AiRewrite, Kind=ActionKind.Ai, Output=OutputMode.Replace, Order=21,
            Description="Corrige ortografía y gramática y sustituye la selección",
            PromptTemplate="Corrige ortografía y gramática. Mantén el idioma original. Devuelve SOLO el texto corregido:\n\n{text}" },
        new() { Id="ai.shorten", Title="Hacer conciso", Category=ActionCategory.AiRewrite, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=22, IsEnabled=false,
            Description="Más corto sin perder información", MultiWordOnly=true,
            PromptTemplate="Reescribe el texto de forma más concisa sin perder información. SOLO el resultado:\n\n{text}" },
        new() { Id="ai.formal", Title="Tono formal", Category=ActionCategory.AiRewrite, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=23, IsEnabled=false,
            Description="Tono formal y educado",
            PromptTemplate="Reescribe en tono formal y educado. SOLO el resultado:\n\n{text}" },

        // ---------- IA: explicar / resumir ----------
        new() { Id="ai.summarize", Title="Resumir", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=30, LongTextOnly=true,
            Description="3-5 puntos clave (texto largo)",
            PromptTemplate="Resume el siguiente texto en 3-5 puntos clave en {lang}:\n\n{text}" },
        new() { Id="ai.explain", Title="Explicar", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=31,
            Description="Explicado de forma sencilla",
            PromptTemplate="Explica el siguiente texto de forma sencilla, como a un principiante:\n\n{text}" },
        new() { Id="ai.keypoints", Title="Puntos clave", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=32, IsEnabled=false, LongTextOnly=true,
            Description="Puntos clave y tareas pendientes",
            PromptTemplate="Extrae los puntos clave y action items como lista:\n\n{text}" },
        new() { Id="ai.define", Title="Definir término", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=33, SingleWordOnly=true, IsEnabled=false,
            Description="Definición breve de una palabra",
            PromptTemplate="Define brevemente el término '{text}' en {lang} (1-2 líneas)." },

        // ---------- IA: pregunta libre ----------
        new() { Id="ai.ask", Title="Preguntar a la IA", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=34,
            Description="Escribe tu propia instrucción",
            PromptTemplate="{text}" },
        new() { Id="ai.summarize-one", Title="Resumir en 1 línea", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=29, LongTextOnly=true, IsEnabled=false,
            Description="Una sola frase",
            PromptTemplate="Resume el siguiente texto en UNA sola frase en {lang}:\n\n{text}" },
        new() { Id="ai.todos", Title="Extraer tareas", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=35, IsEnabled=false,
            Description="Lista de tareas pendientes",
            PromptTemplate="Extrae las tareas pendientes como lista de verificación (una por línea, empezando por - [ ]):\n\n{text}" },

        // ---------- Sin IA: portapapeles (SnipDo core) ----------
        new() { Id="copy", Title="Copiar", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.DirectCopy, Order=100,
            Description="Copia la selección" },
        new() { Id="cut", Title="Cortar", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.Cut, Order=101, IsEnabled=false,
            Description="Corta la selección al portapapeles" },
        new() { Id="paste", Title="Pegar", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.Paste, Order=102, IsEnabled=false,
            Description="Pega el contenido del portapapeles aquí" },
        new() { Id="pasteplain", Title="Pegar sin formato", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.PastePlain, Order=103, IsEnabled=false,
            Description="Pega como texto plano (sin negritas ni colores)" },
        new() { Id="selectall", Title="Seleccionar todo", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.SelectAll, Order=104, IsEnabled=false,
            Description="Ctrl+A en la app de origen" },
        new() { Id="delete", Title="Eliminar", Category=ActionCategory.Clipboard, Kind=ActionKind.NoAi, Output=OutputMode.Delete, Order=105, IsEnabled=false,
            Description="Borra la selección" },

        // ---------- Sin IA: búsqueda ----------
        new() { Id="search.google", Title="Buscar en Google", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=200,
            Description="Abre la búsqueda en el navegador",
            UrlTemplate="https://www.google.com/search?q={text}" },
        new() { Id="search.wiki", Title="Wikipedia", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=201, IsEnabled=false,
            Description="Busca en Wikipedia en español",
            UrlTemplate="https://es.wikipedia.org/wiki/Special:Search?search={text}" },
        new() { Id="search.youtube", Title="YouTube", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=202, IsEnabled=false,
            Description="Busca el texto en YouTube",
            UrlTemplate="https://www.youtube.com/results?search_query={text}" },
        new() { Id="search.so", Title="StackOverflow", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=203, IsEnabled=false,
            Description="Busca el error en StackOverflow",
            UrlTemplate="https://stackoverflow.com/search?q={text}" },
        new() { Id="search.whatsapp", Title="Enviar por WhatsApp", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=204,
            Description="Abre WhatsApp con el número seleccionado (incluye el código de país)",
            MatchRegex=@"^(?!\s*\d{1,4}\s*[-/.]\s*\d{1,2}\s*[-/.]\s*\d{2,4}\s*$)(?=(?:\D*\d){7,15}\D*$)\+?[\d\s().-]{7,22}$",
            UrlTemplate="https://wa.me/{text}" },

        new() { Id="search.lens-url", Title="Buscar imagen en Google Lens", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=205,
            Description="Búsqueda inversa de una URL de imagen",
            MatchRegex=@"^(?i)https?://[^\s]+\.(?:png|jpe?g|gif|webp|bmp|tiff?)(?:\?[^\s]*)?$",
            UrlTemplate="https://lens.google.com/uploadbyurl?url={text}" },

        // ---------- Sin IA: YouTube (yt-dlp externo) ----------
        new() { Id="sys.youtube-video", Title="Descargar vídeo", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=220,
            Description="Descarga el vídeo con yt-dlp (YouTube, TikTok, Instagram, X…)",
            MatchRegex=YtDlp.SupportedUrlPattern },
        new() { Id="sys.youtube-audio", Title="Descargar audio", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=221,
            Description="Descarga solo el audio con yt-dlp (YouTube, TikTok, SoundCloud…)",
            MatchRegex=YtDlp.SupportedUrlPattern },

        // ---------- Sin IA: voz e integraciones ----------
        new() { Id="sys.speak", Title="Leer en voz alta", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=222,
            Description="Lee la selección con la voz de Windows", MaxLength=5000 },
        new() { Id="sys.obsidian", Title="Guardar en Obsidian", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=223, IsEnabled=false,
            Description="Crea una nota en tu vault de Obsidian (Ajustes → Integraciones)" },

        // ---------- Enlaces inteligentes ----------
        new() { Id="contact.email", Title="Redactar correo", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=209,
            Description="Abre tu app de correo para escribirle a esa dirección",
            MatchRegex=@"^\s*(?:mailto:[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(\?\S*)?|(?:[A-Za-zÀ-ÿ.'-]+\s+)*[A-Za-zÀ-ÿ.'-]+\s*<[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}>|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})\s*$" },
        new() { Id="link.clean", Title="Limpiar enlace", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Copy, Order=206,
            Description="Quita utm_*, fbclid, gclid… y copia el enlace limpio",
            MatchRegex=@"^(?i)(?:https?://|www\.)\S*[?&](?:utm_[a-z_]+|fbclid|gclid|msclkid|yclid|twclid|ttclid|mc_cid|mc_eid|igshid|igsh|_ga|ref_src)=" },
        new() { Id="link.expand", Title="Ver destino real", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=207,
            Description="Sigue las redirecciones de un enlace acortado y muestra a dónde va",
            MatchRegex=@"^(?i)(?:https?://)?(?:www\.)?(?:bit\.ly|t\.co|goo\.gl|tinyurl\.com|ow\.ly|is\.gd|buff\.ly|cutt\.ly|rebrand\.ly|rb\.gy|t\.ly|lnkd\.in|amzn\.to|youtu\.be|fb\.me|trib\.al|shorturl\.at|linktr\.ee)/\S+$" },
        new() { Id="link.summarize", Title="Resumir enlace", Category=ActionCategory.AiExplain, Kind=ActionKind.Ai, Output=OutputMode.Popup, Order=36, IsEnabled=false,
            Description="Lee la página (o el PDF) y lo resume en 5 puntos",
            MatchRegex=@"^(?i)https?://\S+$", MaxLength=400 },
        new() { Id="place.maps", Title="Abrir en Maps", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=208,
            Description="Busca la dirección en Google Maps",
            UrlTemplate="https://www.google.com/maps/search/?api=1&query={text}",
            MatchRegex=@"^(?i)\s*(?:(?:av\.?|avenida|calle|jr\.?|jir[oó]n|pasaje|paseo|street|st\.?|road|rd\.?|ave\.?|blvd\.?|boulevard|lane|ln\.?|drive|dr\.?|camino|ruta)\b|(?:[A-Za-zÀ-ÿ.'-]+\s+){1,5}\d{1,5}\b(?:\s*,[^\n]*)?$)" },
        new() { Id="date.info", Title="Analizar fecha", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=315,
            Description="Qué día es y cuánto falta o pasó (12/03/2025, 12 de marzo, mañana)",
            MatchRegex=@"(?i)^\s*(hoy|mañana|manana|ayer|pasado\s+mañana|\d{4}\s*-\s*(?:0?[1-9]|1[0-2])\s*-\s*\d{1,2}|\d{1,2}\s*[/\-]\s*(?:0?[1-9]|1[0-2])(?:\s*[/\-]\s*\d{2,4})?|\d{1,2}\.(?:0?[1-9]|1[0-2])\.\d{2,4}|\d{1,2}[\s-]+(?:de[\s-]+)?(?:ene|feb|mar|abr|may|jun|jul|ago|sep|set|oct|nov|dic)\w*\.?(?:[\s-]+(?:del?[\s-]+)?\d{2,4})?)(?:\s+(?:a\s+las\s+)?(\d{1,2}:\d{2}(?::\d{2})?(\s*[ap]\.?m\.?)?|\d{1,2}\s*[ap]\.?m\.?|[ap]\.?m\.?))?\s*$" },
        new() { Id="file.open", Title="Abrir ubicación", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=224,
            Description="Abre la carpeta o selecciona el archivo en el Explorador",
            MatchRegex=@"^\s*(""[A-Za-z]:[\\/][^""]+""|'[A-Za-z]:[\\/][^']+'|[A-Za-z]:[\\/]\S+|\\\\[^\\\s]+\\[^\s]+|%[A-Za-z_][A-Za-z0-9_]*%[\\/]\S*|~[\\/]\S+)\s*$" },

        // ---------- Perfiles sociales (@usuario) ----------
        new() { Id="social.x", Title="Perfil en X", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=215,
            Description="Abre el perfil en X (Twitter)", UrlTemplate="https://x.com/{text}",
            MatchRegex=@"^@[A-Za-z0-9_]{2,15}$" },
        new() { Id="social.instagram", Title="Perfil en Instagram", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=216,
            Description="Abre el perfil en Instagram", UrlTemplate="https://www.instagram.com/{text}",
            MatchRegex=@"^@[A-Za-z0-9._]{2,30}$" },
        new() { Id="social.facebook", Title="Perfil en Facebook", Category=ActionCategory.Search, Kind=ActionKind.NoAi, Output=OutputMode.Browser, Order=217,
            Description="Abre el perfil en Facebook", UrlTemplate="https://www.facebook.com/{text}",
            MatchRegex=@"^@[A-Za-z0-9._-]{3,50}$" },

        // ---------- Sin IA: abrir la selección ----------
        new() { Id="sys.open-url", Title="Abrir URL", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.OpenUrl, Order=210,
            Description="Abre la URL seleccionada en el navegador",
            MatchRegex=@"^(https?://|www\.)[^\s]+$" },

        // ---------- Sin IA: texto ----------
        new() { Id="text.upper", Title="MAYÚSCULAS", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=300, IsEnabled=false,
            Description="Convierte la selección a mayúsculas" },
        new() { Id="text.lower", Title="minúsculas", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=301, IsEnabled=false,
            Description="Convierte la selección a minúsculas" },
        new() { Id="text.count", Title="Contar palabras y caracteres", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=302, IsEnabled=false,
            Description="Palabras, caracteres, líneas y tiempo de lectura" },
        new() { Id="text.toggle", Title="Alternar MAY/min", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=303, IsEnabled=false,
            Description="Invierte mayúsculas y minúsculas" },
        new() { Id="text.capitalize", Title="Capitalizar", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=304, IsEnabled=false,
            Description="Pone en mayúscula cada palabra" },
        new() { Id="text.trim", Title="Limpiar espacios", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=305, IsEnabled=false,
            Description="Quita espacios dobles y líneas vacías de más" },
        new() { Id="text.sort", Title="Ordenar líneas", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=306, IsEnabled=false, MultiWordOnly=true,
            Description="Ordena alfabéticamente las líneas" },
        new() { Id="text.dedupe", Title="Quitar repetidas", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=307, IsEnabled=false, MultiWordOnly=true,
            Description="Deja una sola vez cada línea" },
        new() { Id="text.json", Title="Formatear JSON", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=308, IsEnabled=false,
            Description="Embellece el JSON seleccionado (indentado)" },
        new() { Id="text.color", Title="Ver color", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=309,
            Description="Muestra el color HEX y copia HEX/RGB/HSL",
            MatchRegex=@"^(?:#[0-9a-fA-F]{3}|#[0-9a-fA-F]{4}|#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|(?=[0-9a-fA-F]*\d)[0-9a-fA-F]{6}|(?=[0-9a-fA-F]*\d)[0-9a-fA-F]{8})$" },
        new() { Id="text.styles", Title="Estilos de letra", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=310,
            Description="Negrita, cursiva, caligráfica… para redes y marketing",
            // (?s) = el guardia de ActionRegistry deja pasar la regex en selecciones
            // multilínea; TextStyleWindow aplica el estilo al texto completo.
            MatchRegex=@"(?s)[A-Za-zÁÉÍÓÚÜÑáéíóúüñ]" },
        new() { Id="text.qr", Title="Generar código QR", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=312,
            Description="Genera un QR del enlace seleccionado",
            MatchRegex=@"^(?i)(https?://|www\.)\S+$" },
        new() { Id="text.calc", Title="Calcular", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=313,
            Description="Resuelve la operación seleccionada (12*8, 350+20%, (3+4)/2)",
            // Excluye fechas (12/03/2025, 2026-10-26) y teléfonos compactos
            // (987-654-321, +52 55 1234-5678) sin ocultar una suma ni una resta
            // normales (1234+5678, 100 - 45).
            MatchRegex=@"^(?!\s*\d{1,4}\s*[/\-.]\s*\d{1,2}\s*[/\-.]\s*\d{2,4}\s*$)(?!(?=\+?[\d\s().-]{7,22}$)(?!.*\d\s+[+\-]\s+\d))\s*[-+(]?\s*\d[\d.,\s]*([-+*/x×÷^%]\s*[\d(][\d.,\s()]*%?)+%?\s*$" },
        new() { Id="text.convert", Title="Convertir", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Popup, Order=314,
            Description="Convierte unidades o moneda (3 km a millas, 15 usd a pen)",
            MatchRegex=@"^\s*\d[\d.,]*\s*[A-Za-zÁÉÍÓÚÜÑáéíóúüñ°]{1,12}\s+(a|to|en)\s+[A-Za-zÁÉÍÓÚÜÑáéíóúüñ°]{1,12}\s*$" },
        new() { Id="text.nolines", Title="Quitar saltos de línea", Category=ActionCategory.Text, Kind=ActionKind.NoAi, Output=OutputMode.Replace, Order=311, IsEnabled=false,
            Description="Une las líneas de la selección en un solo párrafo" },

        // ---------- Sin IA: sistema / extensiones estilo SnipDo .pbar ----------
        new() { Id="sys.enter", Title="Pulsar Enter", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.SimulateKey, Order=400, IsEnabled=false, KeyName="Enter",
            Description="Pulsa Enter en la app de origen" },
        new() { Id="sys.tab", Title="Pulsar Tab", Category=ActionCategory.System, Kind=ActionKind.NoAi, Output=OutputMode.SimulateKey, Order=401, IsEnabled=false, KeyName="Tab",
            Description="Pulsa Tab en la app de origen" },
    };
}
