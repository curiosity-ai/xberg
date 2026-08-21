// Ported from crates/xberg/src/extraction/html/structure.rs
// Lightweight tag-level HTML walker producing a FLAT list of document nodes in insertion order.
//
// The EPUB extractor (mod.rs `build_internal_document`) iterates `DocumentStructure.nodes` in flat
// insertion order and matches on node content; the tree wiring (parent/children) and node attributes
// are never read there. This port therefore reproduces only the flat node sequence (and per-paragraph
// annotations), which is exactly what the downstream conversion consumes. Node attributes (class/cite/
// start/width/height) and section/container tree wiring from `DocumentStructureBuilder` are omitted.

using System.Text;
using Xberg.Internal.MathMarkup;
using Xberg.Types;

namespace Xberg.Internal.Epub;

/// <summary>A single flat structure node plus its inline annotations (paragraphs only).</summary>
internal sealed class StructNode
{
    public NodeContent Content { get; init; } = NodeContent.Paragraph("");
    public List<TextAnnotation> Annotations { get; init; } = new();
}

internal static class EpubHtmlStructure
{
    /// <summary>Build the flat node list from raw (sanitised) HTML. Mirrors `build_document_structure`.</summary>
    public static List<StructNode> BuildDocumentStructure(string html)
    {
        var walker = new HtmlWalker(html);
        walker.Walk();
        return walker.Nodes;
    }

    private enum InlineKindTag { Bold, Italic, Code, Underline, Strikethrough, Link, Subscript, Superscript, Highlight }

    private sealed class InlineSpan
    {
        public InlineKindTag Kind;
        public uint TextStart;
        public string? Href;
        public string? Title;
    }

    private sealed class PreBlock
    {
        public string? Language;
        public StringBuilder Text = new();
    }

    private sealed class CellMeta
    {
        public string Text = "";
        public uint ColSpan = 1;
        public uint RowSpan = 1;
        public bool IsHeader;
    }

    private sealed class TableAccumulator
    {
        public List<List<CellMeta>> Rows = new();
        public List<CellMeta> CurrentRow = new();
        public StringBuilder CurrentCell = new();
        public uint CurrentColSpan = 1;
        public uint CurrentRowSpan = 1;
        public bool CurrentIsHeader;
        public bool InRow;
        public bool InCell;

        public void OpenRow()
        {
            CurrentRow = new List<CellMeta>();
            InRow = true;
        }

        public void CloseRow()
        {
            if (InRow)
            {
                Rows.Add(CurrentRow);
                CurrentRow = new List<CellMeta>();
                InRow = false;
            }
        }

        public void OpenCell(uint colSpan, uint rowSpan, bool isHeader)
        {
            CurrentCell = new StringBuilder();
            CurrentColSpan = colSpan;
            CurrentRowSpan = rowSpan;
            CurrentIsHeader = isHeader;
            InCell = true;
        }

        public void CloseCell()
        {
            if (InCell)
            {
                CurrentRow.Add(new CellMeta
                {
                    Text = CurrentCell.ToString(),
                    ColSpan = CurrentColSpan,
                    RowSpan = CurrentRowSpan,
                    IsHeader = CurrentIsHeader,
                });
                CurrentCell = new StringBuilder();
                InCell = false;
                CurrentColSpan = 1;
                CurrentRowSpan = 1;
                CurrentIsHeader = false;
            }
        }

        public void PushText(string text)
        {
            if (InCell) CurrentCell.Append(text);
        }
    }

    private sealed class FigureContext
    {
        public string? ImgAlt;
        public string? ImgSrc;
        public bool InCaption;
        public StringBuilder? Caption;
    }

    private sealed class DefListContext
    {
        public string? CurrentTerm;
    }

    private sealed class HtmlWalker
    {
        private readonly string _src;
        private int _pos;

        public readonly List<StructNode> Nodes = new();

        private readonly StringBuilder _textBuf = new();
        private readonly List<InlineSpan> _inlineStack = new();
        private List<TextAnnotation> _annotations = new();

        private bool _inPre;
        private PreBlock? _preBlock;
        private TableAccumulator? _table;
        private bool _inListItem;
        private readonly StringBuilder _listItemText = new();
        private DefListContext? _defList;
        private bool _inDt;
        private bool _inDd;
        private readonly StringBuilder _dtText = new();
        private readonly StringBuilder _ddText = new();
        private FigureContext? _figure;
        private bool _inHead;
        private List<(string, string)> _metaEntries = new();

        public HtmlWalker(string src) => _src = src;

        public void Walk()
        {
            int n = _src.Length;
            while (_pos < n)
            {
                if (StartsWith(_pos, "<!--"))
                {
                    int end = _src.IndexOf("-->", _pos, StringComparison.Ordinal);
                    _pos = end >= 0 ? end + 3 : n;
                    continue;
                }

                if (_src[_pos] == '<') HandleTag();
                else HandleText();
            }
            FlushParagraph();
        }

        private bool StartsWith(int pos, string s)
        {
            if (pos + s.Length > _src.Length) return false;
            for (int i = 0; i < s.Length; i++)
                if (_src[pos + i] != s[i]) return false;
            return true;
        }

        // -------------------------------------------------------------------
        // Text
        // -------------------------------------------------------------------

        private void HandleText()
        {
            int start = _pos;
            while (_pos < _src.Length && _src[_pos] != '<') _pos++;
            string raw = _src.Substring(start, _pos - start);
            string decoded = DecodeEntities(raw);

            if (_table is not null) { _table.PushText(decoded); return; }
            if (_preBlock is not null) { _preBlock.Text.Append(decoded); return; }
            if (_inListItem) { _listItemText.Append(decoded); return; }
            if (_inDt) { _dtText.Append(decoded); return; }
            if (_inDd) { _ddText.Append(decoded); return; }
            if (_figure is { InCaption: true } fig)
            {
                fig.Caption ??= new StringBuilder();
                fig.Caption.Append(decoded);
                return;
            }
            _textBuf.Append(decoded);
        }

        // -------------------------------------------------------------------
        // Tags
        // -------------------------------------------------------------------

        private void HandleTag()
        {
            int gt = _src.IndexOf('>', _pos);
            if (gt < 0) { _pos = _src.Length; return; }
            string tagContent = _src.Substring(_pos + 1, gt - _pos - 1);
            _pos = gt + 1;

            if (tagContent.StartsWith('!') || tagContent.StartsWith('?')) return;

            bool isClosing = tagContent.StartsWith('/');
            bool isSelfClosing = tagContent.TrimEnd().EndsWith('/');
            string content = isClosing ? tagContent.Substring(1) : tagContent;
            content = content.TrimEnd('/').Trim();

            var (tagName, attrsStr) = SplitTagName(content);
            string tagLower = tagName.ToLowerInvariant();

            if (isClosing) HandleCloseTag(tagLower);
            else HandleOpenTag(tagLower, attrsStr, isSelfClosing);
        }

        private void HandleOpenTag(string tag, string attrsStr, bool isSelfClosing)
        {
            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    FlushParagraph();
                    _textBuf.Clear();
                    _annotations.Clear();
                    break;
                case "p":
                    FlushParagraph();
                    break;
                case "br":
                    if (_inPre || _preBlock is not null)
                        _preBlock?.Text.Append('\n');
                    else if (_inListItem)
                        _listItemText.Append('\n');
                    else
                        _textBuf.Append('\x01');
                    break;
                case "strong": case "b": PushInline(InlineKindTag.Bold); break;
                case "em": case "i": PushInline(InlineKindTag.Italic); break;
                case "code":
                    if (_inPre)
                    {
                        string? lang = ExtractLanguageFromClass(ExtractAttr(attrsStr, "class"));
                        _preBlock = new PreBlock { Language = lang };
                    }
                    else PushInline(InlineKindTag.Code);
                    break;
                case "u": case "ins": PushInline(InlineKindTag.Underline); break;
                case "s": case "del": case "strike": PushInline(InlineKindTag.Strikethrough); break;
                case "sub": PushInline(InlineKindTag.Subscript); break;
                case "sup": PushInline(InlineKindTag.Superscript); break;
                case "mark": PushInline(InlineKindTag.Highlight); break;
                case "a":
                {
                    string href = ExtractAttr(attrsStr, "href") ?? "";
                    string? title = ExtractAttr(attrsStr, "title");
                    PushInline(InlineKindTag.Link, href, title);
                    break;
                }
                case "pre":
                    FlushParagraph();
                    _inPre = true;
                    _preBlock = new PreBlock { Language = null };
                    break;
                case "blockquote":
                    FlushParagraph();
                    PushNode(NodeContent.Quote());
                    break;
                case "ul":
                    FlushParagraph();
                    PushNode(NodeContent.List(false));
                    break;
                case "ol":
                    FlushParagraph();
                    PushNode(NodeContent.List(true));
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
                case "tr":
                    _table?.OpenRow();
                    break;
                case "thead": case "tbody": case "tfoot":
                    break;
                case "th": case "td":
                    if (_table is not null)
                    {
                        uint colSpan = ParseU32(ExtractAttr(attrsStr, "colspan")) ?? 1;
                        uint rowSpan = ParseU32(ExtractAttr(attrsStr, "rowspan")) ?? 1;
                        _table.OpenCell(colSpan, rowSpan, tag == "th");
                    }
                    break;
                case "img":
                {
                    string? alt = ExtractAttr(attrsStr, "alt");
                    string? src = ExtractAttr(attrsStr, "src");
                    if (_figure is not null)
                    {
                        _figure.ImgAlt = alt;
                        _figure.ImgSrc = src;
                    }
                    else
                    {
                        FlushParagraph();
                        PushImageWithSrc(alt, src);
                    }
                    break;
                }
                case "figure":
                    FlushParagraph();
                    _figure = new FigureContext();
                    break;
                case "figcaption":
                    if (_figure is not null)
                    {
                        _figure.InCaption = true;
                        _figure.Caption = new StringBuilder();
                    }
                    break;
                case "dl":
                    FlushParagraph();
                    PushNode(NodeContent.DefinitionList());
                    _defList = new DefListContext();
                    break;
                case "dt":
                    FlushDefinitionItem();
                    _inDt = true;
                    _dtText.Clear();
                    break;
                case "dd":
                    _inDt = false;
                    if (_defList is not null)
                    {
                        string term = NormalizeWhitespace(_dtText.ToString());
                        if (term.Length > 0) _defList.CurrentTerm = term;
                    }
                    _dtText.Clear();
                    _inDd = true;
                    _ddText.Clear();
                    break;
                case "head":
                    _inHead = true;
                    _metaEntries.Clear();
                    break;
                case "meta":
                    if (_inHead)
                    {
                        string? name = ExtractAttr(attrsStr, "name");
                        string? contentVal = ExtractAttr(attrsStr, "content");
                        if (name is not null && contentVal is not null)
                            _metaEntries.Add((name, contentVal));
                    }
                    break;
                case "script": case "style":
                {
                    string closeTag = $"</{tag}>";
                    int closePos = _src.IndexOf(closeTag, _pos, StringComparison.Ordinal);
                    if (closePos >= 0)
                    {
                        string blockContent = _src.Substring(_pos, closePos - _pos);
                        _pos = closePos + closeTag.Length;
                        // script/style raw blocks are skipped in node→element conversion anyway.
                        _ = blockContent;
                    }
                    break;
                }
                case "math":
                {
                    // The walker is tag-level, so the subtree is taken verbatim and handed to the
                    // MathML converter as its own document rather than walked token by token —
                    // walking it would flatten the equation into stray inline text.
                    FlushParagraph();
                    if (isSelfClosing) break;
                    string mathClose = "</math>";
                    int mathEnd = _src.IndexOf(mathClose, _pos, StringComparison.Ordinal);
                    if (mathEnd < 0) break;
                    string inner = _src.Substring(_pos, mathEnd - _pos);
                    string rawXml = attrsStr.Length == 0 ? $"<math>{inner}</math>" : $"<math {attrsStr}>{inner}</math>";
                    _pos = mathEnd + mathClose.Length;
                    string latex = MathMl.ConvertMathmlStrToLatex(rawXml);
                    if (latex.Trim().Length != 0) PushNode(NodeContent.Formula(latex));
                    break;
                }
                case "video": case "audio":
                {
                    string closeTag = $"</{tag}>";
                    int closePos = _src.IndexOf(closeTag, _pos, StringComparison.Ordinal);
                    if (closePos >= 0) _pos = closePos + closeTag.Length;
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
                    // span/html/body/title/link and unknown: pass through without flushing
                    break;
            }
        }

        private void HandleCloseTag(string tag)
        {
            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    byte level = (byte)(tag[1] - '0');
                    if (level == 0) level = 1;
                    string text = _textBuf.ToString().Trim();
                    if (text.Length > 0)
                        PushHeading(level, text);
                    _textBuf.Clear();
                    _annotations.Clear();
                    _inlineStack.Clear();
                    break;
                }
                case "p":
                    FlushParagraph();
                    break;
                case "strong": case "b": PopInline(InlineKindTag.Bold); break;
                case "em": case "i": PopInline(InlineKindTag.Italic); break;
                case "code":
                    if (!_inPre) PopInline(InlineKindTag.Code);
                    break;
                case "u": case "ins": PopInline(InlineKindTag.Underline); break;
                case "s": case "del": case "strike": PopInline(InlineKindTag.Strikethrough); break;
                case "sub": PopInline(InlineKindTag.Subscript); break;
                case "sup": PopInline(InlineKindTag.Superscript); break;
                case "mark": PopInline(InlineKindTag.Highlight); break;
                case "a": PopInlineLink(); break;
                case "pre":
                    if (_preBlock is not null)
                    {
                        string text = _preBlock.Text.ToString().TrimEnd('\n');
                        if (text.Length > 0)
                            PushNode(NodeContent.Code(text, _preBlock.Language));
                        _preBlock = null;
                    }
                    _inPre = false;
                    break;
                case "blockquote":
                    FlushParagraph();
                    break;
                case "ul": case "ol":
                    FlushListItem();
                    break;
                case "li":
                    FlushListItem();
                    break;
                case "table":
                    if (_table is not null)
                    {
                        var table = _table;
                        _table = null;
                        table.CloseCell();
                        table.CloseRow();
                        if (table.Rows.Count > 0)
                            EmitTableWithSpans(table.Rows);
                    }
                    break;
                case "tr":
                    if (_table is not null)
                    {
                        _table.CloseCell();
                        _table.CloseRow();
                    }
                    break;
                case "th": case "td":
                    _table?.CloseCell();
                    break;
                case "dl":
                    FlushDefinitionItem();
                    _defList = null;
                    break;
                case "dt":
                    _inDt = false;
                    break;
                case "dd":
                    _inDd = false;
                    FlushDefinitionItem();
                    break;
                case "figure":
                    if (_figure is not null)
                    {
                        var fig = _figure;
                        _figure = null;
                        string? capTrimmed = fig.Caption?.ToString().Trim();
                        string? desc = !string.IsNullOrEmpty(capTrimmed) ? capTrimmed : fig.ImgAlt;
                        PushImageWithSrc(desc, fig.ImgSrc);
                    }
                    break;
                case "figcaption":
                    if (_figure is not null) _figure.InCaption = false;
                    break;
                case "head":
                    _inHead = false;
                    if (_metaEntries.Count > 0)
                    {
                        var entries = _metaEntries;
                        _metaEntries = new List<(string, string)>();
                        PushMetadataBlock(entries);
                    }
                    break;
                case "div": case "section": case "article": case "main": case "aside":
                case "header": case "footer": case "nav": case "details": case "summary":
                    FlushParagraph();
                    break;
            }
        }

        private void EmitTableWithSpans(List<List<CellMeta>> rows)
        {
            uint numRows = (uint)rows.Count;
            uint numCols = 0;
            foreach (var r in rows)
            {
                uint sum = 0;
                foreach (var c in r) sum += c.ColSpan;
                if (sum > numCols) numCols = sum;
            }

            bool hasSpans = rows.Any(r => r.Any(c => c.ColSpan > 1 || c.RowSpan > 1));

            if (!hasSpans)
            {
                var simple = rows.Select(r => r.Select(c => c.Text).ToList()).ToList();
                PushNode(NodeContent.Table(CellsToGrid(simple)));
                return;
            }

            var gridCells = new List<GridCell>();
            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                uint colOffset = 0;
                foreach (var cell in rows[rowIdx])
                {
                    gridCells.Add(new GridCell
                    {
                        Content = cell.Text,
                        Row = (uint)rowIdx,
                        Col = colOffset,
                        RowSpan = cell.RowSpan,
                        ColSpan = cell.ColSpan,
                        IsHeader = cell.IsHeader,
                    });
                    colOffset += cell.ColSpan;
                }
            }

            PushNode(NodeContent.Table(new TableGrid { Rows = numRows, Cols = numCols, Cells = gridCells }));
        }

        // -------------------------------------------------------------------
        // Inline formatting
        // -------------------------------------------------------------------

        private uint CurrentOffset() =>
            (uint)Encoding.UTF8.GetByteCount(_inListItem ? _listItemText.ToString() : _textBuf.ToString());

        private void PushInline(InlineKindTag kind, string? href = null, string? title = null) =>
            _inlineStack.Add(new InlineSpan { Kind = kind, TextStart = CurrentOffset(), Href = href, Title = title });

        private void PopInline(InlineKindTag expected)
        {
            int idx = _inlineStack.FindLastIndex(s => s.Kind == expected);
            if (idx < 0) return;
            var span = _inlineStack[idx];
            _inlineStack.RemoveAt(idx);
            uint end = CurrentOffset();
            if (end > span.TextStart)
                _annotations.Add(new TextAnnotation { Start = span.TextStart, End = end, Kind = MapAnnotation(span.Kind) });
        }

        private void PopInlineLink()
        {
            int idx = _inlineStack.FindLastIndex(s => s.Kind == InlineKindTag.Link);
            if (idx < 0) return;
            var span = _inlineStack[idx];
            _inlineStack.RemoveAt(idx);
            uint end = CurrentOffset();
            if (end > span.TextStart)
            {
                _annotations.Add(new TextAnnotation
                {
                    Start = span.TextStart,
                    End = end,
                    Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = span.Href ?? "", Title = span.Title },
                });
            }
        }

        private static AnnotationKind MapAnnotation(InlineKindTag kind) => kind switch
        {
            InlineKindTag.Bold => AnnotationKind.Bold,
            InlineKindTag.Italic => AnnotationKind.Italic,
            InlineKindTag.Code => new AnnotationKind { Which = AnnotationKind.Tag.Code },
            InlineKindTag.Underline => new AnnotationKind { Which = AnnotationKind.Tag.Underline },
            InlineKindTag.Strikethrough => new AnnotationKind { Which = AnnotationKind.Tag.Strikethrough },
            InlineKindTag.Subscript => new AnnotationKind { Which = AnnotationKind.Tag.Subscript },
            InlineKindTag.Superscript => new AnnotationKind { Which = AnnotationKind.Tag.Superscript },
            InlineKindTag.Highlight => new AnnotationKind { Which = AnnotationKind.Tag.Highlight },
            _ => AnnotationKind.Bold,
        };

        // -------------------------------------------------------------------
        // Flush helpers
        // -------------------------------------------------------------------

        private void FlushParagraph()
        {
            string text = NormalizeWhitespace(_textBuf.ToString());
            if (text.Length > 0)
            {
                var annotations = _annotations;
                _annotations = new List<TextAnnotation>();
                Nodes.Add(new StructNode { Content = NodeContent.Paragraph(text), Annotations = annotations });
            }
            _textBuf.Clear();
            _annotations.Clear();
            _inlineStack.Clear();
        }

        private void FlushListItem()
        {
            if (!_inListItem) return;
            _inListItem = false;
            string text = NormalizeWhitespace(_listItemText.ToString());
            if (text.Length > 0)
                PushNode(NodeContent.ListItem(text));
            _listItemText.Clear();
        }

        private void FlushDefinitionItem()
        {
            if (_inDd)
            {
                _inDd = false;
                if (_defList is not null)
                {
                    string definition = NormalizeWhitespace(_ddText.ToString());
                    if (_defList.CurrentTerm is { } term)
                    {
                        _defList.CurrentTerm = null;
                        PushNode(NodeContent.DefinitionItem(term, definition));
                    }
                }
                _ddText.Clear();
            }
            if (_inDt)
            {
                _inDt = false;
                if (_defList is not null)
                {
                    string term = NormalizeWhitespace(_dtText.ToString());
                    if (term.Length > 0) _defList.CurrentTerm = term;
                }
                _dtText.Clear();
            }
        }

        // -------------------------------------------------------------------
        // Node emission (flat)
        // -------------------------------------------------------------------

        private void PushNode(NodeContent content) => Nodes.Add(new StructNode { Content = content });

        // push_heading emits a Group wrapper then the Heading (mirrors DocumentStructureBuilder order).
        private void PushHeading(byte level, string text)
        {
            Nodes.Add(new StructNode
            {
                Content = new NodeContent { Which = NodeContent.Tag.Group, HeadingLevel = level, HeadingText = text },
            });
            Nodes.Add(new StructNode { Content = NodeContent.Heading(level, text) });
        }

        private void PushImageWithSrc(string? description, string? src) =>
            Nodes.Add(new StructNode
            {
                Content = new NodeContent { Which = NodeContent.Tag.Image, Description = description, Src = src },
            });

        private void PushMetadataBlock(List<(string, string)> entries) =>
            Nodes.Add(new StructNode
            {
                Content = new NodeContent
                {
                    Which = NodeContent.Tag.MetadataBlock,
                    Entries = entries.Select(e => new[] { e.Item1, e.Item2 }).ToList(),
                },
            });
    }

    // -----------------------------------------------------------------------
    // TableGrid construction (mirrors builder::cells_to_grid)
    // -----------------------------------------------------------------------

    private static TableGrid CellsToGrid(List<List<string>> cells)
    {
        uint rows = (uint)cells.Count;
        uint cols = cells.Count == 0 ? 0 : (uint)cells.Max(r => r.Count);
        var gridCells = new List<GridCell>();
        for (int rowIdx = 0; rowIdx < cells.Count; rowIdx++)
        {
            for (int colIdx = 0; colIdx < cells[rowIdx].Count; colIdx++)
            {
                gridCells.Add(new GridCell
                {
                    Content = cells[rowIdx][colIdx],
                    Row = (uint)rowIdx,
                    Col = (uint)colIdx,
                    RowSpan = 1,
                    ColSpan = 1,
                    IsHeader = rowIdx == 0,
                });
            }
        }
        return new TableGrid { Rows = rows, Cols = cols, Cells = gridCells };
    }

    // -----------------------------------------------------------------------
    // Utility functions
    // -----------------------------------------------------------------------

    // Matches Rust `char::is_ascii_whitespace`: space, tab, LF, CR, form feed (NOT vertical tab).
    private static bool IsAsciiWs(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static (string Name, string Attrs) SplitTagName(string content)
    {
        content = content.Trim();
        for (int i = 0; i < content.Length; i++)
        {
            if (IsAsciiWs(content[i]))
                return (content.Substring(0, i), content.Substring(i + 1));
        }
        return (content, "");
    }

    /// <summary>Extract an attribute value; handles quoted and unquoted forms. Mirrors `extract_attr`.</summary>
    internal static string? ExtractAttr(string attrs, string name)
    {
        string search = name + "=";
        int searchFrom = 0;
        int idx;
        while (true)
        {
            int candidate = attrs.IndexOf(search, searchFrom, StringComparison.Ordinal);
            if (candidate < 0) return null;
            int abs = candidate;
            if (abs == 0 || !IsAsciiAlphanumeric(attrs[abs - 1]))
            {
                idx = abs;
                break;
            }
            searchFrom = abs + 1;
        }

        string afterEq = attrs.Substring(idx + search.Length).TrimStart();
        if (afterEq.Length == 0) return null;
        char quote = afterEq[0];
        if (quote == '"' || quote == '\'')
        {
            string rest = afterEq.Substring(1);
            int end = rest.IndexOf(quote);
            if (end < 0) return null;
            return rest.Substring(0, end);
        }
        else
        {
            int end = afterEq.Length;
            for (int i = 0; i < afterEq.Length; i++)
            {
                char c = afterEq[i];
                if (IsAsciiWs(c) || c == '>')
                {
                    end = i;
                    break;
                }
            }
            return afterEq.Substring(0, end);
        }
    }

    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static string? ExtractLanguageFromClass(string? cls)
    {
        if (cls is null) return null;
        foreach (var token in cls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("language-", StringComparison.Ordinal)) return token.Substring("language-".Length);
            if (token.StartsWith("lang-", StringComparison.Ordinal)) return token.Substring("lang-".Length);
        }
        return null;
    }

    private static uint? ParseU32(string? s) =>
        s is not null && uint.TryParse(s, out var v) ? v : null;

    /// <summary>Decode a curated set of HTML entities. Mirrors `decode_entities`.</summary>
    internal static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        var outSb = new StringBuilder(s.Length);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i++];
            if (c == '&')
            {
                var entity = new StringBuilder();
                bool emittedRaw = false;
                while (i < n)
                {
                    char ec = s[i++];
                    if (ec == ';') break;
                    entity.Append(ec);
                    if (entity.Length > 10)
                    {
                        outSb.Append('&');
                        outSb.Append(entity);
                        entity.Clear();
                        emittedRaw = true;
                        break;
                    }
                }
                if (emittedRaw) continue;
                if (entity.Length == 0) continue;

                string e = entity.ToString();
                string? mapped = e switch
                {
                    "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'",
                    "nbsp" => " ", "copy" => "©", "reg" => "®", "trade" => "™",
                    "mdash" => "—", "ndash" => "–", "laquo" => "«", "raquo" => "»",
                    "hellip" => "…",
                    "eacute" => "é", "egrave" => "è", "ecirc" => "ê", "euml" => "ë",
                    "aacute" => "á", "agrave" => "à", "acirc" => "â", "auml" => "ä",
                    "iacute" => "í", "ocirc" => "ô", "ouml" => "ö", "uuml" => "ü",
                    "ntilde" => "ñ", "ccedil" => "ç",
                    "ldquo" => "“", "rdquo" => "”", "lsquo" => "‘", "rsquo" => "’",
                    "bull" => "•", "middot" => "·",
                    "euro" => "€", "pound" => "£", "yen" => "¥",
                    "times" => "×", "divide" => "÷", "plusmn" => "±",
                    _ => null,
                };
                if (mapped is not null)
                {
                    outSb.Append(mapped);
                }
                else if (e.StartsWith('#'))
                {
                    string numStr = e.Substring(1);
                    int? codePoint = null;
                    if (numStr.StartsWith('x') || numStr.StartsWith('X'))
                    {
                        if (int.TryParse(numStr.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var hv))
                            codePoint = hv;
                    }
                    else if (int.TryParse(numStr, out var dv))
                    {
                        codePoint = dv;
                    }
                    if (codePoint is int cp && cp >= 0 && cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF))
                        outSb.Append(char.ConvertFromUtf32(cp));
                    else
                    {
                        outSb.Append('&');
                        outSb.Append(e);
                        outSb.Append(';');
                    }
                }
                else
                {
                    outSb.Append('&');
                    outSb.Append(e);
                    outSb.Append(';');
                }
            }
            else
            {
                outSb.Append(c);
            }
        }
        return outSb.ToString();
    }

    /// <summary>Collapse whitespace runs; the \x01 sentinel from &lt;br&gt; becomes a newline. Mirrors `normalize_whitespace`.</summary>
    internal static string NormalizeWhitespace(string s)
    {
        var outSb = new StringBuilder(s.Length);
        bool lastWasSpace = true; // trim leading
        foreach (char c in s)
        {
            if (c == '\x01')
            {
                while (outSb.Length > 0 && outSb[^1] == ' ') outSb.Remove(outSb.Length - 1, 1);
                outSb.Append('\n');
                lastWasSpace = true;
            }
            else if (IsAsciiWs(c))
            {
                if (!lastWasSpace)
                {
                    outSb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                outSb.Append(c);
                lastWasSpace = false;
            }
        }
        if (outSb.Length > 0 && outSb[^1] == ' ') outSb.Remove(outSb.Length - 1, 1);
        return outSb.ToString();
    }
}
