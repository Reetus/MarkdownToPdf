using System.Globalization;
using MarkdownToPdf.Fonts;
using MarkdownToPdf.Images;

namespace MarkdownToPdf.Layout;

public readonly record struct Color(float R, float G, float B)
{
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(1, 1, 1);

    public static Color Parse(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"Invalid colour '{hex}'. Use #rrggbb.");
        return new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    /// <summary>Linear blend towards <paramref name="other"/> (t = 0 → this, 1 → other).</summary>
    public Color Mix(Color other, float t) => new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);
}

/// <summary>Theme with every colour resolved.</summary>
internal sealed class ResolvedTheme
{
    public Color Text, Heading, H3, Accent, Link, Muted, Border, CodeBackground, CodeBorder, CodeText,
                 InlineCodeBackground, InlineCodeText, QuoteBar, QuoteText, TableHeaderBackground, TableStripe, Rule;
    public bool HeadingRules, Justify;
    public float ParagraphSpacing;
    public CodePalette Code = CodePalette.Light;

    public static ResolvedTheme From(ThemeOptions o, CodeOptions code)
    {
        var t = new ResolvedTheme();
        switch (o.Name.Trim().ToLowerInvariant())
        {
            case "github":
                t.Set("#1F2328", "#1F2328", "#1F2328", "#0969DA", "#0969DA", "#59636E", "#D1D9E0");
                break;
            case "ocean":
                t.Set("#1E293B", "#0C4A6E", "#0E7490", "#0E7490", "#0369A1", "#64748B", "#E2E8F0");
                break;
            case "forest":
                t.Set("#1C2A1F", "#14532D", "#15803D", "#15803D", "#166534", "#6B7280", "#E5E7EB");
                break;
            case "mono" or "grayscale":
                t.Set("#111111", "#000000", "#222222", "#333333", "#111111", "#666666", "#DDDDDD");
                break;
            default:
                t.Set("#1F2937", "#0F172A", "#1D4ED8", "#2563EB", "#1D4ED8", "#6B7280", "#E5E7EB");
                break;
        }

        Color P(string? s, Color fallback) => string.IsNullOrWhiteSpace(s) ? fallback : Color.Parse(s);
        t.Text = P(o.Text, t.Text);
        t.Heading = P(o.Heading, t.Heading);
        t.Accent = P(o.Accent, t.Accent);
        t.Link = P(o.Link, t.Link);
        t.Muted = P(o.Muted, t.Muted);
        t.Border = P(o.Border, t.Border);
        t.Rule = t.Border;
        t.QuoteBar = P(o.QuoteBar, t.Border.Mix(Color.Black, 0.12f));
        t.QuoteText = t.Text.Mix(t.Muted, 0.75f);
        t.TableHeaderBackground = P(o.TableHeaderBackground, new Color(0.955f, 0.96f, 0.97f));
        t.TableStripe = P(o.TableStripeBackground, new Color(0.982f, 0.984f, 0.988f));
        t.InlineCodeBackground = P(o.InlineCodeBackground, new Color(0.937f, 0.945f, 0.953f));
        t.InlineCodeText = P(o.InlineCodeText, t.Text);
        t.HeadingRules = o.HeadingRules;
        t.Justify = o.Justify;
        t.ParagraphSpacing = o.ParagraphSpacing;

        t.Code = code.Theme.Trim().ToLowerInvariant() is "dark" or "onedark" or "one-dark" ? CodePalette.Dark : CodePalette.Light;
        t.CodeBackground = P(o.CodeBackground, t.Code.Background);
        t.CodeBorder = t.Code.Border;
        t.CodeText = t.Code.Plain;
        return t;
    }

    private void Set(string text, string heading, string h3, string accent, string link, string muted, string border)
    {
        Text = Color.Parse(text); Heading = Color.Parse(heading); H3 = Color.Parse(h3); Accent = Color.Parse(accent);
        Link = Color.Parse(link); Muted = Color.Parse(muted); Border = Color.Parse(border);
    }
}

internal sealed class CodePalette
{
    public Color Background, Border, Plain, Keyword, Type, String, Number, Comment, Function, Literal, Property,
                 Tag, Attribute, Variable, Decorator, Operator, Preprocessor, Inserted, Deleted, LineNumber, Label;

    public static readonly CodePalette Light = new()
    {
        Background = Color.Parse("#F6F8FA"), Border = Color.Parse("#E1E4E8"), Plain = Color.Parse("#24292F"),
        Keyword = Color.Parse("#CF222E"), Type = Color.Parse("#953800"), String = Color.Parse("#0A3069"),
        Number = Color.Parse("#0550AE"), Comment = Color.Parse("#6E7781"), Function = Color.Parse("#8250DF"),
        Literal = Color.Parse("#0550AE"), Property = Color.Parse("#0550AE"), Tag = Color.Parse("#116329"),
        Attribute = Color.Parse("#0550AE"), Variable = Color.Parse("#953800"), Decorator = Color.Parse("#8250DF"),
        Operator = Color.Parse("#CF222E"), Preprocessor = Color.Parse("#CF222E"), Inserted = Color.Parse("#116329"),
        Deleted = Color.Parse("#82071E"), LineNumber = Color.Parse("#8C959F"), Label = Color.Parse("#8C959F"),
    };

    public static readonly CodePalette Dark = new()
    {
        Background = Color.Parse("#282C34"), Border = Color.Parse("#21252B"), Plain = Color.Parse("#ABB2BF"),
        Keyword = Color.Parse("#C678DD"), Type = Color.Parse("#E5C07B"), String = Color.Parse("#98C379"),
        Number = Color.Parse("#D19A66"), Comment = Color.Parse("#7F848E"), Function = Color.Parse("#61AFEF"),
        Literal = Color.Parse("#D19A66"), Property = Color.Parse("#E06C75"), Tag = Color.Parse("#E06C75"),
        Attribute = Color.Parse("#D19A66"), Variable = Color.Parse("#E06C75"), Decorator = Color.Parse("#61AFEF"),
        Operator = Color.Parse("#56B6C2"), Preprocessor = Color.Parse("#C678DD"), Inserted = Color.Parse("#98C379"),
        Deleted = Color.Parse("#E06C75"), LineNumber = Color.Parse("#636D83"), Label = Color.Parse("#7F848E"),
    };
}

// ───────────────────────────── draw operations ─────────────────────────────
// All coordinates are in points, measured from the top-left corner of the page (y grows downwards).

internal abstract record DrawOp;

internal sealed record TextOp(FaceRef Face, float Size, float X, float Baseline, string Text, Color Color) : DrawOp;

/// <summary>Text whose content is only known when the PDF is written (page numbers).</summary>
internal sealed record DeferredTextOp(Func<string> Text, FaceRef Face, float Size, float X, float Baseline, Color Color, TextAlign Align) : DrawOp;

internal sealed record RectOp(float X, float Y, float W, float H, Color? Fill, Color? Stroke, float LineWidth = 0.75f,
    float Radius = 0, bool RoundTop = true, bool RoundBottom = true) : DrawOp;

internal sealed record LineOp(float X1, float Y1, float X2, float Y2, Color Color, float Width, float[]? Dash = null, bool RoundCap = false) : DrawOp;

internal sealed record PolylineOp(IReadOnlyList<(float X, float Y)> Points, Color Color, float Width, bool Fill = false) : DrawOp;

internal sealed record CircleOp(float Cx, float Cy, float R, Color? Fill, Color? Stroke, float LineWidth = 0.75f) : DrawOp;

internal sealed record ImageOp(PdfImage Image, float X, float Y, float W, float H) : DrawOp;

/// <summary>Starts clipping to a rectangle (until the matching <see cref="ClipEndOp"/>).</summary>
internal sealed record ClipOp(float X, float Y, float W, float H) : DrawOp;

internal sealed record ClipEndOp : DrawOp;

internal enum TextAlign { Left, Center, Right, Justify }

/// <summary>A clickable area: either an external URI or an internal anchor key resolved at write time.</summary>
internal sealed record LinkArea(float X, float Y, float W, float H, string? Uri, string? Anchor);

/// <summary>A resolved position inside the document.</summary>
internal sealed record Destination(int PageIndex, float Y);

internal sealed class PageContent(int index, float width, float height)
{
    public int Index { get; } = index;
    public float Width { get; } = width;
    public float Height { get; } = height;
    private readonly List<(int Z, int Seq, DrawOp Op)> _background = [];
    public List<DrawOp> Foreground { get; } = [];
    public List<LinkArea> Links { get; } = [];
    public bool SuppressHeaderFooter { get; set; }
    public string? SectionAtStart { get; set; }
    public string? FirstSectionOnPage { get; set; }
    public string? SourceFile { get; set; }

    public void AddBackground(DrawOp op, int z = 0) => _background.Add((z, _background.Count, op));

    public IEnumerable<DrawOp> Background => _background.OrderBy(b => b.Z).ThenBy(b => b.Seq).Select(b => b.Op);
}
