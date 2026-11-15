---
title: MarkdownToPdf Showcase
author: MarkdownToPdf
---

# MarkdownToPdf Showcase

This document exercises **every** Markdown element supported by the converter: *emphasis*, **strong**,
***both***, ~~strikethrough~~, `inline code`, [links](https://commonmark.org "CommonMark"), autolinks like
<https://github.github.com/gfm/> and www.example.com, footnotes[^1], and HTML such as <kbd>Ctrl</kbd>+<kbd>C</kbd>,
H<sub>2</sub>O, E = mc<sup>2</sup> and <u>underlined</u> text.  
This line follows a hard line break (two trailing spaces), and this one a backslash\
break. Entities: &copy; &trade; &mdash; &frac12; &#8364; &#x1F600;. Escapes: \*not emphasis\*, \`not code\`.

## Headings

### Third level heading
#### Fourth level heading
##### Fifth level heading
###### Sixth level heading

Setext Heading 2
----------------

## Lists

* Unordered item one
* Unordered item two with a much longer text that wraps onto the next line to check the hanging indentation of list content
  * Nested item
    * Deeper nested item
* Back to the first level

1. First ordered item
2. Second ordered item
   1. Nested ordered
   2. Another nested
7. Third item (only the first number of a list matters)

- [x] Completed task
- [ ] Open task
- [ ] Another open task with `code`

A loose list:

1. First paragraph of the item.

   Second paragraph of the same item.

2. Second item

## Block quotes

> Block quotes can contain *formatted* text and span several lines.
>
> > Nested quotes are supported too.
>
> - and lists
> - inside quotes

> [!NOTE]
> Useful information that users should know, even when skimming content.

> [!TIP]
> Helpful advice for doing things better or more easily.

> [!IMPORTANT]
> Key information users need to know to achieve their goal.

> [!WARNING]
> Urgent info that needs immediate user attention to avoid problems.

> [!CAUTION]
> Advises about risks or negative outcomes of certain actions.

## Code

Inline: use `dotnet tool install -g MarkdownToPdf.Tool` to install.

```csharp
// Convert a file with custom options
using MarkdownToPdf;

var options = new MarkdownPdfOptions();
options.Page.Size = "Letter";
options.Fonts.Body = "Segoe UI";
var result = new MarkdownPdfConverter(options).ConvertFile("README.md", "README.pdf");
Console.WriteLine($"Pages: {result.PageCount}"); /* block comment */
```

```python
@decorator
def fibonacci(n: int) -> list[int]:
    """Return the first n Fibonacci numbers."""
    a, b = 0, 1
    result = []
    for _ in range(n):
        result.append(a)
        a, b = b, a + b  # tuple assignment
    return result

print(fibonacci(10), True, None, 0x1F, 3.14)
```

```json
{
  "name": "markdowntopdf",
  "version": 1.0,
  "enabled": true,
  "tags": ["pdf", "markdown", null]
}
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- comment -->
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
</Project>
```

```bash
#!/bin/bash
for f in docs/*.md; do
  echo "Converting $f"   # comment
  markdowntopdf "$f" -o "${f%.md}.pdf" --toc
done
```

```diff
- removed line
+ added line
  unchanged line
```

    Indented code block
    with two lines

```
A very long line without a language that needs to be wrapped because it is far wider than the page content area allows, so it continues on the next visual line.
```

## Tables

| Left aligned | Centered | Right aligned |
|:-------------|:--------:|--------------:|
| `code` | **bold** | 1.00 |
| *italic* | text | 22.50 |
| A longer cell that will need to wrap inside its column when the table is wide | x | 333.33 |
| [link](#lists) | ~~strike~~ | 4 |

| Command | Description |
|---------|-------------|
| `Attack(obj)` | Attack mobile (parameter can be serial or alias). |
| `UseObject(obj)` | Sends use (double click) request for given object. |

## Images

![Sample image](sample.png "A generated PNG with transparency")

## Horizontal rule

Above the rule.

---

Below the rule.

## Unicode

Accents: àéîõü ÀÉÎÕÜ ñ ç ß. Greek: αβγδ. Arrows: ← → ↑ ↓. Math: ∑ ∞ ≤ ≥ ≠.
Korean: 안녕하세요. Japanese: こんにちは. Chinese: 你好. Symbols: ✓ ✗ ★ ♥ ☀.

## Links

Internal links: [jump to Tables](#tables), [jump to Code](#code). External: [GitHub](https://github.com).

Footnote with longer content[^long].

[^1]: This is a footnote.
[^long]: Footnotes can contain *formatting* and `code`, and they are collected at the end of the document.
