using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownToPdf.Markdown;

/// <summary>
/// CommonMark inline parser (code spans, emphasis via the delimiter-run algorithm, links, images,
/// autolinks, raw HTML, entities, hard/soft breaks) with GFM strikethrough, extended autolinks and footnotes.
/// </summary>
internal sealed partial class InlineParser(MarkdownDocument doc)
{
    private readonly MarkdownDocument _doc = doc;

    private sealed class Delimiter
    {
        public required TextInline Node;
        public char Ch;
        public int Count;
        public int OrigCount;
        public bool CanOpen;
        public bool CanClose;
    }

    private sealed class Bracket
    {
        public required TextInline Node;
        public bool Image;
        public bool Active = true;
        public int DelimiterBottom;
        public int TextStart;
    }

    [GeneratedRegex(@"\G<([A-Za-z][A-Za-z0-9+.\-]{1,31}:[^\s<>]*)>")]
    private static partial Regex UriAutolink();

    [GeneratedRegex(@"\G<([a-zA-Z0-9.!#$%&'*+/=?^_`{|}~\-]+@[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*)>")]
    private static partial Regex EmailAutolink();

    [GeneratedRegex(@"\G(?:<[A-Za-z][A-Za-z0-9\-]*(?:\s+[A-Za-z_:][A-Za-z0-9_.:\-]*(?:\s*=\s*(?:[^\s""'=<>`]+|'[^']*'|""[^""]*""))?)*\s*/?>|</[A-Za-z][A-Za-z0-9\-]*\s*>|<!--[\s\S]*?-->|<\?[\s\S]*?\?>)")]
    private static partial Regex RawHtml();

    [GeneratedRegex(@"\G&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"\G(?:https?://|www\.)[A-Za-z0-9\-_]+(?:\.[A-Za-z0-9\-_]+)*[^\s<]*", RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedAutolink();

    [GeneratedRegex(@"\G\[\^([^\]\s]+)\]")]
    private static partial Regex FootnoteRef();

    private string _text = "";
    private int _pos;
    private List<Inline> _nodes = [];
    private List<Delimiter> _delims = [];
    private List<Bracket> _brackets = [];
    private readonly StringBuilder _buffer = new();

    public static string NormalizeLabel(string label)
    {
        var collapsed = Regex.Replace(label.Trim(), @"\s+", " ");
        return collapsed.ToUpperInvariant().ToLowerInvariant();
    }

    public static string ToPlainText(IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        void Walk(IEnumerable<Inline> list)
        {
            foreach (var i in list)
            {
                switch (i)
                {
                    case TextInline t: sb.Append(t.Text); break;
                    case CodeInline c: sb.Append(c.Code); break;
                    case ContainerInline c: Walk(c.Children); break;
                    case ImageInline img: sb.Append(img.Alt); break;
                    case LineBreakInline: sb.Append(' '); break;
                }
            }
        }
        Walk(inlines);
        return sb.ToString();
    }

    public List<Inline> Parse(string text)
    {
        _text = text;
        _pos = 0;
        _nodes = [];
        _delims = [];
        _brackets = [];
        _buffer.Clear();

        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            switch (c)
            {
                case '\\': ParseBackslash(); break;
                case '`': ParseCodeSpan(); break;
                case '*' or '_' or '~': ParseDelimiterRun(c); break;
                case '[': ParseOpenBracket(); break;
                case '!':
                    if (_pos + 1 < _text.Length && _text[_pos + 1] == '[')
                    {
                        Flush();
                        var node = new TextInline("![");
                        _nodes.Add(node);
                        _brackets.Add(new Bracket { Node = node, Image = true, DelimiterBottom = _delims.Count, TextStart = _pos + 2 });
                        _pos += 2;
                    }
                    else { _buffer.Append(c); _pos++; }
                    break;
                case ']': ParseCloseBracket(); break;
                case '<': ParseAngle(); break;
                case '&': ParseEntity(); break;
                case '\n': ParseNewline(); break;
                case 'h' or 'H' or 'w' or 'W':
                    if (!TryExtendedAutolink()) { _buffer.Append(c); _pos++; }
                    break;
                default: _buffer.Append(c); _pos++; break;
            }
        }
        Flush();
        ProcessEmphasis(0);
        return Merge(_nodes);
    }

    private void Flush()
    {
        if (_buffer.Length == 0) return;
        _nodes.Add(new TextInline(_buffer.ToString()));
        _buffer.Clear();
    }

    private void Add(Inline node)
    {
        Flush();
        _nodes.Add(node);
    }

    private static List<Inline> Merge(List<Inline> nodes)
    {
        var result = new List<Inline>(nodes.Count);
        foreach (var n in nodes)
        {
            if (n is TextInline t)
            {
                if (t.Text.Length == 0) continue;
                if (result.Count > 0 && result[^1] is TextInline prev) { prev.Text += t.Text; continue; }
                result.Add(new TextInline(t.Text));
                continue;
            }
            if (n is ContainerInline ci) ci.Children = Merge(ci.Children);
            result.Add(n);
        }
        return result;
    }

    private void ParseBackslash()
    {
        if (_pos + 1 < _text.Length)
        {
            char n = _text[_pos + 1];
            if (n == '\n')
            {
                Add(new LineBreakInline { Hard = true });
                _pos += 2;
                SkipLeadingSpaces();
                return;
            }
            if (Entities.IsAsciiPunctuation(n)) { _buffer.Append(n); _pos += 2; return; }
        }
        _buffer.Append('\\');
        _pos++;
    }

    private void SkipLeadingSpaces()
    {
        while (_pos < _text.Length && _text[_pos] == ' ') _pos++;
    }

    private void ParseNewline()
    {
        // Hard break if the preceding text ends with two or more spaces.
        int spaces = 0;
        while (spaces < _buffer.Length && _buffer[_buffer.Length - 1 - spaces] == ' ') spaces++;
        if (spaces > 0) _buffer.Length -= spaces;
        Add(new LineBreakInline { Hard = spaces >= 2 });
        _pos++;
        SkipLeadingSpaces();
    }

    private void ParseCodeSpan()
    {
        int start = _pos;
        while (_pos < _text.Length && _text[_pos] == '`') _pos++;
        int run = _pos - start;
        int search = _pos;
        while (search < _text.Length)
        {
            int idx = _text.IndexOf('`', search);
            if (idx < 0) break;
            int e = idx;
            while (e < _text.Length && _text[e] == '`') e++;
            if (e - idx == run)
            {
                var content = _text[_pos..idx].Replace('\n', ' ');
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0)
                    content = content[1..^1];
                Add(new CodeInline(content));
                _pos = e;
                return;
            }
            search = e;
        }
        _buffer.Append('`', run); // no matching closer: literal backticks
    }

    private static bool IsWhite(char c) => char.IsWhiteSpace(c);
    private static bool IsPunct(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private void ParseDelimiterRun(char c)
    {
        int start = _pos;
        while (_pos < _text.Length && _text[_pos] == c) _pos++;
        int count = _pos - start;
        char before = start == 0 ? '\n' : _text[start - 1];
        char after = _pos >= _text.Length ? '\n' : _text[_pos];

        bool left = !IsWhite(after) && (!IsPunct(after) || IsWhite(before) || IsPunct(before));
        bool right = !IsWhite(before) && (!IsPunct(before) || IsWhite(after) || IsPunct(after));
        bool canOpen, canClose;
        if (c == '_')
        {
            canOpen = left && (!right || IsPunct(before));
            canClose = right && (!left || IsPunct(after));
        }
        else
        {
            canOpen = left;
            canClose = right;
        }
        if (c == '~' && count > 2) { canOpen = canClose = false; }

        var node = new TextInline(new string(c, count));
        Add(node);
        if (canOpen || canClose)
            _delims.Add(new Delimiter { Node = node, Ch = c, Count = count, OrigCount = count, CanOpen = canOpen, CanClose = canClose });
    }

    private void ProcessEmphasis(int bottom)
    {
        int ci = bottom;
        while (ci < _delims.Count)
        {
            var closer = _delims[ci];
            if (!closer.CanClose) { ci++; continue; }

            int oi = ci - 1;
            bool found = false;
            for (; oi >= bottom; oi--)
            {
                var o = _delims[oi];
                if (o.Ch != closer.Ch || !o.CanOpen) continue;
                if (closer.Ch == '~')
                {
                    if (o.Count == closer.Count) { found = true; break; }
                    continue;
                }
                bool ruleOf3 = (closer.CanOpen || o.CanClose)
                               && (o.OrigCount + closer.OrigCount) % 3 == 0
                               && !(o.OrigCount % 3 == 0 && closer.OrigCount % 3 == 0);
                if (!ruleOf3) { found = true; break; }
            }

            if (!found)
            {
                if (!closer.CanOpen) _delims.RemoveAt(ci);
                else ci++;
                continue;
            }

            var opener = _delims[oi];
            int use = closer.Ch == '~' ? closer.Count : (closer.Count >= 2 && opener.Count >= 2 ? 2 : 1);
            opener.Count -= use;
            closer.Count -= use;
            opener.Node.Text = opener.Node.Text[..opener.Count];
            closer.Node.Text = closer.Node.Text[..closer.Count];

            int oNode = _nodes.IndexOf(opener.Node);
            int cNode = _nodes.IndexOf(closer.Node);
            var inner = _nodes.GetRange(oNode + 1, cNode - oNode - 1);
            _nodes.RemoveRange(oNode + 1, cNode - oNode - 1);
            ContainerInline wrap = closer.Ch == '~' ? new StrikethroughInline() : use == 2 ? new StrongInline() : new EmphasisInline();
            wrap.Children = inner;
            _nodes.Insert(oNode + 1, wrap);

            _delims.RemoveRange(oi + 1, ci - oi - 1);
            ci = oi + 1;
            if (opener.Count == 0)
            {
                _nodes.Remove(opener.Node);
                _delims.RemoveAt(oi);
                ci--;
            }
            if (closer.Count == 0)
            {
                _nodes.Remove(closer.Node);
                _delims.RemoveAt(ci);
            }
        }
        if (bottom < _delims.Count) _delims.RemoveRange(bottom, _delims.Count - bottom);
    }

    private void ParseOpenBracket()
    {
        var fm = FootnoteRef().Match(_text, _pos);
        if (fm.Success)
        {
            var label = NormalizeLabel(fm.Groups[1].Value);
            if (_doc.Footnotes.TryGetValue(label, out var def))
            {
                if (def.Number == 0)
                {
                    _doc.FootnoteOrder.Add(def);
                    def.Number = _doc.FootnoteOrder.Count;
                }
                Add(new FootnoteReference { Label = label, Definition = def });
                _pos += fm.Length;
                return;
            }
        }
        Flush();
        var node = new TextInline("[");
        _nodes.Add(node);
        _brackets.Add(new Bracket { Node = node, DelimiterBottom = _delims.Count, TextStart = _pos + 1 });
        _pos++;
    }

    private void ParseCloseBracket()
    {
        if (_brackets.Count == 0) { _buffer.Append(']'); _pos++; return; }
        var opener = _brackets[^1];
        if (!opener.Active)
        {
            _brackets.RemoveAt(_brackets.Count - 1);
            _buffer.Append(']');
            _pos++;
            return;
        }

        int closePos = _pos;
        int p = _pos + 1;
        string? url = null, title = null;
        int end = -1;

        if (p < _text.Length && _text[p] == '(' && TryParseInlineLinkTail(p, out url, out title, out end)) { }
        else
        {
            string bracketText = _text[opener.TextStart..closePos];
            string? label = null;
            int afterLabel = p;
            if (p < _text.Length && _text[p] == '[')
            {
                int close = _text.IndexOf(']', p + 1);
                if (close > p)
                {
                    var inner = _text[(p + 1)..close];
                    label = inner.Trim().Length == 0 ? bracketText : inner;
                    afterLabel = close + 1;
                }
            }
            if (label != null && _doc.References.TryGetValue(NormalizeLabel(label), out var r1))
            {
                url = r1.Url; title = r1.Title; end = afterLabel;
            }
            else if (_doc.References.TryGetValue(NormalizeLabel(bracketText), out var r2))
            {
                url = r2.Url; title = r2.Title; end = p;
            }
        }

        if (url == null)
        {
            _brackets.RemoveAt(_brackets.Count - 1);
            _buffer.Append(']');
            _pos++;
            return;
        }

        Flush();
        int idx = _nodes.IndexOf(opener.Node);
        ProcessEmphasis(opener.DelimiterBottom);
        var children = _nodes.GetRange(idx + 1, _nodes.Count - idx - 1);
        _nodes.RemoveRange(idx, _nodes.Count - idx);
        _brackets.RemoveAt(_brackets.Count - 1);

        if (opener.Image)
        {
            _nodes.Add(new ImageInline { Url = url, Title = title, Alt = ToPlainText(Merge(children)) });
        }
        else
        {
            _nodes.Add(new LinkInline { Url = url, Title = title, Children = children });
            foreach (var b in _brackets) if (!b.Image) b.Active = false;
        }
        _pos = end;
    }

    private bool TryParseInlineLinkTail(int p, out string? url, out string? title, out int end)
    {
        url = null; title = null; end = -1;
        int q = p + 1;
        SkipWs(ref q);
        string dest;
        if (q < _text.Length && _text[q] == '<')
        {
            int close = q + 1;
            while (close < _text.Length && _text[close] != '>' && _text[close] != '\n' && _text[close] != '<')
            {
                if (_text[close] == '\\') close++;
                close++;
            }
            if (close >= _text.Length || _text[close] != '>') return Lenient(p, out url, out end);
            dest = _text[(q + 1)..close];
            q = close + 1;
        }
        else
        {
            int s = q, depth = 0;
            while (q < _text.Length)
            {
                char c = _text[q];
                if (c == '\\' && q + 1 < _text.Length && Entities.IsAsciiPunctuation(_text[q + 1])) { q += 2; continue; }
                if (char.IsWhiteSpace(c) || char.IsControl(c)) break;
                if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) break; depth--; }
                q++;
            }
            dest = _text[s..q];
        }

        int beforeTitle = q;
        SkipWs(ref q);
        if (q < _text.Length && q > beforeTitle && _text[q] is '"' or '\'' or '(')
        {
            char open = _text[q];
            char closeCh = open == '(' ? ')' : open;
            int t = q + 1;
            var sb = new StringBuilder();
            while (t < _text.Length && _text[t] != closeCh)
            {
                if (_text[t] == '\\' && t + 1 < _text.Length && Entities.IsAsciiPunctuation(_text[t + 1])) { sb.Append(_text[t + 1]); t += 2; continue; }
                sb.Append(_text[t]);
                t++;
            }
            if (t >= _text.Length) return Lenient(p, out url, out end);
            title = Entities.DecodeAndUnescape(sb.ToString());
            q = t + 1;
            SkipWs(ref q);
        }
        if (q >= _text.Length || _text[q] != ')') return Lenient(p, out url, out end);
        url = Entities.DecodeAndUnescape(dest);
        end = q + 1;
        return true;
    }

    /// <summary>Wiki-style fallback: [text](Page Name) with spaces in the destination.</summary>
    private bool Lenient(int p, out string? url, out int end)
    {
        url = null; end = -1;
        int close = _text.IndexOf(')', p + 1);
        int nl = _text.IndexOf('\n', p + 1);
        if (close < 0 || (nl >= 0 && nl < close)) return false;
        var inner = _text[(p + 1)..close].Trim();
        if (inner.Length == 0 || inner.IndexOfAny(['(', '[', '"', '<', '>']) >= 0) return false;
        url = Entities.DecodeAndUnescape(inner);
        end = close + 1;
        return true;
    }

    private void SkipWs(ref int q)
    {
        int newlines = 0;
        while (q < _text.Length && (_text[q] == ' ' || _text[q] == '\n'))
        {
            if (_text[q] == '\n' && ++newlines > 1) break;
            q++;
        }
    }

    private void ParseAngle()
    {
        var m = UriAutolink().Match(_text, _pos);
        if (m.Success)
        {
            var u = m.Groups[1].Value;
            Add(new LinkInline { Url = u, IsAutoLink = true, Children = [new TextInline(u)] });
            _pos += m.Length;
            return;
        }
        m = EmailAutolink().Match(_text, _pos);
        if (m.Success)
        {
            var e = m.Groups[1].Value;
            Add(new LinkInline { Url = "mailto:" + e, IsAutoLink = true, Children = [new TextInline(e)] });
            _pos += m.Length;
            return;
        }
        m = RawHtml().Match(_text, _pos);
        if (m.Success)
        {
            Add(new HtmlInline(m.Value));
            _pos += m.Length;
            return;
        }
        _buffer.Append('<');
        _pos++;
    }

    private void ParseEntity()
    {
        var m = Entity().Match(_text, _pos);
        if (m.Success && Entities.TryDecode(m.Groups[1].Value, out var v))
        {
            _buffer.Append(v);
            _pos += m.Length;
            return;
        }
        _buffer.Append('&');
        _pos++;
    }

    private bool TryExtendedAutolink()
    {
        if (_pos > 0)
        {
            char prev = _text[_pos - 1];
            if (!(char.IsWhiteSpace(prev) || prev is '*' or '_' or '~' or '(')) return false;
        }
        var m = ExtendedAutolink().Match(_text, _pos);
        if (!m.Success) return false;
        var link = m.Value;
        // trailing punctuation is not part of the link
        while (link.Length > 0)
        {
            char last = link[^1];
            if (last is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~' or '\'' or '"' or ';') { link = link[..^1]; continue; }
            if (last == ')' && link.Count(ch => ch == '(') < link.Count(ch => ch == ')')) { link = link[..^1]; continue; }
            break;
        }
        if (link.Length < 5 || link.EndsWith("://") || link.Equals("www.", StringComparison.OrdinalIgnoreCase)) return false;
        var href = link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + link : link;
        Add(new LinkInline { Url = href, IsAutoLink = true, Children = [new TextInline(link)] });
        _pos += link.Length;
        return true;
    }
}
