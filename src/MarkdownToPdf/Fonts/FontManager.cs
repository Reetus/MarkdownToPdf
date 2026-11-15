using System.Text;

namespace MarkdownToPdf.Fonts;

internal enum FontRole { Body, Heading, Mono }

/// <summary>A face plus any synthetic styling needed to emulate a missing bold / italic variant.</summary>
internal sealed record FaceRef(FontFace Face, bool SynthBold, bool SynthItalic);

/// <summary>Resolves configured font families to faces, with per-character fallback and synthetic styles.</summary>
internal sealed class FontManager
{
    private readonly FontOptions _options;
    private readonly Action<string> _warn;
    private readonly Dictionary<(FontRole, int, bool), FaceRef> _resolved = new();
    private readonly Dictionary<(string, int), FontFace> _loaded = new(StringComparer.OrdinalIgnoreCase.ToTupleComparer());
    private readonly Dictionary<(int, bool), List<FaceRef>> _fallbacks = new();
    private readonly HashSet<int> _missingReported = [];
    private readonly HashSet<string> _warned = [];

    public FontManager(FontOptions options, Action<string> warn)
    {
        _options = options;
        _warn = warn;
    }

    public IEnumerable<FontFace> LoadedFaces => _loaded.Values.Concat(_standard.Values).Where(f => f.Used).Distinct();

    private readonly Dictionary<string, StandardFace> _standard = new();

    public FaceRef Get(FontRole role, bool bold, bool italic)
    {
        int weight = role == FontRole.Heading ? (bold ? Math.Max(_options.HeadingWeight, 700) : _options.HeadingWeight) : (bold ? 700 : 400);
        if (role == FontRole.Heading && !bold) weight = _options.HeadingWeight;
        var key = (role, weight, italic);
        if (_resolved.TryGetValue(key, out var r)) return r;

        string spec = role switch
        {
            FontRole.Mono => _options.Monospace,
            FontRole.Heading => string.IsNullOrWhiteSpace(_options.Heading) ? _options.Body : _options.Heading!,
            _ => _options.Body,
        };
        r = Resolve(spec, weight, italic, role == FontRole.Mono) ?? Standard(role == FontRole.Mono, weight >= 600, italic);
        _resolved[key] = r;
        return r;
    }

    private FaceRef Standard(bool mono, bool bold, bool italic)
    {
        var name = (mono ? "m" : "s") + (bold ? "b" : "") + (italic ? "i" : "");
        if (!_standard.TryGetValue(name, out var face))
        {
            face = mono ? StandardFonts.Mono(bold, italic) : StandardFonts.Sans(bold, italic);
            _standard[name] = face;
        }
        return new FaceRef(face, false, false);
    }

    private static IEnumerable<string> Expand(string spec, bool mono)
    {
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = raw.Trim('"', '\'');
            switch (c.ToLowerInvariant())
            {
                case "sans-serif" or "sans":
                    foreach (var s in new[] { "Segoe UI", "Helvetica Neue", "Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans" }) yield return s;
                    yield return "@helvetica";
                    break;
                case "serif":
                    foreach (var s in new[] { "Georgia", "Cambria", "Times New Roman", "Liberation Serif", "DejaVu Serif", "Noto Serif" }) yield return s;
                    yield return "@helvetica";
                    break;
                case "monospace" or "mono":
                    foreach (var s in new[] { "Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono" }) yield return s;
                    yield return "@courier";
                    break;
                case "helvetica" or "arial-builtin":
                    yield return "Helvetica";
                    yield return "@helvetica";
                    break;
                case "courier":
                    yield return "Courier New";
                    yield return "@courier";
                    break;
                default:
                    yield return c;
                    break;
            }
        }
        _ = mono;
    }

    private FaceRef? Resolve(string spec, int weight, bool italic, bool mono)
    {
        foreach (var candidate in Expand(spec, mono))
        {
            if (candidate == "@helvetica") return Standard(false, weight >= 600, italic);
            if (candidate == "@courier") return Standard(true, weight >= 600, italic);

            FontEntry? entry;
            if (LooksLikePath(candidate))
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                if (!File.Exists(full)) { WarnOnce($"Font file not found: {full}"); continue; }
                var own = FontCatalog.ScanFile(full);
                if (own.Count == 0) { WarnOnce($"Not a usable font file: {full}"); continue; }
                // look for sibling files of the same family (bold / italic variants)
                var siblings = FontCatalog.ScanDirectory(Path.GetDirectoryName(full)!);
                entry = FontCatalog.Find(siblings, own[0].DisplayFamily, weight, italic) ?? own[0];
                // only accept siblings when the requested style is closer than the given file
                if (entry.Italic != italic && own[0].Italic == italic) entry = own[0];
            }
            else
            {
                entry = FontCatalog.Find(FontCatalog.Installed, candidate, weight, italic);
            }
            if (entry == null) continue;
            var face = Load(entry);
            if (face == null) continue;
            bool synthBold = weight >= 600 && face.Weight < 550;
            bool synthItalic = italic && !face.Italic;
            return new FaceRef(face, synthBold, synthItalic);
        }
        return null;
    }

    private static bool LooksLikePath(string s) =>
        s.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || s.Contains('/') || s.Contains('\\');

    private FontFace? Load(FontEntry entry)
    {
        var key = (entry.Path, entry.FaceIndex);
        if (_loaded.TryGetValue(key, out var f)) return f;
        try
        {
            var data = File.ReadAllBytes(entry.Path);
            var tt = TrueTypeFont.Load(data, entry.Path, entry.FaceIndex);
            if (!tt.HasGlyf && !tt.IsCff) { WarnOnce($"Unsupported outline format in {entry.Path}"); return null; }
            f = new TrueTypeFace(tt);
            _loaded[key] = f;
            return f;
        }
        catch (Exception ex)
        {
            WarnOnce($"Failed to load font {entry.Path}: {ex.Message}");
            return null;
        }
    }

    private List<FaceRef> Fallbacks(int weight, bool italic)
    {
        var key = (weight >= 600 ? 700 : 400, italic);
        if (_fallbacks.TryGetValue(key, out var list)) return list;
        list = [];
        foreach (var fam in _options.Fallback.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var r = Resolve(fam, key.Item1, italic, false);
            if (r != null && list.All(x => x.Face != r.Face)) list.Add(r);
        }
        _fallbacks[key] = list;
        return list;
    }

    private static bool IsKana(int cp) => cp is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;

    private readonly Dictionary<(int, bool), List<FaceRef>> _japanese = new();

    private List<FaceRef> JapaneseFallbacks(int weight, bool italic)
    {
        var key = (weight >= 600 ? 700 : 400, italic);
        if (_japanese.TryGetValue(key, out var list)) return list;
        list = [];
        foreach (var fam in new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Noto Sans JP" })
        {
            var r = Resolve(fam, key.Item1, italic, false);
            if (r != null) { list.Add(r); break; }
        }
        _japanese[key] = list;
        return list;
    }

    /// <summary>Splits text into runs, each rendered by a face that contains its glyphs.</summary>
    public List<(FaceRef Face, string Text)> Split(string text, FaceRef primary, FaceRef? secondary = null)
    {
        var result = new List<(FaceRef, string)>();
        var sb = new StringBuilder();
        FaceRef? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            FaceRef chosen;
            if (cp == ' ' || primary.Face.HasChar(cp)) chosen = primary;
            else if (cp == 0xA0) { chosen = primary; cp = ' '; }
            else if (secondary != null && secondary.Face.HasChar(cp)) chosen = secondary;
            else
            {
                chosen = primary;
                bool found = false;
                int fbWeight = primary.Face.Weight + (primary.SynthBold ? 300 : 0);
                bool fbItalic = primary.Face.Italic || primary.SynthItalic;
                // Kana: prefer Japanese fonts (Korean / Chinese fonts often carry poorly spaced kana).
                var candidates = IsKana(cp) ? JapaneseFallbacks(fbWeight, fbItalic).Concat(Fallbacks(fbWeight, fbItalic)) : Fallbacks(fbWeight, fbItalic);
                foreach (var fb in candidates)
                {
                    if (fb.Face.HasChar(cp))
                    {
                        chosen = fb with { SynthBold = fb.SynthBold || (primary.SynthBold && fb.Face.Weight < 550) };
                        found = true;
                        break;
                    }
                }
                if (!found && _missingReported.Add(cp))
                    _warn($"No installed font contains U+{cp:X4} '{char.ConvertFromUtf32(cp)}'; it will render as a missing glyph.");
            }
            if (current != null && !ReferenceEquals(current, chosen) && current != chosen)
            {
                result.Add((current, sb.ToString()));
                sb.Clear();
            }
            current = chosen;
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (current != null && sb.Length > 0) result.Add((current, sb.ToString()));
        return result;
    }

    private void WarnOnce(string message)
    {
        if (_warned.Add(message)) _warn(message);
    }
}

internal static class TupleComparerExtensions
{
    public static IEqualityComparer<(string, int)> ToTupleComparer(this StringComparer comparer) => new TupleComparer(comparer);

    private sealed class TupleComparer(StringComparer c) : IEqualityComparer<(string, int)>
    {
        public bool Equals((string, int) x, (string, int) y) => c.Equals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((string, int) obj) => HashCode.Combine(c.GetHashCode(obj.Item1), obj.Item2);
    }
}
