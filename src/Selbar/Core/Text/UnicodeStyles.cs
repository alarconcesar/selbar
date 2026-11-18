using System.Text;

namespace Selbar.Core.Text;

/// <summary>Un estilo de letra Unicode (negrita, cursiva, caligráfica…).</summary>
public sealed record TextStyle(string Id, string Name, Func<string, string> Apply);

/// <summary>
/// Convierte texto normal a las “tipografías” Unicode que se usan en redes y
/// marketing (𝗯𝗼𝗹𝗱, 𝘪𝘵𝘢𝘭𝘪𝘤, 𝓼𝓬𝓻𝓲𝓹𝓽…). Solo transforma A-Z, a-z y 0-9; el
/// resto de caracteres (tildes, ñ, emojis, signos) se mantiene tal cual.
/// </summary>
public static class UnicodeStyles
{
    private static readonly Dictionary<char, string> ScriptUpper = new()
    {
        ['B'] = "\u212C", ['E'] = "\u2130", ['F'] = "\u2131", ['H'] = "\u210B",
        ['I'] = "\u2110", ['L'] = "\u2112", ['M'] = "\u2133", ['R'] = "\u211B"
    };

    private static readonly Dictionary<char, string> ScriptLower = new()
    {
        ['e'] = "\u212F", ['g'] = "\u210A", ['o'] = "\u2134"
    };

    private static readonly Dictionary<char, string> DoubleStruckUpper = new()
    {
        ['C'] = "\u2102", ['H'] = "\u210D", ['N'] = "\u2115", ['P'] = "\u2119",
        ['Q'] = "\u211A", ['R'] = "\u211D", ['Z'] = "\u2124"
    };

    private static readonly Dictionary<char, char> SmallCaps = new()
    {
        ['a'] = '\u1D00', ['b'] = '\u0299', ['c'] = '\u1D04', ['d'] = '\u1D05',
        ['e'] = '\u1D07', ['f'] = '\uA730', ['g'] = '\u0262', ['h'] = '\u029C',
        ['i'] = '\u026A', ['j'] = '\u1D0A', ['k'] = '\u1D0B', ['l'] = '\u029F',
        ['m'] = '\u1D0D', ['n'] = '\u0274', ['o'] = '\u1D0F', ['p'] = '\u1D18',
        ['q'] = '\u01EB', ['r'] = '\u0280', ['s'] = '\uA731', ['t'] = '\u1D1B',
        ['u'] = '\u1D1C', ['v'] = '\u1D20', ['w'] = '\u1D21', ['x'] = 'x',
        ['y'] = '\u028F', ['z'] = '\u1D22'
    };

    /// <summary>Estilos en el orden en que se muestran.</summary>
    public static IReadOnlyList<TextStyle> All { get; } = new List<TextStyle>
    {
        new("bold", "Negrita", t => MathAlphanumeric(t, 0x1D400, 0x1D41A, 0x1D7CE)),
        new("italic", "Cursiva", t => MathAlphanumeric(t, 0x1D434, 0x1D44E, -1,
            new Dictionary<char, string> { ['h'] = "\u210E" })),
        new("bolditalic", "Negrita cursiva", t => MathAlphanumeric(t, 0x1D468, 0x1D482, -1)),
        new("script", "Caligráfica", t => MathAlphanumeric(t, 0x1D49C, 0x1D4B6, -1,
            Merge(ScriptUpper, ScriptLower))),
        new("boldscript", "Caligráfica negrita", t => MathAlphanumeric(t, 0x1D4D0, 0x1D4EA, -1)),
        new("doublestruck", "Doble trazo", t => MathAlphanumeric(t, 0x1D538, 0x1D552, 0x1D7D8,
            DoubleStruckUpper)),
        new("mono", "Monoespaciada", t => MathAlphanumeric(t, 0x1D670, 0x1D68A, 0x1D7F6)),
        new("sansbold", "Sans negrita", t => MathAlphanumeric(t, 0x1D5D4, 0x1D5EE, -1)),
        new("sansitalic", "Sans cursiva", t => MathAlphanumeric(t, 0x1D608, 0x1D622, -1)),
        new("fullwidth", "Ancha", FullWidth),
        new("smallcaps", "Versalitas", ToSmallCaps),
        new("circled", "Círculos", Circled)
    };

    public static string Apply(string id, string text)
    {
        var style = All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return style?.Apply(text ?? "") ?? (text ?? "");
    }

    private static string MathAlphanumeric(string text, int upperBase, int lowerBase, int digitBase,
        Dictionary<char, string>? exceptions = null)
    {
        var sb = new StringBuilder(text.Length * 2);

        foreach (char c in text)
        {
            if (exceptions != null && exceptions.TryGetValue(c, out var special))
            {
                sb.Append(special);
                continue;
            }

            int codePoint = -1;
            if (c is >= 'A' and <= 'Z')
                codePoint = upperBase + (c - 'A');
            else if (c is >= 'a' and <= 'z')
                codePoint = lowerBase + (c - 'a');
            else if (digitBase > 0 && c is >= '0' and <= '9')
                codePoint = digitBase + (c - '0');

            if (codePoint > 0 && codePoint <= 0x10FFFF)
                sb.Append(char.ConvertFromUtf32(codePoint));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static Dictionary<char, string> Merge(params Dictionary<char, string>[] maps)
    {
        var result = new Dictionary<char, string>();
        foreach (var map in maps)
            foreach (var pair in map)
                result[pair.Key] = pair.Value;
        return result;
    }

    private static string FullWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == ' ') sb.Append('\u3000');
            else if (c is >= '!' and <= '~') sb.Append((char)(c + 0xFEE0));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ToSmallCaps(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            char lower = char.ToLowerInvariant(c);
            sb.Append(SmallCaps.TryGetValue(lower, out var mapped) ? mapped : c);
        }
        return sb.ToString();
    }

    private static string Circled(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is >= 'A' and <= 'Z') sb.Append((char)(0x24B6 + (c - 'A')));
            else if (c is >= 'a' and <= 'z') sb.Append((char)(0x24D0 + (c - 'a')));
            else if (c == '0') sb.Append('\u24EA');
            else if (c is >= '1' and <= '9') sb.Append((char)(0x2460 + (c - '1')));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
