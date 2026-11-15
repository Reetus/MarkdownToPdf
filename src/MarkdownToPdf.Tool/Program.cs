using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MarkdownToPdf;
using MarkdownToPdf.Fonts;

namespace MarkdownToPdf.Tool;

internal static class Program
{
    private const string Usage = """
        markdowntopdf - convert Markdown to PDF (CommonMark + GitHub Flavored Markdown)

        Usage:
          markdowntopdf [options] <input...>

        Inputs may be files, directories (all *.md files, README/index first) or wildcards (docs/*.md).
        Multiple inputs are merged into one PDF; links between them become internal PDF links.

        Output:
          -o, --output <file>          Output PDF (default: <first input>.pdf)
          -c, --config <file>          Load options from a JSON file (command line flags override it)
              --init-config [file]     Write the default config (markdowntopdf.json) and exit

        Page:
          -p, --page-size <size>       A4 (default), Letter, Legal, A3, A5, B5, Tabloid, Executive, "210mm x 297mm"
              --landscape              Landscape orientation
          -m, --margins <spec>         CSS-like margins, e.g. "20mm", "1in 0.75in", "25mm 20mm 20mm 20mm"

        Fonts (comma separated candidates: installed family names, font file paths, sans-serif / serif / monospace):
              --font <families>        Body font            (default: Segoe UI, Inter, Helvetica Neue, Arial, ...)
              --heading-font <fams>    Heading font         (default: body font)
              --mono-font <families>   Code font            (default: Cascadia Mono, Consolas, JetBrains Mono, ...)
              --fallback-font <fams>   Fonts for missing characters (symbols, CJK, emoji)
              --font-size <pt>         Body size (default 10.5)
              --code-size <pt>         Code size (default 9)
              --line-height <factor>   Line height (default 1.5)
              --heading-weight <n>     400-900 (default 700; 600 = semibold)
              --list-fonts [filter]    List installed font families and exit

        Appearance:
              --theme <name>           default, github, ocean, forest, mono
              --accent <#rrggbb>       Accent colour (rules, bullets, H3)
              --link-color <#rrggbb>
              --code-theme <name>      light (default) or dark
              --line-numbers           Number code block lines
              --no-highlight           Disable syntax highlighting
              --no-wrap-code           Clip long code lines instead of wrapping
              --justify                Justify paragraphs

        Structure:
              --toc                    Generate a table of contents page
              --toc-depth <n>          Heading depth for the TOC (default 3)
              --toc-title <text>       TOC heading (default "Contents")
              --bookmark-depth <n>     Heading depth for PDF bookmarks (default 3, 0 = none)
              --cover                  Generate a cover page
              --title <text>           Document title (default: front matter / first H1)
              --subtitle <text>        Cover subtitle
              --author <text>          Author (cover + metadata)
              --subject <text>         PDF subject
              --keywords <text>        PDF keywords
              --header <l|c|r>         Header text: "left|center|right"; tokens {page} {pages} {title}
              --footer <l|c|r>         {author} {date} {section} {file}. Default footer: "|Page {page} of {pages}|"
              --no-header / --no-footer
              --page-break-h1          Start every H1 on a new page
              --page-break-level <n>   Start every heading of level n on a new page
              --no-file-breaks         Do not start each input file on a new page
              --keep-preamble          Keep repeated leading blocks of merged files
              --follow-links           Also include local Markdown files linked from the inputs
              --exclude <pattern>      Do not follow links to files matching pattern (repeatable)
              --remote-images          Download http(s) images
              --culture <name>         Culture for dates (e.g. en-GB)

        General:
          -q, --quiet                  Only print errors
          -v, --verbose                Print included files and timings
              --version                Print version
          -h, --help                   Show this help
        """;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("Run 'markdowntopdf --help' for usage.");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private static int Run(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine(Usage); return 2; }

        var inputs = new List<string>();
        string? output = null, config = null;
        bool quiet = false, verbose = false;
        var actions = new List<Action<MarkdownPdfOptions>>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new UsageException($"Missing value for {a}");
                return args[++i];
            }
            float NextFloat()
            {
                var v = Next();
                return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw new UsageException($"Invalid number '{v}' for {a}");
            }
            int NextInt()
            {
                var v = Next();
                return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException($"Invalid integer '{v}' for {a}");
            }
            void Opt(Action<MarkdownPdfOptions> act) => actions.Add(act);

            switch (a)
            {
                case "-h" or "--help" or "-?" or "/?": Console.WriteLine(Usage); return 0;
                case "--version":
                    Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0");
                    return 0;
                case "--init-config":
                {
                    var path = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "markdowntopdf.json";
                    if (File.Exists(path)) throw new UsageException($"{path} already exists.");
                    File.WriteAllText(path, new MarkdownPdfOptions().ToJson());
                    Console.WriteLine($"Wrote {path}");
                    return 0;
                }
                case "--list-fonts":
                {
                    var filter = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
                    foreach (var g in FontCatalog.Installed.GroupBy(f => f.DisplayFamily).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (filter != null && !g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                        var styles = string.Join(", ", g.OrderBy(f => f.Weight).ThenBy(f => f.Italic).Select(f => f.Subfamily + (f.EmbeddingRestricted ? " (no embedding)" : "")).Distinct());
                        Console.WriteLine($"{g.Key,-36} {styles}");
                    }
                    return 0;
                }
                case "-o" or "--output": output = Next(); break;
                case "-c" or "--config": config = Next(); break;
                case "-q" or "--quiet": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;

                case "-p" or "--page-size": { var v = Next(); Opt(o => o.Page.Size = v); break; }
                case "--landscape": Opt(o => o.Page.Landscape = true); break;
                case "-m" or "--margins" or "--margin": { var v = Next(); Opt(o => o.Page.Margins = v); break; }

                case "--font" or "--body-font": { var v = Next(); Opt(o => o.Fonts.Body = v); break; }
                case "--heading-font": { var v = Next(); Opt(o => o.Fonts.Heading = v); break; }
                case "--mono-font" or "--code-font": { var v = Next(); Opt(o => o.Fonts.Monospace = v); break; }
                case "--fallback-font": { var v = Next(); Opt(o => o.Fonts.Fallback = v + ", " + o.Fonts.Fallback); break; }
                case "--font-size": { var v = NextFloat(); Opt(o => o.Fonts.Size = v); break; }
                case "--code-size": { var v = NextFloat(); Opt(o => o.Fonts.CodeSize = v); break; }
                case "--line-height": { var v = NextFloat(); Opt(o => o.Fonts.LineHeight = v); break; }
                case "--heading-weight": { var v = NextInt(); Opt(o => o.Fonts.HeadingWeight = v); break; }

                case "--theme": { var v = Next(); Opt(o => o.Theme.Name = v); break; }
                case "--accent": { var v = Next(); Opt(o => o.Theme.Accent = v); break; }
                case "--link-color": { var v = Next(); Opt(o => o.Theme.Link = v); break; }
                case "--code-theme": { var v = Next(); Opt(o => o.Code.Theme = v); break; }
                case "--line-numbers": Opt(o => o.Code.LineNumbers = true); break;
                case "--no-highlight": Opt(o => o.Code.Highlight = false); break;
                case "--no-wrap-code": Opt(o => o.Code.Wrap = false); break;
                case "--justify": Opt(o => o.Theme.Justify = true); break;

                case "--toc": Opt(o => o.Toc.Enabled = true); break;
                case "--toc-depth": { var v = NextInt(); Opt(o => { o.Toc.Enabled = true; o.Toc.Depth = v; }); break; }
                case "--toc-title": { var v = Next(); Opt(o => o.Toc.Title = v); break; }
                case "--bookmark-depth": { var v = NextInt(); Opt(o => o.Toc.BookmarkDepth = v); break; }
                case "--cover": Opt(o => o.Cover.Enabled = true); break;
                case "--title": { var v = Next(); Opt(o => { o.Metadata.Title = v; o.Cover.Title = v; }); break; }
                case "--subtitle": { var v = Next(); Opt(o => o.Cover.Subtitle = v); break; }
                case "--author": { var v = Next(); Opt(o => { o.Metadata.Author = v; o.Cover.Author = v; }); break; }
                case "--subject": { var v = Next(); Opt(o => o.Metadata.Subject = v); break; }
                case "--keywords": { var v = Next(); Opt(o => o.Metadata.Keywords = v); break; }
                case "--header": { var v = Next(); Opt(o => o.Header = ParseHeaderFooter(v, o.Header)); break; }
                case "--footer": { var v = Next(); Opt(o => o.Footer = ParseHeaderFooter(v, o.Footer)); break; }
                case "--no-header": Opt(o => o.Header.Enabled = false); break;
                case "--no-footer": Opt(o => o.Footer.Enabled = false); break;
                case "--page-break-h1": Opt(o => o.Document.PageBreakBeforeH1 = true); break;
                case "--page-break-level": { var v = NextInt(); Opt(o => o.Document.PageBreakBeforeLevel = v); break; }
                case "--no-file-breaks": Opt(o => o.Document.PageBreakBetweenFiles = false); break;
                case "--keep-preamble": Opt(o => o.Document.StripRepeatedPreamble = false); break;
                case "--follow-links": Opt(o => o.Document.FollowLinks = true); break;
                case "--exclude": { var v = Next(); Opt(o => o.Document.FollowExclude.Add(v)); break; }
                case "--remote-images": Opt(o => o.Document.AllowRemoteImages = true); break;
                case "--culture": { var v = Next(); Opt(o => o.Metadata.Culture = v); break; }

                default:
                    if (a.StartsWith('-') && a.Length > 1 && !File.Exists(a)) throw new UsageException($"Unknown option {a}");
                    inputs.Add(a);
                    break;
            }
        }

        var files = ExpandInputs(inputs);
        if (files.Count == 0) throw new UsageException("No input files.");

        var options = config != null ? MarkdownPdfOptions.Load(config) : new MarkdownPdfOptions();
        foreach (var act in actions) act(options);

        output ??= Path.ChangeExtension(files[0], ".pdf");
        var sw = Stopwatch.StartNew();
        var converter = new MarkdownPdfConverter(options);
        if (!quiet) converter.Warning += w => Console.Error.WriteLine("warning: " + w);
        if (verbose) converter.Info += m => Console.WriteLine(m);

        var result = converter.ConvertFiles(files, output);
        if (!quiet)
            Console.WriteLine($"Created {Path.GetFullPath(output)} ({result.PageCount} pages from {result.Documents.Count} file{(result.Documents.Count == 1 ? "" : "s")}) in {sw.Elapsed.TotalSeconds:0.00}s");
        return 0;
    }

    private static HeaderFooterOptions ParseHeaderFooter(string spec, HeaderFooterOptions current)
    {
        var parts = spec.Split('|');
        string? P(int i) => i < parts.Length && parts[i].Length > 0 ? parts[i] : null;
        return new HeaderFooterOptions
        {
            Left = parts.Length == 1 ? null : P(0),
            Center = parts.Length == 1 ? P(0) : P(1),
            Right = P(2),
            Rule = current.Rule,
            Size = current.Size,
            SkipFirstPage = current.SkipFirstPage,
            Enabled = true,
        };
    }

    private static List<string> ExpandInputs(List<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var mds = Directory.GetFiles(input, "*.md").Concat(Directory.GetFiles(input, "*.markdown"))
                    .OrderBy(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant() switch { "readme" => 0, "index" => 1, _ => 2 })
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
                files.AddRange(mds);
            }
            else if (input.Contains('*') || input.Contains('?'))
            {
                var dir = Path.GetDirectoryName(input);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                var pattern = Path.GetFileName(input);
                if (!Directory.Exists(dir)) throw new UsageException($"Directory not found: {dir}");
                files.AddRange(Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(input)) files.Add(input);
            else throw new UsageException($"Input not found: {input}");
        }
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
