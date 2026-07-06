using System.Text;
using Xberg.Types;

namespace Xberg.Internal.Html;

/// <summary>
/// Byte-level HTML walker that emits directly into an <see cref="InternalDocumentBuilder"/>.
/// Ports the Rust `extraction/html/structure.rs` walker combined with the flat→element mapping
/// of `extractors/html.rs::walk_nodes`. Headings open nested "section" Groups (mirroring
/// html-to-markdown's DocumentStructure), so element depth and the derived JSON section tree
/// match the Rust output. The document <c>&lt;head&gt;</c> is skipped for content (metadata is
/// scanned separately).
/// </summary>
public sealed class HtmlWalker
{
    private readonly string _src;
    private int _pos;
    private readonly InternalDocumentBuilder _b;

    // Heading-section group stack (heading levels of open Groups).
    private readonly List<byte> _groupStack = new();

    // Paragraph / inline accumulation. _textBuf is kept whitespace-normalized incrementally
    // so annotation byte offsets align with the final (normalized) paragraph text.
    private readonly StringBuilder _textBuf = new();
    private bool _lastWasSpace = true;
    private readonly List<InlineSpan> _inlineStack = new();
    private readonly List<TextAnnotation> _annotations = new();

    // Container state
    private bool _inPre;
    private PreBlock? _preBlock;
    private TableAccumulator? _table;
    private int _listDepth;                 // number of open <ul>/<ol>
    private readonly List<bool> _listOrdered = new();
    private bool _inListItem;
    private readonly StringBuilder _listItemText = new();

    // Definition list
    private bool _inDl;
    private string? _dlTerm;
    private bool _inDt, _inDd;
    private readonly StringBuilder _dtText = new();
    private readonly StringBuilder _ddText = new();

    // Figure
    private FigureContext? _figure;

    public HtmlWalker(string src, InternalDocumentBuilder builder)
    {
        _src = src;
        _b = builder;
    }

    public void Walk()
    {
        while (_pos < _src.Length)
        {
            if (Starts("<!--"))
            {
                int end = _src.IndexOf("-->", _pos, StringComparison.Ordinal);
                _pos = end < 0 ? _src.Length : end + 3;
                continue;
            }
            if (_src[_pos] == '<') HandleTag();
            else HandleText();
        }
        FlushParagraph();
        while (_groupStack.Count > 0) { _b.PushGroupEnd(); _groupStack.RemoveAt(_groupStack.Count - 1); }
    }

    private bool Starts(string s) => string.CompareOrdinal(_src, _pos, s, 0, s.Length) == 0 && _pos + s.Length <= _src.Length;

    // ── text ─────────────────────────────────────────────────────────────────
    private void HandleText()
    {
        int start = _pos;
        while (_pos < _src.Length && _src[_pos] != '<') _pos++;
        string decoded = DecodeEntities(_src[start.._pos]);

        if (_table is not null) { _table.PushText(decoded); return; }
        if (_preBlock is not null) { _preBlock.Text.Append(decoded); return; }
        if (_inListItem) { _listItemText.Append(decoded); return; }
        if (_inDt) { _dtText.Append(decoded); return; }
        if (_inDd) { _ddText.Append(decoded); return; }
        if (_figure is { InCaption: true } fig) { fig.Caption.Append(decoded); return; }
        AppendNormalized(decoded);
    }

    // Append text to the paragraph buffer, collapsing whitespace on the fly (mirrors
    // NormalizeWhitespace) and preserving the \x01 <br> sentinel so offsets stay stable.
    private void AppendNormalized(string s)
    {
        foreach (char c in s)
        {
            if (c == '\x01')
            {
                while (_textBuf.Length > 0 && _textBuf[^1] == ' ') _textBuf.Remove(_textBuf.Length - 1, 1);
                _textBuf.Append('\x01');
                _lastWasSpace = true;
            }
            else if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (!_lastWasSpace) { _textBuf.Append(' '); _lastWasSpace = true; }
            }
            else { _textBuf.Append(c); _lastWasSpace = false; }
        }
    }

    // Append a literal inline string to whichever buffer is active (used by <q> quotes).
    private void AppendInline(string s)
    {
        if (_table is not null) { _table.PushText(s); return; }
        if (_preBlock is not null) { _preBlock.Text.Append(s); return; }
        if (_inListItem) { _listItemText.Append(s); return; }
        if (_inDt) { _dtText.Append(s); return; }
        if (_inDd) { _ddText.Append(s); return; }
        if (_figure is { InCaption: true } fig) { fig.Caption.Append(s); return; }
        AppendNormalized(s);
    }

    private void ClearTextBuf() { _textBuf.Clear(); _lastWasSpace = true; }

    // ── tags ───────────────────────────────────────────────────────────────
    private void HandleTag()
    {
        int gt = _src.IndexOf('>', _pos);
        if (gt < 0) { _pos = _src.Length; return; }
        string tagContent = _src[(_pos + 1)..gt];
        _pos = gt + 1;

        if (tagContent.StartsWith('!') || tagContent.StartsWith('?')) return;

        bool isClosing = tagContent.StartsWith('/');
        string content = isClosing ? tagContent[1..] : tagContent;
        content = content.TrimEnd('/').Trim();

        var (tagName, attrs) = SplitTagName(content);
        string tag = tagName.ToLowerInvariant();

        if (isClosing) HandleClose(tag);
        else HandleOpen(tag, attrs, tagContent.EndsWith('/'));
    }

    private void HandleOpen(string tag, string attrs, bool selfClosing)
    {
        switch (tag)
        {
            case "head":
            {
                // Skip the entire head — metadata is handled by a separate scan.
                int close = _src.IndexOf("</head>", _pos, StringComparison.OrdinalIgnoreCase);
                _pos = close < 0 ? _src.Length : close + "</head>".Length;
                break;
            }
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                FlushParagraph();
                ClearTextBuf();
                _annotations.Clear();
                break;
            case "p":
                FlushParagraph();
                break;
            case "br":
                if (_inPre || _preBlock is not null) { _preBlock?.Text.Append('\n'); }
                else if (_inListItem) _listItemText.Append('\n');
                else AppendNormalized("\x01");
                break;
            case "strong": case "b": PushInline(InlineKind.Bold, null, null); break;
            case "em": case "i": case "var": case "cite": case "dfn": PushInline(InlineKind.Italic, null, null); break;
            case "kbd": case "samp": PushInline(InlineKind.Code, null, null); break;
            case "code":
                if (_inPre)
                    _preBlock = new PreBlock { Language = ExtractLanguageFromClass(ExtractAttr(attrs, "class")) };
                else PushInline(InlineKind.Code, null, null);
                break;
            case "u": case "ins": PushInline(InlineKind.Underline, null, null); break;
            case "s": case "del": case "strike": PushInline(InlineKind.Strikethrough, null, null); break;
            case "sub": PushInline(InlineKind.Subscript, null, null); break;
            case "sup": PushInline(InlineKind.Superscript, null, null); break;
            case "mark": PushInline(InlineKind.Highlight, null, null); break;
            case "a":
                PushInline(InlineKind.Link, ExtractAttr(attrs, "href") ?? "", ExtractAttr(attrs, "title"));
                break;
            case "q":
                AppendInline("\"");
                break;
            case "pre":
                FlushParagraph();
                _inPre = true;
                _preBlock = new PreBlock();
                break;
            case "blockquote":
                FlushParagraph();
                _b.PushQuoteStart();
                break;
            case "ul":
                FlushParagraph();
                _b.PushList(false); _listDepth++; _listOrdered.Add(false);
                break;
            case "ol":
                FlushParagraph();
                _b.PushList(true); _listDepth++; _listOrdered.Add(true);
                break;
            case "li":
                FlushListItem();
                _inListItem = true;
                _listItemText.Clear();
                break;
            case "table":
                FlushParagraph();
                _table = new TableAccumulator();
                break;
            case "tr": if (_table is not null) _table.OpenRow(); break;
            case "thead": case "tbody": case "tfoot": break;
            case "th": case "td":
                if (_table is not null)
                {
                    uint colSpan = ParseU32(ExtractAttr(attrs, "colspan"), 1);
                    uint rowSpan = ParseU32(ExtractAttr(attrs, "rowspan"), 1);
                    _table.OpenCell(colSpan, rowSpan, tag == "th");
                }
                break;
            case "img":
            {
                string? alt = ExtractAttr(attrs, "alt");
                string? src = ExtractAttr(attrs, "src");
                if (_figure is not null) { _figure.ImgAlt = alt; _figure.ImgSrc = src; }
                else { FlushParagraph(); EmitImage(alt, src); }
                break;
            }
            case "figure":
                FlushParagraph();
                _figure = new FigureContext();
                break;
            case "figcaption":
                if (_figure is not null) { _figure.InCaption = true; }
                break;
            case "dl":
                FlushParagraph();
                _inDl = true; _dlTerm = null;
                break;
            case "dt":
                FlushDefinitionItem();
                _inDt = true; _dtText.Clear();
                break;
            case "dd":
                _inDt = false;
                if (_inDl) { string term = NormalizeWhitespace(_dtText.ToString()); if (term.Length > 0) _dlTerm = term; }
                _dtText.Clear();
                _inDd = true; _ddText.Clear();
                break;
            case "script": case "style":
            {
                string closeTag = $"</{tag}>";
                int close = _src.IndexOf(closeTag, _pos, StringComparison.OrdinalIgnoreCase);
                _pos = close < 0 ? _src.Length : close + closeTag.Length;
                break; // raw block skipped for content
            }
            case "video": case "audio":
            {
                string closeTag = $"</{tag}>";
                int close = _src.IndexOf(closeTag, _pos, StringComparison.OrdinalIgnoreCase);
                if (close >= 0) _pos = close + closeTag.Length;
                break;
            }
            case "hr":
                FlushParagraph();
                break;
            case "div": case "section": case "article": case "main": case "aside":
            case "header": case "footer": case "nav": case "details": case "summary":
                FlushParagraph();
                break;
            default:
                break; // span/html/body/title/link and unknowns: passthrough
        }
    }

    private void HandleClose(string tag)
    {
        switch (tag)
        {
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            {
                byte level = byte.TryParse(tag.AsSpan(1), out var l) ? l : (byte)1;
                string text = _textBuf.ToString().Trim();
                if (text.Length > 0) EmitHeading(level, text);
                ClearTextBuf();
                _annotations.Clear();
                _inlineStack.Clear();
                break;
            }
            case "p": FlushParagraph(); break;
            case "strong": case "b": PopInline(InlineKind.Bold); break;
            case "em": case "i": case "var": case "cite": case "dfn": PopInline(InlineKind.Italic); break;
            case "kbd": case "samp": PopInline(InlineKind.Code); break;
            case "code": if (!_inPre) PopInline(InlineKind.Code); break;
            case "u": case "ins": PopInline(InlineKind.Underline); break;
            case "s": case "del": case "strike": PopInline(InlineKind.Strikethrough); break;
            case "sub": PopInline(InlineKind.Subscript); break;
            case "sup": PopInline(InlineKind.Superscript); break;
            case "mark": PopInline(InlineKind.Highlight); break;
            case "a": PopInlineLink(); break;
            case "q": AppendInline("\""); break;
            case "pre":
                if (_preBlock is not null)
                {
                    string text = _preBlock.Text.ToString().TrimEnd('\n');
                    if (text.Length > 0) _b.PushCode(text, _preBlock.Language, null, null);
                    _preBlock = null;
                }
                _inPre = false;
                break;
            case "blockquote":
                FlushParagraph();
                _b.PushQuoteEnd();
                break;
            case "ul": case "ol":
                FlushListItem();
                if (_listDepth > 0) { _b.EndList(); _listDepth--; if (_listOrdered.Count > 0) _listOrdered.RemoveAt(_listOrdered.Count - 1); }
                break;
            case "li": FlushListItem(); break;
            case "table":
                if (_table is not null)
                {
                    _table.CloseCell(); _table.CloseRow();
                    if (_table.Rows.Count > 0) EmitTable(_table.Rows);
                    _table = null;
                }
                break;
            case "tr": if (_table is not null) { _table.CloseCell(); _table.CloseRow(); } break;
            case "th": case "td": if (_table is not null) _table.CloseCell(); break;
            case "dl": FlushDefinitionItem(); _inDl = false; break;
            case "dt": _inDt = false; break;
            case "dd": _inDd = false; FlushDefinitionItem(); break;
            case "figure":
                if (_figure is not null)
                {
                    string? cap = _figure.Caption.ToString().Trim();
                    string? desc = !string.IsNullOrEmpty(cap) ? cap : _figure.ImgAlt;
                    EmitImage(desc, _figure.ImgSrc);
                    _figure = null;
                }
                break;
            case "figcaption": if (_figure is not null) _figure.InCaption = false; break;
            case "div": case "section": case "article": case "main": case "aside":
            case "header": case "footer": case "nav": case "details": case "summary":
                FlushParagraph();
                break;
        }
    }

    // ── heading grouping ─────────────────────────────────────────────────────
    private void EmitHeading(byte level, string text)
    {
        while (_groupStack.Count > 0 && _groupStack[^1] >= level)
        {
            _b.PushGroupEnd();
            _groupStack.RemoveAt(_groupStack.Count - 1);
        }
        _b.PushGroupStart(null, null);
        _groupStack.Add(level);
        _b.PushHeading(level, text, null, null);
    }

    private void EmitImage(string? description, string? src)
    {
        string text = description ?? "";
        if (text.Length > 0 || !string.IsNullOrEmpty(src))
        {
            string display;
            if (!string.IsNullOrEmpty(src))
                display = text.Length == 0 ? $"![]({src})" : $"![{text}]({src})";
            else
                display = text;
            _b.PushParagraph(display, new(), null, null);
        }
        if (!string.IsNullOrEmpty(src))
            _b.PushUri(new ExtractedUri { Url = src!, Label = description, Kind = UriKind.Image });
    }

    private void EmitTable(List<List<CellMeta>> rows)
    {
        bool hasSpans = rows.Any(r => r.Any(c => c.ColSpan > 1 || c.RowSpan > 1));
        if (!hasSpans)
        {
            var simple = rows.Select(r => r.Select(c => NormalizeWhitespace(c.Text)).ToList()).ToList();
            _b.PushTableFromCells(simple, null, null);
            return;
        }
        int numCols = rows.Count == 0 ? 0 : rows.Max(r => (int)r.Sum(c => c.ColSpan));
        var grid = new List<List<string>>();
        foreach (var row in rows)
        {
            var line = new List<string>(new string[numCols]);
            for (int k = 0; k < numCols; k++) line[k] = "";
            int col = 0;
            foreach (var cell in row)
            {
                if (col < numCols) line[col] = NormalizeWhitespace(cell.Text);
                col += (int)cell.ColSpan;
            }
            grid.Add(line);
        }
        _b.PushTableFromCells(grid, null, null);
    }

    // ── inline formatting ─────────────────────────────────────────────────────
    // html-to-markdown bakes inline markdown directly into the element text (the plain
    // renderer strips annotations, so text carries the formatting). Nested same-type spans
    // are de-duplicated: only the outermost open/close emits its marker.
    private static string Marker(InlineKind kind) => kind switch
    {
        InlineKind.Bold => "**",
        InlineKind.Italic => "*",
        InlineKind.Code => "`",
        InlineKind.Strikethrough => "~~",
        _ => "",
    };

    private void PushInline(InlineKind kind, string? href, string? title)
    {
        if (kind == InlineKind.Link)
        {
            AppendInline("[");
            if (ParagraphContext) _lastWasSpace = true; // suppress leading space inside link text
            _inlineStack.Add(new InlineSpan { Kind = kind, Href = href, Title = title });
            return;
        }
        string mk = Marker(kind);
        bool alreadyOpen = mk.Length > 0 && _inlineStack.Exists(s => s.Kind == kind);
        if (mk.Length > 0 && !alreadyOpen) AppendInline(mk);
        _inlineStack.Add(new InlineSpan { Kind = kind });
    }

    private void PopInline(InlineKind expected)
    {
        int idx = _inlineStack.FindLastIndex(s => s.Kind == expected);
        if (idx < 0) return;
        _inlineStack.RemoveAt(idx);
        string mk = Marker(expected);
        if (mk.Length > 0 && !_inlineStack.Exists(s => s.Kind == expected)) AppendInline(mk);
    }

    private void PopInlineLink()
    {
        int idx = _inlineStack.FindLastIndex(s => s.Kind == InlineKind.Link);
        if (idx < 0) return;
        var span = _inlineStack[idx];
        _inlineStack.RemoveAt(idx);
        if (ParagraphContext)
            while (_textBuf.Length > 0 && _textBuf[^1] == ' ') _textBuf.Remove(_textBuf.Length - 1, 1);
        string suffix = string.IsNullOrEmpty(span.Title) ? $"]({span.Href})" : $"]({span.Href} \"{span.Title}\")";
        AppendInline(suffix);
    }

    private bool ParagraphContext =>
        _table is null && _preBlock is null && !_inListItem && !_inDt && !_inDd && !(_figure?.InCaption ?? false);

    // ── flush helpers ─────────────────────────────────────────────────────────
    private void FlushParagraph()
    {
        string text = NormalizeWhitespace(_textBuf.ToString()).TrimEnd('\n');
        if (text.Length > 0)
        {
            var anns = new List<TextAnnotation>(_annotations);
            _b.PushParagraph(text, anns, null, null);
        }
        ClearTextBuf();
        _annotations.Clear();
        _inlineStack.Clear();
    }

    private void FlushListItem()
    {
        if (!_inListItem) return;
        _inListItem = false;
        string text = NormalizeWhitespace(_listItemText.ToString());
        if (text.Length > 0 && _listDepth > 0)
            _b.PushListItem(text, _listOrdered.Count > 0 && _listOrdered[^1], new(), null, null);
        _listItemText.Clear();
    }

    private void FlushDefinitionItem()
    {
        if (_inDd)
        {
            _inDd = false;
            if (_inDl)
            {
                string definition = NormalizeWhitespace(_ddText.ToString());
                if (_dlTerm is not null)
                {
                    _b.PushDefinitionTerm(_dlTerm, null);
                    _b.PushDefinitionDescription(definition, null);
                    _dlTerm = null;
                }
            }
            _ddText.Clear();
        }
        if (_inDt)
        {
            _inDt = false;
            if (_inDl) { string term = NormalizeWhitespace(_dtText.ToString()); if (term.Length > 0) _dlTerm = term; }
            _dtText.Clear();
        }
    }

    private static int Utf8Len(StringBuilder sb) => Encoding.UTF8.GetByteCount(sb.ToString());
    private static uint ParseU32(string? s, uint fallback) => uint.TryParse(s, out var v) ? v : fallback;

    // ── static utilities (ported from structure.rs) ──────────────────────────
    internal static (string name, string attrs) SplitTagName(string content)
    {
        content = content.Trim();
        int sp = content.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '\f' });
        return sp < 0 ? (content, "") : (content[..sp], content[(sp + 1)..]);
    }

    internal static string? ExtractAttr(string attrs, string name)
    {
        string search = name + "=";
        int searchFrom = 0;
        int abs;
        while (true)
        {
            int candidate = attrs.IndexOf(search, searchFrom, StringComparison.Ordinal);
            if (candidate < 0) return null;
            abs = candidate;
            if (abs == 0 || !char.IsLetterOrDigit(attrs[abs - 1])) break;
            searchFrom = abs + 1;
        }
        string afterEq = attrs[(abs + search.Length)..].TrimStart();
        if (afterEq.Length == 0) return null;
        char quote = afterEq[0];
        if (quote == '"' || quote == '\'')
        {
            string rest = afterEq[1..];
            int end = rest.IndexOf(quote);
            return end < 0 ? null : rest[..end];
        }
        int e = afterEq.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '\f', '>' });
        return e < 0 ? afterEq : afterEq[..e];
    }

    private static string? ExtractLanguageFromClass(string? cls)
    {
        if (cls is null) return null;
        foreach (var c in cls.Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (c.StartsWith("language-", StringComparison.Ordinal)) return c["language-".Length..];
            if (c.StartsWith("lang-", StringComparison.Ordinal)) return c["lang-".Length..];
        }
        return null;
    }

    internal static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        var outp = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c != '&') { outp.Append(c); continue; }
            var entity = new StringBuilder();
            bool tooLong = false;
            while (i < s.Length)
            {
                char ec = s[i++];
                if (ec == ';') break;
                entity.Append(ec);
                if (entity.Length > 10) { outp.Append('&'); outp.Append(entity); tooLong = true; break; }
            }
            if (tooLong) continue;
            if (entity.Length == 0) continue;
            string e = entity.ToString();
            string? mapped = e switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'",
                "nbsp" => " ", "copy" => "©", "reg" => "®", "trade" => "™",
                "mdash" => "—", "ndash" => "–", "laquo" => "«", "raquo" => "»",
                "hellip" => "…", "eacute" => "é", "egrave" => "è", "ecirc" => "ê",
                "euml" => "ë", "aacute" => "á", "agrave" => "à", "acirc" => "â",
                "auml" => "ä", "iacute" => "í", "ocirc" => "ô", "ouml" => "ö",
                "uuml" => "ü", "ntilde" => "ñ", "ccedil" => "ç", "ldquo" => "“",
                "rdquo" => "”", "lsquo" => "‘", "rsquo" => "’", "bull" => "•",
                "middot" => "·", "euro" => "€", "pound" => "£", "yen" => "¥",
                "times" => "×", "divide" => "÷", "plusmn" => "±",
                _ => null,
            };
            if (mapped is not null) { outp.Append(mapped); continue; }
            if (e.StartsWith('#'))
            {
                string num = e[1..];
                int? cp = null;
                if (num.StartsWith('x') || num.StartsWith('X'))
                {
                    if (int.TryParse(num[1..], System.Globalization.NumberStyles.HexNumber, null, out var h)) cp = h;
                }
                else if (int.TryParse(num, out var d)) cp = d;
                if (cp is not null && cp >= 0 && cp <= 0x10FFFF)
                {
                    try { outp.Append(char.ConvertFromUtf32(cp.Value)); continue; } catch { }
                }
            }
            outp.Append('&'); outp.Append(e); outp.Append(';');
        }
        return outp.ToString();
    }

    internal static string NormalizeWhitespace(string s)
    {
        var outp = new StringBuilder(s.Length);
        bool lastWasSpace = true;
        foreach (char c in s)
        {
            if (c == '\x01')
            {
                while (outp.Length > 0 && outp[^1] == ' ') outp.Remove(outp.Length - 1, 1);
                outp.Append('\n');
                lastWasSpace = true;
            }
            else if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                if (!lastWasSpace) { outp.Append(' '); lastWasSpace = true; }
            }
            else { outp.Append(c); lastWasSpace = false; }
        }
        if (outp.Length > 0 && outp[^1] == ' ') outp.Remove(outp.Length - 1, 1);
        return outp.ToString();
    }

    // ── nested state types ─────────────────────────────────────────────────
    private enum InlineKind { Bold, Italic, Code, Underline, Strikethrough, Link, Subscript, Superscript, Highlight }

    private struct InlineSpan { public InlineKind Kind; public int TextStart; public string? Href; public string? Title; }

    private sealed class PreBlock { public string? Language; public readonly StringBuilder Text = new(); }

    internal sealed class CellMeta { public string Text = ""; public uint ColSpan = 1; public uint RowSpan = 1; public bool IsHeader; }

    private sealed class FigureContext
    {
        public string? ImgAlt;
        public string? ImgSrc;
        public readonly StringBuilder Caption = new();
        public bool InCaption;
    }

    private sealed class TableAccumulator
    {
        public readonly List<List<CellMeta>> Rows = new();
        private List<CellMeta> _currentRow = new();
        private readonly StringBuilder _currentCell = new();
        private uint _colSpan = 1, _rowSpan = 1;
        private bool _isHeader, _inRow, _inCell;

        public void OpenRow() { _currentRow = new(); _inRow = true; }
        public void CloseRow() { if (_inRow) { Rows.Add(_currentRow); _inRow = false; } }
        public void OpenCell(uint colSpan, uint rowSpan, bool isHeader)
        {
            _currentCell.Clear(); _colSpan = colSpan; _rowSpan = rowSpan; _isHeader = isHeader; _inCell = true;
        }
        public void CloseCell()
        {
            if (!_inCell) return;
            _currentRow.Add(new CellMeta { Text = _currentCell.ToString(), ColSpan = _colSpan, RowSpan = _rowSpan, IsHeader = _isHeader });
            _inCell = false; _colSpan = 1; _rowSpan = 1; _isHeader = false;
        }
        public void PushText(string t) { if (_inCell) _currentCell.Append(t); }
    }
}
