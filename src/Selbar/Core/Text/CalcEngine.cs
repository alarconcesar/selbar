using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Selbar.Core.Text;

/// <summary>
/// Motor de cálculo local: aritmética con paréntesis, porcentajes, unidades y
/// conversión de moneda. Todo offline salvo la moneda, que acepta un proveedor
/// de tipos de cambio inyectable (los tests usan uno falso; con
/// SELBAR_OFFLINE=1 no se hace ninguna petición).
/// </summary>
public static class CalcEngine
{
    /// <summary>Proveedor de tipos de cambio con base USD. Sustituible en tests.</summary>
    public static Func<CancellationToken, Task<IReadOnlyDictionary<string, double>?>>? RatesProvider { get; set; }

    // ---------------- Aritmética ----------------

    /// <summary>¿El texto parece una operación aritmética?</summary>
    public static bool LooksLikeMath(string? text) => TryEvaluate(text, out _);

    /// <summary>
    /// Evalúa una expresión aritmética. Soporta + - * / x × ÷ ^ % y paréntesis.
    /// Los porcentajes se interpretan como "de": 350+20% = 420, 20% de 350 = 70.
    /// </summary>
    public static bool TryEvaluate(string? text, out string result)
    {
        result = "";
        if (!TryCompute(text, out double value)) return false;

        result = Format(value);
        return true;
    }

    /// <summary>Devuelve el valor numérico o false si la expresión no es válida.</summary>
    public static bool TryCompute(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string raw = text.Trim();
        if (raw.Length > 120) return false;

        // "20% de 350" → 70 (forma natural en español, no es una operación con operador).
        var percentOf = System.Text.RegularExpressions.Regex.Match(raw,
            @"^\s*(\d[\d.,]*)\s*%\s*(?:del|de)\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (percentOf.Success &&
            double.TryParse(Normalize(percentOf.Groups[1].Value), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) &&
            ComputeAny(percentOf.Groups[2].Value, out double basis))
        {
            value = pct / 100.0 * basis;
            return true;
        }

        // Un número suelto no es una operación; una fecha tampoco lo es.
        if (!ContainsOperator(raw)) return false;
        if (LooksLikeDate(raw)) return false;
        if (!ComputeAny(raw, out value)) return false;

        return true;
    }

    /// <summary>Evalúa sin exigir operador (permite números sueltos y paréntesis).</summary>
    private static bool ComputeAny(string text, out double value)
    {
        value = 0;
        var parser = new Parser(Normalize(text));
        try
        {
            value = parser.ParseExpression();
            if (!parser.AtEnd) return false;
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (Math.Abs(value) > 1e15) return false;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            // Literales hexadecimales gigantes: no deben escapar de aquí.
            return false;
        }
    }

    /// <summary>Explicación legible de la operación: "12 × 8 + 2 = 98".</summary>
    public static string Explain(string text, double value)
    {
        string pretty = (text ?? "").Trim()
            .Replace("\r", " ").Replace("\n", " ")
            .Replace("*", "×").Replace("/", "÷");
        return $"{pretty} = {Format(value)}";
    }

    private static bool ContainsOperator(string s) =>
        s.IndexOfAny(new[] { '+', '-', '*', '/', 'x', 'X', '×', '÷', '^', '%' }) >= 0;

    /// <summary>
    /// Evita que "2026-10-26" o "10/26/2026" se lean como restas/divisiones.
    /// Exige la forma completa de fecha: así "1000-100-50" sigue siendo aritmética.
    /// </summary>
    private static bool LooksLikeDate(string s)
    {
        string t = s.Trim();
        if (t.Length is < 6 or > 20) return false;
        if (t.Contains('+') || t.Contains('*') || t.Contains('%')) return false;

        var m = Regex.Match(t, @"^(\d{1,4})([-/])(\d{1,2})\2(\d{2,4})$");
        if (!m.Success) return false;

        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int c = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

        if (m.Groups[1].Value.Length == 4)   // yyyy-MM-dd
            return a is >= 1900 and <= 2100 && b is >= 1 and <= 12 && c is >= 1 and <= 31;

        // dd/MM/yyyy (o MM/dd/yyyy, que también se pega a menudo).
        bool dmy = a is >= 1 and <= 31 && b is >= 1 and <= 12;
        bool mdy = a is >= 1 and <= 12 && b is >= 1 and <= 31;
        return dmy || mdy;
    }

    /// <summary>
    /// Deja la expresión lista para el parser: quita espacios (incluidos NBSP y
    /// tabuladores), unifica ×/÷ y resuelve separadores de miles/decimales
    /// ("1.000,50" y "1,000.50" → "1000.50"). Nunca lanza.
    /// </summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch
            {
                '×' => '*',
                '÷' => '/',
                _ => c
            });
        }

        return Regex.Replace(sb.ToString(), @"\d[\d.,]*", m => NormalizeNumber(m.Value));
    }

    /// <summary>
    /// Normaliza un número con separadores. Con coma y punto, el que va más a la
    /// derecha es el decimal; un tipo repetido son miles solo si agrupa de 3 en 3.
    /// Si la agrupación no cuadra se deja igual y el parser lo rechaza.
    /// </summary>
    private static string NormalizeNumber(string number)
    {
        int lastDot = number.LastIndexOf('.');
        int lastComma = number.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            int dec = Math.Max(lastDot, lastComma);
            char decChar = number[dec];
            char groupChar = decChar == '.' ? ',' : '.';
            string integerPart = number[..dec];
            string fraction = number[(dec + 1)..];
            if (fraction.Length == 0 || fraction.Any(c => !char.IsDigit(c))) return number;
            if (!ValidGrouping(integerPart, groupChar)) return number;
            return integerPart.Replace(groupChar.ToString(), "") + "." + fraction;
        }

        if (lastComma >= 0)
        {
            int commas = number.Count(c => c == ',');
            if (commas == 1) return number.Replace(',', '.');
            return ValidGrouping(number, ',') ? number.Replace(",", "") : number;
        }

        if (lastDot >= 0)
        {
            int dots = number.Count(c => c == '.');
            if (dots == 1) return number;
            return ValidGrouping(number, '.') ? number.Replace(".", "") : number;
        }

        return number;
    }

    /// <summary>¿Los grupos separados por <paramref name="sep"/> son de 3 dígitos?</summary>
    private static bool ValidGrouping(string number, char sep)
    {
        string[] parts = number.Split(sep);
        if (parts.Length < 2) return false;
        if (parts[0].Length is < 1 or > 3 || parts[0].Any(c => !char.IsDigit(c))) return false;
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].Length != 3 || parts[i].Any(c => !char.IsDigit(c))) return false;
        return true;
    }

    /// <summary>Formatea sin decimales sobrantes y sin notación científica.</summary>
    public static string Format(double value)
    {
        double rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        if (double.IsNaN(rounded) || double.IsInfinity(rounded))
            return rounded.ToString(CultureInfo.InvariantCulture);

        if (Math.Abs(rounded) >= 1e18) return rounded.ToString("0.###E+0", CultureInfo.InvariantCulture);
        bool integral = Math.Abs(rounded - Math.Round(rounded)) < 1e-9;
        if (integral)
            return ((long)Math.Round(rounded)).ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");

        string decimals = rounded.ToString("N6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return decimals.Replace(",", " ").Replace(".", ",");
    }

    /// <summary>Parser descendente recursivo mínimo (sin dependencias).</summary>
    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public bool AtEnd => _i >= _s.Length;

        public double ParseExpression()
        {
            double left = ParseTerm();
            while (!AtEnd && (_s[_i] == '+' || _s[_i] == '-'))
            {
                char op = _s[_i++];
                bool percent = PeekPercent();
                double right = ParseTerm();
                if (percent) { ConsumePercent(); right = left * right / 100.0; }  // 350 + 20% → 350 + 70
                left = op == '+' ? left + right : left - right;
            }
            return left;
        }

        private double ParseTerm()
        {
            double left = ParseUnary();
            while (!AtEnd && (_s[_i] == '*' || _s[_i] == '/' || _s[_i] == 'x' || _s[_i] == 'X'))
            {
                // La 'x' solo es operador si NO es un literal hexadecimal ("0x1F").
                if ((_s[_i] == 'x' || _s[_i] == 'X') && IsHexStart()) break;
                char op = _s[_i++];
                bool percent = PeekPercent();
                double right = ParseUnary();
                if (percent) { ConsumePercent(); right /= 100.0; }                // 200 * 15% → 30
                if (op is '/' && Math.Abs(right) < 1e-12) throw new FormatException("división por cero");
                left = op switch
                {
                    '*' or 'x' or 'X' => left * right,
                    _ => left / right
                };
            }
            return left;
        }

        /// <summary>¿La 'x' actual forma parte de un literal "0x…" que empieza aquí?</summary>
        private bool IsHexStart()
        {
            // Venimos de ParseUnary: un "0x" solo es literal si '0' es el inicio del número.
            // Se mira atrás: si el carácter previo es dígito o '.', la 'x' es operador.
            if (_i == 0) return false;
            char prev = _s[_i - 1];
            return prev == '0' && (_i == 1 || !char.IsDigit(_s[_i - 2]) && _s[_i - 2] != '.');
        }

        private double ParsePower()
        {
            double left = ParsePrimaryWithHex();
            if (!AtEnd && _s[_i] == '^')
            {
                _i++;
                double right = ParseUnary();
                return Math.Pow(left, right);
            }
            return left;
        }

        private double ParseUnary()
        {
            if (!AtEnd && (_s[_i] == '-' || _s[_i] == '+'))
            {
                char sign = _s[_i++];
                double v = ParseUnary();
                return sign == '-' ? -v : v;
            }
            return ParsePower();
        }

        /// <summary>Número decimal o literal hexadecimal ("0x1F" = 31).</summary>
        private double ParsePrimaryWithHex()
        {
            if (!AtEnd && _s[_i] == '0' && _i + 1 < _s.Length && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X'))
            {
                int j = _i + 2;
                while (j < _s.Length && Uri.IsHexDigit(_s[j])) j++;
                string digits = _s[(_i + 2)..j];
                // ulong evita que 0xFFFFFFFFFFFFFFFF se lea como -1 y que un
                // literal larguísimo desborde Convert.ToInt64.
                if (digits.Length is 0 or > 16 ||
                    !ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex))
                    throw new FormatException("hexadecimal inválido");
                _i = j;
                return hex;
            }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (AtEnd) throw new FormatException("fin inesperado");

            if (_s[_i] == '(')
            {
                _i++;
                double inner = ParseExpression();
                if (AtEnd || _s[_i] != ')') throw new FormatException("paréntesis sin cerrar");
                _i++;
                return inner;
            }

            int start = _i;
            while (!AtEnd && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i == start) throw new FormatException("se esperaba un número");

            string number = _s[start.._i];
            if (number.Count(c => c == '.') > 1) throw new FormatException("número inválido");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException("número inválido");

            return value;
        }

        /// <summary>Consume el '%' del operando actual (el valor ya se leyó sin él).</summary>
        private void ConsumePercent()
        {
            if (!AtEnd && _s[_i] == '%') _i++;
        }

        /// <summary>Mira si el operando que empieza ahora termina en % (sin consumirlo).</summary>
        private bool PeekPercent()
        {
            int j = _i;
            int depth = 0;
            while (j < _s.Length && (char.IsDigit(_s[j]) || _s[j] == '.' || _s[j] == '(' || _s[j] == ')' || _s[j] == ' '))
            {
                if (_s[j] == '(') depth++;
                if (_s[j] == ')') depth--;
                j++;
            }
            return j < _s.Length && _s[j] == '%' && depth == 0;
        }
    }

    // ---------------- Unidades y moneda ----------------

    private static readonly Dictionary<string, (string Dimension, double Factor, string Label)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        // longitud (base: metro)
        ["mm"] = ("len", 0.001, "mm"),
        ["cm"] = ("len", 0.01, "cm"),
        ["m"] = ("len", 1, "m"),
        ["km"] = ("len", 1000, "km"),
        ["in"] = ("len", 0.0254, "pulg"),
        ["pulgada"] = ("len", 0.0254, "pulg"),
        ["pulgadas"] = ("len", 0.0254, "pulg"),
        ["ft"] = ("len", 0.3048, "pies"),
        ["pie"] = ("len", 0.3048, "pies"),
        ["pies"] = ("len", 0.3048, "pies"),
        ["yd"] = ("len", 0.9144, "yd"),
        ["mi"] = ("len", 1609.344, "millas"),
        ["milla"] = ("len", 1609.344, "millas"),
        ["millas"] = ("len", 1609.344, "millas"),
        // masa (base: gramo)
        ["mg"] = ("mass", 0.001, "mg"),
        ["g"] = ("mass", 1, "g"),
        ["kg"] = ("mass", 1000, "kg"),
        ["t"] = ("mass", 1_000_000, "t"),
        ["oz"] = ("mass", 28.349523125, "oz"),
        ["lb"] = ("mass", 453.59237, "lb"),
        ["libra"] = ("mass", 453.59237, "lb"),
        ["libras"] = ("mass", 453.59237, "lb"),
        // volumen (base: litro)
        ["ml"] = ("vol", 0.001, "ml"),
        ["l"] = ("vol", 1, "l"),
        ["litro"] = ("vol", 1, "l"),
        ["litros"] = ("vol", 1, "l"),
        ["gal"] = ("vol", 3.785411784, "gal"),
        ["galon"] = ("vol", 3.785411784, "gal"),
        ["galones"] = ("vol", 3.785411784, "gal"),
        // datos (base: byte)
        ["kb"] = ("data", 1024d, "KB"),
        ["mb"] = ("data", 1024d * 1024, "MB"),
        ["gb"] = ("data", 1024d * 1024 * 1024, "GB"),
        ["tb"] = ("data", 1024d * 1024 * 1024 * 1024, "TB"),
        // tiempo (base: segundo)
        ["s"] = ("time", 1, "s"),
        ["seg"] = ("time", 1, "s"),
        ["segundos"] = ("time", 1, "s"),
        ["min"] = ("time", 60, "min"),
        ["minutos"] = ("time", 60, "min"),
        ["h"] = ("time", 3600, "h"),
        ["horas"] = ("time", 3600, "h"),
        ["d"] = ("time", 86400, "d"),
        ["dias"] = ("time", 86400, "d"),
        ["días"] = ("time", 86400, "d")
    };

    private static readonly Dictionary<string, string> Currencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usd"] = "USD", ["dolar"] = "USD", ["dolares"] = "USD", ["dólar"] = "USD", ["dólares"] = "USD",
        ["pen"] = "PEN", ["sol"] = "PEN", ["soles"] = "PEN",
        ["eur"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR",
        ["mxn"] = "MXN", ["cop"] = "COP", ["clp"] = "CLP", ["ars"] = "ARS",
        ["brl"] = "BRL", ["bob"] = "BOB", ["gbp"] = "GBP", ["jpy"] = "JPY"
    };

    /// <summary>¿El texto parece una conversión de unidades o moneda?</summary>
    public static bool LooksLikeConversion(string? text) => TryParseConversion(text, out _, out _, out _);

    private static bool TryParseConversion(string? text, out double amount, out string from, out string to)
    {
        amount = 0; from = ""; to = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        // "3km a millas": separar número pegado a la unidad ("3km" → "3 km").
        string t = Regex.Replace(text.Trim(), @"^(\d[\d.,]*)([A-Za-zÁÉÍÓÚÜÑáéíóúüñ°])", "$1 $2")
            .Replace(",", ".");
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 4 or > 6) return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return false;

        string separator = parts[2].ToLowerInvariant();
        if (separator is not ("a" or "to" or "en" or "->")) return false;

        from = parts[1].Trim().ToLowerInvariant();
        to = parts[3].Trim().ToLowerInvariant();
        if (from.Length == 0 || to.Length == 0) return false;
        return true;
    }

    /// <summary>
    /// Convierte unidades (offline) o moneda (requiere tipos de cambio).
    /// Devuelve null si no es una conversión conocida.
    /// </summary>
    public static async Task<string?> ConvertAsync(string? text, CancellationToken ct = default)
    {
        if (!TryParseConversion(text, out double amount, out string from, out string to)) return null;

        // Temperatura: fórmula, no factor.
        string? temp = ConvertTemperature(amount, from, to);
        if (temp != null) return temp;

        bool fromUnit = Units.TryGetValue(from, out var u1);
        bool toUnit = Units.TryGetValue(to, out var u2);

        if (fromUnit && toUnit)
        {
            if (!string.Equals(u1.Dimension, u2.Dimension, StringComparison.Ordinal))
                return $"No se puede convertir {u1.Label} a {u2.Label}: son magnitudes distintas.";
            double inBase = amount * u1.Factor;
            double value = inBase / u2.Factor;
            return $"{Format(amount)} {u1.Label} = {Format(value)} {u2.Label}";
        }

        if (Currencies.TryGetValue(from, out string? c1) && Currencies.TryGetValue(to, out string? c2)
            && c1 is not null && c2 is not null)
            return await ConvertCurrencyAsync(amount, c1, c2, ct).ConfigureAwait(false);

        return null;
    }

    private static string? ConvertTemperature(double amount, string from, string to)
    {
        string f = NormalizeTempUnit(from);
        string t = NormalizeTempUnit(to);
        if (f.Length == 0 || t.Length == 0) return null;

        double celsius = f switch
        {
            "C" => amount,
            "F" => (amount - 32) * 5 / 9,
            "K" => amount - 273.15,
            _ => double.NaN
        };

        double value = t switch
        {
            "C" => celsius,
            "F" => celsius * 9 / 5 + 32,
            "K" => celsius + 273.15,
            _ => double.NaN
        };

        if (double.IsNaN(value)) return null;
        return $"{Format(amount)} °{f} = {Format(value)} °{t}";
    }

    private static string NormalizeTempUnit(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "c" or "celsius" or "centigrados" or "centígrados" or "°c" => "C",
        "f" or "fahrenheit" or "°f" => "F",
        "k" or "kelvin" => "K",
        _ => ""
    };

    private static readonly Dictionary<string, (IReadOnlyDictionary<string, double> Rates, DateTime At)> Cache = new();

    private static async Task<string> ConvertCurrencyAsync(double amount, string from, string to, CancellationToken ct)
    {
        if (from == to) return $"{Format(amount)} {from} = {Format(amount)} {to}";

        var rates = await GetRatesAsync(ct).ConfigureAwait(false);
        if (rates == null)
            return "No hay conexión para consultar los tipos de cambio (solo la moneda necesita internet).";

        if (!rates.TryGetValue(from, out double rFrom) || !rates.TryGetValue(to, out double rTo))
            return $"No tengo el tipo de cambio de {from} o {to}.";

        double value = amount / rFrom * rTo;   // las tasas vienen con base USD
        return $"{Format(amount)} {from} = {Format(value)} {to}";
    }

    private static async Task<IReadOnlyDictionary<string, double>?> GetRatesAsync(CancellationToken ct)
    {
        bool offline = Environment.GetEnvironmentVariable("SELBAR_OFFLINE") == "1";
        var provider = RatesProvider;
        // Offline bloquea la red real, pero deja inyectar proveedores deterministas en tests.
        if (offline && provider is null) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue("usd", out var hit) && (DateTime.UtcNow - hit.At).TotalHours < 6)
                return hit.Rates;
        }

        try
        {
            provider ??= FetchRatesAsync;
            var rates = await provider(ct).ConfigureAwait(false);
            if (rates == null) return null;
            lock (Cache) Cache["usd"] = (rates, DateTime.UtcNow);
            return rates;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>open.er-api.com: gratuito y sin clave.</summary>
    private static async Task<IReadOnlyDictionary<string, double>?> FetchRatesAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await client.GetAsync("https://open.er-api.com/v6/latest/USD", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("rates", out var ratesEl)) return null;

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in ratesEl.EnumerateObject())
        {
            if (prop.Value.TryGetDouble(out double v)) map[prop.Name] = v;
        }
        map["USD"] = 1;
        return map;
    }
}
