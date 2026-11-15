using System.Globalization;
using System.Text;

namespace MarkdownToPdf.Fonts;

/// <summary>A concrete font face that can measure and encode text for a PDF content stream.</summary>
internal abstract class FontFace
{
    /// <summary>PDF resource name (F1, F2...). Assigned by the document writer.</summary>
    public string ResourceName { get; set; } = "";
    public abstract string BaseName { get; }
    /// <summary>Metrics are in 1/1000 em.</summary>
    public abstract float Ascent { get; }
    public abstract float Descent { get; }
    public abstract float CapHeight { get; }
    public abstract int Weight { get; }
    public abstract bool Italic { get; }
    public abstract bool IsFixedPitch { get; }
    public abstract bool HasChar(int codepoint);
    public abstract float Advance(int codepoint);
    /// <summary>Appends the hex encoding of a code point for use in a &lt;...&gt; Tj string and records its usage.</summary>
    public abstract void AppendEncoded(int codepoint, StringBuilder hex);
    public bool Used { get; set; }

    public float Measure(string text, float size)
    {
        float w = 0;
        foreach (var r in text.EnumerateRunes()) w += Advance(r.Value);
        return w * size / 1000f;
    }

    public string Encode(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (var r in text.EnumerateRunes()) AppendEncoded(r.Value, sb);
        Used = true;
        return sb.ToString();
    }

    public override string ToString() => BaseName;
}

/// <summary>Embedded TrueType/OpenType face, written as a Type0 / Identity-H CID font.</summary>
internal sealed class TrueTypeFace : FontFace
{
    public TrueTypeFont Font { get; }
    /// <summary>Glyph id → code point (for the ToUnicode CMap).</summary>
    public SortedDictionary<ushort, int> UsedGlyphs { get; } = new();
    private readonly float _scale;

    public TrueTypeFace(TrueTypeFont font)
    {
        Font = font;
        font.EnsureLoaded();
        _scale = 1000f / font.UnitsPerEm;
    }

    public override string BaseName => string.IsNullOrEmpty(Font.PostScriptName) ? "Font" : Font.PostScriptName;
    public override float Ascent => Font.Ascender * _scale;
    public override float Descent => Font.Descender * _scale;
    public override float CapHeight => Font.CapHeight * _scale;
    public override int Weight => Font.WeightClass;
    public override bool Italic => Font.IsItalic;
    public override bool IsFixedPitch => Font.IsFixedPitch;
    public float Scale => _scale;

    public override bool HasChar(int codepoint) => Font.TryGetGlyph(codepoint, out _);

    public override float Advance(int codepoint)
    {
        Font.TryGetGlyph(codepoint, out var gid);
        return Font.AdvanceWidth(gid) * _scale;
    }

    public override void AppendEncoded(int codepoint, StringBuilder hex)
    {
        Font.TryGetGlyph(codepoint, out var gid);
        UsedGlyphs.TryAdd(gid, codepoint);
        hex.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
    }
}

/// <summary>One of the PDF standard 14 fonts (not embedded, WinAnsi encoding). Used when no system font is found.</summary>
internal sealed class StandardFace : FontFace
{
    private readonly string _name;
    private readonly short[] _widths; // index = code - 32 for 32..126
    private readonly bool _bold, _italic, _mono;

    public StandardFace(string name, bool bold, bool italic, bool mono, short[] widths)
    {
        _name = name; _bold = bold; _italic = italic; _mono = mono; _widths = widths;
    }

    public override string BaseName => _name;
    public override float Ascent => _mono ? 629 : 718;
    public override float Descent => _mono ? -157 : -207;
    public override float CapHeight => _mono ? 562 : 718;
    public override int Weight => _bold ? 700 : 400;
    public override bool Italic => _italic;
    public override bool IsFixedPitch => _mono;

    public override bool HasChar(int codepoint) => WinAnsi.TryEncode(codepoint, out _);

    public override float Advance(int codepoint)
    {
        if (_mono) return 600;
        if (codepoint is >= 32 and <= 126) return _widths[codepoint - 32];
        switch (codepoint)
        {
            case 0xA0: return 278;
            case '•': return 350;
            case '–': return 556;
            case '—' or '…' or '‰': return 1000;
            case '‘' or '’' or '‚': return _bold ? 278 : 222;
            case '“' or '”' or '„': return _bold ? 500 : 333;
            case '©' or '®': return 737;
            case '°': return 400;
            case '×' or '÷' or '±' or '¬': return 584;
            case '·': return 278;
            case '™': return 1000;
        }
        // accented letters: use the width of the base letter
        var decomposed = char.ConvertFromUtf32(codepoint).Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 0 && decomposed[0] is >= ' ' and <= '~') return _widths[decomposed[0] - 32];
        return 556;
    }

    public override void AppendEncoded(int codepoint, StringBuilder hex)
    {
        if (!WinAnsi.TryEncode(codepoint, out byte b)) b = (byte)'?';
        hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
    }
}

internal static class WinAnsi
{
    private static readonly Dictionary<int, byte> High = new()
    {
        [0x20AC] = 0x80, [0x201A] = 0x82, [0x0192] = 0x83, [0x201E] = 0x84, [0x2026] = 0x85, [0x2020] = 0x86, [0x2021] = 0x87,
        [0x02C6] = 0x88, [0x2030] = 0x89, [0x0160] = 0x8A, [0x2039] = 0x8B, [0x0152] = 0x8C, [0x017D] = 0x8E, [0x2018] = 0x91,
        [0x2019] = 0x92, [0x201C] = 0x93, [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97, [0x02DC] = 0x98,
        [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B, [0x0153] = 0x9C, [0x017E] = 0x9E, [0x0178] = 0x9F,
    };

    public static bool TryEncode(int cp, out byte b)
    {
        if (cp is >= 32 and <= 126 or >= 0xA0 and <= 0xFF) { b = (byte)cp; return true; }
        return High.TryGetValue(cp, out b);
    }
}

internal static class StandardFonts
{
    // Helvetica widths for ASCII 32..126 (Adobe AFM).
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] HelveticaBold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static readonly short[] Courier = Enumerable.Repeat((short)600, 95).ToArray();

    public static StandardFace Sans(bool bold, bool italic) => new(
        bold ? (italic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : (italic ? "Helvetica-Oblique" : "Helvetica"),
        bold, italic, false, bold ? HelveticaBold : Helvetica);

    public static StandardFace Mono(bool bold, bool italic) => new(
        bold ? (italic ? "Courier-BoldOblique" : "Courier-Bold") : (italic ? "Courier-Oblique" : "Courier"),
        bold, italic, true, Courier);
}
