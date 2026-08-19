using System.Text;
using System.Text.Json;

namespace Xberg.Core;

/// <summary>
/// MIME detection ported from Rust `core/mime.rs`.
///
/// The extension→MIME table is ported verbatim. Content sniffing reproduces the common
/// signatures directly (the Rust code delegates to the `infer` crate + `mime_guess`; a
/// byte-exact port of those crates is deferred — see PORT_NOTES). The ZIP→Office/iWork/HWPX
/// subsequence scan and the UTF-8 text heuristics are ported faithfully.
/// </summary>
public static class Mime
{
    public const string OctetStream = "application/octet-stream";

    private static readonly Dictionary<string, string> ExtToMime = BuildExtTable();

    /// <summary>Extension-only detection (never reads content). Returns null when unknown.</summary>
    public static string? DetectMimeType(string path, bool checkExists)
    {
        if (checkExists && !File.Exists(path))
            throw new FileNotFoundException($"File does not exist: {path}", path);

        string ext = Path.GetExtension(path);
        if (ext.StartsWith('.')) ext = ext.Substring(1);
        ext = ext.ToLowerInvariant();
        if (ext.Length > 0 && ExtToMime.TryGetValue(ext, out var mime))
            return mime;
        return null;
    }

    /// <summary>Content-based detection. Returns null when the type cannot be determined.</summary>
    public static string? DetectMimeTypeFromBytes(ReadOnlySpan<byte> content)
    {
        string? magic = SniffMagic(content);
        if (magic is not null)
        {
            if (magic == "application/zip")
            {
                string? office = DetectOfficeFormatFromZip(content);
                if (office is not null) return office;
            }
            if (SupportedMimeTypes.Contains(magic) || magic.StartsWith("image/"))
                return magic;
            // else fall through to PST/text heuristics
        }

        // PST
        if (content.Length >= 4 && content[0] == 0x21 && content[1] == 0x42 && content[2] == 0x44 && content[3] == 0x4E)
            return "application/vnd.ms-outlook-pst";

        // UTF-8 text heuristics
        string? text = TryDecodeUtf8(content);
        if (text is not null)
        {
            string trimmed = text.TrimStart();
            if ((trimmed.StartsWith('{') || trimmed.StartsWith('[')) && IsValidJson(text))
                return "application/json";
            // The HTML test must precede the generic `<` fallback. Behind it, the fallback claims
            // every tag first and the HTML test never runs — upstream's issue #235, which this
            // port had inherited. It went unnoticed while the extension always won; once content
            // can overrule the extension, every HTML file routes to the XML extractor.
            if (!trimmed.StartsWith("<?xml", StringComparison.Ordinal) && LooksLikeHtml(trimmed))
                return "text/html";
            if (trimmed.StartsWith("<?xml", StringComparison.Ordinal) || trimmed.StartsWith('<'))
                return "application/xml";
            if (trimmed.StartsWith("%PDF", StringComparison.Ordinal))
                return "application/pdf";
            return "text/plain";
        }

        return null;
    }

    /// <summary>
    /// Resolve the type of a file whose extension is already known, letting content overrule the
    /// extension when the two disagree.
    /// <para>
    /// An extension is a claim, not evidence, and the corpus is full of files where it is simply
    /// wrong — DocTags streams named <c>.doctags.txt</c>, markup saved as <c>.txt</c>. Where the
    /// content carries a recognisable signature, it decides.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Three cases keep the extension despite disagreement, because the content signature is
    /// strictly less informative than the extension rather than in conflict with it: plain text
    /// says nothing at all; generic XML cannot tell FictionBook from DocBook; generic JSON cannot
    /// tell a notebook from line-delimited JSON.
    /// </remarks>
    public static string ResolveWithContent(string? extensionMime, ReadOnlySpan<byte> content)
    {
        if (extensionMime is null)
            return DetectMimeTypeFromBytes(content) ?? OctetStream;

        // Upstream reads a 4 KiB header rather than the whole file, which is visible behaviour:
        // the JSON check parses what it was given, so a large JSON body in a mis-named file does
        // not validate and does not override.
        var header = content.Length > MagicHeaderBytes ? content[..MagicHeaderBytes] : content;
        if (header.IsEmpty) return extensionMime;

        string? fromMagic = DetectMimeTypeFromBytes(header);
        if (fromMagic is null || fromMagic == extensionMime) return extensionMime;

        if (fromMagic == "text/plain") return extensionMime;
        if (IsGenericXmlMime(fromMagic) && IsSpecificXmlMime(extensionMime)) return extensionMime;
        if (fromMagic == "application/json" && IsSpecificJsonMime(extensionMime)) return extensionMime;
        if (IsAmbiguousContainer(fromMagic)) return extensionMime;

        return SupportedMimeTypes.Contains(fromMagic) || fromMagic.StartsWith("image/", StringComparison.Ordinal)
            ? fromMagic
            : extensionMime;
    }

    /// <summary>How much of a file upstream reads when checking content against the extension.</summary>
    private const int MagicHeaderBytes = 4096;

    private static bool IsGenericXmlMime(string mime) => mime is "application/xml" or "text/xml";

    private static bool IsSpecificXmlMime(string mime) =>
        mime != "application/xml"
        && (mime.EndsWith("+xml", StringComparison.Ordinal) || mime.Contains("xml+", StringComparison.Ordinal));

    /// <summary>
    /// Signatures that identify a container without identifying the format inside it, and so can
    /// never overrule an extension that names one.
    /// <para>
    /// An OLE compound file is the container for .doc, .xls, .ppt, .msg and .hwp alike; telling
    /// them apart needs the root storage's CLSID, which this port does not read, so the sniffer
    /// answers <c>application/msword</c> as a placeholder rather than a finding. A ZIP that is
    /// not recognised as one of the Office or ODF layouts is likewise just a ZIP — an .epub is
    /// one, and so is a .jar.
    /// </para>
    /// </summary>
    public static bool IsAmbiguousContainer(string mime) =>
        mime is "application/msword" or "application/zip";

    private static bool IsSpecificJsonMime(string mime) =>
        mime != "application/json"
        && (mime.EndsWith("+json", StringComparison.Ordinal)
            || mime is "application/x-ndjson" or "application/jsonl" or "application/x-jsonlines");

    /// <summary>
    /// Elements common enough at the start of a bare HTML fragment to identify one. A whole
    /// document announces itself with a doctype or an <c>&lt;html&gt;</c> tag; a fragment has
    /// only its first element to go on.
    /// </summary>
    private static readonly HashSet<string> HtmlFragmentElements = new(StringComparer.Ordinal)
    {
        "a", "b", "blockquote", "body", "br", "button", "div", "em", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "i",
        "iframe", "img", "input", "label", "li", "main", "meta", "nav", "ol", "option", "p",
        "pre", "script", "select", "span", "strong", "style", "table", "tbody", "td", "textarea",
        "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>Whether markup opens as HTML rather than as some other XML vocabulary.</summary>
    private static bool LooksLikeHtml(string trimmed)
    {
        string lowered = (trimmed.Length > 16 ? trimmed[..16] : trimmed).ToLowerInvariant();
        if (lowered.StartsWith("<!doctype html", StringComparison.Ordinal)
            || lowered.StartsWith("<html", StringComparison.Ordinal))
            return true;

        if (!trimmed.StartsWith('<')) return false;
        string afterBracket = trimmed[1..];

        int nameLength = 0;
        while (nameLength < afterBracket.Length && char.IsAsciiLetterOrDigit(afterBracket[nameLength]))
            nameLength++;

        // The tag has to actually end here, so `<tr:foo>` — a namespace prefix that happens to
        // collide with an HTML element name — stays XML.
        if (nameLength >= afterBracket.Length) return false;
        if (afterBracket[nameLength] is not ('>' or ' ' or '/' or '\t' or '\n' or '\r')) return false;

        return HtmlFragmentElements.Contains(afterBracket[..nameLength].ToLowerInvariant());
    }

    private static string? SniffMagic(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if (b.Length >= 4 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) ||
                              (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A))) return "image/tiff";
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return "application/pdf";
        // OLE2 / CFB compound file (doc/xls/ppt/msg/hwp) — default to msword without CLSID resolution.
        if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 &&
            b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1) return "application/msword";
        if (b.Length >= 6 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF && b[4] == 0x27 && b[5] == 0x1C)
            return "application/x-7z-compressed";
        if (b.Length >= 2 && b[0] == 0x1F && b[1] == 0x8B) return "application/gzip";
        // ZIP (PK\x03\x04) — catch-all; caller runs office subsequence scan.
        if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B &&
            (b[2] == 0x03 || b[2] == 0x05 || b[2] == 0x07)) return "application/zip";
        return null;
    }

    private static string? DetectOfficeFormatFromZip(ReadOnlySpan<byte> content)
    {
        (byte[] Marker, string Mime)[] markers =
        {
            (Encoding.ASCII.GetBytes("Contents/content.hpf"), "application/haansofthwpx"),
            (Encoding.ASCII.GetBytes("Index/Document.iwa"), "application/x-iwork-pages-sffpages"),
            (Encoding.ASCII.GetBytes("Index/CalculationEngine.iwa"), "application/x-iwork-numbers-sffnumbers"),
            (Encoding.ASCII.GetBytes("Index/Presentation.iwa"), "application/x-iwork-keynote-sffkey"),
            (Encoding.ASCII.GetBytes("word/document.xml"),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            (Encoding.ASCII.GetBytes("xl/workbook.xml"),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            (Encoding.ASCII.GetBytes("ppt/presentation.xml"),
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        };
        foreach (var (marker, mime) in markers)
        {
            if (IndexOf(content, marker) >= 0) return mime;
        }
        return null;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    private static string? TryDecodeUtf8(ReadOnlySpan<byte> content)
    {
        try
        {
            var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return enc.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetExtensionsForMime(string mimeType) =>
        ExtToMime.Where(kv => kv.Value == mimeType).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<(string Extension, string MimeType)> ListSupportedFormats() =>
        ExtToMime.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

    private static readonly HashSet<string> SupportedMimeTypes = new(ExtToMime.Values)
    {
        "text/troff", "text/x-mdoc", "text/x-pod", "text/x-dokuwiki", "text/x-gfm",
        "text/x-markdown-extra", "text/x-multimarkdown", "application/csl+json", "text/x-source-code",
    };

    private static Dictionary<string, string> BuildExtTable()
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string mime, params string[] exts) { foreach (var e in exts) m[e] = mime; }

        Add("text/plain", "txt");
        Add("text/markdown", "md", "markdown");
        Add("text/x-commonmark", "commonmark");
        Add("text/mdx", "mdx");
        Add("text/x-djot", "djot");
        Add("application/pdf", "pdf");
        Add("text/html", "html", "htm");
        Add("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx");
        Add("application/vnd.ms-word.document.macroEnabled.12", "docm");
        Add("application/vnd.openxmlformats-officedocument.wordprocessingml.template", "dotx");
        Add("application/vnd.ms-word.template.macroEnabled.12", "dotm");
        Add("application/msword", "doc", "dot");
        Add("application/vnd.oasis.opendocument.text", "odt");
        Add("application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx");
        Add("application/vnd.openxmlformats-officedocument.presentationml.slideshow", "ppsx");
        Add("application/vnd.ms-powerpoint.presentation.macroEnabled.12", "pptm");
        Add("application/vnd.openxmlformats-officedocument.presentationml.template", "potx");
        Add("application/vnd.ms-powerpoint.template.macroEnabled.12", "potm");
        Add("application/vnd.ms-powerpoint", "ppt", "pot");
        Add("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
        Add("application/vnd.openxmlformats-officedocument.spreadsheetml.template", "xltx");
        Add("application/vnd.ms-excel", "xls", "xlt");
        Add("application/vnd.ms-excel.sheet.macroEnabled.12", "xlsm");
        Add("application/vnd.ms-excel.sheet.binary.macroEnabled.12", "xlsb");
        Add("application/vnd.ms-excel.addin.macroEnabled.12", "xlam");
        Add("application/vnd.ms-excel.template.macroEnabled.12", "xla");
        Add("application/vnd.oasis.opendocument.spreadsheet", "ods");
        Add("application/vnd.oasis.opendocument.presentation", "odp");
        Add("application/x-dbf", "dbf");
        Add("application/x-hwp", "hwp");
        Add("application/haansofthwpx", "hwpx");
        Add("image/bmp", "bmp");
        Add("image/gif", "gif");
        Add("image/jpeg", "jpg", "jpeg");
        Add("image/png", "png");
        Add("image/tiff", "tiff", "tif");
        Add("image/webp", "webp");
        Add("image/jp2", "jp2", "j2k", "j2c");
        Add("image/jpx", "jpx");
        Add("image/jpm", "jpm");
        Add("image/mj2", "mj2");
        Add("image/x-jbig2", "jbig2", "jb2");
        Add("image/heic", "heic", "heics");
        Add("image/heif", "heif");
        Add("image/avif", "avif");
        Add("image/avcs", "avcs");
        Add("image/x-portable-anymap", "pnm");
        Add("image/x-portable-bitmap", "pbm");
        Add("image/x-portable-graymap", "pgm");
        Add("image/x-portable-pixmap", "ppm");
        Add("text/csv", "csv");
        Add("text/tab-separated-values", "tsv");
        Add("application/json", "json");
        Add("application/x-ndjson", "jsonl", "ndjson");
        Add("application/x-yaml", "yaml", "yml");
        Add("application/toml", "toml");
        Add("application/xml", "xml");
        Add("image/svg+xml", "svg");
        Add("message/rfc822", "eml");
        Add("application/vnd.ms-outlook", "msg");
        Add("application/vnd.ms-outlook-pst", "pst");
        Add("application/zip", "zip");
        Add("application/x-tar", "tar");
        Add("application/gzip", "gz", "tgz");
        Add("application/x-7z-compressed", "7z");
        Add("text/x-rst", "rst");
        Add("text/x-org", "org");
        Add("application/epub+zip", "epub");
        Add("application/rtf", "rtf");
        Add("application/x-bibtex", "bib");
        Add("application/x-research-info-systems", "ris");
        Add("application/x-pubmed", "nbib");
        Add("application/x-endnote+xml", "enw");
        Add("application/x-fictionbook+xml", "fb2");
        Add("application/xml+opml", "opml");
        Add("application/docbook+xml", "dbk", "docbook", "docbook4", "docbook5");
        Add("application/x-jats+xml", "jats");
        Add("application/x-ipynb+json", "ipynb");
        Add("application/x-latex", "tex", "latex");
        Add("application/x-typst", "typst", "typ");
        Add("application/x-iwork-pages-sffpages", "pages");
        Add("application/x-iwork-numbers-sffnumbers", "numbers");
        Add("application/x-iwork-keynote-sffkey", "key");
        Add("audio/mpeg", "mp3", "mpga");
        Add("audio/mp4", "m4a");
        Add("audio/wav", "wav");
        Add("audio/webm", "webm");
        Add("video/mp4", "mp4", "mpeg");
        return m;
    }
}
