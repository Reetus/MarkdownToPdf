using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MarkdownToPdf;

/// <summary>All settings that control conversion. Serialisable to / from JSON.</summary>
public sealed class MarkdownPdfOptions
{
    public PageOptions Page { get; set; } = new();
    public FontOptions Fonts { get; set; } = new();
    public ThemeOptions Theme { get; set; } = new();
    public CodeOptions Code { get; set; } = new();
    public HeaderFooterOptions Header { get; set; } = new() { Left = "{title}", Right = "{section}", SkipFirstPage = true };
    public HeaderFooterOptions Footer { get; set; } = new() { Center = "Page {page} of {pages}" };
    public TocOptions Toc { get; set; } = new();
    public CoverOptions Cover { get; set; } = new();
    public DocumentMetadata Metadata { get; set; } = new();
    public DocumentOptions Document { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter( JsonNamingPolicy.CamelCase ) },
    };

    public static MarkdownPdfOptions FromJson( string json ) =>
        JsonSerializer.Deserialize<MarkdownPdfOptions>( json, JsonOptions ) ?? new MarkdownPdfOptions();

    public static MarkdownPdfOptions Load( string path ) => FromJson( File.ReadAllText( path ) );

    public string ToJson() => JsonSerializer.Serialize( this, JsonOptions );
}

public sealed class PageOptions
{
    /// <summary>A4, A3, A5, B5, Letter, Legal, Tabloid, Executive, or a custom size such as "210mm x 297mm" / "8.5in x 11in".</summary>
    public string Size { get; set; } = "A4";
    public bool Landscape { get; set; }
    /// <summary>CSS-like margin shorthand: "20mm", "20mm 18mm", "1in 0.75in 1in 0.75in" (top right bottom left).</summary>
    public string Margins { get; set; } = "22mm 20mm 20mm 20mm";

    public (float Width, float Height) ResolveSize()
    {
        var (w, h) = Size.Trim().ToLowerInvariant() switch
        {
            "a3" => (841.89f, 1190.55f),
            "a4" => (595.28f, 841.89f),
            "a5" => (419.53f, 595.28f),
            "b5" => (498.9f, 708.66f),
            "letter" => (612f, 792f),
            "legal" => (612f, 1008f),
            "tabloid" or "ledger" => (792f, 1224f),
            "executive" => (522f, 756f),
            _ => ParseCustom( Size ),
        };
        return Landscape ? (Math.Max( w, h ), Math.Min( w, h )) : (w, h);
    }

    private static (float, float) ParseCustom( string s )
    {
        var parts = s.Split( ['x', 'X', '*', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if ( parts.Length == 2 )
        {
            return (Units.Parse( parts[0] ), Units.Parse( parts[1] ));
        }

        throw new ArgumentException( $"Unknown page size '{s}'. Use A4, Letter, Legal, A3, A5, B5, Tabloid, Executive or e.g. '210mm x 297mm'." );
    }

    public (float Top, float Right, float Bottom, float Left) ResolveMargins()
    {
        var p = Margins.Split( [' ', ','], StringSplitOptions.RemoveEmptyEntries ).Select( Units.Parse ).ToArray();
        return p.Length switch
        {
            1 => (p[0], p[0], p[0], p[0]),
            2 => (p[0], p[1], p[0], p[1]),
            3 => (p[0], p[1], p[2], p[1]),
            4 => (p[0], p[1], p[2], p[3]),
            _ => throw new ArgumentException( $"Invalid margins '{Margins}'." ),
        };
    }
}

public sealed class FontOptions
{
    /// <summary>Comma-separated candidates; each is an installed family name, a font file path, or a generic name (sans-serif, serif, monospace).</summary>
    public string Body { get; set; } = "Segoe UI, Inter, Helvetica Neue, Arial, Liberation Sans, DejaVu Sans, sans-serif";
    /// <summary>Heading font candidates; null = same as body.</summary>
    public string? Heading { get; set; }
    public string Monospace { get; set; } = "Cascadia Mono, Consolas, JetBrains Mono, SF Mono, Menlo, DejaVu Sans Mono, Liberation Mono, monospace";
    /// <summary>Fonts tried, in order, for characters missing from the primary font (symbols, CJK, emoji...).</summary>
    public string Fallback { get; set; } = "Segoe UI Symbol, Segoe UI Emoji, Malgun Gothic, Microsoft YaHei, Yu Gothic, Microsoft JhengHei, Nirmala UI, Arial Unicode MS, Noto Sans, Noto Sans CJK SC, Noto Sans Symbols, Noto Sans Symbols2, DejaVu Sans, Apple SD Gothic Neo, PingFang SC, Hiragino Sans";
    /// <summary>Body text size in points.</summary>
    public float Size { get; set; } = 10.5f;
    /// <summary>Code size in points (code blocks and inline code).</summary>
    public float CodeSize { get; set; } = 9f;
    /// <summary>Line height as a multiple of the font size.</summary>
    public float LineHeight { get; set; } = 1.5f;
    /// <summary>Font weight used for headings (400..900). 600 = semibold, 700 = bold.</summary>
    public int HeadingWeight { get; set; } = 700;
    /// <summary>Heading sizes for H1..H6 as multiples of the body size.</summary>
    public float[] HeadingScale { get; set; } = [2.1f, 1.65f, 1.35f, 1.15f, 1.0f, 0.95f];
}

public sealed class ThemeOptions
{
    /// <summary>Built-in palette: "default", "github", "ocean", "forest", "mono".</summary>
    public string Name { get; set; } = "default";
    public string? Text { get; set; }
    public string? Heading { get; set; }
    public string? Accent { get; set; }
    public string? Link { get; set; }
    public string? Muted { get; set; }
    public string? Border { get; set; }
    public string? CodeBackground { get; set; }
    public string? InlineCodeBackground { get; set; }
    public string? InlineCodeText { get; set; }
    public string? QuoteBar { get; set; }
    public string? TableHeaderBackground { get; set; }
    public string? TableStripeBackground { get; set; }
    /// <summary>Draw a rule under H1 and H2 headings.</summary>
    public bool HeadingRules { get; set; } = true;
    /// <summary>Justify paragraph text.</summary>
    public bool Justify { get; set; }
    /// <summary>Paragraph spacing as a multiple of the body size.</summary>
    public float ParagraphSpacing { get; set; } = 0.75f;
}

public sealed class CodeOptions
{
    public bool Highlight { get; set; } = true;
    /// <summary>"light" (GitHub-like) or "dark" (One Dark-like).</summary>
    public string Theme { get; set; } = "light";
    public bool LineNumbers { get; set; }
    /// <summary>Wrap long lines (otherwise they are clipped at the right edge).</summary>
    public bool Wrap { get; set; } = true;
    /// <summary>Show the language name in the top right corner of fenced blocks.</summary>
    public bool ShowLanguage { get; set; } = true;
    public int TabSize { get; set; } = 4;
}

public sealed class HeaderFooterOptions
{
    /// <summary>Tokens: {page}, {pages}, {title}, {author}, {date}, {section}, {file}.</summary>
    public string? Left { get; set; }
    public string? Center { get; set; }
    public string? Right { get; set; }
    public bool Rule { get; set; } = true;
    public bool Enabled { get; set; } = true;
    /// <summary>Do not draw on the first content page (useful for headers above the document title).</summary>
    public bool SkipFirstPage { get; set; }
    /// <summary>Font size in points.</summary>
    public float Size { get; set; } = 8f;
    public bool IsEmpty => string.IsNullOrWhiteSpace( Left ) && string.IsNullOrWhiteSpace( Center ) && string.IsNullOrWhiteSpace( Right );
}

public sealed class TocOptions
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "Contents";
    /// <summary>Deepest heading level included in the generated table of contents.</summary>
    public int Depth { get; set; } = 3;
    /// <summary>Deepest heading level included as PDF bookmarks (0 disables bookmarks).</summary>
    public int BookmarkDepth { get; set; } = 3;
}

public sealed class CoverOptions
{
    public bool Enabled { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Author { get; set; }
    /// <summary>Text for the date line; "{date}" means today's date.</summary>
    public string? Date { get; set; } = "{date}";
}

public sealed class DocumentMetadata
{
    /// <summary>Document title; defaults to the cover title, front matter, or first H1.</summary>
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    /// <summary>Culture used for {date} and number formatting.</summary>
    public string? Culture { get; set; }
}

public sealed class DocumentOptions
{
    /// <summary>Start each input file on a new page.</summary>
    public bool PageBreakBetweenFiles { get; set; } = true;
    /// <summary>Start every H1 on a new page.</summary>
    public bool PageBreakBeforeH1 { get; set; }
    /// <summary>Start every heading of this level on a new page (0 = off).</summary>
    public int PageBreakBeforeLevel { get; set; }
    /// <summary>Remove leading blocks of subsequent files that repeat the first file's preamble (ignoring digits).</summary>
    public bool StripRepeatedPreamble { get; set; } = true;
    /// <summary>Append local Markdown files linked from the inputs (recursively) into the same PDF.</summary>
    public bool FollowLinks { get; set; }
    /// <summary>Glob-like patterns (with * and ?) of linked files that are never followed.</summary>
    public List<string> FollowExclude { get; set; } = [];
    /// <summary>Download http(s) images.</summary>
    public bool AllowRemoteImages { get; set; }
    /// <summary>Heading level used for the {section} header token.</summary>
    public int SectionLevel { get; set; } = 2;
    /// <summary>Keep a short paragraph that ends with ':' on the same page as the following block.</summary>
    public bool KeepLabelWithNext { get; set; } = true;
}

/// <summary>Unit parsing: pt (default), mm, cm, in, px (96 dpi).</summary>
public static partial class Units
{
    [GeneratedRegex( @"^\s*(-?[0-9]*\.?[0-9]+)\s*(pt|mm|cm|in|px)?\s*$", RegexOptions.IgnoreCase )]
    private static partial Regex UnitRegex();

    public static float Parse( string value )
    {
        var m = UnitRegex().Match( value );
        if ( !m.Success )
        {
            throw new FormatException( $"Invalid length '{value}'. Use e.g. 20mm, 0.75in, 54pt." );
        }

        var v = float.Parse( m.Groups[1].Value, CultureInfo.InvariantCulture );
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "mm" => v * 72f / 25.4f,
            "cm" => v * 72f / 2.54f,
            "in" => v * 72f,
            "px" => v * 0.75f,
            _ => v,
        };
    }
}
