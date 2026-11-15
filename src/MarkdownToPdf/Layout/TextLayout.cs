using System.Text;
using MarkdownToPdf.Fonts;
using MarkdownToPdf.Images;

namespace MarkdownToPdf.Layout;

/// <summary>Visual style of a run of inline text.</summary>
internal sealed record TextStyle
{
    public FontRole Role { get; init; } = FontRole.Body;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public float Size { get; init; } = 10.5f;
    public float LineHeight { get; init; } = 1.5f;
    public Color Color { get; init; }
    public bool Code { get; init; }
    public bool Kbd { get; init; }
    public bool Strike { get; init; }
    public bool Underline { get; init; }
    /// <summary>Baseline shift (positive = up) for superscripts.</summary>
    public float Rise { get; init; }
    public string? LinkUri { get; init; }
    public string? LinkAnchor { get; init; }
    /// <summary>Non-zero for pieces belonging to the same inline code span (used to merge backgrounds).</summary>
    public int SpanId { get; init; }
    public bool NoWrap { get; init; }
}

internal abstract record FlowItem;
internal sealed record TextItem( string Text, TextStyle Style ) : FlowItem;
internal sealed record BreakItem : FlowItem;
internal sealed record PadItem( float Width, TextStyle Style ) : FlowItem;
internal sealed record ImageItem( PdfImage Image, float Width, float Height, TextStyle Style ) : FlowItem;
internal sealed record AnchorItem( string Key ) : FlowItem;

internal sealed class Piece
{
    public FaceRef? Face;
    public float Size;
    public string Text = "";
    public float Width;
    public required TextStyle Style;
    public bool IsSpace;
    public bool IsPad;
    public PdfImage? Image;
    public float ImageHeight;
    public List<string>? Anchors;
    public float X; // set when placed on a line
}

internal sealed class LineBox
{
    public List<Piece> Pieces { get; } = [];
    public float Width;
    public float Above;
    public float Below;
    public bool HardBreak;
    public float Height => Above + Below;
}

/// <summary>Breaks flow items into lines and emits them as draw operations.</summary>
internal sealed class TextLayouter( FontManager fonts )
{
    private readonly FontManager _fonts = fonts;

    private enum AtomKind { Word, Space, Break }

    private sealed class Atom
    {
        public AtomKind Kind;
        public List<Piece> Pieces = [];
        public float Width;
    }

    public FaceRef FaceFor( TextStyle s ) => _fonts.Get( s.Role, s.Bold, s.Italic );

    private static bool IsCjk( int cp ) =>
        cp is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x2FFFF;

    private static bool IsInvisible( int cp ) => cp is 0x00AD or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF;

    public List<LineBox> Layout( IReadOnlyList<FlowItem> items, float maxWidth )
    {
        var atoms = BuildAtoms( items );
        return Fill( atoms, Math.Max( maxWidth, 10f ) );
    }

    /// <summary>Width of the widest unbreakable word (minimum content width).</summary>
    public float MinContentWidth( IReadOnlyList<FlowItem> items )
    {
        float max = 0;
        foreach ( var a in BuildAtoms( items ) )
        {
            if ( a.Kind == AtomKind.Word )
            {
                max = Math.Max( max, a.Width );
            }
        }

        return max;
    }

    private List<Atom> BuildAtoms( IReadOnlyList<FlowItem> items )
    {
        var atoms = new List<Atom>();
        Atom? word = null;
        List<string>? pendingAnchors = null;

        void CloseWord()
        {
            if ( word != null && word.Pieces.Count > 0 )
            {
                atoms.Add( word );
            }

            word = null;
        }

        void AddPiece( Piece p )
        {
            if ( pendingAnchors != null ) { p.Anchors = pendingAnchors; pendingAnchors = null; }
            word ??= new Atom { Kind = AtomKind.Word };
            word.Pieces.Add( p );
            word.Width += p.Width;
        }

        void AddText( string text, TextStyle style )
        {
            if ( text.Length == 0 )
            {
                return;
            }

            var primary = FaceFor( style );
            var secondary = style.Role == FontRole.Mono ? _fonts.Get( FontRole.Body, style.Bold, style.Italic ) : null;
            foreach ( var (face, run) in _fonts.Split( text, primary, secondary ) )
            {
                AddPiece( new Piece
                {
                    Face = face,
                    Size = style.Size,
                    Text = run,
                    Style = style,
                    Width = face.Face.Measure( run, style.Size ),
                } );
            }
        }

        foreach ( var item in items )
        {
            switch ( item )
            {
                case TextItem t:
                    {
                        var sb = new StringBuilder();
                        foreach ( var rune in t.Text.EnumerateRunes() )
                        {
                            var cp = rune.Value;
                            if ( IsInvisible( cp ) )
                            {
                                continue;
                            }

                            if ( cp == '\n' || cp == '\r' || cp == '\t' )
                            {
                                cp = ' ';
                            }

                            if ( cp == ' ' && !t.Style.NoWrap )
                            {
                                AddText( sb.ToString(), t.Style );
                                sb.Clear();
                                CloseWord();
                                var collapse = !t.Style.Code && atoms.Count > 0 && atoms[^1].Kind == AtomKind.Space;
                                if ( !collapse )
                                {
                                    var face = FaceFor( t.Style );
                                    var sp = new Piece { Face = face, Size = t.Style.Size, Text = " ", Style = t.Style, IsSpace = true, Width = face.Face.Measure( " ", t.Style.Size ) };
                                    if ( pendingAnchors != null ) { sp.Anchors = pendingAnchors; pendingAnchors = null; }
                                    atoms.Add( new Atom { Kind = AtomKind.Space, Pieces = [sp], Width = sp.Width } );
                                }
                                continue;
                            }
                            if ( IsCjk( cp ) )
                            {
                                AddText( sb.ToString(), t.Style );
                                sb.Clear();
                                CloseWord();
                                AddText( rune.ToString(), t.Style );
                                CloseWord();
                                continue;
                            }
                            sb.Append( rune.ToString() );
                            // soft break opportunity after hyphen / slash (not at the start of a word)
                            if ( ( cp == '-' || cp == '/' || cp == '\\' ) && sb.Length > 1 && !t.Style.NoWrap )
                            {
                                AddText( sb.ToString(), t.Style );
                                sb.Clear();
                                CloseWord();
                            }
                        }
                        AddText( sb.ToString(), t.Style );
                        break;
                    }
                case PadItem p:
                    AddPiece( new Piece { Width = p.Width, Style = p.Style, IsPad = true, Size = p.Style.Size } );
                    break;
                case ImageItem img:
                    CloseWord();
                    AddPiece( new Piece { Image = img.Image, Width = img.Width, ImageHeight = img.Height, Style = img.Style, Size = img.Style.Size } );
                    CloseWord();
                    break;
                case BreakItem:
                    CloseWord();
                    atoms.Add( new Atom { Kind = AtomKind.Break } );
                    break;
                case AnchorItem a:
                    ( pendingAnchors ??= [] ).Add( a.Key );
                    break;
            }
        }
        CloseWord();
        if ( pendingAnchors != null )
        {
            atoms.Add( new Atom { Kind = AtomKind.Word, Pieces = [new Piece { Style = new TextStyle(), IsPad = true, Anchors = pendingAnchors }] } );
        }

        // Words separated only by a soft opportunity are separate atoms; adjacent word atoms with no space
        // between them may break there. Spaces are explicit atoms.
        return atoms;
    }

    private List<LineBox> Fill( List<Atom> atoms, float maxWidth )
    {
        var lines = new List<LineBox>();
        var line = new LineBox();
        Atom? pendingSpace = null;

        void Finish( bool hard )
        {
            line.HardBreak = hard;
            lines.Add( line );
            line = new LineBox();
            pendingSpace = null;
        }

        void Place( Atom a )
        {
            foreach ( var p in a.Pieces )
            {
                p.X = line.Width;
                line.Pieces.Add( p );
                line.Width += p.Width;
            }
        }

        foreach ( var atom in atoms )
        {
            switch ( atom.Kind )
            {
                case AtomKind.Break:
                    Finish( true );
                    break;
                case AtomKind.Space:
                    if ( line.Pieces.Count == 0 && !atom.Pieces[0].Style.Code )
                    {
                        break;
                    }

                    if ( pendingSpace != null && atom.Pieces[0].Style.Code )
                    {
                        // preserve multiple spaces inside code spans
                        Place( pendingSpace );
                    }
                    pendingSpace = atom;
                    break;
                case AtomKind.Word:
                    {
                        var spaceW = pendingSpace?.Width ?? 0;
                        if ( line.Pieces.Count == 0 || line.Width + spaceW + atom.Width <= maxWidth + 0.01f )
                        {
                            if ( pendingSpace != null && line.Pieces.Count > 0 )
                            {
                                Place( pendingSpace );
                            }

                            pendingSpace = null;
                            if ( line.Width + atom.Width > maxWidth + 0.01f && atom.Width > maxWidth )
                            {
                                PlaceSplitting( atom, maxWidth, ref line, lines );
                            }
                            else
                            {
                                Place( atom );
                            }
                        }
                        else
                        {
                            Finish( false );
                            if ( atom.Width > maxWidth )
                            {
                                PlaceSplitting( atom, maxWidth, ref line, lines );
                            }
                            else
                            {
                                Place( atom );
                            }
                        }
                        break;
                    }
            }
        }
        if ( line.Pieces.Count > 0 || lines.Count == 0 )
        {
            lines.Add( line );
        }
        else if ( lines.Count > 0 && lines[^1].HardBreak && line.Pieces.Count == 0 ) { /* trailing hard break: ignore */ }

        foreach ( var l in lines )
        {
            ComputeMetrics( l );
        }

        return lines;
    }

    /// <summary>Places a word that is wider than a line, splitting it at character boundaries.</summary>
    private void PlaceSplitting( Atom atom, float maxWidth, ref LineBox line, List<LineBox> lines )
    {
        foreach ( var p in atom.Pieces )
        {
            if ( p.Face == null || p.IsPad || p.Image != null )
            {
                if ( line.Width + p.Width > maxWidth && line.Pieces.Count > 0 ) { lines.Add( line ); line = new LineBox(); }
                p.X = line.Width; line.Pieces.Add( p ); line.Width += p.Width;
                continue;
            }
            var sb = new StringBuilder();
            float w = 0;
            foreach ( var rune in p.Text.EnumerateRunes() )
            {
                var cw = p.Face.Face.Advance( rune.Value ) * p.Size / 1000f;
                if ( line.Width + w + cw > maxWidth && ( line.Pieces.Count > 0 || sb.Length > 0 ) )
                {
                    if ( sb.Length > 0 )
                    {
                        var part = new Piece { Face = p.Face, Size = p.Size, Text = sb.ToString(), Width = w, Style = p.Style, Anchors = p.Anchors, X = line.Width };
                        p.Anchors = null;
                        line.Pieces.Add( part );
                        line.Width += w;
                    }
                    lines.Add( line );
                    line = new LineBox();
                    sb.Clear();
                    w = 0;
                }
                sb.Append( rune.ToString() );
                w += cw;
            }
            if ( sb.Length > 0 )
            {
                var part = new Piece { Face = p.Face, Size = p.Size, Text = sb.ToString(), Width = w, Style = p.Style, Anchors = p.Anchors, X = line.Width };
                line.Pieces.Add( part );
                line.Width += w;
            }
        }
    }

    private void ComputeMetrics( LineBox line )
    {
        float above = 0, below = 0;
        foreach ( var p in line.Pieces )
        {
            if ( p.Image != null )
            {
                above = Math.Max( above, p.ImageHeight + 1 );
                below = Math.Max( below, 2 );
                continue;
            }
            var (a, b) = TextExtent( p.Style );
            above = Math.Max( above, a + p.Style.Rise );
            below = Math.Max( below, b );
        }
        if ( line.Pieces.Count == 0 )
        {
            // empty line (e.g. consecutive hard breaks): use default body metrics
            var (a, b) = TextExtent( new TextStyle() );
            above = a; below = b;
        }
        line.Above = above;
        line.Below = below;
    }

    /// <summary>Distance above / below the baseline occupied by a line of text in this style (includes leading).</summary>
    public (float Above, float Below) TextExtent( TextStyle s )
    {
        var face = _fonts.Get( s.Role, false, false ).Face;
        float asc = face.Ascent / 1000f, desc = -face.Descent / 1000f;
        var content = ( asc + desc ) * s.Size;
        var lineBox = Math.Max( s.Size * s.LineHeight, content );
        var half = ( lineBox - content ) / 2f;
        return (half + asc * s.Size, half + desc * s.Size);
    }

    /// <summary>Applies horizontal alignment / justification by setting piece X positions.</summary>
    public static float Align( LineBox line, float width, TextAlign align, bool lastLine )
    {
        // trim trailing spaces
        while ( line.Pieces.Count > 0 && line.Pieces[^1].IsSpace && !line.Pieces[^1].Style.Code )
        {
            line.Width -= line.Pieces[^1].Width;
            line.Pieces.RemoveAt( line.Pieces.Count - 1 );
        }
        var offset = align switch
        {
            TextAlign.Center => Math.Max( 0, ( width - line.Width ) / 2f ),
            TextAlign.Right => Math.Max( 0, width - line.Width ),
            _ => 0,
        };
        if ( align == TextAlign.Justify && !lastLine && !line.HardBreak )
        {
            var spaces = line.Pieces.Count( p => p.IsSpace && !p.Style.Code );
            var extra = width - line.Width;
            if ( spaces > 0 && extra > 0 && extra < width * 0.25f )
            {
                var per = extra / spaces;
                float shift = 0;
                foreach ( var p in line.Pieces )
                {
                    p.X += shift;
                    if ( p.IsSpace && !p.Style.Code ) { p.Width += per; shift += per; }
                }
                line.Width = width;
            }
        }
        return offset;
    }
}
