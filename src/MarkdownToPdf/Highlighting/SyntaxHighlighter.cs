using System.Text.RegularExpressions;

namespace MarkdownToPdf.Highlighting;

internal enum TokenKind
{
    Plain, Keyword, Type, String, Number, Comment, Function, Literal, Property, Tag, Attribute,
    Variable, Decorator, Operator, Preprocessor, Inserted, Deleted
}

internal readonly record struct Token( string Text, TokenKind Kind );

internal sealed class LanguageDefinition
{
    public string Name { get; init; } = "";
    public HashSet<string> Keywords { get; init; } = [];
    public HashSet<string> Types { get; init; } = [];
    public HashSet<string> Literals { get; init; } = [];
    public HashSet<string> Builtins { get; init; } = [];
    public string[] LineComments { get; init; } = [];
    public (string Open, string Close)[] BlockComments { get; init; } = [];
    public string StringDelimiters { get; init; } = "\"'";
    public bool TripleQuotes { get; init; }
    public bool CaseInsensitive { get; init; }
    public bool CapitalizedAreTypes { get; init; }
    public bool DollarVariables { get; init; }
    public bool AtDecorators { get; init; }
    public bool HashPreprocessor { get; init; }
    public bool BacktickStrings { get; init; }
    public bool KeyBeforeColonIsProperty { get; init; }
    public bool AllowDashInIdentifiers { get; init; }
}

/// <summary>A small, table-driven lexer producing coloured tokens for common languages.</summary>
internal static class SyntaxHighlighter
{
    private static HashSet<string> W( string words, bool ci = false ) =>
        new( words.Split( ' ', StringSplitOptions.RemoveEmptyEntries ), ci ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal );

    private static readonly LanguageDefinition Python = new()
    {
        Name = "python",
        Keywords = W( "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case" ),
        Literals = W( "True False None" ),
        Builtins = W( "print len range str int float list dict set tuple bool open isinstance super self cls enumerate zip map filter sorted min max sum abs any all type object" ),
        LineComments = ["#"],
        TripleQuotes = true,
        AtDecorators = true,
    };

    private const string CFamilyKeywords = "if else for while do switch case default break continue return goto try catch finally throw new delete this sizeof typeof";

    private static readonly LanguageDefinition CSharp = new()
    {
        Name = "csharp",
        Keywords = W( CFamilyKeywords + " abstract as base checked class const delegate enum event explicit extern fixed foreach implicit in interface internal is lock namespace operator out override params private protected public readonly ref sealed stackalloc static struct unchecked unsafe using virtual volatile async await var get set init value yield where when record required file scoped partial global nameof with and or not let from select group into orderby join on equals by" ),
        Types = W( "bool byte char decimal double float int long object sbyte short string uint ulong ushort void dynamic nint nuint Task List Dictionary IEnumerable Span" ),
        Literals = W( "true false null default" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        CapitalizedAreTypes = true,
        HashPreprocessor = true,
    };

    private static readonly LanguageDefinition C = new()
    {
        Name = "c",
        Keywords = W( CFamilyKeywords + " auto const constexpr extern register static struct typedef union volatile inline class namespace template typename using public private protected virtual override final friend operator explicit noexcept nullptr_t static_cast dynamic_cast reinterpret_cast const_cast co_await co_return co_yield concept requires enum" ),
        Types = W( "int char short long float double void bool unsigned signed size_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t wchar_t auto std string vector map" ),
        Literals = W( "true false NULL nullptr" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        HashPreprocessor = true,
    };

    private static readonly LanguageDefinition Java = new()
    {
        Name = "java",
        Keywords = W( CFamilyKeywords + " abstract assert class const enum extends final implements import instanceof interface native package private protected public static strictfp super synchronized throws transient volatile var record sealed permits yield fun val object companion data when is in out override open lateinit suspend internal init constructor" ),
        Types = W( "boolean byte char double float int long short void String Integer Object List Map Unit Any" ),
        Literals = W( "true false null" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        CapitalizedAreTypes = true,
        AtDecorators = true,
    };

    private static readonly LanguageDefinition JavaScript = new()
    {
        Name = "javascript",
        Keywords = W( CFamilyKeywords + " async await class const export extends from function import in instanceof let of static super var void with yield get set as implements interface package private protected public type enum namespace declare readonly abstract keyof infer is satisfies module" ),
        Types = W( "string number boolean any unknown never object symbol bigint Array Promise Record Map Set Date" ),
        Literals = W( "true false null undefined NaN Infinity" ),
        Builtins = W( "console window document Math JSON require module exports process" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        BacktickStrings = true,
        CapitalizedAreTypes = true,
        AtDecorators = true,
    };

    private static readonly LanguageDefinition Go = new()
    {
        Name = "go",
        Keywords = W( "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var" ),
        Types = W( "bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any" ),
        Literals = W( "true false nil iota" ),
        Builtins = W( "append cap close copy delete len make new panic print println recover" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        BacktickStrings = true,
    };

    private static readonly LanguageDefinition Rust = new()
    {
        Name = "rust",
        Keywords = W( "as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while" ),
        Types = W( "bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize String Vec Option Result Box" ),
        Literals = W( "true false None Some Ok Err" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        CapitalizedAreTypes = true,
        StringDelimiters = "\"",
    };

    private static readonly LanguageDefinition Bash = new()
    {
        Name = "bash",
        Keywords = W( "if then else elif fi case esac for select while until do done in function time coproc return exit break continue local export readonly declare unset shift source alias" ),
        Builtins = W( "echo cd ls pwd cat grep sed awk find mkdir rm cp mv chmod chown sudo apt yum git dotnet npm curl wget tar printf read test set" ),
        Literals = W( "true false" ),
        LineComments = ["#"],
        DollarVariables = true,
        AllowDashInIdentifiers = true,
    };

    private static readonly LanguageDefinition PowerShell = new()
    {
        Name = "powershell",
        Keywords = W( "begin break catch class continue data define do dynamicparam else elseif end exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow", ci: true ),
        Literals = W( "$true $false $null", ci: true ),
        LineComments = ["#"],
        BlockComments = [("<#", "#>")],
        DollarVariables = true,
        CaseInsensitive = true,
        AllowDashInIdentifiers = true,
    };

    private static readonly LanguageDefinition Sql = new()
    {
        Name = "sql",
        Keywords = W( "select from where and or not insert into values update set delete create table alter drop index view join inner left right outer full cross on as group by order having limit offset union all distinct case when then else end exists in is like between primary key foreign references default constraint unique with returning begin commit rollback transaction procedure function trigger declare if while return grant revoke top asc desc", ci: true ),
        Types = W( "int integer bigint smallint tinyint decimal numeric float real double varchar nvarchar char nchar text date datetime timestamp time boolean bit blob uuid serial", ci: true ),
        Literals = W( "null true false", ci: true ),
        Builtins = W( "count sum avg min max coalesce cast convert now getdate lower upper substring trim length", ci: true ),
        LineComments = ["--"],
        BlockComments = [("/*", "*/")],
        CaseInsensitive = true,
        StringDelimiters = "'\"",
    };

    private static readonly LanguageDefinition Json = new()
    {
        Name = "json",
        Literals = W( "true false null" ),
        LineComments = ["//"],
        BlockComments = [("/*", "*/")],
        StringDelimiters = "\"",
        KeyBeforeColonIsProperty = true,
    };

    private static readonly LanguageDefinition Yaml = new()
    {
        Name = "yaml",
        Literals = W( "true false null yes no on off ~" ),
        LineComments = ["#"],
        KeyBeforeColonIsProperty = true,
        AllowDashInIdentifiers = true,
    };

    private static readonly LanguageDefinition Css = new()
    {
        Name = "css",
        BlockComments = [("/*", "*/")],
        LineComments = [],
        KeyBeforeColonIsProperty = true,
        AllowDashInIdentifiers = true,
        Keywords = W( "!important @media @import @font-face @keyframes @supports @layer" ),
    };

    private static readonly LanguageDefinition Ini = new()
    {
        Name = "ini",
        LineComments = [";", "#"],
        Literals = W( "true false yes no on off" ),
        KeyBeforeColonIsProperty = true,
    };

    private static readonly LanguageDefinition Lua = new()
    {
        Name = "lua",
        Keywords = W( "and break do else elseif end for function goto if in local not or repeat return then until while" ),
        Literals = W( "true false nil" ),
        LineComments = ["--"],
        BlockComments = [("--[[", "]]")],
    };

    private static readonly LanguageDefinition Ruby = new()
    {
        Name = "ruby",
        Keywords = W( "alias and begin break case class def defined? do else elsif end ensure for if in module next not or redo rescue retry return self super then undef unless until when while yield require attr_accessor puts" ),
        Literals = W( "true false nil" ),
        LineComments = ["#"],
        CapitalizedAreTypes = true,
    };

    private static readonly LanguageDefinition Php = new()
    {
        Name = "php",
        Keywords = W( CFamilyKeywords + " abstract and array as class clone const declare echo elseif empty enddeclare endfor endforeach endif endswitch endwhile extends final fn foreach function global implements include include_once instanceof insteadof interface isset list match namespace or print private protected public readonly require require_once static trait unset use var xor yield" ),
        Literals = W( "true false null TRUE FALSE NULL" ),
        LineComments = ["//", "#"],
        BlockComments = [("/*", "*/")],
        DollarVariables = true,
    };

    private static readonly LanguageDefinition Dockerfile = new()
    {
        Name = "dockerfile",
        Keywords = W( "FROM RUN CMD LABEL EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL AS", ci: true ),
        LineComments = ["#"],
        DollarVariables = true,
        CaseInsensitive = true,
    };

    private static readonly Dictionary<string, LanguageDefinition> Languages = new( StringComparer.OrdinalIgnoreCase )
    {
        ["python"] = Python,
        ["py"] = Python,
        ["python3"] = Python,
        ["pyi"] = Python,
        ["gyp"] = Python,
        ["csharp"] = CSharp,
        ["cs"] = CSharp,
        ["c#"] = CSharp,
        ["fsharp"] = CSharp,
        ["c"] = C,
        ["cpp"] = C,
        ["c++"] = C,
        ["h"] = C,
        ["hpp"] = C,
        ["cc"] = C,
        ["cxx"] = C,
        ["objc"] = C,
        ["glsl"] = C,
        ["hlsl"] = C,
        ["java"] = Java,
        ["kotlin"] = Java,
        ["kt"] = Java,
        ["scala"] = Java,
        ["groovy"] = Java,
        ["swift"] = Java,
        ["dart"] = Java,
        ["javascript"] = JavaScript,
        ["js"] = JavaScript,
        ["jsx"] = JavaScript,
        ["mjs"] = JavaScript,
        ["cjs"] = JavaScript,
        ["typescript"] = JavaScript,
        ["ts"] = JavaScript,
        ["tsx"] = JavaScript,
        ["go"] = Go,
        ["golang"] = Go,
        ["rust"] = Rust,
        ["rs"] = Rust,
        ["bash"] = Bash,
        ["sh"] = Bash,
        ["shell"] = Bash,
        ["zsh"] = Bash,
        ["console"] = Bash,
        ["shellscript"] = Bash,
        ["makefile"] = Bash,
        ["make"] = Bash,
        ["powershell"] = PowerShell,
        ["ps1"] = PowerShell,
        ["pwsh"] = PowerShell,
        ["ps"] = PowerShell,
        ["batch"] = PowerShell,
        ["bat"] = PowerShell,
        ["cmd"] = PowerShell,
        ["sql"] = Sql,
        ["mysql"] = Sql,
        ["postgres"] = Sql,
        ["postgresql"] = Sql,
        ["tsql"] = Sql,
        ["sqlite"] = Sql,
        ["plsql"] = Sql,
        ["json"] = Json,
        ["jsonc"] = Json,
        ["json5"] = Json,
        ["yaml"] = Yaml,
        ["yml"] = Yaml,
        ["css"] = Css,
        ["scss"] = Css,
        ["less"] = Css,
        ["sass"] = Css,
        ["ini"] = Ini,
        ["toml"] = Ini,
        ["cfg"] = Ini,
        ["conf"] = Ini,
        ["properties"] = Ini,
        ["editorconfig"] = Ini,
        ["env"] = Ini,
        ["lua"] = Lua,
        ["ruby"] = Ruby,
        ["rb"] = Ruby,
        ["php"] = Php,
        ["dockerfile"] = Dockerfile,
        ["docker"] = Dockerfile,
    };

    private static readonly HashSet<string> MarkupLanguages = new( StringComparer.OrdinalIgnoreCase )
        { "xml", "html", "htm", "xhtml", "xaml", "svg", "csproj", "vbproj", "fsproj", "props", "targets", "nuspec", "config", "resx", "plist", "vue", "razor", "cshtml", "wsdl", "xsd", "xsl", "xslt" };

    private static readonly HashSet<string> DiffLanguages = new( StringComparer.OrdinalIgnoreCase ) { "diff", "patch", "udiff" };

    public static bool IsSupported( string? language ) =>
        language != null && ( Languages.ContainsKey( language ) || MarkupLanguages.Contains( language ) || DiffLanguages.Contains( language ) );

    /// <summary>Tokenises <paramref name="code"/> and returns the tokens split into lines.</summary>
    public static List<List<Token>> Highlight( string code, string? language )
    {
        List<Token> tokens;
        if ( language == null )
        {
            tokens = [new Token( code, TokenKind.Plain )];
        }
        else if ( MarkupLanguages.Contains( language ) )
        {
            tokens = LexMarkup( code );
        }
        else if ( DiffLanguages.Contains( language ) )
        {
            tokens = LexDiff( code );
        }
        else if ( Languages.TryGetValue( language, out var def ) )
        {
            tokens = Lex( code, def );
        }
        else
        {
            tokens = [new Token( code, TokenKind.Plain )];
        }

        return SplitLines( tokens );
    }

    private static List<List<Token>> SplitLines( List<Token> tokens )
    {
        var lines = new List<List<Token>> { new() };
        foreach ( var t in tokens )
        {
            var parts = t.Text.Split( '\n' );
            for ( var i = 0; i < parts.Length; i++ )
            {
                if ( i > 0 )
                {
                    lines.Add( [] );
                }

                if ( parts[i].Length > 0 )
                {
                    lines[^1].Add( new Token( parts[i], t.Kind ) );
                }
            }
        }
        return lines;
    }

    private static readonly Regex NumberRegex = new( @"\G(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|(?:\d[\d_]*\.?[\d_]*|\.\d[\d_]*)(?:[eE][+\-]?\d+)?)[a-zA-Z%]*", RegexOptions.Compiled );

    private static List<Token> Lex( string s, LanguageDefinition def )
    {
        var tokens = new List<Token>();
        var i = 0;
        var plainStart = -1;
        var lineStart = true;

        void FlushPlain( int end )
        {
            if ( plainStart >= 0 && end > plainStart )
            {
                tokens.Add( new Token( s[plainStart..end], TokenKind.Plain ) );
            }

            plainStart = -1;
        }
        void Emit( int start, int end, TokenKind kind )
        {
            FlushPlain( start );
            tokens.Add( new Token( s[start..end], kind ) );
        }

        while ( i < s.Length )
        {
            var c = s[i];

            if ( c == '\n' ) { if ( plainStart < 0 ) { plainStart = i; } i++; lineStart = true; continue; }
            if ( c == ' ' || c == '\t' ) { if ( plainStart < 0 ) { plainStart = i; } i++; continue; }

            // block comments
            var matched = false;
            foreach ( var (open, close) in def.BlockComments )
            {
                if ( string.CompareOrdinal( s, i, open, 0, open.Length ) == 0 )
                {
                    var end = s.IndexOf( close, i + open.Length, StringComparison.Ordinal );
                    end = end < 0 ? s.Length : end + close.Length;
                    Emit( i, end, TokenKind.Comment );
                    i = end;
                    matched = true;
                    break;
                }
            }
            if ( matched ) { lineStart = false; continue; }

            foreach ( var lc in def.LineComments )
            {
                if ( string.CompareOrdinal( s, i, lc, 0, lc.Length ) == 0 )
                {
                    // '#' in CSS-like/ini only at line start; '#' after '$' in bash is not a comment
                    if ( lc == "#" && def.DollarVariables && i > 0 && s[i - 1] == '$' )
                    {
                        continue;
                    }

                    var end = s.IndexOf( '\n', i );
                    if ( end < 0 )
                    {
                        end = s.Length;
                    }

                    Emit( i, end, TokenKind.Comment );
                    i = end;
                    matched = true;
                    break;
                }
            }
            if ( matched )
            {
                continue;
            }

            if ( def.HashPreprocessor && lineStart && c == '#' )
            {
                var end = s.IndexOf( '\n', i );
                if ( end < 0 )
                {
                    end = s.Length;
                }

                Emit( i, end, TokenKind.Preprocessor );
                i = end;
                continue;
            }
            lineStart = false;

            // strings
            if ( def.TripleQuotes && i + 2 < s.Length && ( c == '"' || c == '\'' ) && s[i + 1] == c && s[i + 2] == c )
            {
                var q = new string( c, 3 );
                var end = s.IndexOf( q, i + 3, StringComparison.Ordinal );
                end = end < 0 ? s.Length : end + 3;
                Emit( i, end, TokenKind.String );
                i = end;
                continue;
            }
            if ( def.StringDelimiters.Contains( c ) || ( def.BacktickStrings && c == '`' ) )
            {
                // Python/C# prefixes are already emitted as identifiers; fine.
                var multiline = c == '`';
                var verbatim = i > 0 && s[i - 1] == '@' && def == CSharp;
                var j = i + 1;
                while ( j < s.Length )
                {
                    if ( !verbatim && s[j] == '\\' && def.Name != "powershell" ) { j += 2; continue; }
                    if ( s[j] == c )
                    {
                        if ( verbatim && j + 1 < s.Length && s[j + 1] == c ) { j += 2; continue; }
                        j++;
                        break;
                    }
                    if ( s[j] == '\n' && !multiline && !verbatim )
                    {
                        break;
                    }

                    j++;
                }
                j = Math.Min( j, s.Length );
                // JSON / YAML keys
                var kind = TokenKind.String;
                if ( def.KeyBeforeColonIsProperty )
                {
                    var k = j;
                    while ( k < s.Length && s[k] == ' ' )
                    {
                        k++;
                    }

                    if ( k < s.Length && s[k] == ':' )
                    {
                        kind = TokenKind.Property;
                    }
                }
                Emit( i, j, kind );
                i = j;
                continue;
            }

            if ( def.DollarVariables && c == '$' && i + 1 < s.Length && ( char.IsLetter( s[i + 1] ) || s[i + 1] is '_' or '{' or '(' or '?' or '@' or '#' or '!' || char.IsDigit( s[i + 1] ) ) )
            {
                var j = i + 1;
                if ( s[j] == '{' ) { var e = s.IndexOf( '}', j ); j = e < 0 ? s.Length : e + 1; }
                else if ( s[j] == '(' ) { j++; }
                else if ( !char.IsLetterOrDigit( s[j] ) && s[j] != '_' )
                {
                    j++;
                }
                else { while ( j < s.Length && ( char.IsLetterOrDigit( s[j] ) || s[j] is '_' or ':' ) )
                    {
                        j++;
                    }
                }
                var word = s[i..j];
                Emit( i, j, def.Literals.Contains( word ) ? TokenKind.Literal : TokenKind.Variable );
                i = j;
                continue;
            }

            if ( def.AtDecorators && c == '@' && i + 1 < s.Length && char.IsLetter( s[i + 1] ) )
            {
                var j = i + 1;
                while ( j < s.Length && ( char.IsLetterOrDigit( s[j] ) || s[j] is '_' or '.' ) )
                {
                    j++;
                }

                Emit( i, j, TokenKind.Decorator );
                i = j;
                continue;
            }

            if ( char.IsDigit( c ) || ( c == '.' && i + 1 < s.Length && char.IsDigit( s[i + 1] ) ) )
            {
                var prevIdent = i > 0 && ( char.IsLetterOrDigit( s[i - 1] ) || s[i - 1] == '_' );
                var m = NumberRegex.Match( s, i );
                if ( !prevIdent && m.Success && m.Length > 0 )
                {
                    Emit( i, i + m.Length, TokenKind.Number );
                    i += m.Length;
                    continue;
                }
            }

            if ( char.IsLetter( c ) || c == '_' || ( c == '@' && def.Keywords.Overlaps( ["@media"] ) ) || ( c == '!' && def.Name == "css" ) || ( c == '-' && def.Name == "css" && i + 1 < s.Length && char.IsLetter( s[i + 1] ) ) )
            {
                var j = i + 1;
                while ( j < s.Length && ( char.IsLetterOrDigit( s[j] ) || s[j] == '_' || ( def.AllowDashInIdentifiers && s[j] == '-' && j + 1 < s.Length && char.IsLetter( s[j + 1] ) ) || ( def.Name == "ruby" && s[j] == '?' ) ) )
                {
                    j++;
                }

                var word = s[i..j];
                TokenKind kind;
                var k = j;
                while ( k < s.Length && s[k] == ' ' )
                {
                    k++;
                }

                var call = k < s.Length && s[k] == '(';
                var colonNext = k < s.Length && s[k] == ':' && ( k + 1 >= s.Length || s[k + 1] != ':' );
                if ( def.Keywords.Contains( word ) )
                {
                    kind = TokenKind.Keyword;
                }
                else if ( def.Literals.Contains( word ) )
                {
                    kind = TokenKind.Literal;
                }
                else if ( def.Types.Contains( word ) )
                {
                    kind = TokenKind.Type;
                }
                else if ( def.KeyBeforeColonIsProperty && colonNext )
                {
                    kind = TokenKind.Property;
                }
                else if ( call )
                {
                    kind = TokenKind.Function;
                }
                else if ( def.Builtins.Contains( word ) )
                {
                    kind = TokenKind.Function;
                }
                else if ( def.CapitalizedAreTypes && char.IsUpper( word[0] ) && word.Length > 1 && word.Any( char.IsLower ) )
                {
                    kind = TokenKind.Type;
                }
                else if ( def.Name == "powershell" && word.Contains( '-' ) && char.IsUpper( word[0] ) )
                {
                    kind = TokenKind.Function;
                }
                else
                {
                    kind = TokenKind.Plain;
                }

                if ( kind == TokenKind.Plain ) { if ( plainStart < 0 )
                    {
                        plainStart = i;
                    }
                }
                else
                {
                    Emit( i, j, kind );
                }

                i = j;
                continue;
            }

            if ( "+-*/%=<>!&|^~?:".Contains( c ) )
            {
                var j = i + 1;
                while ( j < s.Length && "+-*/%=<>!&|^~?:".Contains( s[j] ) && j - i < 3 )
                {
                    j++;
                }

                Emit( i, j, TokenKind.Operator );
                i = j;
                continue;
            }

            if ( plainStart < 0 )
            {
                plainStart = i;
            }

            i++;
        }
        FlushPlain( s.Length );
        return tokens;
    }

    private static List<Token> LexMarkup( string s )
    {
        var tokens = new List<Token>();
        var i = 0;
        while ( i < s.Length )
        {
            if ( string.CompareOrdinal( s, i, "<!--", 0, 4 ) == 0 )
            {
                var e = s.IndexOf( "-->", i + 4, StringComparison.Ordinal );
                e = e < 0 ? s.Length : e + 3;
                tokens.Add( new( s[i..e], TokenKind.Comment ) );
                i = e;
                continue;
            }
            if ( string.CompareOrdinal( s, i, "<![CDATA[", 0, 9 ) == 0 )
            {
                var e = s.IndexOf( "]]>", i, StringComparison.Ordinal );
                e = e < 0 ? s.Length : e + 3;
                tokens.Add( new( s[i..e], TokenKind.String ) );
                i = e;
                continue;
            }
            if ( s[i] == '<' )
            {
                var j = i + 1;
                if ( j < s.Length && ( s[j] == '/' || s[j] == '?' || s[j] == '!' ) )
                {
                    j++;
                }

                var nameStart = j;
                while ( j < s.Length && ( char.IsLetterOrDigit( s[j] ) || s[j] is ':' or '-' or '_' or '.' ) )
                {
                    j++;
                }

                tokens.Add( new( s[i..nameStart], TokenKind.Plain ) );
                if ( j > nameStart )
                {
                    tokens.Add( new( s[nameStart..j], TokenKind.Tag ) );
                }

                i = j;
                // attributes
                while ( i < s.Length && s[i] != '>' )
                {
                    var c = s[i];
                    if ( c == '"' || c == '\'' )
                    {
                        var e = s.IndexOf( c, i + 1 );
                        e = e < 0 ? s.Length : e + 1;
                        tokens.Add( new( s[i..e], TokenKind.String ) );
                        i = e;
                    }
                    else if ( char.IsLetter( c ) || c == '_' || c == ':' )
                    {
                        var e = i;
                        while ( e < s.Length && ( char.IsLetterOrDigit( s[e] ) || s[e] is ':' or '-' or '_' or '.' ) )
                        {
                            e++;
                        }

                        tokens.Add( new( s[i..e], TokenKind.Attribute ) );
                        i = e;
                    }
                    else { tokens.Add( new( c.ToString(), TokenKind.Plain ) ); i++; }
                }
                if ( i < s.Length ) { tokens.Add( new( ">", TokenKind.Plain ) ); i++; }
                continue;
            }
            var next = s.IndexOf( '<', i );
            if ( next < 0 )
            {
                next = s.Length;
            }

            tokens.Add( new( s[i..next], s[i] == '&' ? TokenKind.Literal : TokenKind.Plain ) );
            i = next;
        }
        return tokens;
    }

    private static List<Token> LexDiff( string s )
    {
        var tokens = new List<Token>();
        var lines = s.Split( '\n' );
        for ( var i = 0; i < lines.Length; i++ )
        {
            var l = lines[i];
            var kind = l.StartsWith( "+++" ) || l.StartsWith( "---" ) ? TokenKind.Keyword
                : l.StartsWith( '+' ) ? TokenKind.Inserted
                : l.StartsWith( '-' ) ? TokenKind.Deleted
                : l.StartsWith( "@@" ) ? TokenKind.Function
                : l.StartsWith( "diff " ) || l.StartsWith( "index " ) ? TokenKind.Comment
                : TokenKind.Plain;
            tokens.Add( new( l + ( i < lines.Length - 1 ? "\n" : "" ), kind ) );
        }
        return tokens;
    }
}
