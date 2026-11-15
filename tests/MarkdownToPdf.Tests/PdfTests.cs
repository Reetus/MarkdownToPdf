using System.Text;
using System.Text.RegularExpressions;
using MarkdownToPdf.Fonts;

namespace MarkdownToPdf.Tests;

public class PdfTests
{
    private static string Latin1( byte[] b ) => Encoding.Latin1.GetString( b );

    [Fact]
    public void ProducesStructurallyValidPdf()
    {
        var bytes = new MarkdownPdfConverter().ConvertMarkdown( "# Hello\n\nWorld with **bold**, `code` and a [link](https://example.com)." );
        var text = Latin1( bytes );
        Assert.StartsWith( "%PDF-1.7", text );
        Assert.EndsWith( "%%EOF\n", text );

        // every xref offset must point at "n 0 obj"
        var startxref = int.Parse( Regex.Match( text, @"startxref\n(\d+)" ).Groups[1].Value );
        Assert.StartsWith( "xref", text[startxref..] );
        var entries = Regex.Matches( text[startxref..], @"(\d{10}) 00000 n" ).Select( m => int.Parse( m.Groups[1].Value ) ).ToList();
        Assert.NotEmpty( entries );
        for ( var i = 0; i < entries.Count; i++ )
        {
            Assert.Matches( @"^\d+ 0 obj", text.Substring( entries[i], 12 ) );
        }

        Assert.Contains( "/URI (https://example.com)", text );
        Assert.Contains( "/Type /Catalog", text );
    }

    [Theory]
    [InlineData( "A4", 595.28f, 841.89f )]
    [InlineData( "Letter", 612f, 792f )]
    [InlineData( "210mm x 100mm", 595.28f, 283.46f )]
    public void PageSizes( string size, float w, float h )
    {
        var options = new MarkdownPdfOptions();
        options.Page.Size = size;
        var text = Latin1( new MarkdownPdfConverter( options ).ConvertMarkdown( "x" ) );
        var m = Regex.Match( text, @"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]" );
        Assert.Equal( w, float.Parse( m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture ), 1 );
        Assert.Equal( h, float.Parse( m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture ), 1 );
    }

    [Fact]
    public void LandscapeSwapsDimensions()
    {
        var o = new MarkdownPdfOptions();
        o.Page.Landscape = true;
        o.Page.Size = "Letter";
        Assert.Contains( "/MediaBox [0 0 792 612]", Latin1( new MarkdownPdfConverter( o ).ConvertMarkdown( "x" ) ) );
    }

    [Fact]
    public void StandardFontFallbackWorksWithoutSystemFonts()
    {
        var o = new MarkdownPdfOptions();
        o.Fonts.Body = "Helvetica";
        o.Fonts.Monospace = "Courier";
        o.Fonts.Fallback = "";
        var text = Latin1( new MarkdownPdfConverter( o ).ConvertMarkdown( "# Title\n\nText `code`" ) );
        Assert.Contains( "/BaseFont /Helvetica-Bold", text );
        Assert.Matches( @"/BaseFont /(\w{6}\+)?Courier", text ); // built-in Courier or installed Courier New
    }

    [Fact]
    public void MultipleDocumentsBecomeInternalLinks()
    {
        var dir = Directory.CreateTempSubdirectory( "md2pdf" );
        try
        {
            File.WriteAllText( Path.Combine( dir.FullName, "index.md" ), "# Docs\nGenerated 2024\n\n[Go](Other#section-two)" );
            File.WriteAllText( Path.Combine( dir.FullName, "Other.md" ), "# Docs\nGenerated 2025\n\n## Section Two\n\ntext" );
            var o = new MarkdownPdfOptions();
            o.Document.FollowLinks = true;
            var converter = new MarkdownPdfConverter( o );
            var warnings = new List<string>();
            converter.Warning += warnings.Add;
            var output = Path.Combine( dir.FullName, "out.pdf" );
            var result = converter.ConvertFile( Path.Combine( dir.FullName, "index.md" ), output );
            Assert.Equal( 2, result.Documents.Count );
            Assert.Empty( warnings );
            var text = Latin1( File.ReadAllBytes( output ) );
            Assert.Contains( "/Dest [", text );
            Assert.DoesNotContain( "/URI (Other", text );
        }
        finally { dir.Delete( true ); }
    }

    [Fact]
    public void OptionsRoundTripThroughJson()
    {
        var o = new MarkdownPdfOptions();
        o.Page.Size = "Letter";
        o.Fonts.Size = 12;
        o.Toc.Enabled = true;
        var back = MarkdownPdfOptions.FromJson( o.ToJson() );
        Assert.Equal( "Letter", back.Page.Size );
        Assert.Equal( 12, back.Fonts.Size );
        Assert.True( back.Toc.Enabled );
    }

    [Theory]
    [InlineData( "20mm", 56.69f )]
    [InlineData( "1in", 72f )]
    [InlineData( "12pt", 12f )]
    [InlineData( "16px", 12f )]
    public void UnitParsing( string value, float expected ) => Assert.Equal( expected, Units.Parse( value ), 1 );

    [Fact]
    public void TrueTypeSubsetIsLoadable()
    {
        // pick an installed text font that actually contains 'A' (symbol fonts don't)
        TrueTypeFont? font = null;
        FontEntry? entry = null;
        ushort gid = 0;
        foreach ( var e in FontCatalog.Installed.Where( f => !f.IsCff && !f.EmbeddingRestricted && f.Path.EndsWith( ".ttf", StringComparison.OrdinalIgnoreCase ) ) )
        {
            var candidate = TrueTypeFont.Load( File.ReadAllBytes( e.Path ), e.Path, e.FaceIndex );
            if ( candidate.HasGlyf && candidate.TryGetGlyph( 'A', out gid ) ) { font = candidate; entry = e; break; }
        }
        if ( font == null || entry == null )
        {
            return; // no suitable system fonts on this machine
        }

        var subset = font.Subset( [gid] );
        var reloaded = TrueTypeFont.Load( subset, "subset", 0 );
        Assert.Equal( font.NumGlyphs, reloaded.NumGlyphs );
        Assert.Equal( font.AdvanceWidth( gid ), reloaded.AdvanceWidth( gid ) );
        Assert.True( subset.Length < new FileInfo( entry.Path ).Length );
    }
}
