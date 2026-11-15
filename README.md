# MarkdownToPdf

A Markdown to PDF converter for .NET 11 with **no external dependencies**: no browser, no native libraries
and no NuGet packages. It parses Markdown itself, lays the content out itself, and writes the PDF itself,
embedding subsetted TrueType fonts.

It ships as two packages:

| Package | What |
|---|---|
| `Reetus.MarkdownToPdf` | Class library (`MarkdownPdfConverter`, namespace `MarkdownToPdf`) |
| `MarkdownToPdf.Tool` | .NET global tool, command `markdowntopdf` |

## Features

- **CommonMark + GitHub Flavored Markdown**: ATX/Setext headings, paragraphs, emphasis, strong, strikethrough,
  inline code, fenced and indented code blocks, block quotes, nested ordered/unordered lists, task lists,
  tables with column alignment, thematic breaks, links (inline, reference, autolinks, `www.` links), images,
  footnotes, hard/soft breaks, entities, backslash escapes, YAML front matter, GitHub alerts (`> [!NOTE]`).
- **HTML subset**: `<br>`, `<b>`, `<i>`, `<u>`, `<s>`, `<code>`, `<kbd>`, `<sup>`, `<sub>`, `<a href|name|id>`,
  `<img src width height>`, `align="center"` blocks. Page breaks via `<!-- pagebreak -->` or `\newpage`.
- **Syntax highlighting** for C#, C/C++, Java/Kotlin, JavaScript/TypeScript, Python, Go, Rust, Bash, PowerShell,
  SQL, JSON, YAML, XML/HTML, CSS, INI/TOML, Lua, Ruby, PHP, Dockerfile and diff. Light and dark themes,
  optional line numbers, wrapping of long lines.
- **Fonts**: uses installed fonts (Windows, macOS, Linux) or font files, embedded as subsets with ToUnicode
  maps, so text can be searched and copied. Per-character fallback handles symbols, CJK and emoji.
  Missing bold/italic faces are synthesized. Falls back to the PDF standard fonts when nothing is installed.
- **Layout**: A4, Letter, Legal, A3, A5, B5, Tabloid, Executive or custom sizes, portrait or landscape.
  Configurable margins, headers and footers with tokens, keep-with-next for headings and `Label:` paragraphs,
  orphan/widow control, table headers repeated across pages, and code blocks/quotes that split across pages.
- **Navigation**: PDF bookmarks (outline), optional table of contents page with page numbers, optional cover
  page, and clickable internal links. Links between merged files become internal links.
- **Multi-file**: merge several files into one PDF, or let `--follow-links` pull in every local Markdown file
  linked from the input. A preamble that each file repeats (title, "generated on" banner) is removed automatically.
- **Images**: PNG (all colour types, bit depths, interlacing, transparency), JPEG (incl. CMYK), BMP, `data:` URIs,
  and optionally remote images.

## Command line

```bash
dotnet tool install -g MarkdownToPdf.Tool        # from a feed, or:
dotnet tool install -g MarkdownToPdf.Tool --add-source ./artifacts/packages

markdowntopdf README.md                           # -> README.pdf
markdowntopdf docs/ -o manual.pdf --toc --cover --title "Manual" --author "Me"
markdowntopdf index.md --follow-links -p Letter --font "Georgia" --mono-font "Consolas" --code-theme dark
markdowntopdf notes.md --header "{title}||{section}" --footer "{file}||{page} / {pages}"
markdowntopdf --list-fonts segoe
markdowntopdf --init-config                        # writes markdowntopdf.json with every option
markdowntopdf book.md -c markdowntopdf.json
```

Run `markdowntopdf --help` for all options. Header/footer tokens: `{page}`, `{pages}`, `{title}`, `{author}`,
`{date}`, `{section}` (current level-2 heading), `{file}`.

## Library

```csharp
using MarkdownToPdf;

var options = new MarkdownPdfOptions();
options.Page.Size = "Letter";                 // or "A4", "210mm x 297mm"
options.Page.Margins = "20mm";
options.Fonts.Body = "Segoe UI, Arial";       // family names, file paths, or sans-serif/serif/monospace
options.Fonts.Monospace = "Cascadia Mono, Consolas";
options.Fonts.Size = 11;
options.Theme.Name = "github";                // default, github, ocean, forest, mono
options.Code.Theme = "dark";
options.Toc.Enabled = true;
options.Document.FollowLinks = true;

var converter = new MarkdownPdfConverter(options);
converter.Warning += Console.Error.WriteLine;

ConversionResult result = converter.ConvertFile("README.md", "README.pdf");
byte[] pdf = converter.ConvertMarkdown("# Hello\n\nWorld");
converter.Convert([MarkdownSource.FromFile("a.md"), MarkdownSource.FromText("# Appendix")], stream);
```

Options can also be loaded from JSON with `MarkdownPdfOptions.Load("options.json")`.

## ClassicAssist command reference

`scripts/Build-ClassicAssistPdf.ps1` runs ClassicAssist's `ExportCommands.exe` in a temporary copy of the
binaries, then converts `Macro-Commands.md` together with every category file it links to into one PDF
(`artifacts/ClassicAssist-Macro-Commands.pdf`). Use `-Locale ko-KR` (or it-IT, pl-PL, cs-CZ) for the
translated reference.

## Building

```bash
dotnet build MarkdownToPdf.slnx
dotnet test  MarkdownToPdf.slnx
dotnet pack  MarkdownToPdf.slnx -c Release       # -> artifacts/packages
```

## License

[MIT](LICENSE)
