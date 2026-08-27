// Generated from tree-sitter-language-pack 1.15.8's `extensions_generated.rs`, itself generated
// from that crate's `sources/language_definitions.json`. Regenerate both together after a bump.

namespace Xberg.Internal.Code;

/// <summary>
/// Which programming language a file is written in, decided the way
/// <c>tree_sitter_language_pack</c> decides it: by file extension, or by the shebang line when
/// the extension says nothing.
/// </summary>
/// <remarks>
/// Only the detection half of that crate is reproduced here. The grammars themselves are C, and
/// the port has no counterpart for them — see <c>CodeExtractor</c> for what that costs and what
/// it does not.
/// </remarks>
internal static class CodeLanguages
{
    /// <summary>Extension (lowercase, no dot) to language name.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.Ordinal)
    {
        ["cls"] = "abl",
        ["p"] = "abl",
        ["w"] = "abl",
        ["abnf"] = "abnf",
        ["as"] = "actionscript",
        ["ada"] = "ada",
        ["adb"] = "ada",
        ["ads"] = "ada",
        ["agda"] = "agda",
        ["ak"] = "aiken",
        ["al"] = "al",
        ["trigger"] = "apex",
        ["applescript"] = "applescript",
        ["scpt"] = "applescript",
        ["ino"] = "arduino",
        ["adoc"] = "asciidoc",
        ["asciidoc"] = "asciidoc",
        ["asm"] = "asm",
        ["s"] = "asm",
        ["astro"] = "astro",
        ["avdl"] = "avro",
        ["awk"] = "awk",
        ["bal"] = "ballerina",
        ["bash"] = "bash",
        ["sh"] = "bash",
        ["bat"] = "batch",
        ["cmd"] = "batch",
        ["beancount"] = "beancount",
        ["bib"] = "bibtex",
        ["bicep"] = "bicep",
        ["bb"] = "bitbake",
        ["bbappend"] = "bitbake",
        ["bbclass"] = "bitbake",
        ["blade"] = "blade",
        ["bt"] = "bpftrace",
        ["brs"] = "brightscript",
        ["bsl"] = "bsl",
        ["c"] = "c",
        ["h"] = "c",
        ["c3"] = "c3",
        ["c3i"] = "c3",
        ["c3t"] = "c3",
        ["caddyfile"] = "caddy",
        ["cairo"] = "cairo",
        ["capnp"] = "capnp",
        ["cedar"] = "cedar",
        ["cedarschema"] = "cedarschema",
        ["cel"] = "cel",
        ["cfc"] = "cfml",
        ["chatito"] = "chatito",
        ["ck"] = "chuck",
        ["circom"] = "circom",
        ["clar"] = "clarity",
        ["clj"] = "clojure",
        ["cljc"] = "clojure",
        ["cljs"] = "clojure",
        ["cmake"] = "cmake",
        ["cbl"] = "cobol",
        ["cob"] = "cobol",
        ["cobol"] = "cobol",
        ["cl"] = "commonlisp",
        ["lisp"] = "commonlisp",
        ["cook"] = "cooklang",
        ["corn"] = "corn",
        ["cpon"] = "cpon",
        ["cc"] = "cpp",
        ["cpp"] = "cpp",
        ["cxx"] = "cpp",
        ["hpp"] = "cpp",
        ["hxx"] = "cpp",
        ["cr"] = "crystal",
        ["cs"] = "csharp",
        ["css"] = "css",
        ["cst"] = "cst",
        ["csv"] = "csv",
        ["cu"] = "cuda",
        ["cuda"] = "cuda",
        ["cue"] = "cue",
        ["cylc"] = "cylc",
        ["cql"] = "cypher",
        ["cypher"] = "cypher",
        ["pxd"] = "cython",
        ["pxi"] = "cython",
        ["pyx"] = "cython",
        ["d"] = "d",
        ["d2"] = "d2",
        ["dart"] = "dart",
        ["desktop"] = "desktop",
        ["dts"] = "devicetree",
        ["dtsi"] = "devicetree",
        ["dhall"] = "dhall",
        ["diff"] = "diff",
        ["patch"] = "diff",
        ["dj"] = "djot",
        ["dockerfile"] = "dockerfile",
        ["dot"] = "dot",
        ["gv"] = "dot",
        ["dtd"] = "dtd",
        ["ebnf"] = "ebnf",
        ["edoc"] = "edoc",
        ["eds"] = "eds",
        ["eex"] = "eex",
        ["leex"] = "eex",
        ["e"] = "eiffel",
        ["el"] = "elisp",
        ["ex"] = "elixir",
        ["exs"] = "elixir",
        ["elm"] = "elm",
        ["lc"] = "elsa",
        ["elv"] = "elvish",
        ["erb"] = "embeddedtemplate",
        ["enforce"] = "enforce",
        ["erl"] = "erlang",
        ["hrl"] = "erlang",
        ["fsd"] = "facility",
        ["dsp"] = "faust",
        ["fnl"] = "fennel",
        ["fidl"] = "fidl",
        ["fir"] = "firrtl",
        ["fish"] = "fish",
        ["fbs"] = "flatbuffers",
        ["ftl"] = "fluent",
        ["4th"] = "forth",
        ["fth"] = "forth",
        ["f"] = "fortran",
        ["f03"] = "fortran",
        ["f08"] = "fortran",
        ["f90"] = "fortran",
        ["f95"] = "fortran",
        ["fs"] = "fsharp",
        ["fsx"] = "fsharp",
        ["fsi"] = "fsharp_signature",
        ["fc"] = "func",
        ["fusion"] = "fusion",
        ["g"] = "gap",
        ["gi"] = "gap",
        ["cnc"] = "gcode",
        ["gco"] = "gcode",
        ["gcode"] = "gcode",
        ["nc"] = "gcode",
        ["ngc"] = "gcode",
        ["tap"] = "gcode",
        ["gd"] = "gdscript",
        ["gdshader"] = "gdshader",
        ["feature"] = "gherkin",
        ["gitattributes"] = "gitattributes",
        ["gitignore"] = "gitignore",
        ["gleam"] = "gleam",
        ["hbs"] = "glimmer",
        ["glsl"] = "glsl",
        ["gn"] = "gn",
        ["gni"] = "gn",
        ["gnuplot"] = "gnuplot",
        ["gp"] = "gnuplot",
        ["plt"] = "gnuplot",
        ["go"] = "go",
        ["tres"] = "godot_resource",
        ["tscn"] = "godot_resource",
        ["mod"] = "gomod",
        ["gotmpl"] = "gotmpl",
        ["gql"] = "graphql",
        ["graphql"] = "graphql",
        ["gren"] = "gren",
        ["gradle"] = "groovy",
        ["groovy"] = "groovy",
        ["hack"] = "hack",
        ["haml"] = "haml",
        ["hare"] = "hare",
        ["hs"] = "haskell",
        ["hx"] = "haxe",
        ["hcl"] = "hcl",
        ["heex"] = "heex",
        ["hjson"] = "hjson",
        ["hlsl"] = "hlsl",
        ["hocon"] = "hocon",
        ["hoon"] = "hoon",
        ["htm"] = "html",
        ["html"] = "html",
        ["http"] = "http",
        ["hurl"] = "hurl",
        ["idl"] = "idl",
        ["idr"] = "idris",
        ["cfg"] = "ini",
        ["ini"] = "ini",
        ["ispc"] = "ispc",
        ["jai"] = "jai",
        ["janet"] = "janet",
        ["java"] = "java",
        ["cjs"] = "javascript",
        ["js"] = "javascript",
        ["jsx"] = "javascript",
        ["mjs"] = "javascript",
        ["j2"] = "jinja2",
        ["jinja2"] = "jinja2",
        ["jjdescription"] = "jjdescription",
        ["jq"] = "jq",
        ["json"] = "json",
        ["json5"] = "json5",
        ["jsonnet"] = "jsonnet",
        ["libsonnet"] = "jsonnet",
        ["jl"] = "julia",
        ["just"] = "just",
        ["k"] = "kcl",
        ["kdl"] = "kdl",
        ["kk"] = "koka",
        ["kt"] = "kotlin",
        ["kts"] = "kotlin",
        ["koto"] = "koto",
        ["kql"] = "kusto",
        ["lalrpop"] = "lalrpop",
        ["tex"] = "latex",
        ["lean"] = "lean",
        ["journal"] = "ledger",
        ["ldg"] = "ledger",
        ["ledger"] = "ledger",
        ["leo"] = "leo",
        ["less"] = "less",
        ["lds"] = "linkerscript",
        ["liquid"] = "liquid",
        ["ll"] = "llvm",
        ["mir"] = "llvm_mir",
        ["lua"] = "lua",
        ["luau"] = "luau",
        ["magik"] = "magik",
        ["makefile"] = "make",
        ["mk"] = "make",
        ["markdown"] = "markdown",
        ["md"] = "markdown",
        ["matlab"] = "matlab",
        ["mly"] = "menhir",
        ["mermaid"] = "mermaid",
        ["mmd"] = "mermaid",
        ["meson"] = "meson",
        ["mlir"] = "mlir",
        ["mojo"] = "mojo",
        ["mbt"] = "moonbit",
        ["mbti"] = "moonbit",
        ["mo"] = "motoko",
        ["move"] = "move",
        ["nasm"] = "nasm",
        ["axi"] = "netlinx",
        ["axs"] = "netlinx",
        ["conf"] = "nginx",
        ["nginx"] = "nginx",
        ["ncl"] = "nickel",
        ["nim"] = "nim",
        ["nims"] = "nim",
        ["ninja"] = "ninja",
        ["nix"] = "nix",
        ["norg"] = "norg",
        ["nqc"] = "nqc",
        ["nu"] = "nushell",
        ["m"] = "objc",
        ["ml"] = "ocaml",
        ["mli"] = "ocaml_interface",
        ["mll"] = "ocamllex",
        ["odin"] = "odin",
        ["scad"] = "openscad",
        ["org"] = "org",
        ["pas"] = "pascal",
        ["pem"] = "pem",
        ["domain"] = "penrose",
        ["style"] = "penrose",
        ["substance"] = "penrose",
        ["pl"] = "perl",
        ["pm"] = "perl",
        ["pgn"] = "pgn",
        ["php"] = "php",
        ["pi"] = "picat",
        ["picat"] = "picat",
        ["pkl"] = "pkl",
        ["iuml"] = "plantuml",
        ["plantuml"] = "plantuml",
        ["puml"] = "plantuml",
        ["po"] = "po",
        ["pot"] = "po",
        ["filter"] = "poe_filter",
        ["pony"] = "pony",
        ["pgsql"] = "postgres",
        ["psql"] = "postgres",
        ["eps"] = "postscript",
        ["ps"] = "postscript",
        ["ps1"] = "powershell",
        ["psd1"] = "powershell",
        ["psm1"] = "powershell",
        ["prisma"] = "prisma",
        ["pro"] = "prolog",
        ["pml"] = "promela",
        ["promql"] = "promql",
        ["properties"] = "properties",
        ["proto"] = "proto",
        ["prql"] = "prql",
        ["psv"] = "psv",
        ["pug"] = "pug",
        ["pp"] = "puppet",
        ["purs"] = "purescript",
        ["py"] = "python",
        ["pyi"] = "python",
        ["pyw"] = "python",
        ["ql"] = "ql",
        ["qml"] = "qmljs",
        ["r"] = "r",
        ["rkt"] = "racket",
        ["rasi"] = "rasi",
        ["cshtml"] = "razor",
        ["razor"] = "razor",
        ["rbs"] = "rbs",
        ["re"] = "re2c",
        ["rei"] = "reason",
        ["rego"] = "rego",
        ["res"] = "rescript",
        ["resi"] = "rescript",
        ["robot"] = "robot",
        ["roc"] = "roc",
        ["ron"] = "ron",
        ["rst"] = "rst",
        ["rtf"] = "rtf",
        ["rb"] = "ruby",
        ["rs"] = "rust",
        ["sas"] = "sas",
        ["scala"] = "scala",
        ["scfg"] = "scfg",
        ["scm"] = "scheme",
        ["scss"] = "scss",
        ["sflog"] = "sflog",
        ["slang"] = "slang",
        ["slim"] = "slim",
        ["slint"] = "slint",
        ["smali"] = "smali",
        ["st"] = "smalltalk",
        ["smithy"] = "smithy",
        ["fun"] = "sml",
        ["sig"] = "sml",
        ["sml"] = "sml",
        ["smk"] = "snakemake",
        ["stt"] = "snl",
        ["sol"] = "solidity",
        ["soql"] = "soql",
        ["sosl"] = "sosl",
        ["dl"] = "souffle",
        ["inc"] = "sourcepawn",
        ["sp"] = "sourcepawn",
        ["sparql"] = "sparql",
        ["zed"] = "spicedb",
        ["sql"] = "sql",
        ["bq"] = "sql_bigquery",
        ["nut"] = "squirrel",
        ["squirrel"] = "squirrel",
        ["stan"] = "stan",
        ["bzl"] = "starlark",
        ["star"] = "starlark",
        ["strace"] = "strace",
        ["shtml"] = "superhtml",
        ["svelte"] = "svelte",
        ["sw"] = "sway",
        ["swift"] = "swift",
        ["sxhkdrc"] = "sxhkdrc",
        ["sysml"] = "sysml",
        ["stp"] = "systemtap",
        ["stpm"] = "systemtap",
        ["sv"] = "systemverilog",
        ["svh"] = "systemverilog",
        ["cmm"] = "t32",
        ["cmmt"] = "t32",
        ["t32"] = "t32",
        ["td"] = "tablegen",
        ["tact"] = "tact",
        ["task"] = "task",
        ["tcl"] = "tcl",
        ["tl"] = "teal",
        ["templ"] = "templ",
        ["tera"] = "tera",
        ["tf"] = "terraform",
        ["tfvars"] = "terraform",
        ["pbtxt"] = "textproto",
        ["textproto"] = "textproto",
        ["thrift"] = "thrift",
        ["tla"] = "tlaplus",
        ["todotxt"] = "todotxt",
        ["toml"] = "toml",
        ["tsv"] = "tsv",
        ["tsx"] = "tsx",
        ["ttl"] = "turtle",
        ["twig"] = "twig",
        ["cts"] = "typescript",
        ["mts"] = "typescript",
        ["ts"] = "typescript",
        ["tsp"] = "typespec",
        ["tsconfig"] = "typoscript",
        ["typoscript"] = "typoscript",
        ["typst"] = "typst",
        ["u"] = "unison",
        ["tal"] = "uxntal",
        ["v"] = "v",
        ["vala"] = "vala",
        ["vapi"] = "vala",
        ["vb"] = "vb",
        ["vto"] = "vento",
        ["verilog"] = "verilog",
        ["vhd"] = "vhdl",
        ["vhdl"] = "vhdl",
        ["tape"] = "vhs",
        ["vim"] = "vim",
        ["txt"] = "vimdoc",
        ["vrl"] = "vrl",
        ["vue"] = "vue",
        ["wast"] = "wast",
        ["wat"] = "wat",
        ["wdl"] = "wdl",
        ["wgsl"] = "wgsl",
        ["wit"] = "wit",
        ["wl"] = "wolfram",
        ["wls"] = "wolfram",
        ["xit"] = "xit",
        ["xml"] = "xml",
        ["xsl"] = "xml",
        ["xslt"] = "xml",
        ["xq"] = "xquery",
        ["xquery"] = "xquery",
        ["xqy"] = "xquery",
        ["xdefaults"] = "xresources",
        ["xresources"] = "xresources",
        ["yaml"] = "yaml",
        ["yml"] = "yaml",
        ["yang"] = "yang",
        ["yuck"] = "yuck",
        ["yul"] = "yul",
        ["zig"] = "zig",
        ["ziggy"] = "ziggy",
        ["zsh"] = "zsh",
    };

    /// <summary>
    /// Multi-dot suffixes and whole file names, matched against the file name rather than the
    /// extension. The generated table above holds only single dot-free keys, so a name like
    /// <c>foo.app.src</c> — Erlang, not "src" — has nowhere else to resolve.
    /// </summary>
    private static readonly (string Suffix, string Language)[] CompoundExtensions =
    {
        (".app.src", "erlang"),
        (".rs.html", "rshtml"),
        ("kitty.conf", "kitty"),
        (".env", "dotenv"),
    };

    /// <summary>The language for a bare extension, or null. Case-insensitive.</summary>
    /// <remarks>
    /// Upstream lowercases through a 32-byte stack buffer and gives up on anything longer or
    /// non-ASCII, so an extension past that length matches nothing however it is spelled.
    /// </remarks>
    public static string? FromExtension(string ext)
    {
        if (ext.Length == 0 || ext.Length > 32) return null;
        Span<char> lower = stackalloc char[32];
        for (int i = 0; i < ext.Length; i++)
        {
            char c = ext[i];
            if (c > 0x7F) return null;
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        return ByExtension.TryGetValue(new string(lower[..ext.Length]), out var lang) ? lang : null;
    }

    /// <summary>The language for a path, by compound suffix first and then by extension.</summary>
    public static string? FromPath(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Length == 0) return null;

        // Compound first, so `foo.app.src` is Erlang rather than an unknown `src`, and
        // `page.rs.html` is rshtml rather than plain HTML.
        string lowerName = fileName.ToLowerInvariant();
        foreach (var (suffix, language) in CompoundExtensions)
            if (lowerName.EndsWith(suffix, StringComparison.Ordinal))
                return language;

        string ext = Path.GetExtension(path);
        if (ext.Length <= 1) return null;
        return FromExtension(ext[1..]);
    }

    /// <summary>
    /// The language named by a shebang line, or null when the content has none.
    /// </summary>
    /// <remarks>
    /// Only the first line is read. A leading BOM is stepped over first — files saved by
    /// Windows-oriented tools carry one, and it would otherwise hide the shebang.
    /// </remarks>
    public static string? FromContent(string content)
    {
        if (content.StartsWith("\uFEFF", StringComparison.Ordinal)) content = content[1..];
        if (!content.StartsWith("#!", StringComparison.Ordinal)) return null;

        int lineEnd = content.IndexOf('\n');
        string shebang = (lineEnd < 0 ? content[2..] : content[2..lineEnd]).TrimEnd();

        var tokens = shebang.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        string interpreterPath = tokens[0];
        string program;
        if (interpreterPath.EndsWith("/env", StringComparison.Ordinal) || interpreterPath == "env")
        {
            // `env` takes flags of its own — `-S` among them — before the interpreter name.
            program = "";
            for (int i = 1; i < tokens.Length; i++)
            {
                if (tokens[i].StartsWith('-')) continue;
                program = tokens[i];
                break;
            }
            if (program.Length == 0) return null;
        }
        else
        {
            int slash = interpreterPath.LastIndexOf('/');
            program = slash < 0 ? interpreterPath : interpreterPath[(slash + 1)..];
        }

        return MapInterpreter(StripVersionSuffix(program));
    }

    /// <summary>
    /// Drop a trailing version from an interpreter name: <c>python3</c> and <c>python3.11</c> are
    /// both <c>python</c>.
    /// </summary>
    private static string StripVersionSuffix(string name)
    {
        int cut = name.Length;
        for (int i = 0; i < name.Length; i++)
            if (name[i] is >= '0' and <= '9') { cut = i; break; }
        if (cut > 0 && name[cut - 1] == '.') cut--;
        return name[..cut];
    }

    private static string? MapInterpreter(string interpreter) => interpreter switch
    {
        "python" or "python3" or "python2" => "python",
        "bash" or "sh" or "dash" or "ash" => "bash",
        "zsh" => "bash",
        "node" or "nodejs" => "javascript",
        "ruby" or "jruby" => "ruby",
        "perl" or "perl5" or "perl6" => "perl",
        "lua" => "lua",
        "php" => "php",
        "elixir" => "elixir",
        "julia" => "julia",
        // Upstream matches these three case-sensitively, after the version strip; `Rscript`
        // keeps its capital R and `r`/`R` are both spelled out.
        "Rscript" or "r" or "R" => "r",
        _ => null,
    };
}
