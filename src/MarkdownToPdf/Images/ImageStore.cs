namespace MarkdownToPdf.Images;

/// <summary>Loads and caches images referenced by documents (local files, data: URIs and optionally http(s)).</summary>
internal sealed class ImageStore(bool allowRemote, Action<string> warn)
{
    private readonly Dictionary<string, PdfImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public IEnumerable<PdfImage> All => _cache.Values.OfType<PdfImage>().Distinct();

    public PdfImage? Load(string url, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string key;
        byte[]? data = null;
        try
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                key = url.Length > 200 ? "data:" + url.GetHashCode() + ":" + url.Length : url;
                if (_cache.TryGetValue(key, out var c0)) return c0;
                int comma = url.IndexOf(',');
                if (comma > 0 && url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    data = Convert.FromBase64String(url[(comma + 1)..]);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                key = url;
                if (_cache.TryGetValue(key, out var c1)) return c1;
                if (!allowRemote)
                {
                    warn($"Remote image skipped (enable AllowRemoteImages / --remote-images): {url}");
                    _cache[key] = null;
                    return null;
                }
                data = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            }
            else
            {
                var path = url;
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
                int q = path.IndexOfAny(['?', '#']);
                if (q >= 0) path = path[..q];
                path = Uri.UnescapeDataString(path);
                key = Path.GetFullPath(Path.Combine(baseDirectory, path));
                if (_cache.TryGetValue(key, out var c2)) return c2;
                if (!File.Exists(key))
                {
                    warn($"Image not found: {key}");
                    _cache[key] = null;
                    return null;
                }
                data = File.ReadAllBytes(key);
            }
        }
        catch (Exception ex)
        {
            warn($"Failed to load image '{url}': {ex.Message}");
            return null;
        }

        PdfImage? image = null;
        if (data != null)
        {
            try { image = ImageDecoder.Decode(data); }
            catch (Exception ex) { warn($"Cannot embed image '{url}': {ex.Message}"); }
        }
        _cache[key] = image;
        return image;
    }
}
