using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Markup;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// FictionBook (FB2) extractor. Ported from Rust `extractors/fictionbook.rs`. Uses a small
/// quick_xml-style pull reader (raw text + separate general-entity refs) to preserve the exact
/// whitespace/entity handling of the Rust extractor. Embedded images and links are not extracted
/// (they do not affect plain/json/metadata/tables parity).
/// </summary>
public sealed class FictionBookExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/x-fictionbook+xml", "text/x-fictionbook", "application/x-fictionbook",
    };
    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string xml = Encoding.UTF8.GetString(content);

        var metadata = ExtractMetadata(xml);
        var tables = ExtractTablesFromBody(xml);
        var doc = BuildInternalDocument(xml);
        doc.MimeType = mimeType;
        doc.Metadata = metadata;
        foreach (var t in tables) doc.PushTable(t);
        return doc;
    }

    // ── metadata ──────────────────────────────────────────────────────────

    private static Metadata ExtractMetadata(string xml)
    {
        var reader = new Reader(xml);
        var meta = new Metadata();
        var authorDetails = new List<Dictionary<string, string>>();
        bool inTitleInfo = false, inDescription = false, inAuthor = false, inAnnotation = false;
        var genres = new List<string>();
        var sequences = new List<string>();
        var authors = new List<string>();
        var annotationText = new StringBuilder();
        string firstName = "", middleName = "", lastName = "", nickname = "";

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Eof) break;
            if (ev.Kind == EvKind.Start)
            {
                string tag = ev.Name;
                switch (tag)
                {
                    case "description": inDescription = true; break;
                    case "title-info" when inDescription: inTitleInfo = true; break;
                    case "author" when inTitleInfo:
                        inAuthor = true; firstName = ""; middleName = ""; lastName = ""; nickname = ""; break;
                    case "first-name" when inAuthor: firstName = ReadTextTrimmed(reader); break;
                    case "middle-name" when inAuthor: middleName = ReadTextTrimmed(reader); break;
                    case "last-name" when inAuthor: lastName = ReadTextTrimmed(reader); break;
                    case "nickname" when inAuthor: nickname = ReadTextTrimmed(reader); break;
                    case "annotation" when inTitleInfo: inAnnotation = true; annotationText.Clear(); break;
                    case "genre" when inTitleInfo:
                    {
                        string g = ReadTextTrimmed(reader);
                        if (g.Length != 0 && g != "unrecognised") genres.Add(g);
                        break;
                    }
                    case "sequence" when inTitleInfo:
                        AddSequence(ev.Attributes, sequences); break;
                    case "date" when inTitleInfo:
                    {
                        string d = ReadTextTrimmed(reader);
                        if (d.Length != 0) meta.CreatedAt = d;
                        break;
                    }
                    case "lang" when inTitleInfo:
                    {
                        string l = ReadTextTrimmed(reader);
                        if (l.Length != 0) meta.Language = l;
                        break;
                    }
                    case "book-title" when inTitleInfo:
                    {
                        string t = ReadTextTrimmed(reader);
                        if (t.Length != 0) meta.Title = t;
                        break;
                    }
                }
            }
            else if (ev.Kind == EvKind.Empty)
            {
                if (ev.Name == "sequence" && inTitleInfo) AddSequence(ev.Attributes, sequences);
            }
            else if (ev.Kind == EvKind.End)
            {
                switch (ev.Name)
                {
                    case "title-info": inTitleInfo = false; break;
                    case "description": inDescription = false; break;
                    case "author" when inAuthor:
                    {
                        inAuthor = false;
                        var parts = new List<string>();
                        if (firstName.Length != 0) parts.Add(firstName);
                        if (middleName.Length != 0) parts.Add(middleName);
                        if (lastName.Length != 0) parts.Add(lastName);
                        string fullName = string.Join(" ", parts);
                        if (fullName.Length != 0)
                        {
                            var detail = new Dictionary<string, string>();
                            if (firstName.Length != 0) detail["first_name"] = firstName;
                            if (middleName.Length != 0) detail["middle_name"] = middleName;
                            if (lastName.Length != 0) detail["last_name"] = lastName;
                            if (nickname.Length != 0) detail["nickname"] = nickname;
                            authors.Add(fullName);
                            authorDetails.Add(detail);
                        }
                        else if (nickname.Length != 0)
                        {
                            authors.Add(nickname);
                            authorDetails.Add(new Dictionary<string, string> { ["nickname"] = nickname });
                        }
                        break;
                    }
                    case "annotation" when inAnnotation: inAnnotation = false; break;
                }
            }
            else if (ev.Kind == EvKind.Text && inAnnotation)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length != 0)
                {
                    if (annotationText.Length != 0) annotationText.Append(' ');
                    annotationText.Append(trimmed);
                }
            }
        }

        if (genres.Count != 0) meta.Subject = string.Join(", ", genres);
        if (authors.Count != 0) meta.Authors = authors;

        // FictionBookMetadata payload struct in the C# port is a stub (no genres/sequences/annotation);
        // populate the available Format tag; note the field gap.
        meta.Format = new FormatMetadata { FormatType = "fiction_book", Payload = new FictionBookMetadata() };

        if (authorDetails.Count != 0)
            meta.Additional["author_details"] = JsonSerializer.SerializeToElement(authorDetails, Json.Options);

        return meta;
    }

    private static void AddSequence(List<(string, string)> attrs, List<string> sequences)
    {
        string seqName = "", seqNumber = "";
        foreach (var (k, v) in attrs)
        {
            if (k == "name") seqName = v;
            else if (k == "number") seqNumber = v;
        }
        if (seqName.Length != 0)
            sequences.Add(seqNumber.Length == 0 ? seqName : $"{seqName} #{seqNumber}");
    }

    private static string ReadTextTrimmed(Reader reader)
    {
        var ev = reader.Read();
        return ev.Kind == EvKind.Text ? ev.Text.Trim() : "";
    }

    // ── tables ──────────────────────────────────────────────────────────────

    private static List<Table> ExtractTablesFromBody(string xml)
    {
        var reader = new Reader(xml);
        var tables = new List<Table>();
        uint tableIndex = 0;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Eof) break;
            if (ev.Kind == EvKind.Start && ev.Name == "table")
            {
                var cells = ExtractTable(reader);
                if (cells.Count > 0)
                {
                    tables.Add(new Table { Cells = cells, Markdown = "", PageNumber = tableIndex + 1 });
                    tableIndex++;
                }
            }
        }
        return tables;
    }

    private static List<List<string>> ExtractTable(Reader reader)
    {
        var table = new List<List<string>>();
        var currentRow = new List<string>();
        bool inRow = false;
        int tableDepth = 1;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Eof) break;
            if (ev.Kind == EvKind.Start)
            {
                switch (ev.Name)
                {
                    case "table": tableDepth++; break;
                    case "tr": inRow = true; currentRow = new List<string>(); break;
                    case "td" or "th" when inRow: currentRow.Add(ExtractTextContent(reader)); break;
                }
            }
            else if (ev.Kind == EvKind.End)
            {
                if (ev.Name == "table") { tableDepth--; if (tableDepth == 0) break; }
                else if (ev.Name == "tr" && inRow)
                {
                    if (currentRow.Count > 0) { table.Add(currentRow); currentRow = new List<string>(); }
                    inRow = false;
                }
            }
        }
        return table;
    }

    // ── text extraction ──────────────────────────────────────────────────────

    private static string ExtractTextContent(Reader reader)
    {
        var text = new StringBuilder();
        int depth = 0;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Start)
            {
                if (ev.Name == "empty-line") text.Append('\n');
                depth++;
            }
            else if (ev.Kind == EvKind.End)
            {
                if (depth == 0) break;
                depth--;
                if ((ev.Name == "p" || ev.Name == "cite" || ev.Name == "section")
                    && text.Length != 0 && text[^1] != '\n') text.Append('\n');
            }
            else if (ev.Kind == EvKind.Text)
            {
                string normalized = NormalizeWhitespace(ev.Text);
                bool hadTrailing = ev.Text.Length > 0 && char.IsWhiteSpace(ev.Text[^1]);
                if (normalized.Length != 0)
                {
                    bool startsPunct = normalized[0] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '[';
                    if (text.Length != 0 && text[^1] != ' ' && text[^1] != '\n' && !startsPunct) text.Append(' ');
                    text.Append(normalized);
                    if (hadTrailing) text.Append(' ');
                }
            }
            else if (ev.Kind == EvKind.CData)
            {
                if (ev.Text.Trim().Length != 0)
                {
                    if (text.Length != 0 && text[^1] != '\n') text.Append('\n');
                    text.Append(ev.Text);
                    text.Append('\n');
                }
            }
            else if (ev.Kind == EvKind.GeneralRef)
            {
                string resolved = ResolveGeneralRef(ev.Name);
                if (resolved.Length != 0) text.Append(resolved);
            }
            else if (ev.Kind == EvKind.Eof) break;
        }
        var lines = MarkupHelpers.Lines(text.ToString()).Select(l => l.Trim()).Where(l => l.Length != 0);
        return string.Join("\n", lines);
    }

    private static (string, List<TextAnnotation>) ExtractParagraphWithAnnotations(Reader reader)
    {
        var text = new Utf8Buf();
        var anns = new List<TextAnnotation>();
        int depth = 0;
        var formatStack = new Stack<(string tag, uint start)>();

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Start)
            {
                depth++;
                if (ev.Name is "emphasis" or "strong" or "strikethrough" or "code")
                    formatStack.Push((ev.Name, text.Len));
            }
            else if (ev.Kind == EvKind.End)
            {
                if ((ev.Name == "p" || ev.Name == "v") && depth <= 1) break;
                if (ev.Name is "emphasis" or "strong" or "strikethrough" or "code" && formatStack.Count > 0)
                {
                    var (fmtTag, start) = formatStack.Pop();
                    uint end = text.Len;
                    if (end > start)
                    {
                        AnnotationKind? kind = fmtTag switch
                        {
                            "emphasis" => MarkupHelpers.Italic,
                            "strong" => MarkupHelpers.Bold,
                            "strikethrough" => MarkupHelpers.Strikethrough,
                            "code" => MarkupHelpers.Code,
                            _ => null,
                        };
                        if (kind is not null) anns.Add(MarkupHelpers.Annotation(start, end, kind));
                    }
                }
                if (depth > 0) depth--;
            }
            else if (ev.Kind == EvKind.Text)
            {
                string normalized = NormalizeWhitespace(ev.Text);
                if (normalized.Length != 0)
                {
                    if (text.Len != 0 && !EndsWithSpace(text)) text.Append(" ");
                    text.Append(normalized);
                }
            }
            else if (ev.Kind == EvKind.GeneralRef)
            {
                string resolved = ResolveGeneralRef(ev.Name);
                if (resolved.Length != 0) text.Append(resolved);
            }
            else if (ev.Kind == EvKind.Eof) break;
        }
        return (text.ToString().Trim(), anns);
    }

    private static bool EndsWithSpace(Utf8Buf buf)
    {
        string s = buf.ToString();
        return s.Length > 0 && s[^1] == ' ';
    }

    private static string ExtractFootnoteText(Reader reader)
    {
        var text = new StringBuilder();
        int sectionDepth = 1;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Start) { if (ev.Name == "section") sectionDepth++; }
            else if (ev.Kind == EvKind.End) { if (ev.Name == "section") { sectionDepth--; if (sectionDepth == 0) break; } }
            else if (ev.Kind == EvKind.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length != 0) { if (text.Length != 0) text.Append(' '); text.Append(trimmed); }
            }
            else if (ev.Kind == EvKind.Eof) break;
        }
        return text.ToString().Trim();
    }

    // ── build ────────────────────────────────────────────────────────────────

    private static InternalDocument BuildInternalDocument(string xml)
    {
        var reader = new Reader(xml);
        var builder = new InternalDocumentBuilder("fictionbook");
        bool inBody = false, isNotesBody = false;
        int sectionDepth = 0;
        int footnoteCounter = 0;

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == EvKind.Eof) break;
            if (ev.Kind == EvKind.Start)
            {
                string tag = ev.Name;
                if (tag == "body")
                {
                    bool isNotes = ev.Attributes.Any(a => a.Item1 == "name" && a.Item2 == "notes");
                    if (isNotes) isNotesBody = true; else inBody = true;
                }
                else if (tag == "section" && inBody) sectionDepth = Math.Min(sectionDepth + 1, 255);
                else if (tag == "title" && inBody)
                {
                    string text = ExtractTextContent(reader);
                    if (text.Length != 0)
                    {
                        int level = sectionDepth == 0 ? 1 : Math.Min(sectionDepth + 1, 6);
                        builder.PushHeading((byte)level, text, null, null);
                    }
                }
                else if (tag == "p" && inBody && !isNotesBody)
                {
                    var (text, annotations) = ExtractParagraphWithAnnotations(reader);
                    if (text.Length != 0) builder.PushParagraph(text, annotations, null, null);
                }
                else if (tag == "v" && inBody && !isNotesBody)
                {
                    var (text, annotations) = ExtractParagraphWithAnnotations(reader);
                    if (text.Length != 0) builder.PushParagraph(text, annotations, null, null);
                }
                else if (tag == "subtitle" && inBody && !isNotesBody)
                {
                    string text = ExtractTextContent(reader);
                    if (text.Length != 0)
                    {
                        int level = Math.Min(sectionDepth + 2, 6);
                        builder.PushHeading((byte)level, text, null, null);
                    }
                }
                else if ((tag == "text-author" || tag == "date") && inBody && !isNotesBody)
                {
                    string text = ExtractTextContent(reader);
                    if (text.Length != 0) builder.PushParagraph(text, new(), null, null);
                }
                else if (tag == "cite" && inBody)
                {
                    string text = ExtractTextContent(reader);
                    if (text.Length != 0)
                    {
                        builder.PushQuoteStart();
                        builder.PushParagraph(text, new(), null, null);
                        builder.PushQuoteEnd();
                    }
                }
                else if ((tag == "programlisting" || tag == "code") && inBody)
                {
                    string text = ExtractTextContent(reader);
                    if (text.Length != 0) builder.PushCode(text, null, null, null);
                }
                else if (tag == "section" && isNotesBody)
                {
                    string text = ExtractFootnoteText(reader);
                    if (text.Length != 0)
                    {
                        footnoteCounter++;
                        builder.PushFootnoteDefinition(text, $"fn-{footnoteCounter}", null);
                    }
                }
            }
            else if (ev.Kind == EvKind.End)
            {
                string tag = ev.Name;
                if (tag == "body") { if (isNotesBody) isNotesBody = false; else inBody = false; }
                else if (tag == "section" && inBody) sectionDepth = Math.Max(sectionDepth - 1, 0);
            }
        }
        return builder.Build();
    }

    // ── entity resolution / whitespace ─────────────────────────────────────

    private static string? ResolveEntity(string name) => name switch
    {
        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => "\u00A0", _ => null,
    };

    private static string ResolveGeneralRef(string name)
    {
        var e = ResolveEntity(name);
        if (e is not null) return e;
        if (name.StartsWith('#'))
        {
            string num = name.Substring(1);
            int? code = num.StartsWith('x') || num.StartsWith('X')
                ? (int.TryParse(num.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null)
                : (int.TryParse(num, out var d) ? d : null);
            if (code is int c && c >= 0 && c <= 0x10FFFF && !(c >= 0xD800 && c <= 0xDFFF))
                return char.ConvertFromUtf32(c);
        }
        return "";
    }

    private static string NormalizeWhitespace(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        bool needs = false;
        for (int i = 0; i + 1 < b.Length; i++)
            if (IsAsciiWs(b[i]) && IsAsciiWs(b[i + 1])) { needs = true; break; }
        if (!needs)
            foreach (var by in b) if (by != (byte)' ' && IsAsciiWs(by)) { needs = true; break; }
        if (!needs) return s;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private static bool IsAsciiWs(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == 0x0C || b == 0x0B;

    // ── minimal quick_xml-style pull reader ─────────────────────────────────

    private enum EvKind { Start, End, Empty, Text, CData, GeneralRef, Eof }

    private readonly record struct Ev(EvKind Kind, string Name, string Text, List<(string, string)> Attributes);

    private sealed class Reader
    {
        private readonly string _s;
        private int _i;
        private readonly Queue<Ev> _pending = new();
        private static readonly List<(string, string)> Empty = new();

        public Reader(string s) { _s = s; _i = 0; }

        public Ev Read()
        {
            if (_pending.Count > 0) return _pending.Dequeue();
            while (true)
            {
                if (_i >= _s.Length) return new Ev(EvKind.Eof, "", "", Empty);
                if (_s[_i] == '<')
                {
                    if (Match("<!--")) { int e = _s.IndexOf("-->", _i + 4, StringComparison.Ordinal); _i = e < 0 ? _s.Length : e + 3; continue; }
                    if (Match("<![CDATA["))
                    {
                        int e = _s.IndexOf("]]>", _i + 9, StringComparison.Ordinal);
                        string body = e < 0 ? _s.Substring(_i + 9) : _s.Substring(_i + 9, e - (_i + 9));
                        _i = e < 0 ? _s.Length : e + 3;
                        return new Ev(EvKind.CData, "", body, Empty);
                    }
                    if (_i + 1 < _s.Length && _s[_i + 1] == '!') { _i = SkipDeclaration(); continue; }
                    if (_i + 1 < _s.Length && _s[_i + 1] == '?') { int e = _s.IndexOf("?>", _i + 2, StringComparison.Ordinal); _i = e < 0 ? _s.Length : e + 2; continue; }
                    if (_i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        int gt = _s.IndexOf('>', _i + 2);
                        if (gt < 0) { _i = _s.Length; return new Ev(EvKind.Eof, "", "", Empty); }
                        string name = ParseName(_s.Substring(_i + 2, gt - (_i + 2)).Trim());
                        _i = gt + 1;
                        return new Ev(EvKind.End, name, "", Empty);
                    }
                    int tagEnd = FindTagEnd(_i);
                    if (tagEnd < 0) { _i = _s.Length; return new Ev(EvKind.Eof, "", "", Empty); }
                    string inner = _s.Substring(_i + 1, tagEnd - (_i + 1));
                    bool empty = inner.EndsWith("/", StringComparison.Ordinal);
                    if (empty) inner = inner.Substring(0, inner.Length - 1);
                    var (tname, attrs) = ParseTag(inner);
                    _i = tagEnd + 1;
                    return new Ev(empty ? EvKind.Empty : EvKind.Start, tname, "", attrs);
                }
                else
                {
                    int lt = _s.IndexOf('<', _i);
                    if (lt < 0) lt = _s.Length;
                    TokenizeText(_i, lt);
                    _i = lt;
                    if (_pending.Count > 0) return _pending.Dequeue();
                    // no events produced (empty run) — loop
                }
            }
        }

        private void TokenizeText(int from, int to)
        {
            int seg = from;
            int j = from;
            while (j < to)
            {
                if (_s[j] == '&')
                {
                    int semi = FindEntityEnd(j, to);
                    if (semi > 0)
                    {
                        if (j > seg) _pending.Enqueue(new Ev(EvKind.Text, "", _s.Substring(seg, j - seg), Empty));
                        string refName = _s.Substring(j + 1, semi - (j + 1));
                        _pending.Enqueue(new Ev(EvKind.GeneralRef, refName, "", Empty));
                        j = semi + 1;
                        seg = j;
                        continue;
                    }
                }
                j++;
            }
            if (to > seg) _pending.Enqueue(new Ev(EvKind.Text, "", _s.Substring(seg, to - seg), Empty));
        }

        private int FindEntityEnd(int a, int limit)
        {
            int j = a + 1;
            if (j < limit && _s[j] == '#') j++;
            int nameStart = j;
            while (j < limit && _s[j] != ';' && j - a <= 32)
            {
                char c = _s[j];
                if (!char.IsLetterOrDigit(c)) return -1;
                j++;
            }
            if (j < limit && _s[j] == ';' && j > nameStart) return j;
            return -1;
        }

        private int SkipDeclaration()
        {
            int i = _i + 2;
            int bracket = 0;
            while (i < _s.Length)
            {
                char c = _s[i];
                if (c == '[') bracket++;
                else if (c == ']') bracket--;
                else if (c == '>' && bracket <= 0) return i + 1;
                i++;
            }
            return _s.Length;
        }

        private int FindTagEnd(int i)
        {
            bool inS = false, inD = false;
            for (int j = i + 1; j < _s.Length; j++)
            {
                char c = _s[j];
                if (c == '\'' && !inD) inS = !inS;
                else if (c == '"' && !inS) inD = !inD;
                else if (c == '>' && !inS && !inD) return j;
            }
            return -1;
        }

        private static (string, List<(string, string)>) ParseTag(string inner)
        {
            int i = 0, n = inner.Length;
            while (i < n && !char.IsWhiteSpace(inner[i])) i++;
            string name = inner.Substring(0, i);
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
                    else { int vs = i; while (i < n && !char.IsWhiteSpace(inner[i])) i++; value = inner.Substring(vs, i - vs); }
                }
                attrs ??= new List<(string, string)>();
                attrs.Add((key, value));
            }
            return (name, attrs ?? Empty);
        }

        private static string ParseName(string s)
        {
            int i = 0;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            return s.Substring(0, i);
        }

        private bool Match(string token) => _i + token.Length <= _s.Length && string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;
    }
}
