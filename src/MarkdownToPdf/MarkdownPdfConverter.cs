using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarkdownToPdf.Fonts;
using MarkdownToPdf.Images;
using MarkdownToPdf.Layout;
using MarkdownToPdf.Markdown;
using MarkdownToPdf.Pdf;

namespace MarkdownToPdf;

/// <summary>A Markdown input: text plus an optional file path (used for relative links and images).</summary>
public sealed class MarkdownSource
{
    public required string Markdown { get; init; }
    public string? FilePath { get; init; }
    public string? BaseDirectory { get; init; }

    public static MarkdownSource FromFile( string path )
    {
        var full = Path.GetFullPath( path );
        return new MarkdownSource { Markdown = File.ReadAllText( full ), FilePath = full, BaseDirectory = Path.GetDirectoryName( full ) };
    }

    public static MarkdownSource FromText( string markdown, string? baseDirectory = null ) =>
        new() { Markdown = markdown, BaseDirectory = baseDirectory };
}

public sealed class ConversionResult
{
    public int PageCount { get; init; }
    public IReadOnlyList<string> Documents { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Title { get; init; } = "";
}

/// <summary>Converts Markdown documents into a single PDF.</summary>
public sealed partial class MarkdownPdfConverter( MarkdownPdfOptions? options = null )
{
    public MarkdownPdfOptions Options { get; } = options ?? new MarkdownPdfOptions();

    /// <summary>Raised for non-fatal problems (missing fonts, images, link targets...).</summary>
    public event Action<string>? Warning;

    /// <summary>Raised for progress information (files included, page count).</summary>
    public event Action<string>? Info;

    public ConversionResult ConvertFile( string inputPath, string outputPath ) => ConvertFiles( [inputPath], outputPath );

    public ConversionResult ConvertFiles( IEnumerable<string> inputPaths, string outputPath )
    {
        var sources = inputPaths.Select( MarkdownSource.FromFile ).ToList();
        using var ms = new MemoryStream();
        var result = Convert( sources, ms );
        var dir = Path.GetDirectoryName( Path.GetFullPath( outputPath ) );
        if ( !string.IsNullOrEmpty( dir ) )
        {
            Directory.CreateDirectory( dir );
        }

        File.WriteAllBytes( outputPath, ms.ToArray() );
        return result;
    }

    public byte[] ConvertMarkdown( string markdown, string? baseDirectory = null )
    {
        using var ms = new MemoryStream();
        Convert( [MarkdownSource.FromText( markdown, baseDirectory ?? Environment.CurrentDirectory )], ms );
        return ms.ToArray();
    }

    public ConversionResult Convert( IEnumerable<MarkdownSource> sources, Stream output )
    {
        var warnings = new List<string>();
        void Warn( string m )
        {
            warnings.Add( m );
            Warning?.Invoke( m );
        }

        var docs = new List<LoadedDocument>();
        var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        var n = 0;
        foreach ( var s in sources )
        {
            var d = Load( s, n++ );
            if ( d.Path != null && !seen.Add( d.Path ) )
            {
                continue;
            }

            docs.Add( d );
        }
        if ( docs.Count == 0 )
        {
            throw new ArgumentException( "No Markdown input was given." );
        }

        if ( Options.Document.FollowLinks )
        {
            FollowLinks( docs, seen, Warn );
        }

        if ( Options.Document.StripRepeatedPreamble && docs.Count > 1 )
        {
            StripPreamble( docs );
        }

        foreach ( var d in docs )
        {
            Info?.Invoke( "Including " + ( d.Path ?? d.Key ) );
        }

        var fonts = new FontManager( Options.Fonts, Warn );
        var images = new ImageStore( Options.Document.AllowRemoteImages, Warn );
        var links = new LinkResolver();
        foreach ( var d in docs )
        {
            links.Register( d );
        }

        var engine = new LayoutEngine( Options, fonts, images, links, Warn ) { Title = ResolveTitle( docs ) };
        engine.Layout( docs );
        new PdfRenderer( engine, Options, fonts, Warn ).Write( output );
        Info?.Invoke( $"Wrote {engine.Pages.Count} pages" );

        return new ConversionResult
        {
            PageCount = engine.Pages.Count,
            Documents = docs.Select( d => d.Path ?? d.Key ).ToList(),
            Warnings = warnings,
            Title = engine.Title,
        };
    }

    private static LoadedDocument Load( MarkdownSource s, int index )
    {
        var doc = MarkdownParser.Parse( s.Markdown, s.FilePath );
        var baseDir = s.BaseDirectory ?? ( s.FilePath != null ? Path.GetDirectoryName( s.FilePath )! : Environment.CurrentDirectory );
        var key = s.FilePath != null ? "doc:" + Path.GetFullPath( s.FilePath ).ToLowerInvariant() : "doc:" + index;
        return new LoadedDocument { Doc = doc, Key = key, Path = s.FilePath, BaseDirectory = baseDir };
    }

    internal static CultureInfo Culture( MarkdownPdfOptions o )
    {
        if ( !string.IsNullOrWhiteSpace( o.Metadata.Culture ) )
        {
            try { return CultureInfo.GetCultureInfo( o.Metadata.Culture! ); }
            catch ( CultureNotFoundException ) { }
        }
        return CultureInfo.CurrentCulture;
    }

    private string ResolveTitle( List<LoadedDocument> docs )
    {
        if ( !string.IsNullOrWhiteSpace( Options.Metadata.Title ) )
        {
            return Options.Metadata.Title!;
        }

        if ( Options.Cover.Enabled && !string.IsNullOrWhiteSpace( Options.Cover.Title ) )
        {
            return Options.Cover.Title!;
        }

        var first = docs[0];
        if ( first.Doc.FrontMatter.TryGetValue( "title", out var fm ) && !string.IsNullOrWhiteSpace( fm ) )
        {
            return fm;
        }

        var h1 = first.Doc.Headings.FirstOrDefault( h => h.Level == 1 ) ?? first.Doc.Headings.FirstOrDefault();
        if ( h1 != null && !string.IsNullOrWhiteSpace( h1.PlainText ) )
        {
            return h1.PlainText;
        }

        return first.Path != null ? Path.GetFileNameWithoutExtension( first.Path ) : "Document";
    }

    // ───────────────────────────── link following ─────────────────────────────

    private static IEnumerable<string> LinkUrls( IEnumerable<Inline> inlines )
    {
        foreach ( var i in inlines )
        {
            if ( i is LinkInline l )
            {
                yield return l.Url;
            }

            if ( i is ContainerInline c )
            {
                foreach ( var u in LinkUrls( c.Children ) )
                {
                    yield return u;
                }
            }

            if ( i is HtmlInline h )
            {
                var m = Regex.Match( h.Html, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase );
                if ( m.Success )
                {
                    yield return m.Groups[1].Value;
                }
            }
        }
    }

    private static IEnumerable<string> LinkUrls( MarkdownDocument doc )
    {
        foreach ( var b in doc.Descendants() )
        {
            switch ( b )
            {
                case LeafBlock leaf:
                    foreach ( var u in LinkUrls( leaf.Inlines ) )
                    {
                        yield return u;
                    }

                    break;
                case Table t:
                    foreach ( var c in t.Header )
                    {
                        foreach ( var u in LinkUrls( c.Inlines ) )
                        {
                            yield return u;
                        }
                    }

                    foreach ( var r in t.Rows )
                    {
                        foreach ( var c in r )
                        {
                            foreach ( var u in LinkUrls( c.Inlines ) )
                            {
                                yield return u;
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static bool Excluded( string path, List<string> patterns )
    {
        var name = Path.GetFileName( path );
        foreach ( var p in patterns )
        {
            var rx = "^" + Regex.Escape( p ).Replace( @"\*", ".*" ).Replace( @"\?", "." ) + "$";
            if ( Regex.IsMatch( name, rx, RegexOptions.IgnoreCase ) || Regex.IsMatch( path, rx, RegexOptions.IgnoreCase ) )
            {
                return true;
            }
        }
        return false;
    }

    private void FollowLinks( List<LoadedDocument> docs, HashSet<string> seen, Action<string> warn )
    {
        for ( var i = 0; i < docs.Count; i++ )
        {
            var d = docs[i];
            foreach ( var url in LinkUrls( d.Doc ) )
            {
                if ( url.StartsWith( '#' ) || LinkResolver.IsExternal( url ) )
                {
                    continue;
                }

                var (path, _) = LinkResolver.Split( url );
                if ( path.Length == 0 )
                {
                    continue;
                }

                foreach ( var candidate in LinkResolver.CandidatePaths( path, d.BaseDirectory ) )
                {
                    if ( !File.Exists( candidate ) )
                    {
                        continue;
                    }

                    if ( !( candidate.EndsWith( ".md", StringComparison.OrdinalIgnoreCase ) || candidate.EndsWith( ".markdown", StringComparison.OrdinalIgnoreCase ) ) )
                    {
                        break;
                    }

                    if ( seen.Contains( candidate ) || Excluded( candidate, Options.Document.FollowExclude ) )
                    {
                        break;
                    }

                    seen.Add( candidate );
                    try
                    {
                        docs.Add( Load( MarkdownSource.FromFile( candidate ), docs.Count ) );
                    }
                    catch ( Exception ex ) { warn( $"Cannot read linked file {candidate}: {ex.Message}" ); }
                    break;
                }
            }
        }
    }

    // ───────────────────────────── preamble de-duplication ─────────────────────────────

    private static string Signature( Block b )
    {
        var text = b switch
        {
            Heading h => "H" + h.Level + ":" + h.RawText,
            Paragraph p => "P:" + p.RawText,
            ThematicBreak => "HR",
            _ => b.GetType().Name + ":" + b.GetHashCode(),
        };
        var sb = new StringBuilder( text.Length );
        var space = false;
        foreach ( var c in text )
        {
            if ( char.IsWhiteSpace( c ) ) { space = true; continue; }
            if ( space && sb.Length > 0 )
            {
                sb.Append( ' ' );
            }

            space = false;
            sb.Append( char.IsDigit( c ) ? '0' : c );
        }
        return sb.ToString();
    }

    /// <summary>Removes leading blocks of later documents that repeat the first document's leading blocks (e.g. a shared title / "generated on" banner).</summary>
    private static void StripPreamble( List<LoadedDocument> docs )
    {
        var first = docs[0].Doc.Children;
        var sigs = first.Take( 4 ).Select( Signature ).ToList();
        foreach ( var d in docs.Skip( 1 ) )
        {
            var children = d.Doc.Children;
            var k = 0;
            while ( k < sigs.Count && k < children.Count - 1 && Signature( children[k] ) == sigs[k] )
            {
                k++;
            }

            if ( k > 0 )
            {
                children.RemoveRange( 0, k );
            }
        }
    }
}
