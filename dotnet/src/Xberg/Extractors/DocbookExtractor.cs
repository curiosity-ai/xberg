using System.Text;
using Xberg.Core;
using Xberg.Internal.MathMarkup;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// DocBook document extractor supporting both 4.x (no namespace) and 5.x (namespaced) formats.
/// Ported from Rust `extractors/docbook.rs`. Single-pass traversal over a lenient XML pull reader
/// (mirrors `EntityReader` semantics: references resolved and merged into the text run).
/// </summary>
public sealed class DocbookExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/docbook+xml",
        "text/docbook",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string docbook = XmlPullReader.Decode(content);

        var (title, author, date, publisher, copyright) = ParseMetadata(docbook, config.SecurityLimits);

        var metadata = new Metadata();
        var subjectParts = new List<string>();
        if (title.Length > 0)
        {
            metadata.Title = title;
            subjectParts.Add($"Title: {title}");
        }
        if (author is not null)
        {
            metadata.Authors = new List<string> { author };
            subjectParts.Add($"Author: {author}");
        }
        if (subjectParts.Count > 0) metadata.Subject = string.Join("; ", subjectParts);
        if (date is not null) metadata.CreatedAt = date;
        if (publisher is not null) metadata.Additional["publisher"] = System.Text.Json.JsonSerializer.SerializeToElement(publisher);
        if (copyright is not null) metadata.Additional["copyright"] = System.Text.Json.JsonSerializer.SerializeToElement(copyright);

        var doc = BuildInternalDocument(docbook, injectPlaceholders: true, config.SecurityLimits);
        doc.MimeType = mimeType;
        doc.Metadata = metadata;
        return doc;
    }

    // ── namespace helper ────────────────────────────────────────────────────
    private static string StripNamespace(string tag)
    {
        if (tag.StartsWith('{'))
        {
            int pos = tag.IndexOf('}');
            if (pos >= 0) return tag[(pos + 1)..];
        }
        return tag;
    }

    // ── synthetic root wrapping (mirrors ensure_root_element) ────────────────
    private static string EnsureRoot(string content)
    {
        string trimmed = content.TrimStart();
        string body = trimmed;
        if (body.StartsWith("<?xml", StringComparison.Ordinal))
        {
            int pos = body.IndexOf("?>", StringComparison.Ordinal);
            body = pos >= 0 ? body[(pos + 2)..].TrimStart() : body;
        }
        if (body.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
        {
            int pos = body.IndexOf('>');
            body = pos >= 0 ? body[(pos + 1)..].TrimStart() : body;
        }
        string[] roots = { "<article", "<book", "<chapter", "<section", "<part", "<set", "<reference",
            "<preface", "<appendix", "<glossary", "<bibliography", "<index", "<colophon", "<dedication",
            "<acknowledgements", "<_root" };
        bool hasRoot = false;
        foreach (var r in roots) if (body.StartsWith(r, StringComparison.Ordinal)) { hasRoot = true; break; }
        return hasRoot ? content : $"<_root>{content}</_root>";
    }

    // ── InternalDocument builder pass (mirrors build_docbook_internal_document) ─
    private static InternalDocument BuildInternalDocument(string content, bool injectPlaceholders, SecurityLimits? limits)
    {
        var reader = new XmlPullReader(EnsureRoot(content), limits);
        var builder = new InternalDocumentBuilder("docbook");

        bool titleExtracted = false;
        bool inInfo = false;
        bool inTable = false, inTgroup = false, inThead = false, inTbody = false, inRow = false;
        var currentTable = new List<List<string>>();
        var currentRow = new List<string>();
        byte sectionDepth = 0;
        byte titleDepth = 0;
        uint footnoteCounter = 0;
        bool inList = false;
        bool inVariablelist = false;
        bool listOrdered = false;

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                string tag = StripNamespace(ev.Name);
                // `<programlisting language="glsl">` names the code block's language, which
                // becomes the fence's info string.
                string? languageAttr = null;
                foreach (var (key, value) in ev.Attrs ?? new List<(string, string)>())
                    if (key == "language") languageAttr = value;
                switch (tag)
                {
                    case "info": case "articleinfo": case "bookinfo": case "chapterinfo":
                        inInfo = true; break;
                    case "chapter": case "sect1": case "sect2": case "sect3": case "sect4": case "sect5": case "section":
                        sectionDepth = (byte)Math.Min(sectionDepth + 1, 255); break;
                    case "title":
                        if (!titleExtracted)
                        {
                            string t = ExtractElementText(reader);
                            if (t.Length > 0) { builder.PushHeading(1, t, null, null); titleDepth = sectionDepth; titleExtracted = true; }
                        }
                        else
                        {
                            string t = ExtractElementText(reader);
                            if (t.Length > 0)
                            {
                                int relative = sectionDepth - titleDepth;
                                byte level = (byte)Math.Min(Math.Max(relative, 0) + 1, 6);
                                builder.PushHeading(level, t, null, null);
                            }
                        }
                        break;
                    case "para": case "simpara":
                    {
                        var (text, anns, paraFormulas) = ExtractParaWithAnnotations(reader);
                        // DocBook puts the equations a sentence refers to ahead of the sentence,
                        // where JATS puts them after it. A paragraph that is only an equation has
                        // no text, and still has its formula.
                        foreach (var latex in paraFormulas) builder.PushFormula(latex, null, null);
                        if (text.Length > 0)
                        {
                            foreach (var ann in anns)
                                if (ann.Kind.Which == AnnotationKind.Tag.Link && !string.IsNullOrEmpty(ann.Kind.Url))
                                    builder.PushUri(new ExtractedUri { Url = ann.Kind.Url!, Label = SliceLabel(text, ann.Start, ann.End), Kind = UriKind.Hyperlink });
                            builder.PushParagraph(text, anns, null, null);
                        }
                        break;
                    }
                    case "equation": case "informalequation": case "inlineequation":
                    {
                        // DocBook writes an equation as MathML, as verbatim TeX in `alt`, or as
                        // plain text. An `<equation>` also takes a `<title>`, which is a caption
                        // rather than an equation number, so it stays out of the LaTeX.
                        string latex = FormulaXml.ExtractFormulaLatex(reader, DocbookFormulaElements);
                        if (latex.Trim().Length > 0) builder.PushFormula(latex.Trim(), null, null);
                        break;
                    }
                    case "programlisting": case "screen":
                    {
                        string t = ExtractElementText(reader);
                        if (t.Length > 0) builder.PushCode(t, languageAttr, null, null);
                        break;
                    }
                    case "itemizedlist":
                        inList = true; listOrdered = false; builder.PushList(false); break;
                    case "orderedlist":
                        inList = true; listOrdered = true; builder.PushList(true); break;
                    case "variablelist":
                        inVariablelist = true; break;
                    case "term":
                        if (inVariablelist)
                        {
                            string t = ExtractElementText(reader);
                            if (t.Length > 0) builder.PushDefinitionTerm(t, null);
                        }
                        break;
                    case "listitem":
                        // A variablelist's listitem is the description half of a definition,
                        // not a bullet; taking the itemizedlist branch here drops the term it
                        // belongs to.
                        if (inVariablelist)
                        {
                            string t = ExtractElementText(reader);
                            if (t.Length > 0) builder.PushDefinitionDescription(t, null);
                        }
                        else if (inList)
                        {
                            string t = ExtractElementText(reader);
                            if (t.Length > 0) builder.PushListItem(t, listOrdered, new(), null, null);
                        }
                        break;
                    case "blockquote":
                    {
                        string t = ExtractElementText(reader);
                        if (t.Length > 0) { builder.PushQuoteStart(); builder.PushParagraph(t, new(), null, null); builder.PushQuoteEnd(); }
                        break;
                    }
                    case "note": case "warning": case "tip": case "caution": case "important":
                    {
                        string t = ExtractElementText(reader);
                        if (t.Length > 0) { builder.PushAdmonition(tag, null, null); builder.PushParagraph(t, new(), null, null); }
                        break;
                    }
                    case "figure":
                    {
                        string caption = ExtractFigureCaption(reader);
                        if (injectPlaceholders)
                            builder.PushParagraph(caption.Length > 0 ? $"[Figure: {caption}]" : "[Figure]", new(), null, null);
                        break;
                    }
                    case "footnote":
                    {
                        string t = ExtractElementText(reader);
                        if (t.Length > 0) { footnoteCounter++; builder.PushFootnoteDefinition(t, $"fn-{footnoteCounter}", null); }
                        break;
                    }
                    case "table": case "informaltable":
                        inTable = true; currentTable.Clear(); break;
                    case "tgroup": if (inTable) inTgroup = true; break;
                    case "thead": if (inTgroup) inThead = true; break;
                    case "tbody": if (inTgroup) inTbody = true; break;
                    case "row": if ((inThead || inTbody) && inTgroup) { inRow = true; currentRow.Clear(); } break;
                    case "entry": if (inRow) currentRow.Add(ExtractElementText(reader)); break;
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                string tag = StripNamespace(ev.Name);
                switch (tag)
                {
                    case "info": case "articleinfo": case "bookinfo": case "chapterinfo":
                        inInfo = false; break;
                    case "chapter": case "sect1": case "sect2": case "sect3": case "sect4": case "sect5": case "section":
                        if (sectionDepth > 0) sectionDepth--; break;
                    case "itemizedlist": case "orderedlist":
                        if (inList) { builder.EndList(); inList = false; } break;
                    case "variablelist":
                        inVariablelist = false; break;
                    case "table": case "informaltable":
                        if (inTable) { if (currentTable.Count > 0) { builder.PushTableFromCells(currentTable, null, null); currentTable.Clear(); } inTable = false; } break;
                    case "tgroup": if (inTgroup) inTgroup = false; break;
                    case "thead": if (inThead) inThead = false; break;
                    case "tbody": if (inTbody) inTbody = false; break;
                    case "row":
                        if (inRow) { if (currentRow.Count > 0) { currentTable.Add(new List<string>(currentRow)); currentRow.Clear(); } inRow = false; } break;
                }
            }
        }
        _ = inInfo;
        return builder.Build();
    }

    // ── metadata single pass (title/author/date/publisher/copyright) ─────────
    private static (string title, string? author, string? date, string? publisher, string? copyright) ParseMetadata(string content, SecurityLimits? limits)
    {
        var reader = new XmlPullReader(EnsureRoot(content), limits);
        string title = "";
        string? author = null, date = null, publisher = null, copyright = null;
        bool inInfo = false;
        bool titleExtracted = false;

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind != XmlEv.Start) continue;
            string tag = StripNamespace(ev.Name);
            switch (tag)
            {
                case "info": case "articleinfo": case "bookinfo": case "chapterinfo":
                    inInfo = true; break;
                case "title":
                    if (!titleExtracted) { title = ExtractElementText(reader); titleExtracted = true; }
                    else ExtractElementText(reader); // section title, skip
                    break;
                case "author": case "personname":
                    if (inInfo && author is null) author = ExtractElementText(reader);
                    break;
                case "date":
                    if (inInfo && date is null) { string t = ExtractElementText(reader); if (t.Length > 0) date = t; }
                    break;
                case "publishername": case "publisher":
                    if (inInfo && publisher is null) { string t = ExtractElementText(reader); if (t.Length > 0) publisher = t; }
                    break;
                case "copyright":
                    if (inInfo && copyright is null) { string t = ExtractElementText(reader); if (t.Length > 0) copyright = t; }
                    break;
            }
        }
        return (title, author, date, publisher, copyright);
    }

    // ── text extraction helpers ──────────────────────────────────────────────
    internal static string ExtractElementText(XmlPullReader reader)
    {
        var text = new StringBuilder();
        int depth = 0;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start) depth++;
            else if (ev.Kind == XmlEv.End) { if (depth == 0) break; depth--; }
            else if (ev.Kind == XmlEv.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0)
                {
                    if (text.Length > 0 && text[^1] != ' ' && text[^1] != '\n') text.Append(' ');
                    text.Append(trimmed);
                }
            }
            else if (ev.Kind == XmlEv.CData)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0) { if (text.Length > 0) text.Append(' '); text.Append(trimmed); }
            }
        }
        return text.ToString().Trim();
    }

    private static string ExtractFigureCaption(XmlPullReader reader)
    {
        string caption = "";
        int depth = 0;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                string tag = StripNamespace(ev.Name);
                if (tag == "title" && depth == 0) caption = ExtractElementText(reader);
                else depth++;
            }
            else if (ev.Kind == XmlEv.End)
            {
                string tag = StripNamespace(ev.Name);
                if (tag == "figure" && depth == 0) break;
                if (depth > 0) depth--;
            }
            else if (ev.Kind == XmlEv.Text && caption.Length == 0)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0) caption += trimmed;
            }
        }
        return caption;
    }

    /// <summary>DocBook writes verbatim TeX in `alt` and has no equation-number element.</summary>
    private static readonly FormulaElements DocbookFormulaElements = new("alt", null);

    private static (string text, List<TextAnnotation> anns, List<string> formulas) ExtractParaWithAnnotations(XmlPullReader reader)
    {
        var text = new StringBuilder();
        var anns = new List<TextAnnotation>();
        var formulas = new List<string>();
        int depth = 0;
        // (kind, openDepth, startByte, href)
        var stack = new List<(string Kind, int OpenDepth, int Start, string? Href)>();

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                depth++;
                string tag = StripNamespace(ev.Name);
                switch (tag)
                {
                    // A paragraph carries its equations inline. The formula belongs in the
                    // formula list, so it is captured here rather than flattened into the
                    // sentence.
                    case "equation": case "informalequation": case "inlineequation":
                    {
                        string latex = FormulaXml.ExtractFormulaLatex(reader, DocbookFormulaElements);
                        depth--;
                        if (latex.Trim().Length > 0) formulas.Add(latex.Trim());
                        break;
                    }
                    case "emphasis":
                    {
                        string role = "";
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs) if (k == "role") role = v;
                        string kind = (role == "bold" || role == "strong") ? "bold" : "italic";
                        stack.Add((kind, depth, Utf8Len(text), null));
                        break;
                    }
                    case "literal": case "command":
                        stack.Add(("code", depth, Utf8Len(text), null)); break;
                    case "link": case "ulink":
                    {
                        string? href = null;
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs)
                                if (k == "url" || k == "href" || k.EndsWith(":href", StringComparison.Ordinal) || k == "linkend") href = v;
                        stack.Add(("link", depth, Utf8Len(text), href)); break;
                    }
                    case "subscript": stack.Add(("subscript", depth, Utf8Len(text), null)); break;
                    case "superscript": stack.Add(("superscript", depth, Utf8Len(text), null)); break;
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                if (depth == 0) break;
                if (stack.Count > 0)
                {
                    var top = stack[^1];
                    if (top.OpenDepth == depth)
                    {
                        int end = Utf8Len(text);
                        int actualStart = top.Start;
                        // skip leading whitespace prepended as separator
                        string span = Utf8Substring(text.ToString(), top.Start, end);
                        int trimmedLead = span.Length - span.TrimStart().Length;
                        // recompute actualStart in bytes
                        if (trimmedLead > 0) actualStart = end - Utf8ByteCount(span.TrimStart());
                        if (end > actualStart)
                        {
                            anns.Add(MakeAnnotation(top.Kind, (uint)actualStart, (uint)end, top.Href));
                        }
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                depth--;
            }
            else if (ev.Kind == XmlEv.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0)
                {
                    if (text.Length > 0 && text[^1] != ' ' && text[^1] != '\n') text.Append(' ');
                    text.Append(trimmed);
                }
            }
            else if (ev.Kind == XmlEv.CData)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0) { if (text.Length > 0) text.Append(' '); text.Append(trimmed); }
            }
        }
        return (text.ToString().Trim(), anns, formulas);
    }

    private static TextAnnotation MakeAnnotation(string kind, uint start, uint end, string? href) => kind switch
    {
        "bold" => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Bold },
        "italic" => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Italic },
        "code" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Code } },
        "subscript" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Subscript } },
        "superscript" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Superscript } },
        "link" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = href ?? "" } },
        _ => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Bold },
    };

    // UTF-8 byte-offset helpers (Rust uses byte offsets for annotation ranges).
    private static int Utf8Len(StringBuilder sb) => Encoding.UTF8.GetByteCount(sb.ToString());
    private static int Utf8ByteCount(string s) => Encoding.UTF8.GetByteCount(s);
    private static string Utf8Substring(string s, int startByte, int endByte)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (startByte < 0 || endByte > bytes.Length || startByte > endByte) return "";
        return Encoding.UTF8.GetString(bytes, startByte, endByte - startByte);
    }

    private static string? SliceLabel(string text, uint startByte, uint endByte)
    {
        var span = Utf8Substring(text, (int)startByte, (int)endByte);
        return span.Length == 0 ? null : span;
    }
}

// ── Shared lenient XML pull reader (mirrors quick-xml event semantics) ────────
// Used by both DocbookExtractor and JatsExtractor. Emits Start/End/Empty/Text/CData/Eof.
// Text arrives with references resolved and merged into the surrounding run, which is what
// `EntityReader` does for these extractors — not the raw, reference-dropping behaviour a bare
// quick-xml reader gives.
internal enum XmlEv { Start, End, Empty, Text, CData, Eof }

internal readonly record struct XmlToken(XmlEv Kind, string Name, string Text, List<(string Key, string Value)>? Attrs);

internal sealed class XmlPullReader
{
    private readonly List<XmlToken> _toks;
    private readonly SecurityBudget _budget;
    private int _i;

    /// <summary>
    /// Read <paramref name="xml"/> as a token stream, charging it against
    /// <paramref name="limits"/> as it goes.
    /// </summary>
    /// <remarks>
    /// Each reader carries its own counters rather than sharing one across a document's passes.
    /// Upstream clones its budget where a second pass starts (`docbook.rs`'s id pass), for the
    /// same reason: a pass that stops early leaves the depth counter mid-descent, and a shared
    /// counter would carry that into the next pass and refuse a document nothing is wrong with.
    /// </remarks>
    public XmlPullReader(string xml, SecurityLimits? limits = null)
    {
        _budget = new SecurityBudget(limits ?? new SecurityLimits());
        _toks = Tokenize(xml, _budget);
    }

    public XmlToken Read()
    {
        var tok = _i < _toks.Count ? _toks[_i++] : new XmlToken(XmlEv.Eof, "", "", null);
        switch (tok.Kind)
        {
            case XmlEv.Start:
                _budget.Enter();
                ChargeAttrs(tok);
                break;
            case XmlEv.Empty:
                // A self-closing element is a descent and an ascent in one event, so it is
                // charged as both — otherwise a document of nothing but `<a/>` would never
                // reach the depth limit no matter how it nested elsewhere.
                _budget.Enter();
                ChargeAttrs(tok);
                _budget.Leave();
                break;
            case XmlEv.End:
                _budget.Leave();
                break;
            case XmlEv.Text:
            case XmlEv.CData:
                _budget.AccountText(Encoding.UTF8.GetByteCount(tok.Text.Trim()));
                break;
        }
        return tok;
    }

    private void ChargeAttrs(XmlToken tok)
    {
        if (tok.Attrs is null) return;
        foreach (var (key, value) in tok.Attrs) _budget.CheckAttr(key, value);
    }

    public static string Decode(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return Encoding.Unicode.GetString(content[2..]);
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(content[2..]);
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return Encoding.UTF8.GetString(content[3..]);
        return Encoding.UTF8.GetString(content);
    }

    /// <summary>
    /// Resolve one XML reference body (what sits between `&amp;` and `;`). Character
    /// references decode to their code point, the five predefined entities and `nbsp` to
    /// their character, and anything else — a DTD-defined entity this reader has no
    /// declaration for — to nothing at all rather than to its own source text.
    /// </summary>
    private static string ResolveReference(ReadOnlySpan<char> body)
    {
        if (body.Length == 0) return "";
        if (body[0] == '#')
        {
            var digits = body[1..];
            bool hex = digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X');
            if (hex) digits = digits[1..];
            if (digits.Length > 0
                && int.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber
                                            : System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out int cp)
                && cp is > 0 and <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF))
                return char.ConvertFromUtf32(cp);
            return "";
        }
        return body switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => "\u00A0",
            _ => "",
        };
    }

    private static List<XmlToken> Tokenize(string s, SecurityBudget budget)
    {
        var result = new List<XmlToken>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (s[i] == '<')
            {
                if (i + 1 < n && s[i + 1] == '!')
                {
                    if (Match(s, i, "<!--"))
                    {
                        int end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = end < 0 ? n : end + 3;
                    }
                    else if (Match(s, i, "<![CDATA["))
                    {
                        int end = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        string body = end < 0 ? s.Substring(i + 9) : s.Substring(i + 9, end - (i + 9));
                        budget.Step();
                        budget.CheckEntity(body);
                        result.Add(new XmlToken(XmlEv.CData, "", body, null));
                        i = end < 0 ? n : end + 3;
                    }
                    else
                    {
                        i = SkipDeclaration(s, i);
                    }
                }
                else if (i + 1 < n && s[i + 1] == '?')
                {
                    int end = s.IndexOf("?>", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                }
                else if (i + 1 < n && s[i + 1] == '/')
                {
                    int gt = s.IndexOf('>', i + 2);
                    if (gt < 0) break;
                    string name = ParseName(s.Substring(i + 2, gt - (i + 2)).Trim());
                    budget.Step();
                    result.Add(new XmlToken(XmlEv.End, name, "", null));
                    i = gt + 1;
                }
                else
                {
                    int gt = FindTagEnd(s, i);
                    if (gt < 0) break;
                    string inner = s.Substring(i + 1, gt - (i + 1));
                    bool empty = inner.EndsWith("/", StringComparison.Ordinal);
                    if (empty) inner = inner[..^1];
                    var (name, attrs) = ParseTag(inner);
                    budget.Step();
                    result.Add(new XmlToken(empty ? XmlEv.Empty : XmlEv.Start, name, "", attrs));
                    i = gt + 1;
                }
            }
            else
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) lt = n;
                // `EntityReader` merges consecutive text and reference events into one
                // resolved run, so a reference is part of the text around it rather than a
                // break in it. Splitting on references instead drops them, which is what
                // turned `print &quot;working&quot;;` into `print working ;`.
                var run = new StringBuilder(lt - i);
                for (int j = i; j < lt; j++)
                {
                    if (s[j] == '&')
                    {
                        int semi = FindEntityEnd(s, j, lt);
                        if (semi > 0)
                        {
                            run.Append(ResolveReference(s.AsSpan(j + 1, semi - j - 1)));
                            j = semi;
                            continue;
                        }
                    }
                    run.Append(s[j]);
                }
                if (run.Length > 0)
                {
                    budget.Step();
                    string body = run.ToString();
                    budget.CheckEntity(body);
                    result.Add(new XmlToken(XmlEv.Text, "", body, null));
                }
                i = lt;
            }
        }
        return result;
    }

    private static int SkipDeclaration(string s, int i)
    {
        int n = s.Length;
        i += 2;
        int bracket = 0;
        while (i < n)
        {
            char c = s[i];
            if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == '>' && bracket <= 0) return i + 1;
            i++;
        }
        return n;
    }

    private static int FindTagEnd(string s, int i)
    {
        bool inS = false, inD = false;
        for (int j = i + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '\'' && !inD) inS = !inS;
            else if (c == '"' && !inS) inD = !inD;
            else if (c == '>' && !inS && !inD) return j;
        }
        return -1;
    }

    private static (string Name, List<(string, string)>? Attributes) ParseTag(string inner)
    {
        int i = 0, n = inner.Length;
        while (i < n && !char.IsWhiteSpace(inner[i])) i++;
        string name = inner[..i];

        List<(string, string)>? attrs = null;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(inner[i])) i++;
            if (i >= n) break;
            int keyStart = i;
            while (i < n && inner[i] != '=' && !char.IsWhiteSpace(inner[i])) i++;
            string key = inner.Substring(keyStart, i - keyStart);
            if (key.Length == 0) { i++; continue; }
            while (i < n && char.IsWhiteSpace(inner[i])) i++;
            string value = "";
            if (i < n && inner[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(inner[i])) i++;
                if (i < n && (inner[i] == '"' || inner[i] == '\''))
                {
                    char q = inner[i++];
                    int vs = i;
                    while (i < n && inner[i] != q) i++;
                    value = inner.Substring(vs, i - vs);
                    if (i < n) i++;
                }
                else
                {
                    int vs = i;
                    while (i < n && !char.IsWhiteSpace(inner[i])) i++;
                    value = inner.Substring(vs, i - vs);
                }
            }
            attrs ??= new List<(string, string)>();
            attrs.Add((key, value));
        }
        return (name, attrs);
    }

    private static string ParseName(string s)
    {
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s[..i];
    }

    private static int FindEntityEnd(string s, int a, int limit)
    {
        int j = a + 1;
        if (j < limit && s[j] == '#') j++;
        int nameStart = j;
        while (j < limit && s[j] != ';' && j - a <= 32)
        {
            char c = s[j];
            if (!char.IsLetterOrDigit(c)) return -1;
            j++;
        }
        if (j < limit && s[j] == ';' && j > nameStart) return j;
        return -1;
    }

    private static bool Match(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
}
