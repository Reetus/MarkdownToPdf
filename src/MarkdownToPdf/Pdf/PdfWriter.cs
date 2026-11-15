using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MarkdownToPdf.Pdf;

/// <summary>Low level PDF object writer: numbered objects, streams, cross-reference table and trailer.</summary>
internal sealed class PdfWriter(Stream output)
{
    private readonly Stream _out = output;
    private readonly Dictionary<int, long> _offsets = new();
    private int _next = 1;
    private long _pos;
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public int Reserve() => _next++;

    public void WriteHeader()
    {
        Raw("%PDF-1.7\n");
        WriteBytes([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    public void WriteObject(int number, string body)
    {
        _offsets[number] = _pos;
        Raw($"{number} 0 obj\n");
        Raw(body);
        Raw("\nendobj\n");
    }

    /// <summary>Writes a stream object. <paramref name="dict"/> holds extra entries (without &lt;&lt; &gt;&gt;).</summary>
    public void WriteStream(int number, string dict, byte[] data, bool compress)
    {
        if (compress)
        {
            data = Deflate(data);
            dict = "/Filter /FlateDecode " + dict;
        }
        _offsets[number] = _pos;
        Raw($"{number} 0 obj\n<< {dict} /Length {data.Length} >>\nstream\n");
        WriteBytes(data);
        Raw("\nendstream\nendobj\n");
    }

    public void WriteTrailer(int root, int info, string id)
    {
        long xref = _pos;
        int count = _next;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(count).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            long off = _offsets.TryGetValue(i, out var o) ? o : 0;
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(_offsets.ContainsKey(i) ? " 00000 n \n" : " 00000 f \n");
        }
        sb.Append("trailer\n<< /Size ").Append(count).Append(" /Root ").Append(root).Append(" 0 R /Info ").Append(info)
          .Append(" 0 R /ID [<").Append(id).Append("> <").Append(id).Append(">] >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        Raw(sb.ToString());
        _out.Flush();
    }

    private void Raw(string s) => WriteBytes(Latin1.GetBytes(s));

    private void WriteBytes(ReadOnlySpan<byte> b)
    {
        _out.Write(b);
        _pos += b.Length;
    }

    public static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ───────────────────────── value formatting ─────────────────────────

    public static string N(float v)
    {
        if (Math.Abs(v) < 0.0005f) return "0";
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>A PDF text string (UTF-16BE with BOM, hex encoded) for metadata and outline titles.</summary>
    public static string TextString(string s)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(s);
        var sb = new StringBuilder("<FEFF", bytes.Length * 2 + 6);
        foreach (var b in bytes) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    /// <summary>A literal ASCII string; non-ASCII characters in URIs are percent-encoded.</summary>
    public static string AsciiString(string s)
    {
        var sb = new StringBuilder("(");
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b >= 0x80 || b < 0x20) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    public static string Name(string s)
    {
        var sb = new StringBuilder("/");
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '+' or '.') sb.Append(c);
            else if (c < 0x80) sb.Append('#').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
