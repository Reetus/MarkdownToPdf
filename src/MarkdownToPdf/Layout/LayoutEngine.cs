using System.Text.RegularExpressions;
using MarkdownToPdf.Fonts;
using MarkdownToPdf.Images;
using MarkdownToPdf.Markdown;

namespace MarkdownToPdf.Layout;

/// <summary>A parsed input document with its identity for link resolution.</summary>
internal sealed class LoadedDocument
{
    public required MarkdownDocument Doc { get; init; }
    /// <summary>Unique key used as anchor prefix ("key#id").</summary>
    public required string Key { get; init; }
    public string? Path { get; init; }
    public required string BaseDirectory { get; init; }
    public string DisplayName => Path != null ? System.IO.Path.GetFileName( Path ) : Key;
}

internal sealed record OutlineEntry( int Level, string Title, string Anchor );

/// <summary>Turns Markdown documents into positioned draw operations on pages.</summary>
internal sealed partial class LayoutEngine
{
    private readonly MarkdownPdfOptions _o;
    private readonly ResolvedTheme _t;
    private readonly FontManager _fonts;
    private readonly TextLayouter _text;
    private readonly ImageStore _images;
    private readonly LinkResolver _links;
    private readonly Action<string> _warn;

    public List<PageContent> Pages { get; } = [];
    public Dictionary<string, Destination> Anchors { get; } = new( StringComparer.Ordinal );
    public List<OutlineEntry> Outline { get; } = [];
    public string Title { get; set; } = "";

    private readonly float _pageW, _pageH, _mTop, _mRight, _mBottom, _mLeft;
    private PageContent _page = null!;
    private float _y;
    private float _left, _right;
    private float _pending;
    private int _depth;
    private float _scale = 1f;
    private Color? _textColor;
    private bool _tight;
    private readonly List<Action<PageContent, float>> _markers = [];
    private string? _currentSection;
    private LoadedDocument _doc = null!;
    private int _spanCounter;

    public LayoutEngine( MarkdownPdfOptions options, FontManager fonts, ImageStore images, LinkResolver links, Action<string> warn )
    {
        _o = options;
        _fonts = fonts;
        _images = images;
        _links = links;
        _warn = warn;
        _t = ResolvedTheme.From( options.Theme, options.Code );
        _text = new TextLayouter( fonts );
        (_pageW, _pageH) = options.Page.ResolveSize();
        (_mTop, _mRight, _mBottom, _mLeft) = options.Page.ResolveMargins();
        if ( _mLeft + _mRight > _pageW - 72 || _mTop + _mBottom > _pageH - 72 )
        {
            throw new ArgumentException( "Margins are too large for the page size." );
        }

        _left = _mLeft;
        _right = _pageW - _mRight;
    }

    internal ResolvedTheme Theme => _t;
    internal float PageWidth => _pageW;
    internal float PageHeight => _pageH;
    internal (float Top, float Right, float Bottom, float Left) Margins => (_mTop, _mRight, _mBottom, _mLeft);

    // ───────────────────────────── sizes & styles ─────────────────────────────

    private float Base => _o.Fonts.Size * _scale;
    private float CodeSize => _o.Fonts.CodeSize * _scale;
    private float Width => _right - _left;
    private float ContentTop => _mTop;
    private float ContentBottom => _pageH - _mBottom;
    private float ContentHeight => ContentBottom - ContentTop;
    private float ParagraphGap => _tight ? Base * 0.25f : Base * _t.ParagraphSpacing;

    private TextStyle BodyStyle() => new()
    {
        Role = FontRole.Body,
        Size = Base,
        LineHeight = _o.Fonts.LineHeight,
        Color = _textColor ?? _t.Text,
    };

    // ───────────────────────────── pages & vertical flow ─────────────────────────────

    private void NewPage( bool suppressHeaderFooter = false )
    {
        _page = new PageContent( Pages.Count, _pageW, _pageH )
        {
            SuppressHeaderFooter = suppressHeaderFooter,
            SectionAtStart = _currentSection,
            SourceFile = _doc?.DisplayName,
        };
        Pages.Add( _page );
        _y = ContentTop;
        _pending = 0;
    }

    private bool AtPageTop => _y <= ContentTop + 0.5f;
    private bool PageIsEmpty => AtPageTop && _page.Foreground.Count == 0;

    private void Pending( float space ) => _pending = Math.Max( _pending, space );

    /// <summary>Applies pending space and starts a new page if <paramref name="height"/> does not fit.</summary>
    private void Prepare( float height )
    {
        var y = AtPageTop ? _y : _y + _pending;
        if ( y + height > ContentBottom + 0.01f && !AtPageTop )
        {
            NewPage();
            return;
        }
        _y = y;
        _pending = 0;
    }

    private void RegisterAnchor( string key, float y )
    {
        Anchors.TryAdd( key, new Destination( _page.Index, Math.Max( 0, y ) ) );
    }

    // ───────────────────────────── inline flattening ─────────────────────────────

    private sealed class HtmlState
    {
        public int Bold, Italic, Underline, Strike, Code, Sup, Sub, Kbd;
        public Stack<(string? Uri, string? Anchor)> Links = new();
    }

    [GeneratedRegex( @"^<\s*(/?)\s*([A-Za-z][A-Za-z0-9]*)([^>]*)>$", RegexOptions.Singleline )]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex( @"([A-Za-z_:][A-Za-z0-9_.:\-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))" )]
    private static partial Regex HtmlAttrRegex();

    internal static Dictionary<string, string> ParseAttributes( string attrs )
    {
        var d = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        foreach ( Match m in HtmlAttrRegex().Matches( attrs ) )
        {
            d[m.Groups[1].Value] = Entities.DecodeAndUnescape( m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value );
        }

        return d;
    }

    private List<FlowItem> Flatten( IEnumerable<Inline> inlines, TextStyle style )
    {
        var items = new List<FlowItem>();
        Flatten( inlines, style, items, new HtmlState() );
        return items;
    }

    private TextStyle ApplyHtml( TextStyle s, HtmlState h )
    {
        if ( h.Bold > 0 )
        {
            s = s with { Bold = true };
        }

        if ( h.Italic > 0 )
        {
            s = s with { Italic = true };
        }

        if ( h.Underline > 0 )
        {
            s = s with { Underline = true };
        }

        if ( h.Strike > 0 )
        {
            s = s with { Strike = true };
        }

        if ( h.Sup > 0 )
        {
            s = s with { Size = s.Size * 0.75f, Rise = s.Size * 0.35f };
        }

        if ( h.Sub > 0 )
        {
            s = s with { Size = s.Size * 0.75f, Rise = -s.Size * 0.15f };
        }

        if ( h.Links.Count > 0 && h.Links.Peek() is var (uri, anchor) && ( uri != null || anchor != null ) )
        {
            s = s with { LinkUri = uri, LinkAnchor = anchor, Color = _t.Link };
        }

        if ( h.Code > 0 || h.Kbd > 0 )
        {
            s = CodeStyle( s, h.Kbd > 0 );
        }

        return s;
    }

    private TextStyle CodeStyle( TextStyle s, bool kbd = false ) => s with
    {
        Role = FontRole.Mono,
        Bold = s.Bold && s.Role == FontRole.Heading ? true : s.Bold,
        Size = s.Size * ( _o.Fonts.CodeSize / _o.Fonts.Size ),
        Color = s.LinkUri != null || s.LinkAnchor != null ? _t.Link : ( s.Role == FontRole.Heading ? s.Color : _t.InlineCodeText ),
        Code = true,
        Kbd = kbd,
        SpanId = ++_spanCounter,
    };

    private void Flatten( IEnumerable<Inline> inlines, TextStyle s, List<FlowItem> items, HtmlState html )
    {
        foreach ( var inline in inlines )
        {
            switch ( inline )
            {
                case TextInline t:
                    items.Add( new TextItem( t.Text, ApplyHtml( s, html ) ) );
                    break;
                case CodeInline c:
                    {
                        var cs = CodeStyle( ApplyHtml( s, html ) );
                        var pad = cs.Size * 0.3f;
                        items.Add( new PadItem( pad, cs ) );
                        items.Add( new TextItem( c.Code, cs ) );
                        items.Add( new PadItem( pad, cs ) );
                        break;
                    }
                case EmphasisInline e:
                    Flatten( e.Children, s with { Italic = true }, items, html );
                    break;
                case StrongInline st:
                    Flatten( st.Children, s with { Bold = true }, items, html );
                    break;
                case StrikethroughInline del:
                    Flatten( del.Children, s with { Strike = true }, items, html );
                    break;
                case LinkInline link:
                    {
                        var (uri, anchor) = _links.Resolve( link.Url, _doc );
                        var ls = s with { LinkUri = uri, LinkAnchor = anchor, Color = _t.Link };
                        Flatten( link.Children, ls, items, html );
                        break;
                    }
                case ImageInline img:
                    AddInlineImage( img, ApplyHtml( s, html ), items );
                    break;
                case LineBreakInline br:
                    if ( br.Hard )
                    {
                        items.Add( new BreakItem() );
                    }
                    else
                    {
                        items.Add( new TextItem( " ", ApplyHtml( s, html ) ) );
                    }

                    break;
                case FootnoteReference fr when fr.Definition != null:
                    {
                        var fs = s with
                        {
                            Size = s.Size * 0.72f,
                            Rise = s.Size * 0.38f,
                            Color = _t.Link,
                            LinkAnchor = _doc.Key + "#" + fr.Definition.Id.ToLowerInvariant(),
                            LinkUri = null,
                        };
                        items.Add( new TextItem( fr.Definition.Number.ToString(), fs ) );
                        break;
                    }
                case HtmlInline h:
                    ApplyHtmlTag( h.Html, s, items, html );
                    break;
            }
        }
    }

    private void ApplyHtmlTag( string tag, TextStyle s, List<FlowItem> items, HtmlState h )
    {
        var m = HtmlTagRegex().Match( tag.Trim() );
        if ( !m.Success )
        {
            return; // comments, processing instructions
        }

        var close = m.Groups[1].Value == "/";
        var name = m.Groups[2].Value.ToLowerInvariant();
        var attrs = m.Groups[3].Value;
        var d = close ? -1 : 1;
        var selfClosing = attrs.TrimEnd().EndsWith( '/' );
        void Bump( ref int counter ) { if ( !selfClosing ) { counter = Math.Max( 0, counter + d ); } }

        switch ( name )
        {
            case "br": items.Add( new BreakItem() ); break;
            case "b" or "strong": Bump( ref h.Bold ); break;
            case "i" or "em" or "cite" or "var" or "dfn": Bump( ref h.Italic ); break;
            case "u" or "ins": Bump( ref h.Underline ); break;
            case "s" or "del" or "strike": Bump( ref h.Strike ); break;
            case "code" or "tt" or "samp": Bump( ref h.Code ); break;
            case "kbd": Bump( ref h.Kbd ); break;
            case "sup": Bump( ref h.Sup ); break;
            case "sub": Bump( ref h.Sub ); break;
            case "mark": Bump( ref h.Bold ); break;
            case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "tr" or "li" or "ul" or "ol" or "table" or "blockquote" or "pre" or "section" or "article" or "header" or "footer" or "details" or "summary" or "figure" or "figcaption" or "dl" or "dt" or "dd":
                if ( items.Count > 0 && items[^1] is not BreakItem )
                {
                    items.Add( new BreakItem() );
                }

                if ( name == "li" && !close )
                {
                    items.Add( new TextItem( "• ", ApplyHtml( s, h ) ) );
                }

                break;
            case "hr":
                items.Add( new BreakItem() );
                break;
            case "a":
                if ( close ) { if ( h.Links.Count > 0 ) { h.Links.Pop(); } break; }
                var a = ParseAttributes( attrs );
                if ( a.TryGetValue( "name", out var nm ) || a.TryGetValue( "id", out nm ) )
                {
                    items.Add( new AnchorItem( _doc.Key + "#" + nm.ToLowerInvariant() ) );
                }

                if ( a.TryGetValue( "href", out var href ) && !selfClosing )
                {
                    h.Links.Push( _links.Resolve( href, _doc ) );
                }
                else if ( !selfClosing )
                {
                    h.Links.Push( (null, null) );
                }

                break;
            case "img":
                {
                    if ( close )
                    {
                        break;
                    }

                    var a2 = ParseAttributes( attrs );
                    if ( !a2.TryGetValue( "src", out var src ) )
                    {
                        break;
                    }

                    var img = new ImageInline { Url = src, Alt = a2.GetValueOrDefault( "alt" ) ?? "", Title = a2.GetValueOrDefault( "title" ) };
                    if ( a2.TryGetValue( "width", out var ws ) && float.TryParse( ws.TrimEnd( 'p', 'x' ), System.Globalization.CultureInfo.InvariantCulture, out var wv ) )
                    {
                        img.Width = wv;
                    }

                    if ( a2.TryGetValue( "height", out var hs ) && float.TryParse( hs.TrimEnd( 'p', 'x' ), System.Globalization.CultureInfo.InvariantCulture, out var hv ) )
                    {
                        img.Height = hv;
                    }

                    AddInlineImage( img, ApplyHtml( s, h ), items );
                    break;
                }
            default:
                if ( !close )
                {
                    var a3 = ParseAttributes( attrs );
                    if ( a3.TryGetValue( "id", out var id ) )
                    {
                        items.Add( new AnchorItem( _doc.Key + "#" + id.ToLowerInvariant() ) );
                    }
                }
                break;
        }
    }

    private (float W, float H) ImageSize( PdfImage image, ImageInline img, float maxW, float maxH )
    {
        float w = image.NaturalWidth, h = image.NaturalHeight;
        if ( img.Width is > 0 && img.Height is > 0 ) { w = img.Width.Value * 0.75f; h = img.Height.Value * 0.75f; }
        else if ( img.Width is > 0 ) { h = h * ( img.Width.Value * 0.75f ) / w; w = img.Width.Value * 0.75f; }
        else if ( img.Height is > 0 ) { w = w * ( img.Height.Value * 0.75f ) / h; h = img.Height.Value * 0.75f; }
        var k = Math.Min( 1f, Math.Min( maxW / w, maxH / h ) );
        return (w * k, h * k);
    }

    private void AddInlineImage( ImageInline img, TextStyle s, List<FlowItem> items )
    {
        var image = _images.Load( img.Url, _doc.BaseDirectory );
        if ( image == null )
        {
            var alt = string.IsNullOrWhiteSpace( img.Alt ) ? "image" : img.Alt;
            items.Add( new TextItem( "[" + alt + "]", s with { Italic = true, Color = _t.Muted } ) );
            return;
        }
        var (w, h) = ImageSize( image, img, Width, ContentHeight * 0.6f );
        items.Add( new ImageItem( image, w, h, s ) );
    }

    // ───────────────────────────── line emission ─────────────────────────────

    private void EmitLine( LineBox line, float x, float top, float width, TextAlign align, bool lastLine )
    {
        var off = TextLayouter.Align( line, width, align, lastLine );
        var baseline = top + line.Above;

        if ( _markers.Count > 0 )
        {
            foreach ( var m in _markers )
            {
                m( _page, baseline );
            }

            _markers.Clear();
        }

        var pieces = line.Pieces;
        // Merge adjacent pieces (including spaces) that share face, size and colour into one text run, so the
        // PDF contains real space characters (better copy / paste and search). Justified spaces break runs.
        System.Text.StringBuilder? run = null;
        Piece? runStart = null;
        float runEnd = 0;
        void FlushRun()
        {
            if ( run != null && runStart != null && run.Length > 0 )
            {
                // whitespace-only runs are kept (as a single space) so text extraction sees word boundaries
                var text = run.ToString();
                text = text.Trim().Length == 0 ? " " : text.TrimEnd();
                _page.Foreground.Add( new TextOp( runStart.Face!, runStart.Size, x + off + runStart.X, baseline - runStart.Style.Rise, text, runStart.Style.Color ) );
            }
            run = null;
            runStart = null;
        }
        for ( var i = 0; i < pieces.Count; i++ )
        {
            var p = pieces[i];
            if ( p.Anchors != null )
            {
                foreach ( var a in p.Anchors )
                {
                    RegisterAnchor( a, top - 4 );
                }
            }

            if ( p.Image != null )
            {
                FlushRun();
                _page.Foreground.Add( new ImageOp( p.Image, x + off + p.X, baseline - p.ImageHeight, p.Width, p.ImageHeight ) );
                continue;
            }
            if ( p.IsPad || p.Face == null || p.Text.Length == 0 ) { FlushRun(); continue; }
            var natural = !p.IsSpace || Math.Abs( p.Width - p.Face.Face.Measure( p.Text, p.Size ) ) < 0.01f;
            var compatible = runStart != null && natural && runStart.Face == p.Face && runStart.Size == p.Size
                              && runStart.Style.Color == p.Style.Color && runStart.Style.Rise == p.Style.Rise && Math.Abs( runEnd - p.X ) < 0.01f;
            if ( !compatible )
            {
                FlushRun();
                if ( p.IsSpace && !natural )
                {
                    continue; // stretched (justified) space: leave a gap
                }

                run = new System.Text.StringBuilder();
                runStart = p;
            }
            run!.Append( p.Text );
            runEnd = p.X + p.Width;
        }
        FlushRun();

        // inline code backgrounds
        for ( var i = 0; i < pieces.Count; )
        {
            var p = pieces[i];
            if ( p.Style.SpanId == 0 || !p.Style.Code ) { i++; continue; }
            var j = i;
            while ( j + 1 < pieces.Count && pieces[j + 1].Style.SpanId == p.Style.SpanId )
            {
                j++;
            }

            float x0 = x + off + p.X, x1 = x + off + pieces[j].X + pieces[j].Width;
            var size = p.Style.Size;
            float rTop = baseline - p.Style.Rise - size * 0.9f, rH = size * 1.22f;
            if ( p.Style.Kbd )
            {
                _page.AddBackground( new RectOp( x0, rTop, x1 - x0, rH, new Color( 0.98f, 0.98f, 0.985f ), _t.Border.Mix( Color.Black, 0.15f ), 0.6f, 2.5f ), _depth + 1 );
            }
            else
            {
                _page.AddBackground( new RectOp( x0, rTop, x1 - x0, rH, _t.InlineCodeBackground, null, 0, 2.2f ), _depth + 1 );
            }

            i = j + 1;
        }

        // underline & strikethrough
        for ( var i = 0; i < pieces.Count; i++ )
        {
            var p = pieces[i];
            if ( !p.Style.Underline && !p.Style.Strike )
            {
                continue;
            }

            if ( p.IsSpace && ( i == pieces.Count - 1 || i == 0 ) )
            {
                continue;
            }

            var px = x + off + p.X;
            var th = Math.Max( 0.5f, p.Size * 0.055f );
            if ( p.Style.Underline )
            {
                _page.Foreground.Add( new LineOp( px, baseline + p.Size * 0.14f, px + p.Width, baseline + p.Size * 0.14f, p.Style.Color, th ) );
            }

            if ( p.Style.Strike )
            {
                _page.Foreground.Add( new LineOp( px, baseline - p.Size * 0.3f, px + p.Width, baseline - p.Size * 0.3f, p.Style.Color, th ) );
            }
        }

        // links
        for ( var i = 0; i < pieces.Count; )
        {
            var p = pieces[i];
            if ( p.Style.LinkUri == null && p.Style.LinkAnchor == null ) { i++; continue; }
            var j = i;
            while ( j + 1 < pieces.Count && pieces[j + 1].Style.LinkUri == p.Style.LinkUri && pieces[j + 1].Style.LinkAnchor == p.Style.LinkAnchor )
            {
                j++;
            }

            float x0 = x + off + p.X, x1 = x + off + pieces[j].X + pieces[j].Width;
            _page.Links.Add( new LinkArea( x0, top, x1 - x0, line.Height, p.Style.LinkUri, p.Style.LinkAnchor ) );
            i = j + 1;
        }
    }

    /// <summary>Flows lines onto pages with orphan / widow control.</summary>
    private void FlowLines( List<LineBox> lines, TextAlign align, float keepLead = 0, float x = float.NaN, float width = float.NaN )
    {
        if ( float.IsNaN( x ) )
        {
            x = _left;
        }

        if ( float.IsNaN( width ) )
        {
            width = _right - _left;
        }

        var n = lines.Count;
        if ( n == 0 )
        {
            return;
        }

        var need = lines.Take( Math.Min( 2, n ) ).Sum( l => l.Height );
        if ( n <= 2 )
        {
            need += keepLead;
        }

        Prepare( Math.Min( need, ContentHeight ) );
        for ( var i = 0; i < n; i++ )
        {
            var l = lines[i];
            var overflow = _y + l.Height > ContentBottom + 0.01f;
            var widow = !overflow && n >= 4 && i == n - 2 && i >= 2 && _y + l.Height + lines[n - 1].Height > ContentBottom + 0.01f;
            if ( ( overflow || widow ) && !AtPageTop )
            {
                NewPage();
            }

            EmitLine( l, x, _y, width, align, i == n - 1 );
            _y += l.Height;
        }
    }
}
