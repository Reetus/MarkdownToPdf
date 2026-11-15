using MarkdownToPdf.Markdown;

namespace MarkdownToPdf.Layout;

/// <summary>
/// Maps Markdown link destinations to either external URIs or internal anchors ("docKey#id").
/// Links to other documents that are part of the same PDF become internal links.
/// </summary>
internal sealed class LinkResolver
{
    private readonly Dictionary<string, string> _byPath = new( StringComparer.OrdinalIgnoreCase );
    private readonly Dictionary<string, string> _byStem = new( StringComparer.OrdinalIgnoreCase );

    public void Register( LoadedDocument doc )
    {
        if ( doc.Path == null )
        {
            return;
        }

        _byPath.TryAdd( Path.GetFullPath( doc.Path ), doc.Key );
        _byStem.TryAdd( Path.GetFileNameWithoutExtension( doc.Path ), doc.Key );
    }

    public static bool IsExternal( string url )
    {
        var colon = url.IndexOf( ':' );
        if ( colon <= 1 )
        {
            return false; // no scheme, or a Windows drive letter
        }

        var slash = url.IndexOfAny( ['/', '\\', '#', '?'] );
        if ( slash >= 0 && slash < colon )
        {
            return false;
        }

        return url[..colon].All( c => char.IsAsciiLetterOrDigit( c ) || c is '+' or '-' or '.' );
    }

    /// <summary>Candidate file paths for a relative Markdown link (with and without .md extension).</summary>
    public static IEnumerable<string> CandidatePaths( string relative, string baseDirectory )
    {
        var p = Uri.UnescapeDataString( relative ).Replace( '/', Path.DirectorySeparatorChar );
        string full;
        try { full = Path.GetFullPath( Path.Combine( baseDirectory, p ) ); }
        catch ( Exception ) { yield break; }
        yield return full;
        if ( !Path.HasExtension( full ) || !( full.EndsWith( ".md", StringComparison.OrdinalIgnoreCase ) || full.EndsWith( ".markdown", StringComparison.OrdinalIgnoreCase ) ) )
        {
            yield return full + ".md";
            yield return full + ".markdown";
        }
    }

    public static (string Path, string? Fragment) Split( string url )
    {
        var hash = url.IndexOf( '#' );
        var path = hash >= 0 ? url[..hash] : url;
        var frag = hash >= 0 ? url[( hash + 1 )..] : null;
        var q = path.IndexOf( '?' );
        if ( q >= 0 )
        {
            path = path[..q];
        }

        return (path, frag);
    }

    public (string? Uri, string? Anchor) Resolve( string url, LoadedDocument current )
    {
        url = url.Trim();
        if ( url.Length == 0 )
        {
            return (null, null);
        }

        if ( url.StartsWith( '#' ) )
        {
            return (null, current.Key + "#" + url[1..].ToLowerInvariant());
        }

        if ( IsExternal( url ) )
        {
            return (url, null);
        }

        var (path, frag) = Split( url );
        foreach ( var candidate in CandidatePaths( path, current.BaseDirectory ) )
        {
            if ( _byPath.TryGetValue( candidate, out var key ) )
            {
                return (null, key + "#" + ( frag ?? "" ).ToLowerInvariant());
            }
        }
        // wiki-style link by page name
        var stem = Path.GetFileNameWithoutExtension( Uri.UnescapeDataString( path ) );
        if ( !path.Contains( '/' ) && !path.Contains( '\\' ) && _byStem.TryGetValue( stem, out var k2 ) )
        {
            return (null, k2 + "#" + ( frag ?? "" ).ToLowerInvariant());
        }

        return (url, null);
    }

    /// <summary>Finds the destination of an anchor key, tolerating case and slug differences.</summary>
    public static Destination? Find( Dictionary<string, Destination> anchors, string key )
    {
        if ( anchors.TryGetValue( key, out var d ) )
        {
            return d;
        }

        var hash = key.IndexOf( '#' );
        if ( hash < 0 )
        {
            return null;
        }

        var doc = key[..( hash + 1 )];
        var frag = Uri.UnescapeDataString( key[( hash + 1 )..] );
        if ( anchors.TryGetValue( doc + Slugger.Normalize( frag ), out d ) )
        {
            return d;
        }

        if ( anchors.TryGetValue( doc + frag.Replace( ' ', '-' ).ToLowerInvariant(), out d ) )
        {
            return d;
        }
        // the same anchor in any document
        var suffix = "#" + Slugger.Normalize( frag );
        foreach ( var (k, v) in anchors )
        {
            if ( k.EndsWith( suffix, StringComparison.Ordinal ) )
            {
                return v;
            }
        }
        // fall back to the start of the referenced document
        if ( frag.Length > 0 && anchors.TryGetValue( doc, out d ) )
        {
            return d;
        }

        return null;
    }
}
