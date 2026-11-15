using System.Globalization;
using System.Text;

namespace MarkdownToPdf.Markdown;

/// <summary>HTML entity decoding (common named entities + numeric references) and backslash unescaping.</summary>
internal static class Entities
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–",
        ["laquo"] = "«", ["raquo"] = "»", ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”",
        ["sbquo"] = "‚", ["bdquo"] = "„", ["bull"] = "•", ["middot"] = "·", ["deg"] = "°", ["plusmn"] = "±",
        ["times"] = "×", ["divide"] = "÷", ["euro"] = "€", ["pound"] = "£", ["yen"] = "¥", ["cent"] = "¢",
        ["sect"] = "§", ["para"] = "¶", ["larr"] = "←", ["rarr"] = "→", ["uarr"] = "↑", ["darr"] = "↓",
        ["harr"] = "↔", ["lArr"] = "⇐", ["rArr"] = "⇒", ["hArr"] = "⇔", ["le"] = "≤", ["ge"] = "≥",
        ["ne"] = "≠", ["asymp"] = "≈", ["infin"] = "∞", ["minus"] = "−", ["micro"] = "µ", ["frac12"] = "½",
        ["frac14"] = "¼", ["frac34"] = "¾", ["sup1"] = "¹", ["sup2"] = "²", ["sup3"] = "³", ["iexcl"] = "¡",
        ["iquest"] = "¿", ["shy"] = "­", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ",
        ["zwj"] = "‍", ["zwnj"] = "‌", ["dagger"] = "†", ["Dagger"] = "‡", ["permil"] = "‰",
        ["check"] = "✓", ["cross"] = "✗", ["star"] = "☆", ["hearts"] = "♥", ["spades"] = "♠", ["clubs"] = "♣",
        ["diams"] = "♦", ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["pi"] = "π",
        ["sigma"] = "σ", ["omega"] = "ω", ["lambda"] = "λ", ["mu"] = "μ", ["theta"] = "θ", ["Delta"] = "Δ",
        ["Omega"] = "Ω", ["Sigma"] = "Σ", ["sum"] = "∑", ["prod"] = "∏", ["radic"] = "√", ["int"] = "∫",
        ["part"] = "∂", ["nabla"] = "∇", ["isin"] = "∈", ["notin"] = "∉", ["cap"] = "∩", ["cup"] = "∪",
        ["sub"] = "⊂", ["sup"] = "⊃", ["and"] = "∧", ["or"] = "∨", ["not"] = "¬", ["forall"] = "∀", ["exist"] = "∃",
        ["empty"] = "∅", ["equiv"] = "≡", ["prime"] = "′", ["Prime"] = "″", ["acute"] = "´", ["uml"] = "¨",
        ["ordf"] = "ª", ["ordm"] = "º", ["macr"] = "¯", ["cedil"] = "¸", ["brvbar"] = "¦", ["curren"] = "¤",
        ["Auml"] = "Ä", ["Ouml"] = "Ö", ["Uuml"] = "Ü", ["auml"] = "ä", ["ouml"] = "ö", ["uuml"] = "ü", ["szlig"] = "ß",
        ["eacute"] = "é", ["egrave"] = "è", ["ecirc"] = "ê", ["aacute"] = "á", ["agrave"] = "à", ["acirc"] = "â",
        ["iacute"] = "í", ["oacute"] = "ó", ["uacute"] = "ú", ["ntilde"] = "ñ", ["ccedil"] = "ç", ["Eacute"] = "É",
        ["aring"] = "å", ["Aring"] = "Å", ["aelig"] = "æ", ["AElig"] = "Æ", ["oslash"] = "ø", ["Oslash"] = "Ø",
    };

    public static bool TryDecode(string name, out string value)
    {
        value = "";
        if (name.StartsWith('#'))
        {
            int cp = 0;
            bool ok = name.Length > 1 && (name[1] is 'x' or 'X'
                ? int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp)
                : int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out cp));
            if (!ok) return false;
            if (cp == 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
            value = char.ConvertFromUtf32(cp);
            return true;
        }
        return Named.TryGetValue(name, out value!);
    }

    public static bool IsAsciiPunctuation(char c) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    /// <summary>Resolves backslash escapes and entity references (used for URLs, titles and info strings).</summary>
    public static string DecodeAndUnescape(string s)
    {
        if (s.IndexOfAny(['\\', '&']) < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsAsciiPunctuation(s[i + 1])) { sb.Append(s[++i]); continue; }
            if (c == '&')
            {
                int semi = s.IndexOf(';', i + 1);
                if (semi > i + 1 && semi - i <= 33 && TryDecode(s[(i + 1)..semi], out var v)) { sb.Append(v); i = semi; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Generates GitHub-compatible heading anchors (lower case, punctuation removed, spaces to dashes, de-duplicated).</summary>
public sealed class Slugger
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ') sb.Append('-');
            else if (char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) sb.Append(c);
        }
        return sb.ToString();
    }

    public string Slug(string text)
    {
        var baseSlug = Normalize(text);
        if (baseSlug.Length == 0) baseSlug = "section";
        if (!_seen.TryGetValue(baseSlug, out int n))
        {
            _seen[baseSlug] = 1;
            return baseSlug;
        }
        string candidate;
        do { candidate = $"{baseSlug}-{n++}"; } while (_seen.ContainsKey(candidate));
        _seen[baseSlug] = n;
        _seen[candidate] = 1;
        return candidate;
    }
}
