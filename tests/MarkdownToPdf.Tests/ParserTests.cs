using MarkdownToPdf.Markdown;

namespace MarkdownToPdf.Tests;

public class ParserTests
{
    private static MarkdownDocument P(string md) => MarkdownParser.Parse(md);

    [Theory]
    [InlineData("# Title", 1, "Title")]
    [InlineData("### Title ###", 3, "Title")]
    [InlineData("Title\n=====", 1, "Title")]
    [InlineData("Title\n-----", 2, "Title")]
    public void Headings(string md, int level, string text)
    {
        var h = Assert.IsType<Heading>(Assert.Single(P(md).Children));
        Assert.Equal(level, h.Level);
        Assert.Equal(text, h.PlainText);
    }

    [Fact]
    public void HeadingIdsAreGitHubSlugsAndUnique()
    {
        var doc = P("## Hello, World!\n## Hello, World!\n## WaitForContext");
        Assert.Equal(["hello-world", "hello-world-1", "waitforcontext"], doc.Headings.Select(h => h.Id));
    }

    [Fact]
    public void Emphasis()
    {
        var para = Assert.IsType<Paragraph>(P("*a* **b** ***c*** ~~d~~ _e_ snake_case_word").Children[0]);
        Assert.IsType<EmphasisInline>(para.Inlines[0]);
        Assert.IsType<StrongInline>(para.Inlines[2]);
        var c = para.Inlines[4];
        Assert.True(c is EmphasisInline { Children: [StrongInline] } or StrongInline { Children: [EmphasisInline] });
        Assert.IsType<StrikethroughInline>(para.Inlines[6]);
        Assert.IsType<EmphasisInline>(para.Inlines[8]);
        Assert.Equal(" snake_case_word", Assert.IsType<TextInline>(para.Inlines[9]).Text);
    }

    [Fact]
    public void CodeSpansAndEscapes()
    {
        var para = (Paragraph)P("``a ` b`` \\*x\\* `c`").Children[0];
        Assert.Equal("a ` b", Assert.IsType<CodeInline>(para.Inlines[0]).Code);
        Assert.Equal(" *x* ", Assert.IsType<TextInline>(para.Inlines[1]).Text);
        Assert.Equal("c", Assert.IsType<CodeInline>(para.Inlines[2]).Code);
    }

    [Fact]
    public void LinksImagesAndReferences()
    {
        var doc = P("[a](http://x.com \"T\") ![img](p.png) [ref] <https://auto.link> www.example.com\n\n[ref]: /url");
        var para = (Paragraph)doc.Children[0];
        var links = para.Inlines.OfType<LinkInline>().ToList();
        Assert.Equal("http://x.com", links[0].Url);
        Assert.Equal("T", links[0].Title);
        Assert.Equal("/url", links[1].Url);
        Assert.Equal("https://auto.link", links[2].Url);
        Assert.Equal("http://www.example.com", links[3].Url);
        Assert.Equal("p.png", para.Inlines.OfType<ImageInline>().Single().Url);
    }

    [Fact]
    public void WikiStyleLinkWithSpaces()
    {
        var para = (Paragraph)P("[World Map](World Map)").Children[0];
        Assert.Equal("World Map", Assert.IsType<LinkInline>(Assert.Single(para.Inlines)).Url);
    }

    [Fact]
    public void HardAndSoftBreaks()
    {
        var para = (Paragraph)P("a  \nb\nc\\\nd").Children[0];
        var breaks = para.Inlines.OfType<LineBreakInline>().Select(b => b.Hard).ToList();
        Assert.Equal([true, false, true], breaks);
    }

    [Fact]
    public void Lists()
    {
        var doc = P("- a\n- b\n  - nested\n\n1. one\n2. two\n\n- [x] done\n- [ ] todo");
        var ul = Assert.IsType<ListBlock>(doc.Children[0]);
        Assert.False(ul.Ordered);
        Assert.Equal(2, ul.Items.Count());
        Assert.IsType<ListBlock>(ul.Items.ElementAt(1).Children[1]);
        var ol = Assert.IsType<ListBlock>(doc.Children[1]);
        Assert.True(ol.Ordered);
        var tasks = Assert.IsType<ListBlock>(doc.Children[2]);
        Assert.Equal([true, false], tasks.Items.Select(i => i.Checked!.Value));
    }

    [Fact]
    public void LooseVersusTightLists()
    {
        Assert.True(((ListBlock)P("- a\n- b").Children[0]).Tight);
        Assert.False(((ListBlock)P("- a\n\n- b").Children[0]).Tight);
    }

    [Fact]
    public void FencedAndIndentedCode()
    {
        var doc = P("```python\nprint(1)\n\nx = 2\n```\n\n    indented\n    code");
        var fenced = Assert.IsType<CodeBlock>(doc.Children[0]);
        Assert.Equal("python", fenced.Language);
        Assert.Equal("print(1)\n\nx = 2", fenced.Code);
        Assert.Equal("indented\ncode", Assert.IsType<CodeBlock>(doc.Children[1]).Code);
    }

    [Fact]
    public void Tables()
    {
        var t = Assert.IsType<Table>(P("| a | b | c |\n|:--|:-:|--:|\n| 1 | `x\\|y` | 3 |\n| 4 |").Children[0]);
        Assert.Equal([TableAlignment.Left, TableAlignment.Center, TableAlignment.Right], t.Alignments);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("x|y", Assert.IsType<CodeInline>(t.Rows[0][1].Inlines[0]).Code);
        Assert.Empty(t.Rows[1][2].Inlines);
    }

    [Fact]
    public void BlockQuotesAndAlerts()
    {
        var doc = P("> quote\nlazy\n\n> [!WARNING]\n> careful");
        var q = Assert.IsType<BlockQuote>(doc.Children[0]);
        Assert.Contains("lazy", ((Paragraph)q.Children[0]).RawText);
        var alert = Assert.IsType<BlockQuote>(doc.Children[1]);
        Assert.Equal("warning", alert.AlertKind);
    }

    [Fact]
    public void FootnotesAreNumberedByFirstReference()
    {
        var doc = P("x[^b] y[^a]\n\n[^a]: A\n[^b]: B");
        Assert.Equal(["b", "a"], doc.FootnoteOrder.Select(f => f.Label));
        Assert.Equal(1, doc.Footnotes["b"].Number);
    }

    [Fact]
    public void FrontMatterAndPageBreaks()
    {
        var doc = P("---\ntitle: Hello\nauthor: Me\n---\n\nText\n\n<!-- pagebreak -->\n\nMore");
        Assert.Equal("Hello", doc.FrontMatter["title"]);
        Assert.Contains(doc.Children, b => b is PageBreak);
    }

    [Fact]
    public void EntitiesAndHtml()
    {
        var para = (Paragraph)P("&copy; &#65; &#x42; <b>bold</b>").Children[0];
        Assert.StartsWith("© A B ", ((TextInline)para.Inlines[0]).Text);
        Assert.Contains(para.Inlines, i => i is HtmlInline { Html: "<b>" });
    }

    [Fact]
    public void ThematicBreakIsNotAListItem()
    {
        var doc = P("* * *\n- item");
        Assert.IsType<ThematicBreak>(doc.Children[0]);
        Assert.IsType<ListBlock>(doc.Children[1]);
    }
}
