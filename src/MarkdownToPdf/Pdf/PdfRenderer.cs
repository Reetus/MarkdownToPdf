using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarkdownToPdf.Fonts;
using MarkdownToPdf.Images;
using MarkdownToPdf.Layout;

namespace MarkdownToPdf.Pdf;

/// <summary>Serialises laid-out pages into a PDF file.</summary>
internal sealed class PdfRenderer
{
    private readonly LayoutEngine _layout;
    private readonly MarkdownPdfOptions _o;
    private readonly FontManager _fonts;
    private readonly Action<string> _warn;
    private readonly List<FontFace> _faces = [];
    private readonly List<PdfImage> _images = [];
    private readonly HashSet<string> _warnedAnchors = [];

    public PdfRenderer(LayoutEngine layout, MarkdownPdfOptions options, FontManager fonts, Action<string> warn)
    {
        _layout = layout;
        _o = options;
        _fonts = fonts;
        _warn = warn;
    }

    public void Write(Stream output)
    {
        var pages = _layout.Pages;
        AddHeadersAndFooters(pages);

        // 1. Content streams (this also records which glyphs / images are used).
        var contents = pages.Select(BuildContent).ToList();

        var w = new PdfWriter(output);
        w.WriteHeader();
        int catalog = w.Reserve(), pagesObj = w.Reserve(), info = w.Reserve();
        var pageObjs = pages.Select(_ => w.Reserve()).ToList();
        var contentObjs = pages.Select(_ => w.Reserve()).ToList();
        var fontObjs = _faces.Select(_ => w.Reserve()).ToList();
        var imageObjs = _images.Select(_ => w.Reserve()).ToList();
        int resources = w.Reserve();

        // 2. Resources shared by all pages.
        var res = new StringBuilder("<< /ProcSet [/PDF /Text /ImageB /ImageC] /Font <<");
        for (int i = 0; i < _faces.Count; i++) res.Append(' ').Append('/').Append(_faces[i].ResourceName).Append(' ').Append(fontObjs[i]).Append(" 0 R");
        res.Append(" >> /XObject <<");
        for (int i = 0; i < _images.Count; i++) res.Append(' ').Append('/').Append(_images[i].ResourceName).Append(' ').Append(imageObjs[i]).Append(" 0 R");
        res.Append(" >> >>");
        w.WriteObject(resources, res.ToString());

        // 3. Pages
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var annots = BuildAnnotations(p, pageObjs);
            var sb = new StringBuilder();
            sb.Append("<< /Type /Page /Parent ").Append(pagesObj).Append(" 0 R /MediaBox [0 0 ")
              .Append(PdfWriter.N(p.Width)).Append(' ').Append(PdfWriter.N(p.Height)).Append("] /Resources ")
              .Append(resources).Append(" 0 R /Contents ").Append(contentObjs[i]).Append(" 0 R");
            if (annots.Length > 0) sb.Append(" /Annots [").Append(annots).Append(']');
            sb.Append(" >>");
            w.WriteObject(pageObjs[i], sb.ToString());
            w.WriteStream(contentObjs[i], "", Encoding.Latin1.GetBytes(contents[i]), compress: true);
        }
        w.WriteObject(pagesObj, $"<< /Type /Pages /Kids [{string.Join(' ', pageObjs.Select(o => o + " 0 R"))}] /Count {pages.Count} >>");

        // 4. Fonts & images
        for (int i = 0; i < _faces.Count; i++) WriteFont(w, _faces[i], fontObjs[i]);
        for (int i = 0; i < _images.Count; i++) WriteImage(w, _images[i], imageObjs[i]);

        // 5. Outline, info, catalog
        int outlineRoot = WriteOutline(w, pageObjs);
        WriteInfo(w, info);

        var cat = new StringBuilder($"<< /Type /Catalog /Pages {pagesObj} 0 R /ViewerPreferences << /DisplayDocTitle true >>");
        if (outlineRoot > 0) cat.Append($" /Outlines {outlineRoot} 0 R /PageMode /UseOutlines");
        var lang = MarkdownPdfConverter.Culture(_o).Name;
        if (!string.IsNullOrEmpty(lang)) cat.Append(" /Lang ").Append(PdfWriter.AsciiString(lang));
        cat.Append(" >>");
        w.WriteObject(catalog, cat.ToString());

        var id = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_layout.Title + DateTime.UtcNow.Ticks)));
        w.WriteTrailer(catalog, info, id);
    }

    // ───────────────────────────── header / footer ─────────────────────────────

    private void AddHeadersAndFooters(List<PageContent> pages)
    {
        var (top, right, bottom, left) = _layout.Margins;
        var theme = _layout.Theme;
        var culture = MarkdownPdfConverter.Culture(_o);
        int firstContent = pages.FindIndex(p => !p.SuppressHeaderFooter);

        string Expand(string template, PageContent p) => template
            .Replace("{page}", (p.Index + 1).ToString(culture))
            .Replace("{pages}", pages.Count.ToString(culture))
            .Replace("{title}", _layout.Title)
            .Replace("{author}", _o.Metadata.Author ?? "")
            .Replace("{date}", DateTime.Now.ToString("d", culture))
            .Replace("{section}", p.FirstSectionOnPage ?? p.SectionAtStart ?? "")
            .Replace("{file}", p.SourceFile ?? "");

        void Draw(HeaderFooterOptions hf, PageContent p, float baseline, float ruleY)
        {
            if (!hf.Enabled || hf.IsEmpty) return;
            if (hf.SkipFirstPage && p.Index == firstContent) return;
            var face = _fonts.Get(FontRole.Body, false, false);
            float size = hf.Size;
            float x0 = left, x1 = p.Width - right;
            bool any = false;
            void Put(string? tpl, TextAlign align)
            {
                if (string.IsNullOrWhiteSpace(tpl)) return;
                var text = Expand(tpl, p).Trim();
                if (text.Length == 0) return;
                any = true;
                float x = align switch { TextAlign.Center => (x0 + x1) / 2f, TextAlign.Right => x1, _ => x0 };
                p.Foreground.Add(new DeferredTextOp(() => text, face, size, x, baseline, theme.Muted, align));
            }
            Put(hf.Left, TextAlign.Left);
            Put(hf.Center, TextAlign.Center);
            Put(hf.Right, TextAlign.Right);
            if (any && hf.Rule) p.Foreground.Add(new LineOp(x0, ruleY, x1, ruleY, theme.Border, 0.6f));
        }

        foreach (var p in pages)
        {
            if (p.SuppressHeaderFooter) continue;
            float hb = top * 0.5f;
            Draw(_o.Header, p, hb, hb + 5.5f);
            float fb = p.Height - bottom * 0.42f;
            Draw(_o.Footer, p, fb, fb - 11f);
        }
    }

    // ───────────────────────────── content streams ─────────────────────────────

    private string ResourceFor(FontFace face)
    {
        if (face.ResourceName.Length == 0)
        {
            _faces.Add(face);
            face.ResourceName = "F" + _faces.Count;
        }
        return face.ResourceName;
    }

    private string ResourceFor(PdfImage image)
    {
        if (image.ResourceName.Length == 0)
        {
            _images.Add(image);
            image.ResourceName = "Im" + _images.Count;
        }
        return image.ResourceName;
    }

    private static string N(float v) => PdfWriter.N(v);

    private static string Rgb(Color c) => $"{N(c.R)} {N(c.G)} {N(c.B)}";

    private string BuildContent(PageContent page)
    {
        var sb = new StringBuilder(16 * 1024);
        float H = page.Height;
        foreach (var op in page.Background) Emit(sb, op, H);
        foreach (var op in page.Foreground) Emit(sb, op, H);
        return sb.ToString();
    }

    private void Emit(StringBuilder sb, DrawOp op, float H)
    {
        switch (op)
        {
            case RectOp r:
            {
                if (r.Fill == null && r.Stroke == null) return;
                sb.Append("q ");
                if (r.Fill is { } f) sb.Append(Rgb(f)).Append(" rg ");
                if (r.Stroke is { } s) sb.Append(Rgb(s)).Append(" RG ").Append(N(r.LineWidth)).Append(" w ");
                float inset = r.Stroke != null ? r.LineWidth / 2f : 0;
                RectPath(sb, r.X + inset, H - (r.Y + r.H) + inset, r.W - 2 * inset, r.H - 2 * inset, r.Radius, r.RoundTop, r.RoundBottom);
                sb.Append(r.Fill != null && r.Stroke != null ? "B" : r.Fill != null ? "f" : "S").Append(" Q\n");
                break;
            }
            case LineOp l:
                sb.Append("q ").Append(Rgb(l.Color)).Append(" RG ").Append(N(l.Width)).Append(" w ");
                if (l.RoundCap) sb.Append("1 J ");
                if (l.Dash != null) sb.Append('[').Append(string.Join(' ', l.Dash.Select(N))).Append("] 0 d ");
                sb.Append(N(l.X1)).Append(' ').Append(N(H - l.Y1)).Append(" m ").Append(N(l.X2)).Append(' ').Append(N(H - l.Y2)).Append(" l S Q\n");
                break;
            case PolylineOp pl when pl.Points.Count > 1:
                sb.Append("q ").Append(Rgb(pl.Color)).Append(pl.Fill ? " rg " : " RG ").Append(N(pl.Width)).Append(" w 1 J 1 j ");
                for (int i = 0; i < pl.Points.Count; i++)
                    sb.Append(N(pl.Points[i].X)).Append(' ').Append(N(H - pl.Points[i].Y)).Append(i == 0 ? " m " : " l ");
                sb.Append(pl.Fill ? "f" : "S").Append(" Q\n");
                break;
            case CircleOp c:
            {
                sb.Append("q ");
                if (c.Fill is { } f) sb.Append(Rgb(f)).Append(" rg ");
                if (c.Stroke is { } s) sb.Append(Rgb(s)).Append(" RG ").Append(N(c.LineWidth)).Append(" w ");
                float cx = c.Cx, cy = H - c.Cy, r = c.R, k = r * 0.5523f;
                sb.Append($"{N(cx + r)} {N(cy)} m ");
                sb.Append($"{N(cx + r)} {N(cy + k)} {N(cx + k)} {N(cy + r)} {N(cx)} {N(cy + r)} c ");
                sb.Append($"{N(cx - k)} {N(cy + r)} {N(cx - r)} {N(cy + k)} {N(cx - r)} {N(cy)} c ");
                sb.Append($"{N(cx - r)} {N(cy - k)} {N(cx - k)} {N(cy - r)} {N(cx)} {N(cy - r)} c ");
                sb.Append($"{N(cx + k)} {N(cy - r)} {N(cx + r)} {N(cy - k)} {N(cx + r)} {N(cy)} c ");
                sb.Append(c.Fill != null && c.Stroke != null ? "B" : c.Fill != null ? "f" : "S").Append(" Q\n");
                break;
            }
            case ImageOp im:
                sb.Append("q ").Append(N(im.W)).Append(" 0 0 ").Append(N(im.H)).Append(' ').Append(N(im.X)).Append(' ')
                  .Append(N(H - im.Y - im.H)).Append(" cm /").Append(ResourceFor(im.Image)).Append(" Do Q\n");
                break;
            case ClipOp cl:
                sb.Append("q ").Append(N(cl.X)).Append(' ').Append(N(H - cl.Y - cl.H)).Append(' ').Append(N(cl.W)).Append(' ').Append(N(cl.H)).Append(" re W n\n");
                break;
            case ClipEndOp:
                sb.Append("Q\n");
                break;
            case TextOp t:
                EmitText(sb, t.Face, t.Size, t.X, H - t.Baseline, t.Text, t.Color);
                break;
            case DeferredTextOp d:
            {
                var text = d.Text();
                if (text.Length == 0) return;
                var runs = _fonts.Split(text, d.Face);
                float total = runs.Sum(r => r.Face.Face.Measure(r.Text, d.Size));
                float x = d.Align switch { TextAlign.Center => d.X - total / 2f, TextAlign.Right => d.X - total, _ => d.X };
                foreach (var (face, run) in runs)
                {
                    EmitText(sb, face, d.Size, x, H - d.Baseline, run, d.Color);
                    x += face.Face.Measure(run, d.Size);
                }
                break;
            }
        }
    }

    private void EmitText(StringBuilder sb, FaceRef face, float size, float x, float y, string text, Color color)
    {
        var name = ResourceFor(face.Face);
        sb.Append("BT /").Append(name).Append(' ').Append(N(size)).Append(" Tf ").Append(Rgb(color)).Append(" rg ");
        if (face.SynthBold) sb.Append("2 Tr ").Append(N(size * 0.03f)).Append(" w ").Append(Rgb(color)).Append(" RG ");
        sb.Append(face.SynthItalic ? "1 0 0.2 1 " : "1 0 0 1 ").Append(N(x)).Append(' ').Append(N(y)).Append(" Tm <")
          .Append(face.Face.Encode(text)).Append("> Tj");
        if (face.SynthBold) sb.Append(" 0 Tr");
        sb.Append(" ET\n");
    }

    private static void RectPath(StringBuilder sb, float x, float y, float w, float h, float r, bool roundTop, bool roundBottom)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        if (r <= 0.01f)
        {
            sb.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(w)).Append(' ').Append(N(h)).Append(" re ");
            return;
        }
        float rb = roundBottom ? r : 0, rt = roundTop ? r : 0;
        float kb = rb * 0.5523f, kt = rt * 0.5523f;
        float x1 = x + w, y1 = y + h;
        sb.Append($"{N(x + rb)} {N(y)} m {N(x1 - rb)} {N(y)} l ");
        if (rb > 0) sb.Append($"{N(x1 - rb + kb)} {N(y)} {N(x1)} {N(y + rb - kb)} {N(x1)} {N(y + rb)} c ");
        sb.Append($"{N(x1)} {N(y1 - rt)} l ");
        if (rt > 0) sb.Append($"{N(x1)} {N(y1 - rt + kt)} {N(x1 - rt + kt)} {N(y1)} {N(x1 - rt)} {N(y1)} c ");
        sb.Append($"{N(x + rt)} {N(y1)} l ");
        if (rt > 0) sb.Append($"{N(x + rt - kt)} {N(y1)} {N(x)} {N(y1 - rt + kt)} {N(x)} {N(y1 - rt)} c ");
        sb.Append($"{N(x)} {N(y + rb)} l ");
        if (rb > 0) sb.Append($"{N(x)} {N(y + rb - kb)} {N(x + rb - kb)} {N(y)} {N(x + rb)} {N(y)} c ");
        sb.Append("h ");
    }

    // ───────────────────────────── annotations ─────────────────────────────

    private string Dest(Destination d, List<int> pageObjs)
    {
        var page = _layout.Pages[d.PageIndex];
        float y = page.Height - Math.Max(0, d.Y);
        return $"[{pageObjs[d.PageIndex]} 0 R /XYZ 0 {N(y)} 0]";
    }

    private string BuildAnnotations(PageContent p, List<int> pageObjs)
    {
        var sb = new StringBuilder();
        foreach (var l in p.Links)
        {
            if (l.W <= 0 || l.H <= 0) continue;
            string rect = $"[{N(l.X)} {N(p.Height - l.Y - l.H)} {N(l.X + l.W)} {N(p.Height - l.Y)}]";
            if (l.Anchor != null)
            {
                var d = LinkResolver.Find(_layout.Anchors, l.Anchor);
                if (d == null)
                {
                    if (_warnedAnchors.Add(l.Anchor)) _warn($"Link target not found: {l.Anchor}");
                    continue;
                }
                sb.Append($"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] /Dest {Dest(d, pageObjs)} >> ");
            }
            else if (l.Uri != null)
            {
                sb.Append($"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] /A << /S /URI /URI {PdfWriter.AsciiString(l.Uri)} >> >> ");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────────── fonts ─────────────────────────────

    private static string SubsetTag(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        var sb = new StringBuilder(6);
        for (int i = 0; i < 6; i++) sb.Append((char)('A' + hash[i] % 26));
        return sb.ToString();
    }

    private void WriteFont(PdfWriter w, FontFace face, int obj)
    {
        if (face is StandardFace sf)
        {
            w.WriteObject(obj, $"<< /Type /Font /Subtype /Type1 /BaseFont /{sf.BaseName} /Encoding /WinAnsiEncoding >>");
            return;
        }
        var tf = (TrueTypeFace)face;
        var font = tf.Font;
        var glyphs = tf.UsedGlyphs;
        if (glyphs.Count == 0) glyphs[0] = 0;

        int cid = w.Reserve(), desc = w.Reserve(), file = w.Reserve(), toUni = w.Reserve();
        string baseName = PdfWriter.Name(SubsetTag(font.PostScriptName + string.Join(',', glyphs.Keys)) + "+" + face.BaseName).TrimStart('/');

        // Font file
        if (font.IsCff)
        {
            w.WriteStream(file, "/Subtype /OpenType", font.ExtractFace(), compress: true);
        }
        else
        {
            var subset = font.Subset(glyphs.Keys);
            w.WriteStream(file, $"/Length1 {subset.Length}", subset, compress: true);
        }

        float s = tf.Scale;
        int flags = 4 | (font.IsFixedPitch ? 1 : 0) | (font.IsItalic ? 64 : 0);
        var bbox = $"[{N(font.XMin * s)} {N(font.YMin * s)} {N(font.XMax * s)} {N(font.YMax * s)}]";
        w.WriteObject(desc, $"<< /Type /FontDescriptor /FontName /{baseName} /Flags {flags} /FontBBox {bbox} /ItalicAngle {N(font.ItalicAngle)} " +
                            $"/Ascent {N(tf.Ascent)} /Descent {N(tf.Descent)} /CapHeight {N(tf.CapHeight)} /StemV {(font.WeightClass >= 600 ? 120 : 80)} " +
                            $"/{(font.IsCff ? "FontFile3" : "FontFile2")} {file} 0 R >>");

        // Widths
        var wsb = new StringBuilder("[");
        var keys = glyphs.Keys.ToList();
        for (int i = 0; i < keys.Count;)
        {
            int j = i;
            while (j + 1 < keys.Count && keys[j + 1] == keys[j] + 1) j++;
            wsb.Append(keys[i]).Append(" [");
            for (int k = i; k <= j; k++) wsb.Append(Math.Round(font.AdvanceWidth(keys[k]) * s).ToString(CultureInfo.InvariantCulture)).Append(' ');
            wsb.Append("] ");
            i = j + 1;
        }
        wsb.Append(']');

        var subtype = font.IsCff ? "CIDFontType0" : "CIDFontType2";
        var cidToGid = font.IsCff ? "" : " /CIDToGIDMap /Identity";
        w.WriteObject(cid, $"<< /Type /Font /Subtype /{subtype} /BaseFont /{baseName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                           $"/FontDescriptor {desc} 0 R /DW 1000 /W {wsb}{cidToGid} >>");

        w.WriteStream(toUni, "", Encoding.ASCII.GetBytes(BuildToUnicode(glyphs)), compress: true);
        w.WriteObject(obj, $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUni} 0 R >>");
    }

    private static string BuildToUnicode(SortedDictionary<ushort, int> glyphs)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = glyphs.Where(g => g.Key != 0 && g.Value > 0).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, cp) in chunk)
            {
                sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (var b in Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(cp))) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    // ───────────────────────────── images ─────────────────────────────

    private static void WriteImage(PdfWriter w, PdfImage img, int obj)
    {
        string smask = "";
        if (img.SoftMask != null)
        {
            int sm = w.Reserve();
            w.WriteStream(sm, $"/Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", img.SoftMask, compress: false);
            smask = $" /SMask {sm} 0 R";
        }
        string decode = img.InvertedCmyk ? " /Decode [1 0 1 0 1 0 1 0]" : "";
        w.WriteStream(obj, $"/Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /ColorSpace /{img.ColorSpace} " +
                           $"/BitsPerComponent {img.BitsPerComponent} /Filter /{img.Filter}{decode}{smask} /Interpolate true", img.Data, compress: false);
    }

    // ───────────────────────────── outline & info ─────────────────────────────

    private sealed class OutlineNode
    {
        public string Title = "";
        public Destination? Dest;
        public int Level;
        public int Obj;
        public OutlineNode? Parent;
        public List<OutlineNode> Children = [];
        public int Depth;
    }

    private int WriteOutline(PdfWriter w, List<int> pageObjs)
    {
        if (_o.Toc.BookmarkDepth <= 0 || _layout.Outline.Count == 0) return 0;
        var root = new OutlineNode { Level = 0 };
        var stack = new Stack<OutlineNode>();
        stack.Push(root);
        foreach (var e in _layout.Outline)
        {
            var dest = LinkResolver.Find(_layout.Anchors, e.Anchor);
            if (dest == null) continue;
            while (stack.Peek().Level >= e.Level) stack.Pop();
            var parent = stack.Peek();
            var node = new OutlineNode { Title = e.Title, Dest = dest, Level = e.Level, Parent = parent, Depth = parent.Depth + 1 };
            parent.Children.Add(node);
            stack.Push(node);
        }
        if (root.Children.Count == 0) return 0;

        // Assign objects
        void Assign(OutlineNode n) { n.Obj = w.Reserve(); foreach (var c in n.Children) Assign(c); }
        Assign(root);

        // Top level items are expanded when there are few of them; deeper levels start collapsed.
        bool expandTop = root.Children.Count <= 3;
        bool IsOpen(OutlineNode n) => n.Depth == 1 && expandTop;
        int Visible(OutlineNode n) => n.Children.Sum(c => 1 + (IsOpen(c) ? Visible(c) : 0));

        void Write(OutlineNode n)
        {
            for (int i = 0; i < n.Children.Count; i++)
            {
                var c = n.Children[i];
                var sb = new StringBuilder($"<< /Title {PdfWriter.TextString(c.Title)} /Parent {n.Obj} 0 R /Dest {Dest(c.Dest!, pageObjs)}");
                if (i > 0) sb.Append($" /Prev {n.Children[i - 1].Obj} 0 R");
                if (i < n.Children.Count - 1) sb.Append($" /Next {n.Children[i + 1].Obj} 0 R");
                if (c.Children.Count > 0)
                {
                    sb.Append($" /First {c.Children[0].Obj} 0 R /Last {c.Children[^1].Obj} 0 R");
                    sb.Append($" /Count {(IsOpen(c) ? Visible(c) : -c.Children.Count)}");
                }
                sb.Append(" >>");
                w.WriteObject(c.Obj, sb.ToString());
                Write(c);
            }
        }
        Write(root);
        w.WriteObject(root.Obj, $"<< /Type /Outlines /First {root.Children[0].Obj} 0 R /Last {root.Children[^1].Obj} 0 R /Count {Visible(root)} >>");
        return root.Obj;
    }

    private void WriteInfo(PdfWriter w, int obj)
    {
        var m = _o.Metadata;
        var now = DateTimeOffset.Now;
        var off = now.Offset;
        string date = $"D:{now:yyyyMMddHHmmss}{(off < TimeSpan.Zero ? '-' : '+')}{Math.Abs(off.Hours):00}'{Math.Abs(off.Minutes):00}'";
        var sb = new StringBuilder("<<");
        if (!string.IsNullOrWhiteSpace(_layout.Title)) sb.Append(" /Title ").Append(PdfWriter.TextString(_layout.Title));
        if (!string.IsNullOrWhiteSpace(m.Author)) sb.Append(" /Author ").Append(PdfWriter.TextString(m.Author!));
        if (!string.IsNullOrWhiteSpace(m.Subject)) sb.Append(" /Subject ").Append(PdfWriter.TextString(m.Subject!));
        if (!string.IsNullOrWhiteSpace(m.Keywords)) sb.Append(" /Keywords ").Append(PdfWriter.TextString(m.Keywords!));
        var version = typeof(PdfRenderer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        sb.Append(" /Creator ").Append(PdfWriter.TextString("MarkdownToPdf " + version));
        sb.Append(" /Producer ").Append(PdfWriter.TextString("MarkdownToPdf " + version + " (.NET)"));
        sb.Append(" /CreationDate (").Append(date).Append(") /ModDate (").Append(date).Append(") >>");
        w.WriteObject(obj, sb.ToString());
    }
}
