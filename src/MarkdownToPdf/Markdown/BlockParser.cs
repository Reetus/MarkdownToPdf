using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownToPdf.Markdown;

/// <summary>Entry point for parsing Markdown (CommonMark + GitHub Flavored Markdown extensions).</summary>
public static class MarkdownParser
{
    public static MarkdownDocument Parse( string markdown, string? sourcePath = null )
    {
        var doc = new MarkdownDocument { SourcePath = sourcePath };
        var lines = SplitLines( markdown );
        var start = ParseFrontMatter( lines, doc );
        var parser = new BlockParser( doc );
        parser.ParseBlocks( lines, start, lines.Count, doc.Children );
        InlinePass.Run( doc );
        return doc;
    }

    internal readonly record struct SourceLine( string Text, int Number );

    private static List<SourceLine> SplitLines( string text )
    {
        if ( text.Length > 0 && text[0] == '﻿' )
        {
            text = text[1..];
        }

        text = text.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Replace( '\0', '�' );
        var raw = text.Split( '\n' );
        var result = new List<SourceLine>( raw.Length );
        for ( var i = 0; i < raw.Length; i++ )
        {
            if ( i == raw.Length - 1 && raw[i].Length == 0 )
            {
                break; // trailing newline
            }

            result.Add( new SourceLine( ExpandTabs( raw[i] ), i + 1 ) );
        }
        return result;
    }

    internal static string ExpandTabs( string s, int tabSize = 4 )
    {
        if ( !s.Contains( '\t' ) )
        {
            return s;
        }

        var sb = new StringBuilder( s.Length + 8 );
        foreach ( var c in s )
        {
            if ( c == '\t' )
            {
                sb.Append( ' ', tabSize - sb.Length % tabSize );
            }
            else
            {
                sb.Append( c );
            }
        }
        return sb.ToString();
    }

    private static int ParseFrontMatter( List<SourceLine> lines, MarkdownDocument doc )
    {
        if ( lines.Count < 2 || lines[0].Text.TrimEnd() != "---" )
        {
            return 0;
        }

        var end = -1;
        for ( var i = 1; i < lines.Count; i++ )
        {
            var t = lines[i].Text.TrimEnd();
            if ( t is "---" or "..." ) { end = i; break; }
        }
        if ( end < 0 )
        {
            return 0;
        }

        var pairs = new List<(string, string)>();
        for ( var i = 1; i < end; i++ )
        {
            var m = Regex.Match( lines[i].Text, @"^([A-Za-z0-9_\-]+)\s*:\s*(.*)$" );
            if ( m.Success )
            {
                pairs.Add( (m.Groups[1].Value, m.Groups[2].Value.Trim().Trim( '"', '\'' )) );
            }
            else if ( !string.IsNullOrWhiteSpace( lines[i].Text ) && !lines[i].Text.StartsWith( ' ' ) && !lines[i].Text.TrimStart().StartsWith( '-' ) )
            {
                return 0;
            }
        }
        if ( pairs.Count == 0 )
        {
            return 0;
        }

        foreach ( var (k, v) in pairs )
        {
            doc.FrontMatter[k] = v;
        }

        return end + 1;
    }
}

internal sealed partial class BlockParser( MarkdownDocument doc )
{
    private readonly MarkdownDocument _doc = doc;

    private static readonly HashSet<string> BlockTags = new( StringComparer.OrdinalIgnoreCase )
    {
        "address","article","aside","base","basefont","blockquote","body","caption","center","col","colgroup","dd","details","dialog",
        "dir","div","dl","dt","fieldset","figcaption","figure","footer","form","frame","frameset","h1","h2","h3","h4","h5","h6","head",
        "header","hr","html","iframe","legend","li","link","main","menu","menuitem","nav","noframes","ol","optgroup","option","p","param",
        "search","section","summary","table","tbody","td","tfoot","th","thead","title","tr","track","ul"
    };

    private static readonly string[] RawTags = ["pre", "script", "style", "textarea"];

    [GeneratedRegex( @"^\[([^\]]+)\]:[ ]*(<[^>]*>|\S+)(?:[ ]+(?:""([^""]*)""|'([^']*)'|\(([^)]*)\)))?[ ]*$" )]
    private static partial Regex LinkRefDefRegex();

    [GeneratedRegex( @"^\[\^([^\]\s]+)\]:[ ]?(.*)$" )]
    private static partial Regex FootnoteDefRegex();

    [GeneratedRegex( @"^</?([A-Za-z][A-Za-z0-9\-]*)(?:\s[^<>]*)?/?>\s*$" )]
    private static partial Regex CompleteTagLineRegex();

    // ───────────────────────── helpers ─────────────────────────

    private static int Indent( string s )
    {
        var i = 0;
        while ( i < s.Length && s[i] == ' ' )
        {
            i++;
        }

        return i;
    }

    private static bool IsBlank( string s )
    {
        foreach ( var c in s )
        {
            if ( c != ' ' && c != '\t' )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsThematicBreak( string line )
    {
        if ( Indent( line ) > 3 )
        {
            return false;
        }

        var m = '\0';
        var count = 0;
        foreach ( var c in line )
        {
            if ( c == ' ' || c == '\t' )
            {
                continue;
            }

            if ( c != '-' && c != '*' && c != '_' )
            {
                return false;
            }

            if ( m == '\0' )
            {
                m = c;
            }
            else if ( c != m )
            {
                return false;
            }

            count++;
        }
        return count >= 3;
    }

    private static bool TryAtxHeading( string line, out int level, out string content )
    {
        level = 0; content = "";
        var ind = Indent( line );
        if ( ind > 3 )
        {
            return false;
        }

        var i = ind;
        while ( i < line.Length && line[i] == '#' )
        {
            i++;
        }

        level = i - ind;
        if ( level is < 1 or > 6 )
        {
            return false;
        }

        if ( i < line.Length && line[i] != ' ' )
        {
            return false;
        }

        var text = line[i..].Trim();
        // optional closing sequence
        var j = text.Length;
        while ( j > 0 && text[j - 1] == '#' )
        {
            j--;
        }

        if ( j == 0 )
        {
            text = "";
        }
        else if ( j < text.Length && text[j - 1] == ' ' )
        {
            text = text[..j].TrimEnd();
        }

        content = text;
        return true;
    }

    private static bool TryFenceOpen( string line, out char ch, out int len, out int indent, out string info )
    {
        ch = '\0'; len = 0; info = "";
        indent = Indent( line );
        if ( indent > 3 || indent >= line.Length )
        {
            return false;
        }

        var c = line[indent];
        if ( c != '`' && c != '~' )
        {
            return false;
        }

        var i = indent;
        while ( i < line.Length && line[i] == c )
        {
            i++;
        }

        len = i - indent;
        if ( len < 3 )
        {
            return false;
        }

        info = line[i..].Trim();
        if ( c == '`' && info.Contains( '`' ) )
        {
            return false;
        }

        ch = c;
        return true;
    }

    private static bool IsFenceClose( string line, char ch, int len )
    {
        var ind = Indent( line );
        if ( ind > 3 )
        {
            return false;
        }

        var i = ind;
        while ( i < line.Length && line[i] == ch )
        {
            i++;
        }

        return i - ind >= len && IsBlank( line[i..] );
    }

    private readonly record struct ListMarker( bool Ordered, char Delimiter, int Number, int ContentOffset, bool EmptyContent );

    private static bool TryListMarker( string line, out ListMarker marker )
    {
        marker = default;
        var ind = Indent( line );
        if ( ind > 3 || ind >= line.Length )
        {
            return false;
        }

        var i = ind;
        var ordered = false;
        char delim;
        var number = 1;
        var c = line[i];
        if ( c is '-' or '+' or '*' )
        {
            delim = c;
            i++;
        }
        else if ( char.IsAsciiDigit( c ) )
        {
            var s = i;
            while ( i < line.Length && char.IsAsciiDigit( line[i] ) && i - s < 9 )
            {
                i++;
            }

            if ( i >= line.Length || ( line[i] != '.' && line[i] != ')' ) )
            {
                return false;
            }

            number = int.Parse( line.AsSpan( s, i - s ) );
            delim = line[i];
            ordered = true;
            i++;
        }
        else
        {
            return false;
        }

        if ( i < line.Length && line[i] != ' ' )
        {
            return false;
        }

        var markerEnd = i;
        var spaces = 0;
        while ( i < line.Length && line[i] == ' ' ) { i++; spaces++; }
        var empty = i >= line.Length;
        var contentOffset = empty || spaces > 4 ? markerEnd + 1 : markerEnd + spaces;
        marker = new ListMarker( ordered, delim, number, contentOffset, empty );
        return true;
    }

    private static bool IsHtmlBlockStart( string line, bool canInterruptParagraph )
    {
        var ind = Indent( line );
        if ( ind > 3 )
        {
            return false;
        }

        var s = line[ind..];
        if ( !s.StartsWith( '<' ) )
        {
            return false;
        }

        if ( s.StartsWith( "<!--" ) || s.StartsWith( "<?" ) || s.StartsWith( "<![CDATA[" ) )
        {
            return true;
        }

        if ( s.Length > 2 && s[1] == '!' && char.IsAsciiLetter( s[2] ) )
        {
            return true;
        }

        var m = Regex.Match( s, @"^</?([A-Za-z][A-Za-z0-9\-]*)(\s|/?>|$)" );
        if ( m.Success )
        {
            var tag = m.Groups[1].Value;
            if ( RawTags.Contains( tag, StringComparer.OrdinalIgnoreCase ) && !s.StartsWith( "</" ) )
            {
                return true;
            }

            if ( BlockTags.Contains( tag ) )
            {
                return true;
            }
        }
        return !canInterruptParagraph && CompleteTagLineRegex().IsMatch( s );
    }

    private static bool IsTableDelimiterRow( string line, out List<TableAlignment> aligns )
    {
        aligns = [];
        var t = line.Trim();
        if ( t.Length == 0 || !t.Contains( '-' ) )
        {
            return false;
        }

        if ( !t.Contains( '|' ) && !t.Contains( ':' ) ) { /* single column tables need a pipe */ return false; }
        foreach ( var cell in SplitRow( t ) )
        {
            var c = cell.Trim();
            if ( c.Length == 0 )
            {
                return false;
            }

            bool left = c.StartsWith( ':' ), right = c.EndsWith( ':' );
            var core = c.Trim( ':' );
            if ( core.Length == 0 || core.Any( ch => ch != '-' ) )
            {
                return false;
            }

            aligns.Add( left && right ? TableAlignment.Center : right ? TableAlignment.Right : left ? TableAlignment.Left : TableAlignment.None );
        }
        return aligns.Count > 0;
    }

    internal static List<string> SplitRow( string row )
    {
        var t = row.Trim();
        if ( t.StartsWith( '|' ) )
        {
            t = t[1..];
        }

        if ( t.EndsWith( '|' ) && !t.EndsWith( "\\|" ) )
        {
            t = t[..^1];
        }

        var cells = new List<string>();
        var sb = new StringBuilder();
        var codeRun = 0; // inside backtick code span?
        for ( var i = 0; i < t.Length; i++ )
        {
            var c = t[i];
            if ( c == '\\' && i + 1 < t.Length && t[i + 1] == '|' ) { sb.Append( '|' ); i++; continue; }
            if ( c == '`' )
            {
                var s = i;
                while ( i + 1 < t.Length && t[i + 1] == '`' )
                {
                    i++;
                }

                var run = i - s + 1;
                if ( codeRun == 0 )
                {
                    codeRun = run;
                }
                else if ( codeRun == run )
                {
                    codeRun = 0;
                }

                sb.Append( '`', run );
                continue;
            }
            if ( c == '|' && codeRun == 0 ) { cells.Add( sb.ToString() ); sb.Clear(); continue; }
            sb.Append( c );
        }
        cells.Add( sb.ToString() );
        return cells;
    }

    private static bool IsPageBreakMarker( string line )
    {
        var t = line.Trim();
        if ( t is @"\newpage" or @"\pagebreak" or @"\page" )
        {
            return true;
        }

        return Regex.IsMatch( t, @"^<!--\s*(page-?break|new-?page)\s*-->$", RegexOptions.IgnoreCase )
            || Regex.IsMatch( t, @"^<div[^>]*page-break-(before|after)\s*:\s*always[^>]*>\s*(</div>)?$", RegexOptions.IgnoreCase );
    }

    private bool InterruptsParagraph( string line )
    {
        if ( Indent( line ) > 3 )
        {
            return false;
        }

        if ( TryFenceOpen( line, out _, out _, out _, out _ ) )
        {
            return true;
        }

        if ( TryAtxHeading( line, out _, out _ ) )
        {
            return true;
        }

        if ( IsThematicBreak( line ) )
        {
            return true;
        }

        var t = line.TrimStart();
        if ( t.StartsWith( '>' ) )
        {
            return true;
        }

        if ( TryListMarker( line, out var m ) && !m.EmptyContent && ( !m.Ordered || m.Number == 1 ) )
        {
            return true;
        }

        if ( IsHtmlBlockStart( line, canInterruptParagraph: true ) )
        {
            return true;
        }

        if ( IsPageBreakMarker( line ) )
        {
            return true;
        }

        return false;
    }

    // ───────────────────────── main loop ─────────────────────────

    public void ParseBlocks( List<MarkdownParser.SourceLine> lines, int from, int to, List<Block> output )
    {
        var i = from;
        while ( i < to )
        {
            var line = lines[i].Text;
            var lineNo = lines[i].Number;
            if ( IsBlank( line ) ) { i++; continue; }
            var ind = Indent( line );

            // Indented code block
            if ( ind >= 4 )
            {
                var sb = new List<string>();
                var lastNonBlank = -1;
                while ( i < to && ( IsBlank( lines[i].Text ) || Indent( lines[i].Text ) >= 4 ) )
                {
                    var l = lines[i].Text;
                    sb.Add( l.Length >= 4 ? l[4..] : "" );
                    if ( !IsBlank( l ) )
                    {
                        lastNonBlank = sb.Count - 1;
                    }

                    i++;
                }
                output.Add( new CodeBlock { Code = string.Join( '\n', sb.Take( lastNonBlank + 1 ) ), Line = lineNo } );
                continue;
            }

            if ( IsPageBreakMarker( line ) ) { output.Add( new PageBreak { Line = lineNo } ); i++; continue; }

            // Fenced code
            if ( TryFenceOpen( line, out var fch, out var flen, out var find, out var info ) )
            {
                i++;
                var code = new List<string>();
                while ( i < to && !IsFenceClose( lines[i].Text, fch, flen ) )
                {
                    var l = lines[i].Text;
                    var strip = Math.Min( find, Indent( l ) );
                    code.Add( l[strip..] );
                    i++;
                }
                if ( i < to )
                {
                    i++; // closing fence
                }

                output.Add( new CodeBlock { Code = string.Join( '\n', code ), Info = Entities.DecodeAndUnescape( info ), Fenced = true, Line = lineNo } );
                continue;
            }

            // ATX heading
            if ( TryAtxHeading( line, out var level, out var htext ) )
            {
                output.Add( new Heading { Level = level, RawText = htext, Line = lineNo } );
                i++;
                continue;
            }

            if ( IsThematicBreak( line ) ) { output.Add( new ThematicBreak { Line = lineNo } ); i++; continue; }

            // Block quote
            if ( line.TrimStart().StartsWith( '>' ) )
            {
                i = ParseBlockQuote( lines, i, to, output );
                continue;
            }

            // Footnote definition
            var fm = FootnoteDefRegex().Match( line.TrimStart() );
            if ( ind <= 3 && fm.Success )
            {
                i = ParseFootnote( lines, i, to, fm );
                continue;
            }

            // List
            if ( TryListMarker( line, out _ ) )
            {
                i = ParseList( lines, i, to, output );
                continue;
            }

            // HTML block
            if ( IsHtmlBlockStart( line, canInterruptParagraph: false ) )
            {
                i = ParseHtmlBlock( lines, i, to, output );
                continue;
            }

            // Table
            if ( line.Contains( '|' ) && i + 1 < to && IsTableDelimiterRow( lines[i + 1].Text, out var aligns ) )
            {
                var header = SplitRow( line );
                if ( header.Count == aligns.Count )
                {
                    i = ParseTable( lines, i, to, header, aligns, output );
                    continue;
                }
            }

            i = ParseParagraph( lines, i, to, output );
        }
    }

    private int ParseParagraph( List<MarkdownParser.SourceLine> lines, int i, int to, List<Block> output )
    {
        var lineNo = lines[i].Number;
        var para = new List<string>();

        // Leading link reference definitions
        while ( i < to && !IsBlank( lines[i].Text ) && Indent( lines[i].Text ) <= 3 )
        {
            var m = LinkRefDefRegex().Match( lines[i].Text.Trim() );
            if ( !m.Success || m.Groups[1].Value.StartsWith( '^' ) )
            {
                break;
            }

            var label = InlineParser.NormalizeLabel( m.Groups[1].Value );
            var url = m.Groups[2].Value;
            if ( url.StartsWith( '<' ) && url.EndsWith( '>' ) )
            {
                url = url[1..^1];
            }

            var title = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Success ? m.Groups[5].Value : null;
            _doc.References.TryAdd( label, new LinkReference( Entities.DecodeAndUnescape( url ), title is null ? null : Entities.DecodeAndUnescape( title ) ) );
            i++;
        }
        if ( i >= to || IsBlank( lines[i].Text ) )
        {
            return i;
        }

        para.Add( lines[i].Text.TrimStart() );
        i++;
        while ( i < to )
        {
            var l = lines[i].Text;
            if ( IsBlank( l ) )
            {
                break;
            }

            var ind = Indent( l );
            if ( ind <= 3 )
            {
                var t = l.Trim();
                if ( t.Length > 0 && ( t.All( c => c == '=' ) || t.All( c => c == '-' ) ) )
                {
                    output.Add( new Heading { Level = t[0] == '=' ? 1 : 2, RawText = string.Join( '\n', para ).Trim(), Line = lineNo } );
                    return i + 1;
                }
            }
            if ( InterruptsParagraph( l ) )
            {
                break;
            }
            // A table header row can start right after a paragraph line in GFM only after a blank line; keep it simple.
            para.Add( l.TrimStart() );
            i++;
        }
        output.Add( new Paragraph { RawText = string.Join( '\n', para ).TrimEnd(), Line = lineNo } );
        return i;
    }

    private int ParseBlockQuote( List<MarkdownParser.SourceLine> lines, int i, int to, List<Block> output )
    {
        var lineNo = lines[i].Number;
        var inner = new List<MarkdownParser.SourceLine>();
        var lastWasParagraphText = false;
        while ( i < to )
        {
            var l = lines[i].Text;
            var ind = Indent( l );
            if ( ind <= 3 && ind < l.Length && l[ind] == '>' )
            {
                var content = l[( ind + 1 )..];
                if ( content.StartsWith( ' ' ) )
                {
                    content = content[1..];
                }

                inner.Add( new( content, lines[i].Number ) );
                lastWasParagraphText = !IsBlank( content ) && !TryFenceOpen( content, out _, out _, out _, out _ );
                i++;
            }
            else if ( lastWasParagraphText && !IsBlank( l ) && !InterruptsParagraph( l ) )
            {
                inner.Add( new( l, lines[i].Number ) ); // lazy continuation
                i++;
            }
            else
            {
                break;
            }
        }
        var quote = new BlockQuote { Line = lineNo };
        ParseBlocks( inner, 0, inner.Count, quote.Children );

        // GitHub alerts: > [!NOTE]
        if ( quote.Children.FirstOrDefault() is Paragraph p )
        {
            var am = Regex.Match( p.RawText, @"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ ]*(\n|$)", RegexOptions.IgnoreCase );
            if ( am.Success )
            {
                quote.AlertKind = am.Groups[1].Value.ToLowerInvariant();
                p.RawText = p.RawText[am.Length..];
                if ( string.IsNullOrWhiteSpace( p.RawText ) )
                {
                    quote.Children.RemoveAt( 0 );
                }
            }
        }
        output.Add( quote );
        return i;
    }

    private int ParseFootnote( List<MarkdownParser.SourceLine> lines, int i, int to, Match fm )
    {
        var label = InlineParser.NormalizeLabel( fm.Groups[1].Value );
        var inner = new List<MarkdownParser.SourceLine> { new( fm.Groups[2].Value, lines[i].Number ) };
        var lineNo = lines[i].Number;
        i++;
        var prevBlank = false;
        while ( i < to )
        {
            var l = lines[i].Text;
            if ( IsBlank( l ) ) { inner.Add( new( "", lines[i].Number ) ); prevBlank = true; i++; continue; }
            if ( Indent( l ) >= 4 ) { inner.Add( new( l[4..], lines[i].Number ) ); prevBlank = false; i++; continue; }
            if ( !prevBlank && !InterruptsParagraph( l ) && !FootnoteDefRegex().IsMatch( l.TrimStart() ) ) { inner.Add( new( l, lines[i].Number ) ); i++; continue; }
            break;
        }
        var def = new FootnoteDefinition { Label = label, Line = lineNo };
        ParseBlocks( inner, 0, inner.Count, def.Children );
        _doc.Footnotes.TryAdd( label, def );
        return i;
    }

    private int ParseList( List<MarkdownParser.SourceLine> lines, int i, int to, List<Block> output )
    {
        TryListMarker( lines[i].Text, out var first );
        var list = new ListBlock { Ordered = first.Ordered, Start = first.Number, Delimiter = first.Delimiter, Line = lines[i].Number };

        while ( i < to && !IsThematicBreak( lines[i].Text ) && TryListMarker( lines[i].Text, out var m )
               && m.Ordered == list.Ordered && m.Delimiter == list.Delimiter )
        {
            var line = lines[i].Text;
            var contentIndent = m.ContentOffset;
            var itemLines = new List<MarkdownParser.SourceLine>
            {
                new(contentIndent < line.Length ? line[contentIndent..] : "", lines[i].Number)
            };
            var itemLine = lines[i].Number;
            i++;
            var prevBlank = m.EmptyContent;
            var inFence = TryFenceOpen( itemLines[0].Text, out var fc, out var fl, out _, out _ );

            while ( i < to )
            {
                var l = lines[i].Text;
                if ( IsBlank( l ) )
                {
                    if ( m.EmptyContent && itemLines.Count == 1 && IsBlank( itemLines[0].Text ) )
                    {
                        break; // item may begin with at most one blank line
                    }

                    itemLines.Add( new( "", lines[i].Number ) );
                    prevBlank = true;
                    i++;
                    continue;
                }
                var ind = Indent( l );
                if ( ind >= contentIndent )
                {
                    var content = l[contentIndent..];
                    if ( inFence ) { if ( IsFenceClose( content, fc, fl ) )
                        {
                            inFence = false;
                        }
                    }
                    else if ( TryFenceOpen( content, out fc, out fl, out _, out _ ) )
                    {
                        inFence = true;
                    }

                    itemLines.Add( new( content, lines[i].Number ) );
                    prevBlank = false;
                    i++;
                    continue;
                }
                if ( !prevBlank && !inFence && !InterruptsParagraph( l ) && !TryListMarker( l, out _ ) )
                {
                    itemLines.Add( new( l.TrimStart(), lines[i].Number ) ); // lazy continuation
                    i++;
                    continue;
                }
                break;
            }

            var trailingBlanks = 0;
            while ( itemLines.Count > 1 && IsBlank( itemLines[^1].Text ) ) { itemLines.RemoveAt( itemLines.Count - 1 ); trailingBlanks++; }

            var item = new ListItem { Line = itemLine };
            var firstText = itemLines[0].Text;
            var tm = Regex.Match( firstText, @"^\[([ xX])\](?:[ ]+|$)" );
            if ( tm.Success )
            {
                item.Checked = tm.Groups[1].Value != " ";
                itemLines[0] = itemLines[0] with { Text = firstText[tm.Length..] };
            }

            ParseBlocks( itemLines, 0, itemLines.Count, item.Children );

            if ( item.Children.Count > 1 )
            {
                var internalBlank = false;
                for ( var k = 1; k < itemLines.Count - 1 && !internalBlank; k++ )
                {
                    if ( IsBlank( itemLines[k].Text ) )
                    {
                        internalBlank = true;
                    }
                }

                if ( internalBlank && !OnlyBlankInsideCode( itemLines ) )
                {
                    list.Tight = false;
                }
            }
            list.Children.Add( item );

            if ( trailingBlanks > 0 )
            {
                if ( i < to && TryListMarker( lines[i].Text, out var nm ) && nm.Ordered == list.Ordered && nm.Delimiter == list.Delimiter && !IsThematicBreak( lines[i].Text ) )
                {
                    list.Tight = false;
                }
                else
                {
                    break;
                }
            }
        }
        output.Add( list );
        return i;
    }

    /// <summary>True when every blank line in the item lies inside a fenced code block (does not make the list loose).</summary>
    private static bool OnlyBlankInsideCode( List<MarkdownParser.SourceLine> itemLines )
    {
        var inFence = false;
        var fc = '\0'; var fl = 0;
        foreach ( var l in itemLines )
        {
            if ( inFence ) { if ( IsFenceClose( l.Text, fc, fl ) ) { inFence = false; } continue; }
            if ( TryFenceOpen( l.Text, out fc, out fl, out _, out _ ) ) { inFence = true; continue; }
            if ( IsBlank( l.Text ) )
            {
                return false;
            }
        }
        return true;
    }

    private int ParseHtmlBlock( List<MarkdownParser.SourceLine> lines, int i, int to, List<Block> output )
    {
        var lineNo = lines[i].Number;
        var first = lines[i].Text.TrimStart();
        var sb = new List<string>();
        string? endMarker = null;
        if ( first.StartsWith( "<!--" ) )
        {
            endMarker = "-->";
        }
        else if ( first.StartsWith( "<?" ) )
        {
            endMarker = "?>";
        }
        else if ( first.StartsWith( "<![CDATA[" ) )
        {
            endMarker = "]]>";
        }
        else
        {
            foreach ( var t in RawTags )
            {
                if ( first.StartsWith( "<" + t, StringComparison.OrdinalIgnoreCase ) ) { endMarker = "</" + t + ">"; break; }
            }
        }

        if ( endMarker != null )
        {
            while ( i < to )
            {
                sb.Add( lines[i].Text );
                var done = lines[i].Text.Contains( endMarker, StringComparison.OrdinalIgnoreCase );
                i++;
                if ( done )
                {
                    break;
                }
            }
        }
        else
        {
            while ( i < to && !IsBlank( lines[i].Text ) ) { sb.Add( lines[i].Text ); i++; }
        }

        var html = string.Join( '\n', sb );
        if ( first.StartsWith( "<!--" ) || first.StartsWith( "<?" ) || first.StartsWith( "<!" ) )
        {
            return i; // comments etc. are not rendered
        }

        output.Add( new HtmlBlock { Html = html, Line = lineNo } );
        return i;
    }

    private int ParseTable( List<MarkdownParser.SourceLine> lines, int i, int to, List<string> header, List<TableAlignment> aligns, List<Block> output )
    {
        var table = new Table { Line = lines[i].Number };
        table.Alignments.AddRange( aligns );
        foreach ( var h in header )
        {
            table.Header.Add( new TableCell { RawText = h.Trim() } );
        }

        i += 2;
        while ( i < to )
        {
            var l = lines[i].Text;
            if ( IsBlank( l ) || InterruptsParagraph( l ) )
            {
                break;
            }

            var cells = SplitRow( l );
            var row = new List<TableCell>( aligns.Count );
            for ( var c = 0; c < aligns.Count; c++ )
            {
                row.Add( new TableCell { RawText = c < cells.Count ? cells[c].Trim() : "" } );
            }

            table.Rows.Add( row );
            i++;
        }
        output.Add( table );
        return i;
    }
}

/// <summary>Second pass: parses inline content of all leaf blocks, assigns heading ids and numbers footnotes.</summary>
internal static class InlinePass
{
    public static void Run( MarkdownDocument doc )
    {
        var parser = new InlineParser( doc );
        var slugger = new Slugger();

        void Visit( List<Block> blocks )
        {
            foreach ( var b in blocks )
            {
                switch ( b )
                {
                    case Heading h:
                        h.Inlines = parser.Parse( h.RawText );
                        h.PlainText = InlineParser.ToPlainText( h.Inlines ).Trim();
                        h.Id = slugger.Slug( h.PlainText );
                        break;
                    case LeafBlock leaf:
                        leaf.Inlines = parser.Parse( leaf.RawText );
                        break;
                    case Table t:
                        foreach ( var c in t.Header )
                        {
                            c.Inlines = parser.Parse( c.RawText );
                        }

                        foreach ( var r in t.Rows )
                        {
                            foreach ( var c in r )
                            {
                                c.Inlines = parser.Parse( c.RawText );
                            }
                        }

                        break;
                    case ContainerBlock cb:
                        Visit( cb.Children );
                        break;
                }
            }
        }

        Visit( doc.Children );
        // Footnote bodies may reference other footnotes; parse until no new ones are discovered.
        for ( var k = 0; k < doc.FootnoteOrder.Count; k++ )
        {
            Visit( doc.FootnoteOrder[k].Children );
        }
    }
}
