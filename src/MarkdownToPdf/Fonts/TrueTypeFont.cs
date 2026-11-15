using System.Buffers.Binary;
using System.Text;

namespace MarkdownToPdf.Fonts;

/// <summary>
/// Minimal TrueType / OpenType reader: names, metrics, cmap, advance widths and glyph subsetting.
/// Supports .ttf, .otf (CFF outlines, embedded whole) and .ttc collections.
/// </summary>
internal sealed class TrueTypeFont
{
    public required byte[] Data { get; init; }
    public required string SourcePath { get; init; }
    public int FaceIndex { get; init; }
    public Dictionary<string, (int Offset, int Length)> Tables { get; } = new(StringComparer.Ordinal);

    public string FamilyName { get; private set; } = "";
    public string SubfamilyName { get; private set; } = "";
    public string TypographicFamily { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public string PostScriptName { get; private set; } = "";

    public int UnitsPerEm { get; private set; } = 1000;
    public short XMin, YMin, XMax, YMax;
    public short Ascender, Descender, LineGap;
    public short CapHeight, XHeight;
    public int NumGlyphs { get; private set; }
    public int WeightClass { get; private set; } = 400;
    public bool IsItalic { get; private set; }
    public bool IsBold { get; private set; }
    public ushort FsType { get; private set; }
    public float ItalicAngle { get; private set; }
    public bool IsFixedPitch { get; private set; }
    public bool IsCff => Tables.ContainsKey("CFF ") || Tables.ContainsKey("CFF2");
    public bool HasGlyf => Tables.ContainsKey("glyf") && Tables.ContainsKey("loca");

    private int _indexToLocFormat;
    private ushort[] _advances = [];
    private Dictionary<int, ushort> _cmap = new();
    private bool _fullyLoaded;

    /// <summary>True when the license forbids embedding (restricted license / bitmap only).</summary>
    public bool EmbeddingRestricted => (FsType & 0x000F) == 0x0002 || (FsType & 0x0200) != 0;

    // ───────────────────────────── loading ─────────────────────────────

    public static int GetFaceCount(byte[] data)
    {
        if (data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "ttcf")
            return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        return 1;
    }

    /// <summary>Loads only names and style information (cheap, for font discovery).</summary>
    public static TrueTypeFont LoadHeader(byte[] data, string path, int faceIndex)
    {
        var f = new TrueTypeFont { Data = data, SourcePath = path, FaceIndex = faceIndex };
        f.ReadDirectory();
        f.ReadNames();
        f.ReadStyle();
        return f;
    }

    public static TrueTypeFont Load(byte[] data, string path, int faceIndex)
    {
        var f = LoadHeader(data, path, faceIndex);
        f.EnsureLoaded();
        return f;
    }

    public void EnsureLoaded()
    {
        if (_fullyLoaded) return;
        ReadMetrics();
        ReadCmap();
        _fullyLoaded = true;
    }

    private void ReadDirectory()
    {
        int offset = 0;
        if (Encoding.ASCII.GetString(Data, 0, 4) == "ttcf")
        {
            int count = (int)U32(8);
            if (FaceIndex >= count) throw new InvalidDataException("Font index out of range in collection.");
            offset = (int)U32(12 + 4 * FaceIndex);
        }
        uint version = U32(offset);
        if (version != 0x00010000 && version != 0x4F54544F /*OTTO*/ && version != 0x74727565 /*true*/)
            throw new InvalidDataException("Not a TrueType/OpenType font.");
        int numTables = U16(offset + 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = offset + 12 + i * 16;
            var tag = Encoding.ASCII.GetString(Data, rec, 4);
            Tables[tag] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }
    }

    private void ReadNames()
    {
        if (!Tables.TryGetValue("name", out var t)) return;
        int b = t.Offset;
        int count = U16(b + 2);
        int strOff = b + U16(b + 4);
        var best = new Dictionary<int, (int score, string value)>();
        for (int i = 0; i < count; i++)
        {
            int r = b + 6 + i * 12;
            int platform = U16(r), encoding = U16(r + 2), lang = U16(r + 4), nameId = U16(r + 6);
            int len = U16(r + 8), off = U16(r + 10);
            if (nameId is not (1 or 2 or 4 or 6 or 16)) continue;
            int start = strOff + off;
            if (start + len > Data.Length) continue;
            string value;
            int score;
            if (platform == 3 || platform == 0)
            {
                value = Encoding.BigEndianUnicode.GetString(Data, start, len);
                score = platform == 3 && lang == 0x0409 ? 3 : 2;
                if (platform == 3 && encoding == 0) score = 1; // symbol
            }
            else if (platform == 1 && encoding == 0)
            {
                value = Encoding.Latin1.GetString(Data, start, len);
                score = lang == 0 ? 1 : 0;
            }
            else continue;
            if (!best.TryGetValue(nameId, out var cur) || score > cur.score) best[nameId] = (score, value);
        }
        FamilyName = best.GetValueOrDefault(1).value ?? "";
        SubfamilyName = best.GetValueOrDefault(2).value ?? "";
        FullName = best.GetValueOrDefault(4).value ?? FamilyName;
        PostScriptName = best.GetValueOrDefault(6).value ?? "";
        TypographicFamily = best.GetValueOrDefault(16).value ?? "";
        if (string.IsNullOrEmpty(PostScriptName))
            PostScriptName = FullName.Replace(" ", "");
    }

    private void ReadStyle()
    {
        if (Tables.TryGetValue("head", out var head))
        {
            UnitsPerEm = U16(head.Offset + 18);
            XMin = S16(head.Offset + 36); YMin = S16(head.Offset + 38);
            XMax = S16(head.Offset + 40); YMax = S16(head.Offset + 42);
            int macStyle = U16(head.Offset + 44);
            IsBold = (macStyle & 1) != 0;
            IsItalic = (macStyle & 2) != 0;
            _indexToLocFormat = S16(head.Offset + 50);
        }
        if (Tables.TryGetValue("OS/2", out var os2) && os2.Length >= 64)
        {
            int o = os2.Offset;
            WeightClass = U16(o + 4);
            FsType = U16(o + 8);
            int fsSelection = U16(o + 62);
            IsItalic |= (fsSelection & 0x01) != 0 || (fsSelection & 0x200) != 0;
            if ((fsSelection & 0x20) != 0) IsBold = true;
            if (WeightClass is > 0 and < 10) WeightClass *= 100; // some old fonts use 1..9
        }
        else if (IsBold) WeightClass = 700;
        if (Tables.TryGetValue("post", out var post) && post.Length >= 16)
        {
            ItalicAngle = S16(post.Offset + 4) + U16(post.Offset + 6) / 65536f;
            IsFixedPitch = U32(post.Offset + 12) != 0;
        }
    }

    private void ReadMetrics()
    {
        var hhea = Tables["hhea"];
        Ascender = S16(hhea.Offset + 4);
        Descender = S16(hhea.Offset + 6);
        LineGap = S16(hhea.Offset + 8);
        int numHMetrics = U16(hhea.Offset + 34);
        NumGlyphs = U16(Tables["maxp"].Offset + 4);

        if (Tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            int o = os2.Offset;
            int fsSelection = U16(o + 62);
            short typoAsc = S16(o + 68), typoDesc = S16(o + 70), typoGap = S16(o + 72);
            if ((fsSelection & 0x80) != 0 || (Ascender == 0 && Descender == 0))
            {
                Ascender = typoAsc; Descender = typoDesc; LineGap = typoGap;
            }
            int version = U16(o);
            if (version >= 2 && os2.Length >= 90)
            {
                XHeight = S16(o + 86);
                CapHeight = S16(o + 88);
            }
        }
        if (CapHeight == 0) CapHeight = (short)(Ascender * 0.7);
        if (XHeight == 0) XHeight = (short)(Ascender * 0.5);

        _advances = new ushort[NumGlyphs];
        int hm = Tables["hmtx"].Offset;
        ushort last = 0;
        for (int g = 0; g < NumGlyphs; g++)
        {
            if (g < numHMetrics) last = U16(hm + g * 4);
            _advances[g] = last;
        }
    }

    private void ReadCmap()
    {
        var map = new Dictionary<int, ushort>();
        if (!Tables.TryGetValue("cmap", out var t)) { _cmap = map; return; }
        int b = t.Offset;
        int n = U16(b + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < n; i++)
        {
            int r = b + 4 + i * 8;
            int platform = U16(r), enc = U16(r + 2);
            int off = b + (int)U32(r + 4);
            int format = U16(off);
            int score = (platform, enc, format) switch
            {
                (3, 10, 12) => 10,
                (0, _, 12) => 9,
                (3, 1, 4) => 8,
                (0, _, 4) => 7,
                (3, 0, 4) => 5, // symbol
                (1, 0, 0) => 1,
                _ => -1
            };
            if (score > bestScore) { bestScore = score; best = off; }
        }
        if (best < 0) { _cmap = map; return; }

        int fmt = U16(best);
        if (fmt == 4)
        {
            int segX2 = U16(best + 6);
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(ends + s * 2), start = U16(starts + s * 2);
                short delta = S16(deltas + s * 2);
                int rangeOffPos = ranges + s * 2;
                int rangeOff = U16(rangeOffPos);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g;
                    if (rangeOff == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        int addr = rangeOffPos + rangeOff + (c - start) * 2;
                        if (addr + 1 >= Data.Length) continue;
                        g = U16(addr);
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) map.TryAdd(c, (ushort)g);
                }
            }
            if (bestScore == 5) // symbol fonts map U+F0xx
                foreach (var kv in map.Where(k => k.Key >= 0xF000 && k.Key <= 0xF0FF).ToList()) map.TryAdd(kv.Key - 0xF000, kv.Value);
        }
        else if (fmt == 12)
        {
            int groups = (int)U32(best + 12);
            for (int gi = 0; gi < groups; gi++)
            {
                int r = best + 16 + gi * 12;
                int start = (int)U32(r), end = (int)U32(r + 4), sg = (int)U32(r + 8);
                if (end - start > 0x30000) continue;
                for (int c = start; c <= end; c++) map.TryAdd(c, (ushort)(sg + c - start));
            }
        }
        else if (fmt == 0)
        {
            for (int c = 0; c < 256; c++) { int g = Data[best + 6 + c]; if (g != 0) map[c] = (ushort)g; }
        }
        _cmap = map;
    }

    // ───────────────────────────── queries ─────────────────────────────

    public bool TryGetGlyph(int codepoint, out ushort gid) => _cmap.TryGetValue(codepoint, out gid);

    public int AdvanceWidth(ushort gid) => gid < _advances.Length ? _advances[gid] : (_advances.Length > 0 ? _advances[0] : UnitsPerEm / 2);

    // ───────────────────────────── subsetting ─────────────────────────────

    /// <summary>
    /// Builds a standalone TrueType font that keeps glyph ids unchanged but drops outlines for unused glyphs.
    /// Keeping ids means the PDF can use CIDToGIDMap /Identity and widths stay valid.
    /// </summary>
    public byte[] Subset(IReadOnlyCollection<ushort> usedGlyphs)
    {
        EnsureLoaded();
        var (glyfOff, glyfLen) = Tables["glyf"];
        var (locaOff, _) = Tables["loca"];

        int GlyphStart(int g) => _indexToLocFormat == 0 ? U16(locaOff + g * 2) * 2 : (int)U32(locaOff + g * 4);

        var keep = new HashSet<ushort>(usedGlyphs) { 0 };
        var queue = new Queue<ushort>(keep);
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (g >= NumGlyphs) continue;
            int s = GlyphStart(g), e = GlyphStart(g + 1);
            if (e - s < 10) continue;
            int p = glyfOff + s;
            if (S16(p) >= 0) continue; // simple glyph
            p += 10;
            while (true)
            {
                int flags = U16(p);
                ushort comp = U16(p + 2);
                if (keep.Add(comp)) queue.Enqueue(comp);
                p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) p += 2;
                else if ((flags & 0x0040) != 0) p += 4;
                else if ((flags & 0x0080) != 0) p += 8;
                if ((flags & 0x0020) == 0) break;
            }
        }

        var glyf = new MemoryStream();
        var loca = new byte[(NumGlyphs + 1) * 4];
        for (int g = 0; g < NumGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            if (!keep.Contains((ushort)g)) continue;
            int s = GlyphStart(g), e = GlyphStart(g + 1);
            if (e <= s || s >= glyfLen) continue;
            glyf.Write(Data, glyfOff + s, Math.Min(e, glyfLen) - s);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(NumGlyphs * 4), (uint)glyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in new[] { "head", "hhea", "maxp", "hmtx", "cvt ", "fpgm", "prep", "OS/2", "post" })
        {
            if (!Tables.TryGetValue(tag, out var t)) continue;
            var bytes = Data.AsSpan(t.Offset, Math.Min(t.Length, Data.Length - t.Offset)).ToArray();
            if (tag == "post" && bytes.Length >= 32)
            {
                // version 3.0 post table: no glyph names (smaller, and avoids invalid name data)
                bytes = bytes[..32];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x00030000);
            }
            tables[tag] = bytes;
        }
        var headBytes = tables["head"];
        BinaryPrimitives.WriteInt16BigEndian(headBytes.AsSpan(50), 1); // long loca
        BinaryPrimitives.WriteUInt32BigEndian(headBytes.AsSpan(8), 0); // checksum adjustment
        tables["glyf"] = glyf.ToArray();
        tables["loca"] = loca;
        tables["cmap"] = BuildMinimalCmap();

        return WriteFont(tables, 0x00010000);
    }

    /// <summary>A tiny (3,0) cmap is required by some strict viewers even though PDF uses glyph ids directly.</summary>
    private static byte[] BuildMinimalCmap()
    {
        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void W32(uint v) { W16((int)(v >> 16)); W16((int)(v & 0xFFFF)); }
        W16(0); W16(1);
        W16(3); W16(0); W32(12);
        // format 4 with one terminating segment
        W16(4); W16(24); W16(0);
        W16(2); W16(2); W16(0); W16(0);
        W16(0xFFFF); W16(0); W16(0xFFFF); W16(1); W16(0);
        return ms.ToArray();
    }

    private static byte[] WriteFont(SortedDictionary<string, byte[]> tables, uint sfntVersion)
    {
        int n = tables.Count;
        int searchRange = 1, entrySelector = 0;
        while (searchRange * 2 <= n) { searchRange *= 2; entrySelector++; }
        searchRange *= 16;
        int headerLen = 12 + n * 16;
        var ms = new MemoryStream();
        var header = new byte[headerLen];
        BinaryPrimitives.WriteUInt32BigEndian(header, sfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)(n * 16 - searchRange));
        ms.Write(header);
        int i = 0;
        int headOffset = 0;
        foreach (var (tag, bytes) in tables)
        {
            int offset = (int)ms.Length;
            if (tag == "head") headOffset = offset;
            ms.Write(bytes);
            while (ms.Length % 4 != 0) ms.WriteByte(0);
            int rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tag, header.AsSpan(rec, 4));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 4), Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 12), (uint)bytes.Length);
            i++;
        }
        var result = ms.ToArray();
        header.CopyTo(result, 0);
        uint adjust = 0xB1B0AFBA - Checksum(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), adjust);
        return result;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data.AsSpan(i).CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }
        return sum;
    }

    /// <summary>Returns the bytes of this single face as a standalone OpenType file (for CFF fonts or TTC members).</summary>
    public byte[] ExtractFace()
    {
        if (GetFaceCount(Data) == 1) return Data;
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, (off, len)) in Tables) tables[tag] = Data.AsSpan(off, len).ToArray();
        return WriteFont(tables, IsCff ? 0x4F54544Fu : 0x00010000u);
    }

    // ───────────────────────────── binary helpers ─────────────────────────────

    private ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o, 2));
    private short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(o, 2));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o, 4));
}
