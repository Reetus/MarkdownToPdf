namespace MarkdownToPdf.Markdown;

// ───────────────────────────── Blocks ─────────────────────────────

/// <summary>Base type of all block-level Markdown nodes.</summary>
public abstract class Block
{
    /// <summary>1-based source line where the block starts (for diagnostics).</summary>
    public int Line { get; set; }
}

/// <summary>Blocks that contain raw inline text which is parsed in a second pass.</summary>
public abstract class LeafBlock : Block
{
    internal string RawText { get; set; } = "";
    public List<Inline> Inlines { get; set; } = [];
}

public abstract class ContainerBlock : Block
{
    public List<Block> Children { get; } = [];
}

public sealed class MarkdownDocument : ContainerBlock
{
    /// <summary>Full path of the source file, when parsed from a file.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Link reference definitions ([label]: url "title"), keyed by normalised label.</summary>
    public Dictionary<string, LinkReference> References { get; } = new( StringComparer.Ordinal );

    /// <summary>Footnote definitions keyed by normalised label, in definition order.</summary>
    public Dictionary<string, FootnoteDefinition> Footnotes { get; } = new( StringComparer.Ordinal );

    /// <summary>Footnotes in order of first reference (numbered 1..n).</summary>
    public List<FootnoteDefinition> FootnoteOrder { get; } = [];

    /// <summary>Simple key/value pairs from a leading YAML front-matter block (title, author, ...).</summary>
    public Dictionary<string, string> FrontMatter { get; } = new( StringComparer.OrdinalIgnoreCase );

    /// <summary>All headings in document order.</summary>
    public IEnumerable<Heading> Headings => Descendants().OfType<Heading>();

    public IEnumerable<Block> Descendants()
    {
        var stack = new Stack<IEnumerator<Block>>();
        stack.Push( Children.GetEnumerator() );
        while ( stack.Count > 0 )
        {
            var e = stack.Peek();
            if ( !e.MoveNext() ) { stack.Pop(); continue; }
            yield return e.Current;
            if ( e.Current is ContainerBlock c )
            {
                stack.Push( c.Children.GetEnumerator() );
            }
        }
    }
}

public sealed record LinkReference( string Url, string? Title );

public sealed class Paragraph : LeafBlock;

public sealed class Heading : LeafBlock
{
    public int Level { get; set; }

    /// <summary>Anchor id (GitHub-style slug), unique inside its document.</summary>
    public string Id { get; set; } = "";

    /// <summary>Plain-text version of the heading (for bookmarks / TOC).</summary>
    public string PlainText { get; set; } = "";
}

public sealed class CodeBlock : Block
{
    /// <summary>Info string of a fenced block (first word is the language).</summary>
    public string? Info { get; set; }
    public string Code { get; set; } = "";
    public bool Fenced { get; set; }
    public string? Language => string.IsNullOrWhiteSpace( Info ) ? null : Info.Trim().Split( ' ', '\t', '{' )[0].Trim().ToLowerInvariant();
}

public sealed class BlockQuote : ContainerBlock
{
    /// <summary>GitHub alert kind (note, tip, important, warning, caution) when the quote starts with [!KIND].</summary>
    public string? AlertKind { get; set; }
}

public sealed class ListBlock : ContainerBlock
{
    public bool Ordered { get; set; }
    public int Start { get; set; } = 1;
    public bool Tight { get; set; } = true;
    public char Delimiter { get; set; }
    public IEnumerable<ListItem> Items => Children.OfType<ListItem>();
}

public sealed class ListItem : ContainerBlock
{
    /// <summary>null = not a task item, otherwise checked state of "[ ]" / "[x]".</summary>
    public bool? Checked { get; set; }
}

public enum TableAlignment { None, Left, Center, Right }

public sealed class TableCell : LeafBlock;

public sealed class Table : Block
{
    public List<TableAlignment> Alignments { get; } = [];
    public List<TableCell> Header { get; } = [];
    public List<List<TableCell>> Rows { get; } = [];
}

public sealed class ThematicBreak : Block;

public sealed class PageBreak : Block;

public sealed class HtmlBlock : Block
{
    public string Html { get; set; } = "";
}

public sealed class FootnoteDefinition : ContainerBlock
{
    public string Label { get; set; } = "";
    public int Number { get; set; }
    /// <summary>Anchor id for the definition (target of references).</summary>
    public string Id => "fn-" + Label;
}

// ───────────────────────────── Inlines ─────────────────────────────

public abstract class Inline;

public sealed class TextInline( string text ) : Inline
{
    public string Text { get; set; } = text;
    public override string ToString() => Text;
}

public sealed class CodeInline( string code ) : Inline
{
    public string Code { get; } = code;
}

public abstract class ContainerInline : Inline
{
    public List<Inline> Children { get; set; } = [];
}

public sealed class EmphasisInline : ContainerInline;
public sealed class StrongInline : ContainerInline;
public sealed class StrikethroughInline : ContainerInline;

public sealed class LinkInline : ContainerInline
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public bool IsAutoLink { get; set; }
}

public sealed class ImageInline : Inline
{
    public string Url { get; set; } = "";
    public string Alt { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Optional explicit size in CSS pixels (from HTML img tags).</summary>
    public float? Width { get; set; }
    public float? Height { get; set; }
}

public sealed class LineBreakInline : Inline
{
    public bool Hard { get; init; }
}

public sealed class HtmlInline( string html ) : Inline
{
    public string Html { get; } = html;
}

public sealed class FootnoteReference : Inline
{
    public string Label { get; set; } = "";
    public FootnoteDefinition? Definition { get; set; }
}
