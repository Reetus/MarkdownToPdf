using MarkdownToPdf.Fonts;
using MarkdownToPdf.Highlighting;
using MarkdownToPdf.Markdown;

namespace MarkdownToPdf.Layout;

internal sealed partial class LayoutEngine
{
    // ───────────────────────────── documents ─────────────────────────────

    public void Layout(IReadOnlyList<LoadedDocument> docs)
    {
        _doc = docs[0];
        if (_o.Cover.Enabled) LayoutCover();
        if (_o.Toc.Enabled) LayoutToc(docs);

        for (int i = 0; i < docs.Count; i++)
        {
            _doc = docs[i];
            if (Pages.Count == 0 || i == 0) NewPage();
            else if (_o.Document.PageBreakBetweenFiles && !PageIsEmpty) NewPage();
            else Pending(Base * 2);
            _page.SourceFile ??= _doc.DisplayName;
            Prepare(0);
            RegisterAnchor(_doc.Key + "#", _y);
            _currentFileStartPage = _page.Index;
            LayoutBlocks(_doc.Doc.Children);
            LayoutFootnotes(_doc.Doc);
        }
        if (Pages.Count == 0) NewPage();
    }

    private int _currentFileStartPage;

    private void LayoutBlocks(List<Block> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            float keepLead = KeepWithNext(b) ? ChainLead(blocks, i + 1, 0) : 0;
            switch (b)
            {
                case Heading h: LayoutHeading(h, keepLead); break;
                case Paragraph p: LayoutParagraph(p, keepLead); break;
                case CodeBlock c: LayoutCode(c); break;
                case BlockQuote q: LayoutQuote(q); break;
                case ListBlock l: LayoutList(l); break;
                case Table t: LayoutTable(t); break;
                case ThematicBreak: LayoutRule(); break;
                case PageBreak: if (!AtPageTop) NewPage(); break;
                case HtmlBlock hb: LayoutHtml(hb, keepLead); break;
            }
        }
    }

    private bool KeepWithNext(Block b) => b switch
    {
        Heading => true,
        Paragraph p => _o.Document.KeepLabelWithNext && !p.RawText.Contains('\n') && p.RawText.TrimEnd().EndsWith(':') && p.RawText.Length < 100,
        _ => false,
    };

    /// <summary>Height that must fit after a keep-with-next block (the next block's first lines, chaining through headings).</summary>
    private float ChainLead(List<Block> blocks, int j, int depth)
    {
        if (j >= blocks.Count || depth > 3) return 0;
        var b = blocks[j];
        if (KeepWithNext(b) && b is Heading h)
            return MeasureHeading(h) + ChainLead(blocks, j + 1, depth + 1);
        if (KeepWithNext(b) && b is Paragraph lp)
            return MeasureParagraphLead(lp, 1) + ParagraphGap + ChainLead(blocks, j + 1, depth + 1);
        return Lead(b);
    }

    private float Lead(Block b) => b switch
    {
        Paragraph p => MeasureParagraphLead(p, 2),
        CodeBlock c => Math.Min(CodeLineHeight * Math.Max(1, c.Code.Split('\n').Length), CodeLineHeight * 3) + 16,
        ListBlock l => l.Items.FirstOrDefault()?.Children.FirstOrDefault() is { } first ? Lead(first) : Base * 1.5f,
        BlockQuote q => (q.Children.FirstOrDefault() is { } qf ? Lead(qf) : 0) + 14,
        Table t => LayoutTable(t, measureOnly: true) + Base * 0.4f,
        _ => Base * _o.Fonts.LineHeight,
    };

    private float MeasureParagraphLead(Paragraph p, int lines)
    {
        if (SingleImage(p.Inlines) is { } img)
        {
            var image = _images.Load(img.Url, _doc.BaseDirectory);
            if (image != null) return ImageSize(image, img, Width, ContentHeight * 0.75f).H + 8;
        }
        var l = _text.Layout(Flatten(p.Inlines, BodyStyle()), Width);
        return l.Take(lines).Sum(x => x.Height);
    }

    // ───────────────────────────── headings ─────────────────────────────

    private TextStyle HeadingStyle(int level)
    {
        float size = Base * _o.Fonts.HeadingScale[Math.Clamp(level, 1, _o.Fonts.HeadingScale.Length) - 1];
        var color = level switch { 3 => _t.H3, 6 => _t.Muted, _ => _t.Heading };
        return new TextStyle { Role = FontRole.Heading, Bold = false, Size = size, LineHeight = 1.25f, Color = color };
    }

    private (float Before, float After, float Rule) HeadingSpacing(int level, float size) => level switch
    {
        1 => (size * 0.9f, size * 0.5f, _t.HeadingRules ? 7f : 0),
        2 => (size * 0.95f, size * 0.45f, _t.HeadingRules ? 5f : 0),
        _ => (size * 0.85f, size * 0.35f, 0),
    };

    private float MeasureHeading(Heading h)
    {
        var st = HeadingStyle(h.Level);
        var lines = _text.Layout(Flatten(h.Inlines, st), Width);
        var (before, after, rule) = HeadingSpacing(h.Level, st.Size);
        return lines.Sum(l => l.Height) + before + after + rule;
    }

    private void LayoutHeading(Heading h, float keepLead)
    {
        var st = HeadingStyle(h.Level);
        bool forceBreak = (_o.Document.PageBreakBeforeH1 && h.Level == 1) || (_o.Document.PageBreakBeforeLevel > 0 && h.Level == _o.Document.PageBreakBeforeLevel);
        if (forceBreak && !AtPageTop && !(_page.Index == _currentFileStartPage && OnlyAnchorsSoFar())) NewPage();

        var lines = _text.Layout(Flatten(h.Inlines, st), Width);
        var (before, after, rule) = HeadingSpacing(h.Level, st.Size);
        float height = lines.Sum(l => l.Height) + rule;
        Pending(before);
        Prepare(Math.Min(height + keepLead, ContentHeight * 0.6f));

        var anchor = _doc.Key + "#" + h.Id.ToLowerInvariant();
        RegisterAnchor(anchor, _y - 6);

        foreach (var l in lines)
        {
            if (_y + l.Height > ContentBottom && !AtPageTop) NewPage();
            EmitLine(l, _left, _y, Width, TextAlign.Left, false);
            _y += l.Height;
        }

        if (rule > 0)
        {
            float ry = _y + rule * 0.55f;
            if (h.Level == 1)
            {
                _page.Foreground.Add(new LineOp(_left, ry, _right, ry, _t.Border, 0.9f));
                _page.Foreground.Add(new LineOp(_left, ry, _left + Math.Min(56, Width), ry, _t.Accent, 2.4f));
            }
            else _page.Foreground.Add(new LineOp(_left, ry, _right, ry, _t.Border, 0.6f));
            _y += rule;
        }
        Pending(after);

        // header section tracking
        if (h.Level == _o.Document.SectionLevel)
        {
            _currentSection = h.PlainText;
            _page.FirstSectionOnPage ??= h.PlainText;
        }
        else if (h.Level < _o.Document.SectionLevel) _currentSection = null;

        if (h.Level <= _o.Toc.BookmarkDepth && !IsNavigationHeading(h))
            Outline.Add(new OutlineEntry(h.Level, h.PlainText, anchor));
    }

    private bool OnlyAnchorsSoFar() => _page.Foreground.Count == 0;

    /// <summary>A heading that only links to another included document (e.g. an index entry) is not bookmarked.</summary>
    private bool IsNavigationHeading(Heading h)
    {
        var meaningful = h.Inlines.Where(i => i is not TextInline t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (meaningful.Count != 1 || meaningful[0] is not LinkInline link) return false;
        var (_, anchor) = _links.Resolve(link.Url, _doc);
        return anchor != null && anchor.EndsWith('#') && !anchor.StartsWith(_doc.Key + "#", StringComparison.Ordinal);
    }

    // ───────────────────────────── paragraphs ─────────────────────────────

    private static ImageInline? SingleImage(List<Inline> inlines)
    {
        var meaningful = inlines.Where(i => i is not TextInline t || !string.IsNullOrWhiteSpace(t.Text)).Where(i => i is not LineBreakInline).ToList();
        if (meaningful.Count != 1) return null;
        return meaningful[0] switch
        {
            ImageInline img => img,
            LinkInline { Children: [ImageInline li] } => li,
            _ => null,
        };
    }

    private void LayoutParagraph(Paragraph p, float keepLead)
    {
        if (SingleImage(p.Inlines) is { } img)
        {
            var link = p.Inlines.OfType<LinkInline>().FirstOrDefault();
            LayoutBlockImage(img, link == null ? (null, null) : _links.Resolve(link.Url, _doc));
            return;
        }
        var lines = _text.Layout(Flatten(p.Inlines, BodyStyle()), Width);
        FlowLines(lines, _t.Justify ? TextAlign.Justify : TextAlign.Left, keepLead);
        Pending(ParagraphGap);
    }

    private void LayoutHtml(HtmlBlock hb, float keepLead)
    {
        var inlines = new InlineParser(_doc.Doc).Parse(hb.Html.Replace("\n", " "));
        var items = Flatten(inlines, BodyStyle());
        var visible = items.Where(i => i is not BreakItem and not AnchorItem && !(i is TextItem t && string.IsNullOrWhiteSpace(t.Text))).ToList();
        if (visible.Count == 1 && visible[0] is ImageItem only)
        {
            PlaceBlockImage(only.Image, only.Width, only.Height, (only.Style.LinkUri, only.Style.LinkAnchor), null);
            return;
        }
        while (items.Count > 0 && items[0] is BreakItem) items.RemoveAt(0);
        while (items.Count > 0 && items[^1] is BreakItem) items.RemoveAt(items.Count - 1);
        if (items.Count == 0) return;
        bool center = System.Text.RegularExpressions.Regex.IsMatch(hb.Html, @"^\s*<[a-zA-Z0-9]+[^>]*align\s*=\s*[""']?center", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var lines = _text.Layout(items, Width);
        FlowLines(lines, center ? TextAlign.Center : TextAlign.Left, keepLead);
        Pending(ParagraphGap);
    }

    private void LayoutBlockImage(ImageInline img, (string? Uri, string? Anchor) link)
    {
        var image = _images.Load(img.Url, _doc.BaseDirectory);
        if (image == null)
        {
            var st = BodyStyle() with { Italic = true, Color = _t.Muted, Size = Base * 0.9f };
            var lines = _text.Layout([new TextItem("Image not available: " + (string.IsNullOrWhiteSpace(img.Alt) ? img.Url : img.Alt), st)], Width - 16);
            float h = lines.Sum(l => l.Height) + 12;
            Prepare(h);
            _page.AddBackground(new RectOp(_left, _y, Width, h, null, _t.Border, 0.8f, 4), _depth);
            float yy = _y + 6;
            foreach (var l in lines) { EmitLine(l, _left + 8, yy, Width - 16, TextAlign.Left, true); yy += l.Height; }
            _y += h;
            Pending(ParagraphGap);
            return;
        }
        var (w, hgt) = ImageSize(image, img, Width, ContentHeight * 0.75f);
        PlaceBlockImage(image, w, hgt, link, img.Title);
    }

    private void PlaceBlockImage(Images.PdfImage image, float w, float hgt, (string? Uri, string? Anchor) link, string? title)
    {
        List<LineBox>? caption = null;
        if (!string.IsNullOrWhiteSpace(title))
            caption = _text.Layout([new TextItem(title!, BodyStyle() with { Italic = true, Color = _t.Muted, Size = Base * 0.88f })], Width);
        float capH = caption?.Sum(l => l.Height) + 4 ?? 0;
        Pending(Base * 0.4f);
        Prepare(hgt + capH);
        float x = _left + (Width - w) / 2f;
        if (_markers.Count > 0) { foreach (var m in _markers) m(_page, _y + Base); _markers.Clear(); }
        _page.Foreground.Add(new ImageOp(image, x, _y, w, hgt));
        if (link.Uri != null || link.Anchor != null) _page.Links.Add(new LinkArea(x, _y, w, hgt, link.Uri, link.Anchor));
        _y += hgt;
        if (caption != null)
        {
            _y += 4;
            foreach (var l in caption) { EmitLine(l, _left, _y, Width, TextAlign.Center, true); _y += l.Height; }
        }
        Pending(ParagraphGap + Base * 0.2f);
    }

    // ───────────────────────────── rule ─────────────────────────────

    private void LayoutRule()
    {
        Pending(Base * 0.9f);
        Prepare(2);
        _page.Foreground.Add(new LineOp(_left, _y + 0.5f, _right, _y + 0.5f, _t.Border, 0.9f));
        _y += 1;
        Pending(Base * 0.9f);
    }

    // ───────────────────────────── code blocks ─────────────────────────────

    private float CodeLineHeight => CodeSize * 1.45f;

    private sealed record CodeRun(FaceRef Face, string Text, Color Color, float Width);

    private Color TokenColor(TokenKind k)
    {
        var p = _t.Code;
        return k switch
        {
            TokenKind.Keyword => p.Keyword, TokenKind.Type => p.Type, TokenKind.String => p.String, TokenKind.Number => p.Number,
            TokenKind.Comment => p.Comment, TokenKind.Function => p.Function, TokenKind.Literal => p.Literal, TokenKind.Property => p.Property,
            TokenKind.Tag => p.Tag, TokenKind.Attribute => p.Attribute, TokenKind.Variable => p.Variable, TokenKind.Decorator => p.Decorator,
            TokenKind.Operator => p.Operator, TokenKind.Preprocessor => p.Preprocessor, TokenKind.Inserted => p.Inserted, TokenKind.Deleted => p.Deleted,
            _ => p.Plain,
        };
    }

    private void LayoutCode(CodeBlock cb)
    {
        var lang = cb.Language;
        var code = MarkdownParser.ExpandTabs(cb.Code.Replace("\r", ""), Math.Max(1, _o.Code.TabSize));
        var tokenLines = _o.Code.Highlight && SyntaxHighlighter.IsSupported(lang)
            ? SyntaxHighlighter.Highlight(code, lang)
            : code.Split('\n').Select(l => new List<Token> { new(l, TokenKind.Plain) }).ToList();

        float size = CodeSize;
        float lineH = CodeLineHeight;
        const float padX = 10, padY = 7.5f;
        var mono = _fonts.Get(FontRole.Mono, false, false);
        var monoItalic = _fonts.Get(FontRole.Mono, false, true);
        var body = _fonts.Get(FontRole.Body, false, false);
        float charW = mono.Face.Measure("0", size);
        int digits = tokenLines.Count.ToString().Length;
        float gutter = _o.Code.LineNumbers ? digits * charW + 12 : 0;
        float avail = Width - 2 * padX - gutter;

        // Build visual lines (wrapping long lines when enabled)
        var visual = new List<(int LineNo, List<CodeRun> Runs)>();
        for (int ln = 0; ln < tokenLines.Count; ln++)
        {
            var cells = new List<(FaceRef Face, string Ch, Color Color, float W)>();
            foreach (var tok in tokenLines[ln])
            {
                var face = tok.Kind == TokenKind.Comment ? monoItalic : mono;
                var color = TokenColor(tok.Kind);
                foreach (var (f, run) in _fonts.Split(tok.Text, face, body))
                    foreach (var r in run.EnumerateRunes())
                        cells.Add((f, r.ToString(), color, f.Face.Advance(r.Value) * size / 1000f));
            }
            int start = 0;
            bool first = true;
            float indentW = 0;
            do
            {
                float w = first ? 0 : indentW;
                int end = start;
                int lastBreak = -1;
                while (end < cells.Count && (w + cells[end].W <= avail || end == start || !_o.Code.Wrap))
                {
                    w += cells[end].W;
                    if (cells[end].Ch is " " or "," or ";" or "(" or ")" or "." or "/" or "\\" or "-" or "=") lastBreak = end + 1;
                    end++;
                }
                if (end < cells.Count && lastBreak > start && lastBreak - start > (end - start) * 0.6) end = lastBreak;
                var runs = new List<CodeRun>();
                if (!first) runs.Add(new CodeRun(mono, "", _t.Code.Plain, indentW));
                for (int c = start; c < end;)
                {
                    int d = c;
                    var sb = new System.Text.StringBuilder();
                    float rw = 0;
                    while (d < end && cells[d].Face == cells[c].Face && cells[d].Color == cells[c].Color) { sb.Append(cells[d].Ch); rw += cells[d].W; d++; }
                    runs.Add(new CodeRun(cells[c].Face, sb.ToString(), cells[c].Color, rw));
                    c = d;
                }
                visual.Add((first ? ln + 1 : 0, runs));
                if (first)
                {
                    // continuation lines keep the original indentation plus two characters
                    int lead = 0;
                    while (lead < cells.Count && cells[lead].Ch == " ") lead++;
                    indentW = Math.Min(avail * 0.5f, (lead + 2) * charW);
                }
                first = false;
                start = end;
            } while (start < cells.Count && _o.Code.Wrap);
        }
        // trim trailing empty visual lines
        while (visual.Count > 1 && visual[^1].Runs.All(r => r.Text.Trim().Length == 0) && visual[^1].LineNo != 0) visual.RemoveAt(visual.Count - 1);

        float total = visual.Count * lineH + padY * 2;
        Pending(Base * 0.35f);
        bool keepTogether = total <= ContentHeight * 0.35f || visual.Count <= 10;
        Prepare(keepTogether ? Math.Min(total, ContentHeight) : padY + lineH * 3);

        var (above, _) = _text.TextExtent(new TextStyle { Role = FontRole.Mono, Size = size, LineHeight = 1.45f });
        float segTop = _y;
        bool firstSeg = true;
        float y = _y + padY;
        float textX = _left + padX + gutter;

        void CloseSegment(float bottom, bool last)
        {
            _page.AddBackground(new RectOp(_left, segTop, Width, bottom - segTop, _t.CodeBackground, _t.CodeBorder, 0.7f, 4f, firstSeg, last), _depth);
            if (_o.Code.LineNumbers)
                _page.AddBackground(new LineOp(_left + padX + gutter - 6, segTop + 1, _left + padX + gutter - 6, bottom - 1, _t.CodeBorder, 0.6f), _depth);
        }

        // language label
        if (_o.Code.ShowLanguage && cb.Fenced && lang != null && visual.Count > 0)
        {
            var label = lang.ToUpperInvariant();
            var lf = _fonts.Get(FontRole.Body, true, false);
            float ls = Math.Max(6f, size * 0.72f);
            float lw = lf.Face.Measure(label, ls);
            float firstW = visual[0].Runs.Sum(r => r.Width);
            if (firstW + lw + 12 < avail)
                _page.Foreground.Add(new TextOp(lf, ls, _right - padX - lw + 3, _y + padY + above - 1, label, _t.Code.Label));
        }

        if (_markers.Count > 0) { foreach (var m in _markers) m(_page, y + above); _markers.Clear(); }

        foreach (var (lineNo, runs) in visual)
        {
            if (y + lineH > ContentBottom - 2)
            {
                CloseSegment(y + 3, false);
                NewPage();
                firstSeg = false;
                segTop = _y;
                y = _y + 4;
            }
            float baseline = y + above;
            if (_o.Code.LineNumbers && lineNo > 0)
            {
                var num = lineNo.ToString();
                float nw = mono.Face.Measure(num, size);
                _page.Foreground.Add(new TextOp(mono, size, _left + padX + gutter - 12 - nw, baseline, num, _t.Code.LineNumber));
            }
            float x = textX;
            bool clip = !_o.Code.Wrap && runs.Sum(r => r.Width) > avail;
            if (clip) _page.Foreground.Add(new ClipOp(textX, y, avail, lineH));
            foreach (var r in runs)
            {
                if (r.Text.Length > 0 && r.Text.Trim().Length > 0)
                {
                    // keep leading spaces as offset
                    int lead = r.Text.Length - r.Text.TrimStart(' ').Length;
                    float leadW = lead * r.Face.Face.Measure(" ", size);
                    _page.Foreground.Add(new TextOp(r.Face, size, x + leadW, baseline, r.Text.TrimStart(' ').TrimEnd(), r.Color));
                }
                x += r.Width;
            }
            if (clip) _page.Foreground.Add(new ClipEndOp());
            y += lineH;
        }
        CloseSegment(y + padY, true);
        _y = y + padY;
        Pending(Base * 0.75f);
    }

    // ───────────────────────────── block quotes & alerts ─────────────────────────────

    private static readonly Dictionary<string, (string Title, string Color)> Alerts = new()
    {
        ["note"] = ("Note", "#0969DA"),
        ["tip"] = ("Tip", "#1A7F37"),
        ["important"] = ("Important", "#8250DF"),
        ["warning"] = ("Warning", "#9A6700"),
        ["caution"] = ("Caution", "#CF222E"),
    };

    private void LayoutQuote(BlockQuote q)
    {
        bool alert = q.AlertKind != null && Alerts.ContainsKey(q.AlertKind);
        var accent = alert ? Color.Parse(Alerts[q.AlertKind!].Color) : _t.QuoteBar;
        Pending(Base * 0.3f);
        Prepare(Math.Min(Lead(q), ContentHeight * 0.3f));
        int startPage = _page.Index;
        float startY = _y;
        float outerLeft = _left, outerRight = _right;
        var savedColor = _textColor;
        bool savedTight = _tight;

        _left += alert ? 14 : 13;
        if (alert) _right -= 10;
        if (!alert) _textColor = _t.QuoteText;
        _depth += 2;
        _tight = false;
        _y += alert ? 7 : 3;

        if (alert)
        {
            var (title, _) = Alerts[q.AlertKind!];
            var st = BodyStyle() with { Bold = true, Color = accent };
            var tl = _text.Layout([new TextItem(title, st)], Width);
            FlowLines(tl, TextAlign.Left);
            Pending(Base * 0.25f);
        }

        LayoutBlocks(q.Children);
        _pending = 0;
        _y += alert ? 6 : 3;

        _depth -= 2;
        _left = outerLeft;
        _right = outerRight;
        _textColor = savedColor;
        _tight = savedTight;

        for (int pi = startPage; pi <= _page.Index; pi++)
        {
            var page = Pages[pi];
            float top = pi == startPage ? startY : ContentTop;
            float bottom = pi == _page.Index ? _y : ContentBottom;
            if (bottom - top < 1) continue;
            if (alert)
            {
                page.AddBackground(new RectOp(outerLeft, top, outerRight - outerLeft, bottom - top, accent.Mix(Color.White, 0.93f), null, 0, 3f, pi == startPage, pi == _page.Index), _depth);
                page.AddBackground(new RectOp(outerLeft, top, 3.2f, bottom - top, accent, null, 0, 0), _depth);
            }
            else page.AddBackground(new RectOp(outerLeft, top, 3f, bottom - top, accent, null, 0, 1.2f), _depth);
        }
        Pending(ParagraphGap);
    }

    // ───────────────────────────── lists ─────────────────────────────

    private int _listDepth;

    private void LayoutList(ListBlock list)
    {
        var items = list.Items.ToList();
        var markerStyle = BodyStyle();
        var markerFace = _text.FaceFor(markerStyle);
        float indent = Base * 1.65f;
        if (list.Ordered)
        {
            int last = list.Start + items.Count - 1;
            float widest = markerFace.Face.Measure(last + ".", markerStyle.Size);
            indent = Math.Max(indent, widest + Base * 0.7f);
        }
        bool savedTight = _tight;
        _tight = list.Tight;
        int depth = _listDepth++;
        float itemGap = list.Tight ? Base * 0.2f : Base * _t.ParagraphSpacing;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i > 0) Pending(itemGap);
            float markerX = _left;
            int number = list.Start + i;
            var color = _textColor ?? _t.Text;
            float size = Base;

            _markers.Add((page, baseline) =>
            {
                if (item.Checked != null)
                {
                    float box = size * 0.86f;
                    float bx = markerX + (indent - box) / 2f - 1, by = baseline - box * 0.86f;
                    if (item.Checked == true)
                    {
                        page.Foreground.Add(new RectOp(bx, by, box, box, _t.Accent, null, 0, 2f));
                        page.Foreground.Add(new PolylineOp([(bx + box * 0.22f, by + box * 0.52f), (bx + box * 0.42f, by + box * 0.72f), (bx + box * 0.78f, by + box * 0.3f)], Color.White, Math.Max(1f, size * 0.11f)));
                    }
                    else page.Foreground.Add(new RectOp(bx, by, box, box, Color.White, _t.Muted, 0.8f, 2f));
                }
                else if (list.Ordered)
                {
                    var txt = number + (list.Delimiter == ')' ? ")" : ".");
                    float w = markerFace.Face.Measure(txt, size);
                    page.Foreground.Add(new TextOp(markerFace, size, markerX + indent - w - size * 0.45f, baseline, txt, color.Mix(_t.Muted, 0.35f)));
                }
                else
                {
                    float cx = markerX + indent * 0.42f, cy = baseline - size * 0.33f, r = size * 0.16f;
                    var bullet = _t.Accent.Mix(color, 0.25f);
                    switch (depth % 3)
                    {
                        case 0: page.Foreground.Add(new CircleOp(cx, cy, r, bullet, null)); break;
                        case 1: page.Foreground.Add(new CircleOp(cx, cy, r * 0.95f, null, bullet, Math.Max(0.6f, size * 0.07f))); break;
                        default: page.Foreground.Add(new RectOp(cx - r * 0.85f, cy - r * 0.85f, r * 1.7f, r * 1.7f, bullet, null)); break;
                    }
                }
            });

            _left += indent;
            if (item.Children.Count == 0)
            {
                var (a, b) = _text.TextExtent(BodyStyle());
                Prepare(a + b);
                foreach (var m in _markers) m(_page, _y + a);
                _markers.Clear();
                _y += a + b;
            }
            else LayoutBlocks(item.Children);
            if (_markers.Count > 0)
            {
                foreach (var m in _markers) m(_page, _y + Base);
                _markers.Clear();
            }
            _left -= indent;
        }
        _listDepth--;
        _tight = savedTight;
        Pending(_tight ? Base * 0.2f : Base * _t.ParagraphSpacing);
    }

    // ───────────────────────────── tables ─────────────────────────────

    /// <summary>Lays out a table. With <paramref name="measureOnly"/> nothing is drawn and the height that must
    /// fit on the current page (whole table when small, otherwise header + first row) is returned.</summary>
    private float LayoutTable(Table table, bool measureOnly = false)
    {
        int cols = table.Alignments.Count;
        if (cols == 0) return 0;
        float size = Base * 0.94f;
        const float padX = 7, padY = 4.5f;
        var bodyStyle = BodyStyle() with { Size = size, LineHeight = 1.4f };
        var headStyle = bodyStyle with { Bold = true, Color = _t.Heading };

        List<FlowItem> Items(TableCell c, bool head) => Flatten(c.Inlines, head ? headStyle : bodyStyle);
        var header = table.Header.Select(c => Items(c, true)).ToList();
        var rows = table.Rows.Select(r => r.Select(c => Items(c, false)).ToList()).ToList();

        var minW = new float[cols];
        var maxW = new float[cols];
        void Measure(List<FlowItem> items, int c)
        {
            var single = _text.Layout(items, 1e6f);
            maxW[c] = Math.Max(maxW[c], single.Max(l => l.Width));
            minW[c] = Math.Max(minW[c], Math.Min(_text.MinContentWidth(items), Width * 0.3f));
        }
        for (int c = 0; c < cols; c++) Measure(header[c], c);
        foreach (var r in rows) for (int c = 0; c < cols && c < r.Count; c++) Measure(r[c], c);

        float avail = Width;
        float sumMax = maxW.Sum() + cols * padX * 2, sumMin = minW.Sum() + cols * padX * 2;
        var widths = new float[cols];
        for (int c = 0; c < cols; c++)
        {
            if (sumMax <= avail) widths[c] = maxW[c] + padX * 2;
            else if (sumMin <= avail)
            {
                float k = (avail - sumMin) / Math.Max(1, sumMax - sumMin);
                widths[c] = minW[c] + padX * 2 + (maxW[c] - minW[c]) * k;
            }
            else widths[c] = (minW[c] + padX * 2) * avail / sumMin;
        }
        // stretch narrow tables to the full width when they are already wide
        float tableW = widths.Sum();
        if (tableW > avail * 0.7f && tableW < avail)
        {
            float k = avail / tableW;
            for (int c = 0; c < cols; c++) widths[c] *= k;
            tableW = avail;
        }

        var xs = new float[cols + 1];
        xs[0] = _left;
        for (int c = 0; c < cols; c++) xs[c + 1] = xs[c] + widths[c];

        TextAlign AlignOf(int c) => table.Alignments[c] switch
        {
            TableAlignment.Center => TextAlign.Center,
            TableAlignment.Right => TextAlign.Right,
            _ => TextAlign.Left,
        };

        List<List<LineBox>> LayoutRow(List<List<FlowItem>> cells) =>
            Enumerable.Range(0, cols).Select(c => _text.Layout(c < cells.Count ? cells[c] : [], widths[c] - padX * 2)).ToList();

        // Align the first baselines of all cells in a row.
        float[] Shifts(List<List<LineBox>> cells)
        {
            float maxAbove = cells.Max(l => l.Count > 0 ? l[0].Above : 0);
            return cells.Select(l => l.Count > 0 ? maxAbove - l[0].Above : 0).ToArray();
        }
        float RowHeight(List<List<LineBox>> cells)
        {
            var sh = Shifts(cells);
            return cells.Select((l, c) => sh[c] + l.Sum(x => x.Height)).Max() + padY * 2;
        }

        var headerLines = LayoutRow(header);
        float headerH = RowHeight(headerLines);
        var rowLines = rows.Select(LayoutRow).ToList();
        float totalH = headerH + rowLines.Sum(RowHeight);

        // keep small tables together; otherwise require the header plus the first row
        float required = totalH <= ContentHeight * 0.35f ? totalH : headerH + (rowLines.Count > 0 ? RowHeight(rowLines[0]) : 0);
        if (measureOnly) return required;
        Pending(Base * 0.4f);
        Prepare(required);

        float segTop = _y;
        var border = _t.Border.Mix(Color.Black, 0.06f);

        void DrawRow(List<List<LineBox>> cells, float h, bool head, int index)
        {
            if (head) _page.AddBackground(new RectOp(xs[0], _y, tableW, h, _t.TableHeaderBackground, null, 0, 0), _depth);
            else if (index % 2 == 1) _page.AddBackground(new RectOp(xs[0], _y, tableW, h, _t.TableStripe, null, 0, 0), _depth);
            var shifts = Shifts(cells);
            for (int c = 0; c < cols; c++)
            {
                float y = _y + padY + shifts[c];
                var cl = cells[c];
                for (int li = 0; li < cl.Count; li++)
                {
                    EmitLine(cl[li], xs[c] + padX, y, widths[c] - padX * 2, AlignOf(c), li == cl.Count - 1);
                    y += cl[li].Height;
                }
            }
            _y += h;
            _page.AddBackground(new LineOp(xs[0], _y, xs[0] + tableW, _y, head ? border.Mix(Color.Black, 0.08f) : border, head ? 0.9f : 0.5f), _depth);
        }

        void CloseSegment()
        {
            for (int c = 1; c < cols; c++)
                _page.AddBackground(new LineOp(xs[c], segTop, xs[c], _y, border, 0.5f), _depth);
            _page.AddBackground(new RectOp(xs[0], segTop, tableW, _y - segTop, null, border, 0.8f, 3f), _depth);
        }

        DrawRow(headerLines, headerH, true, 0);
        for (int r = 0; r < rowLines.Count; r++)
        {
            float h = RowHeight(rowLines[r]);
            if (_y + h > ContentBottom)
            {
                CloseSegment();
                NewPage();
                segTop = _y;
                DrawRow(LayoutRow(header), headerH, true, 0);
            }
            DrawRow(rowLines[r], h, false, r);
        }
        CloseSegment();
        Pending(ParagraphGap + Base * 0.3f);
        return required;
    }

    // ───────────────────────────── footnotes ─────────────────────────────

    private void LayoutFootnotes(MarkdownDocument doc)
    {
        if (doc.FootnoteOrder.Count == 0) return;
        Pending(Base * 1.5f);
        Prepare(Base * 3);
        _page.Foreground.Add(new LineOp(_left, _y, _left + Width * 0.3f, _y, _t.Border, 0.8f));
        _y += Base * 0.6f;
        float savedScale = _scale;
        _scale = 0.86f;
        var savedColor = _textColor;
        _textColor = _t.Text.Mix(_t.Muted, 0.4f);
        var list = new ListBlock { Ordered = true, Start = 1, Tight = true, Delimiter = '.' };
        foreach (var fn in doc.FootnoteOrder)
        {
            var item = new ListItem();
            item.Children.AddRange(fn.Children);
            list.Children.Add(item);
        }
        // register anchors at the start of each footnote using an AnchorItem in the first paragraph
        foreach (var (fn, item) in doc.FootnoteOrder.Zip(list.Items))
        {
            if (item.Children.FirstOrDefault() is Paragraph p)
            {
                var copy = new Paragraph { RawText = p.RawText, Inlines = [new HtmlInline($"<a id=\"{fn.Id}\" />"), .. p.Inlines] };
                item.Children[0] = copy;
            }
        }
        LayoutList(list);
        _scale = savedScale;
        _textColor = savedColor;
    }

    // ───────────────────────────── cover & TOC ─────────────────────────────

    private void LayoutCover()
    {
        NewPage(suppressHeaderFooter: true);
        var c = _o.Cover;
        string title = c.Title ?? Title;
        float y = _pageH * 0.30f;
        _page.Foreground.Add(new RectOp(_left, y, 64, 5, _t.Accent, null, 0, 2.5f));
        y += 26;
        var ts = new TextStyle { Role = FontRole.Heading, Bold = true, Size = Base * 3.0f, LineHeight = 1.15f, Color = _t.Heading };
        foreach (var l in _text.Layout([new TextItem(title, ts)], Width))
        {
            EmitLine(l, _left, y, Width, TextAlign.Left, true);
            y += l.Height;
        }
        if (!string.IsNullOrWhiteSpace(c.Subtitle))
        {
            y += 10;
            var ss = BodyStyle() with { Size = Base * 1.45f, Color = _t.Muted, LineHeight = 1.3f };
            foreach (var l in _text.Layout([new TextItem(c.Subtitle!, ss)], Width))
            {
                EmitLine(l, _left, y, Width, TextAlign.Left, true);
                y += l.Height;
            }
        }
        float by = _pageH * 0.78f;
        var author = c.Author ?? _o.Metadata.Author;
        if (!string.IsNullOrWhiteSpace(author))
        {
            var st = BodyStyle() with { Size = Base * 1.15f, Bold = true, Color = _t.Text };
            foreach (var l in _text.Layout([new TextItem(author!, st)], Width)) { EmitLine(l, _left, by, Width, TextAlign.Left, true); by += l.Height; }
        }
        if (!string.IsNullOrWhiteSpace(c.Date))
        {
            var date = c.Date!.Replace("{date}", DateTime.Now.ToString("D", MarkdownPdfConverter.Culture(_o)));
            var st = BodyStyle() with { Color = _t.Muted };
            foreach (var l in _text.Layout([new TextItem(date, st)], Width)) { EmitLine(l, _left, by, Width, TextAlign.Left, true); by += l.Height; }
        }
        _page.Foreground.Add(new LineOp(_left, _pageH - _mBottom, _right, _pageH - _mBottom, _t.Border, 0.6f));
    }

    private void LayoutToc(IReadOnlyList<LoadedDocument> docs)
    {
        var entries = new List<(int Level, string Text, string Anchor)>();
        foreach (var d in docs)
        {
            _doc = d;
            foreach (var h in d.Doc.Headings)
                if (h.Level <= _o.Toc.Depth && !IsNavigationHeading(h))
                    entries.Add((h.Level, h.PlainText, d.Key + "#" + h.Id.ToLowerInvariant()));
        }
        _doc = docs[0];
        if (entries.Count == 0) return;
        int minLevel = entries.Min(e => e.Level);

        NewPage();
        var ts = HeadingStyle(1);
        foreach (var l in _text.Layout([new TextItem(_o.Toc.Title, ts)], Width)) { EmitLine(l, _left, _y, Width, TextAlign.Left, true); _y += l.Height; }
        _y += 4;
        _page.Foreground.Add(new LineOp(_left, _y, _right, _y, _t.Border, 0.9f));
        _page.Foreground.Add(new LineOp(_left, _y, _left + 56, _y, _t.Accent, 2.4f));
        _y += Base * 1.2f;

        float numW = Base * 3.2f;
        foreach (var (level, text, anchor) in entries)
        {
            int rel = level - minLevel;
            float indent = rel * Base * 1.4f;
            bool top = rel == 0;
            var st = BodyStyle() with
            {
                Bold = top, Size = top ? Base * 1.02f : Base * 0.96f, Color = top ? _t.Heading : _t.Text, LinkAnchor = anchor, LineHeight = 1.35f,
            };
            var lines = _text.Layout([new TextItem(text, st)], Width - indent - numW - 6);
            if (top) Pending(Base * 0.55f);
            else Pending(Base * 0.12f);
            Prepare(lines.Sum(l => l.Height));
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                EmitLine(l, _left + indent, _y, Width - indent - numW, TextAlign.Left, true);
                if (i == lines.Count - 1)
                {
                    float baseline = _y + l.Above;
                    float x0 = _left + indent + l.Width + 5;
                    float x1 = _right - numW + 4;
                    if (x1 > x0 + 4)
                        _page.Foreground.Add(new LineOp(x0, baseline - 1, x1, baseline - 1, _t.Muted.Mix(Color.White, 0.35f), 0.9f, [0.01f, 2.6f], true));
                    var face = _text.FaceFor(st);
                    var key = anchor;
                    _page.Foreground.Add(new DeferredTextOp(() => Anchors.TryGetValue(key, out var d) ? (d.PageIndex + 1).ToString() : "", face, st.Size, _right, baseline, top ? _t.Heading : _t.Muted, TextAlign.Right));
                    _page.Links.Add(new LinkArea(_left + indent, _y, Width - indent, l.Height, null, anchor));
                }
                _y += l.Height;
            }
        }
    }
}
