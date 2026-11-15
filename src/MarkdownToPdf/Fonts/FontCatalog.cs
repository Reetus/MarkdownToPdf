using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace MarkdownToPdf.Fonts;

/// <summary>Describes one installed font face (discovered by scanning font directories).</summary>
public sealed record FontEntry(
    string Path, int FaceIndex, string Family, string TypographicFamily, string FullName, string PostScriptName,
    string Subfamily, int Weight, bool Italic, bool EmbeddingRestricted, bool IsCff)
{
    public string DisplayFamily => string.IsNullOrEmpty(TypographicFamily) ? Family : TypographicFamily;
}

/// <summary>Discovers fonts installed on Windows, Linux and macOS by reading their name tables.</summary>
public static class FontCatalog
{
    private static readonly Lazy<IReadOnlyList<FontEntry>> SystemFonts = new(ScanSystem);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<FontEntry>> FileCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FontEntry> Installed => SystemFonts.Value;

    public static IEnumerable<string> FontDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }

    private static IReadOnlyList<FontEntry> ScanSystem()
    {
        var list = new List<FontEntry>();
        foreach (var dir in FontDirectories().Distinct())
            list.AddRange(ScanDirectory(dir, recursive: true));
        return list;
    }

    public static IReadOnlyList<FontEntry> ScanDirectory(string dir, bool recursive = false)
    {
        var list = new List<FontEntry>();
        if (!Directory.Exists(dir)) return list;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive
            });
            files = files.Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception) { return list; }
        foreach (var f in files) list.AddRange(ScanFile(f));
        return list;
    }

    public static IReadOnlyList<FontEntry> ScanFile(string path) => FileCache.GetOrAdd(path, static p =>
    {
        var result = new List<FontEntry>();
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return result;
            var offsets = new List<int>();
            if (Encoding.ASCII.GetString(head, 0, 4) == "ttcf")
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                var buf = ReadAt(fs, 12, 4 * Math.Min(count, 64));
                for (int i = 0; i < Math.Min(count, 64); i++) offsets.Add((int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i * 4)));
            }
            else offsets.Add(0);

            for (int i = 0; i < offsets.Count; i++)
            {
                try
                {
                    var mini = ReadMiniFont(fs, offsets[i]);
                    if (mini == null) continue;
                    var tt = TrueTypeFont.LoadHeader(mini, p, 0);
                    if (string.IsNullOrEmpty(tt.FamilyName)) continue;
                    result.Add(new FontEntry(p, i, tt.FamilyName, tt.TypographicFamily, tt.FullName, tt.PostScriptName,
                        tt.SubfamilyName, tt.WeightClass, tt.IsItalic, tt.EmbeddingRestricted, tt.IsCff));
                }
                catch (Exception) { /* skip broken face */ }
            }
        }
        catch (Exception) { /* unreadable file */ }
        return result;
    });

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        var buf = new byte[count];
        fs.Position = offset;
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Copies only the small tables needed for identification into a compact single-face font.</summary>
    private static byte[]? ReadMiniFont(FileStream fs, int faceOffset)
    {
        var hdr = ReadAt(fs, faceOffset, 12);
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
        if (numTables is 0 or > 200) return null;
        var dir = ReadAt(fs, faceOffset + 12, numTables * 16);
        string[] wanted = ["head", "name", "OS/2", "post", "CFF ", "CFF2", "glyf", "loca"];
        var records = new List<(string tag, int off, int len)>();
        for (int i = 0; i < numTables; i++)
        {
            var tag = Encoding.ASCII.GetString(dir, i * 16, 4);
            if (!wanted.Contains(tag)) continue;
            int off = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i * 16 + 8));
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i * 16 + 12));
            bool contentNeeded = tag is "head" or "name" or "OS/2" or "post";
            records.Add((tag, off, contentNeeded ? len : 0));
        }
        int total = 12 + records.Count * 16 + records.Sum(r => (r.len + 3) & ~3);
        var mini = new byte[total];
        hdr.AsSpan(0, 4).CopyTo(mini);
        BinaryPrimitives.WriteUInt16BigEndian(mini.AsSpan(4), (ushort)records.Count);
        int pos = 12 + records.Count * 16;
        for (int i = 0; i < records.Count; i++)
        {
            var (tag, off, len) = records[i];
            int rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tag, mini.AsSpan(rec, 4));
            BinaryPrimitives.WriteUInt32BigEndian(mini.AsSpan(rec + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(mini.AsSpan(rec + 12), (uint)len);
            if (len > 0)
            {
                fs.Position = off;
                fs.ReadExactly(mini, pos, len);
                pos += (len + 3) & ~3;
            }
        }
        return mini;
    }

    internal static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Finds the face of <paramref name="family"/> closest to the requested weight / style.</summary>
    public static FontEntry? Find(IEnumerable<FontEntry> entries, string family, int weight, bool italic)
    {
        var q = Normalize(family);
        if (q.Length == 0) return null;
        var candidates = entries.Where(e => !e.EmbeddingRestricted && (
            Normalize(e.TypographicFamily) == q || Normalize(e.Family) == q)).ToList();
        if (candidates.Count == 0)
        {
            // a specific face name such as "Segoe UI Semibold" or "Consolas-Bold"
            var exact = entries.FirstOrDefault(e => !e.EmbeddingRestricted && (Normalize(e.FullName) == q || Normalize(e.PostScriptName) == q));
            return exact;
        }
        return candidates
            .OrderBy(e => (e.Italic == italic ? 0 : 1000) + Math.Abs(e.Weight - weight) + (e.Weight < weight && weight > 500 ? 1 : 0))
            .ThenBy(e => e.IsCff ? 1 : 0)
            .ThenBy(e => e.Path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .First();
    }
}
