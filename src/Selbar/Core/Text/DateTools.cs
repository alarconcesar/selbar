using System.Globalization;
using System.Text.RegularExpressions;

namespace Selbar.Core.Text;

/// <summary>
/// Fechas en texto: reconoce formatos latinos habituales y palabras relativas
/// (hoy, mañana, ayer), y describe qué día es y cuánto falta o pasó.
/// Todo local, sin red. No usa la cultura del sistema: los meses van en español.
/// </summary>
public static class DateTools
{
    private static readonly string[] WeekDays =
        { "domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado" };

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enero"] = 1, ["ene"] = 1, ["febrero"] = 2, ["feb"] = 2, ["marzo"] = 3, ["mar"] = 3,
        ["abril"] = 4, ["abr"] = 4, ["mayo"] = 5, ["may"] = 5, ["junio"] = 6, ["jun"] = 6,
        ["julio"] = 7, ["jul"] = 7, ["agosto"] = 8, ["ago"] = 8, ["septiembre"] = 9, ["sep"] = 9,
        ["setiembre"] = 9, ["set"] = 9, ["octubre"] = 10, ["oct"] = 10, ["noviembre"] = 11, ["nov"] = 11,
        ["diciembre"] = 12, ["dic"] = 12
    };

    /// <summary>¿Parece una fecha (para mostrar el chip)? Rápido y sin excepciones.</summary>
    public static bool LooksLikeDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Length is < 3 or > 60 || t.Contains('\n')) return false;
        return TryParse(t, null, out _);
    }

    /// <summary>
    /// Convierte el texto en fecha (solo día, sin hora). Acepta un "hoy" de referencia
    /// para que las palabras relativas y las fechas sin año sean deterministas en tests.
    /// </summary>
    public static bool TryParse(string? text, DateTime? todayRef, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Length is < 3 or > 60 || t.Contains('\n')) return false;

        DateTime today = (todayRef ?? DateTime.Today).Date;

        // Quitar hora opcional al final ("12/03/2025 15:30", "12/03/2025 a las 3pm").
        // Solo se quita si hay dos puntos o am/pm: así "12 marzo 25" conserva su año.
        string day = Regex.Replace(t.Trim(),
            @"\s+(?:a\s+las\s+)?(?:\d{1,2}:\d{1,2}(?::\d{1,2})?\s*(?:[ap]\.?\s?m\.?)?|\d{1,2}\s*[ap]\.?\s?m\.?)\s*$",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        string lower = day.ToLowerInvariant().Trim('.', ' ', ',', ';');

        // Palabras relativas.
        if (lower is "hoy") { date = today; return true; }
        if (lower is "mañana" or "manana") return TryAddDays(today, 1, out date);
        if (lower is "ayer") return TryAddDays(today, -1, out date);
        if (lower is "pasado mañana" or "pasado manana") return TryAddDays(today, 2, out date);

        // 12/03/2025 · 12-03-2025 · 12.03.2025 · 12/03 · 2025-03-12
        var numeric = Regex.Match(lower,
            @"^(\d{1,4})\s*[/\-.]\s*(\d{1,2})\s*(?:[/\-.]\s*(\d{1,4}))?$",
            RegexOptions.CultureInvariant);
        if (numeric.Success)
        {
            string a = numeric.Groups[1].Value, b = numeric.Groups[2].Value;
            string c = numeric.Groups[3].Success ? numeric.Groups[3].Value : "";
            try
            {
                if (a.Length == 4)
                {
                    // yyyy-MM-dd (el día es obligatorio).
                    if (c.Length == 0 || b.Length > 2) return false;
                    date = new DateTime(int.Parse(a), int.Parse(b), int.Parse(c));
                    return ValidDate(date.Year, date.Month, date.Day);
                }
                if (c.Length > 0)
                {
                    // dd/MM/yyyy (año de 2 dígitos con pivote: 00-29 → 2000s, 30-99 → 1900s).
                    int year = c.Length == 2 ? PivotYear(int.Parse(c)) : int.Parse(c);
                    date = new DateTime(year, int.Parse(b), int.Parse(a));
                    return ValidDate(year, int.Parse(b), int.Parse(a));
                }
                // dd/MM sin año: la próxima vez que venga (este año o el siguiente).
                // El 29 de febrero rueda al próximo bisiesto.
                int d = int.Parse(a), m = int.Parse(b);
                if (m == 2 && d == 29)
                {
                    date = NextLeapDay(today);
                    return true;
                }
                if (!ValidDate(today.Year, m, d)) return false;
                date = new DateTime(today.Year, m, d);
                if (date < today) date = date.AddYears(1);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (FormatException) { return false; }
        }

        // "12 de marzo de 2025" · "12 de marzo" · "12 marzo 2025" · "12-mar-2025"
        var named = Regex.Match(lower,
            @"^(\d{1,2})\s*(?:de\s+|[-/]\s*)?([a-záéíóúñ]+)\.?\s*(?:(?:de|del)\s+|[-/]\s*)?(\d{2,4})?$",
            RegexOptions.CultureInvariant);
        if (named.Success && Months.TryGetValue(named.Groups[2].Value.Trim(), out int month))
        {
            try
            {
                int d = int.Parse(named.Groups[1].Value);
                if (named.Groups[3].Success && named.Groups[3].Value.Length > 0)
                {
                    string ys = named.Groups[3].Value;
                    int year = ys.Length == 2 ? PivotYear(int.Parse(ys)) : int.Parse(ys);
                    date = new DateTime(year, month, d);
                }
                else
                {
                    if (month == 2 && d == 29)
                    {
                        date = NextLeapDay(today);
                        return true;
                    }
                    if (!ValidDate(today.Year, month, d)) return false;
                    date = new DateTime(today.Year, month, d);
                    if (date < today) date = date.AddYears(1);
                }
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (FormatException) { return false; }
        }

        return false;
    }

    private static bool ValidDate(int year, int month, int day)
    {
        if (year is < 1900 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31) return false;
        try { _ = new DateTime(year, month, day); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>Años de 2 dígitos con pivote: 00-29 van a 2000s, 30-99 a 1900s.</summary>
    private static int PivotYear(int yy) => yy is >= 0 and <= 29 ? 2000 + yy : 1900 + yy;

    /// <summary>Suma días sin lanzar si se sale del rango de DateTime.</summary>
    private static bool TryAddDays(DateTime day, int days, out DateTime result)
    {
        result = default;
        try
        {
            result = day.AddDays(days);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Próximo 29 de febrero (hoy incluido si cae en bisiesto).</summary>
    private static DateTime NextLeapDay(DateTime today)
    {
        int year = today.Year;
        while (!DateTime.IsLeapYear(year) || new DateTime(year, 2, 29) < today) year++;
        return new DateTime(year, 2, 29);
    }

    /// <summary>
    /// Describe la fecha: día de semana, distancia a hoy e ISO. Determinista con
    /// el "hoy" que se pase (en la app es DateTime.Today).
    /// </summary>
    public static string Describe(string? text, DateTime? todayRef = null)
    {
        if (!TryParse(text, todayRef, out DateTime date))
            return "No reconozco esa fecha. Prueba con 12/03/2025, 12 de marzo o mañana.";

        DateTime today = (todayRef ?? DateTime.Today).Date;
        int delta = (date.Date - today).Days;

        string weekday = WeekDays[(int)date.DayOfWeek];
        string rel = delta switch
        {
            0 => "Es hoy.",
            1 => "Es mañana (falta 1 día).",
            -1 => "Fue ayer (hace 1 día).",
            > 1 and <= 30 => $"Faltan {delta} días.",
            > 30 => $"Faltan {delta} días ({delta / 30} {(delta / 30 == 1 ? "mes" : "meses")} aprox).",
            < -1 and >= -30 => $"Fue hace {-delta} días.",
            _ => $"Fue hace {-delta} días ({-delta / 30} {(-delta / 30 == 1 ? "mes" : "meses")} aprox)."
        };

        string display = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        string iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{display} ({weekday})\n{rel}\nISO: {iso}";
    }
}
