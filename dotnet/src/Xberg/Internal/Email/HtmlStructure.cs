// Ported from crates/xberg/src/extraction/html/structure.rs (build_document_structure /
// HtmlWalker) and crates/xberg/src/types/builder.rs (DocumentStructureBuilder). Used by the
// email extractor's HTML-body path (email.rs build_internal_document).
using System.Text;
using Xberg.Internal.Tables;
using Xberg.Types;

namespace Xberg.Internal.Email;

/// <summary>Builds a <see cref="DocumentStructure"/> tree from raw HTML (email HTML bodies).</summary>
internal static class HtmlStructure
{
    /// <summary>Port of Rust `build_document_structure`.</summary>
    internal static DocumentStructure Build(string html)
    {
        var builder = new DocStructBuilder();
        builder.SourceFormat("html");
        var walker = new HtmlWalker(html, builder);
        walker.Walk();
        return builder.Build();
    }
}

/// <summary>
/// Port of Rust `types::builder::DocumentStructureBuilder`: heading-driven section nesting
/// plus a container stack for quotes. Operates over the shared <see cref="DocumentStructure"/> type.
/// </summary>
internal sealed class DocStructBuilder
{
    private readonly DocumentStructure _doc = new();
    private readonly List<(byte Level, uint Idx)> _sectionStack = new();
    private readonly List<uint> _containerStack = new();

    internal void SourceFormat(string fmt) => _doc.SourceFormat = fmt;

    internal DocumentStructure Build()
    {
        _doc.FinalizeNodeTypes();
        return _doc;
    }

    private uint PushNodeRaw(NodeContent content, List<TextAnnotation>? annotations)
    {
        uint index = (uint)_doc.Nodes.Count;
        _doc.Nodes.Add(new DocumentNode
        {
            Content = content,
            Parent = null,
            Children = new(),
            ContentLayer = ContentLayer.Body,
            Annotations = annotations ?? new(),
        });
        return index;
    }

    private void AddChild(uint parent, uint child)
    {
        _doc.Nodes[(int)parent].Children.Add(child);
        _doc.Nodes[(int)child].Parent = parent;
    }

    private uint PushBodyNode(NodeContent content, List<TextAnnotation>? annotations)
    {
        uint idx = PushNodeRaw(content, annotations);
        if (_sectionStack.Count > 0) AddChild(_sectionStack[^1].Idx, idx);
        else if (_containerStack.Count > 0) AddChild(_containerStack[^1], idx);
        return idx;
    }

    internal uint PushHeading(byte level, string text)
    {
        while (_sectionStack.Count > 0 && _sectionStack[^1].Level >= level)
            _sectionStack.RemoveAt(_sectionStack.Count - 1);

        var group = new NodeContent { Which = NodeContent.Tag.Group, Label = null, HeadingLevel = level, HeadingText = text };
        uint groupIdx = PushNodeRaw(group, null);
        if (_sectionStack.Count > 0) AddChild(_sectionStack[^1].Idx, groupIdx);
        else if (_containerStack.Count > 0) AddChild(_containerStack[^1], groupIdx);

        uint headingIdx = PushNodeRaw(NodeContent.Heading(level, text), null);
        AddChild(groupIdx, headingIdx);

        _sectionStack.Add((level, groupIdx));
        return groupIdx;
    }

    internal uint PushParagraph(string text, List<TextAnnotation> annotations) =>
        PushBodyNode(NodeContent.Paragraph(text), annotations);

    internal uint PushList(bool ordered) => PushBodyNode(NodeContent.List(ordered), null);

    /// <summary>
    /// Push a list node as a child of <paramref name="parent"/> rather than of the
    /// section/container stack, so a sublist stays inside the item it is written in.
    /// </summary>
    internal uint PushNestedList(uint parent, bool ordered)
    {
        uint idx = PushNodeRaw(NodeContent.List(ordered), null);
        AddChild(parent, idx);
        return idx;
    }

    /// <summary>
    /// Push a list item as a child of <paramref name="list"/>. <paramref name="annotations"/>
    /// carries the item's inline formatting with offsets relative to <paramref name="text"/>,
    /// mirroring <see cref="PushParagraph"/>.
    /// </summary>
    internal uint PushListItem(uint list, string text, List<TextAnnotation>? annotations = null)
    {
        uint idx = PushNodeRaw(NodeContent.ListItem(text), annotations);
        AddChild(list, idx);
        return idx;
    }

    internal uint PushTable(TableGrid grid) => PushBodyNode(NodeContent.Table(grid), null);

    internal uint PushTableFromCells(List<List<string>> cells) => PushTable(CellsToGrid(cells));

    internal uint PushCode(string text, string? language) => PushBodyNode(NodeContent.Code(text, language), null);

    internal uint PushImageWithSrc(string? description, string? src) =>
        PushBodyNode(new NodeContent { Which = NodeContent.Tag.Image, Description = description, Src = src }, null);

    internal uint PushQuote()
    {
        uint idx = PushBodyNode(NodeContent.Quote(), null);
        _containerStack.Add(idx);
        return idx;
    }

    internal void ExitContainer()
    {
        if (_containerStack.Count > 0) _containerStack.RemoveAt(_containerStack.Count - 1);
    }

    internal uint PushDefinitionList() => PushBodyNode(NodeContent.DefinitionList(), null);

    internal uint PushDefinitionItem(uint list, string term, string definition)
    {
        uint idx = PushNodeRaw(NodeContent.DefinitionItem(term, definition), null);
        AddChild(list, idx);
        return idx;
    }

    internal uint PushRawBlock(string format, string content) =>
        PushBodyNode(new NodeContent { Which = NodeContent.Tag.RawBlock, Format = format, RawContent = content }, null);

    internal uint PushMetadataBlock(List<(string Key, string Value)> entries)
    {
        var nc = new NodeContent
        {
            Which = NodeContent.Tag.MetadataBlock,
            Entries = entries.Select(e => new[] { e.Key, e.Value }).ToList(),
        };
        return PushBodyNode(nc, null);
    }

    internal void SetAttributes(uint idx, Dictionary<string, string> attrs) =>
        _doc.Nodes[(int)idx].Attributes = attrs;

    private static TableGrid CellsToGrid(List<List<string>> cells)
    {
        uint rows = (uint)cells.Count;
        uint cols = cells.Count == 0 ? 0 : (uint)cells.Max(r => r.Count);
        var grid = new TableGrid { Rows = rows, Cols = cols };
        for (int r = 0; r < cells.Count; r++)
            for (int c = 0; c < cells[r].Count; c++)
                grid.Cells.Add(new GridCell
                {
                    Content = cells[r][c],
                    Row = (uint)r,
                    Col = (uint)c,
                    RowSpan = 1,
                    ColSpan = 1,
                    IsHeader = r == 0,
                });
        return grid;
    }
}

/// <summary>Port of Rust `HtmlWalker` — a lightweight tag-level HTML → structure walker.</summary>
internal sealed class HtmlWalker
{
    private enum InlineKindTag { Bold, Italic, Code, Underline, Strikethrough, Link, Subscript, Superscript, Highlight }

    private readonly struct InlineSpan
    {
        public InlineSpan(InlineKindTag kind, uint textStart, string? href, string? title)
        {
            Kind = kind; TextStart = textStart; Href = href; Title = title;
        }
        public InlineKindTag Kind { get; }
        public uint TextStart { get; }
        public string? Href { get; }
        public string? Title { get; }
    }

    private sealed class PreBlock { public string? Language; public StringBuilder Text = new(); }

    private sealed class CellMeta { public string Text = ""; public uint ColSpan = 1; public uint RowSpan = 1; public bool IsHeader; }

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

        public void OpenRow() { CurrentRow = new(); InRow = true; }
        public void CloseRow() { if (InRow) { Rows.Add(CurrentRow); CurrentRow = new(); InRow = false; } }
        public void OpenCell(uint colSpan, uint rowSpan, bool isHeader)
        {
            CurrentCell = new(); CurrentColSpan = colSpan; CurrentRowSpan = rowSpan; CurrentIsHeader = isHeader; InCell = true;
        }
        public void CloseCell()
        {
            if (InCell)
            {
                CurrentRow.Add(new CellMeta { Text = CurrentCell.ToString(), ColSpan = CurrentColSpan, RowSpan = CurrentRowSpan, IsHeader = CurrentIsHeader });
                InCell = false; CurrentColSpan = 1; CurrentRowSpan = 1; CurrentIsHeader = false;
            }
        }
        public void PushText(string t) { if (InCell) CurrentCell.Append(t); }
    }

    /// <summary>One open <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> level.</summary>
    private sealed class ListContext
    {
        /// <summary>The <c>List</c> node this level emitted.</summary>
        public uint NodeIdx;

        /// <summary>
        /// Whether an <c>&lt;li&gt;</c> at this nesting level is currently open.
        /// </summary>
        /// <remarks>
        /// Distinct from <c>_inListItem</c>, which only says whether text is being buffered right
        /// now: descending into a sublist flushes and clears that flag while the enclosing item is
        /// still open. This one survives the descent, so closing the sublist can resume buffering
        /// into the enclosing item.
        /// </remarks>
        public bool ItemOpen;

        /// <summary>
        /// The <c>ListItem</c> node most recently emitted at this level, if the currently open
        /// <c>&lt;li&gt;</c> has already produced one. Reset when an <c>&lt;li&gt;</c> opens, so it
        /// never names a previous sibling's item; a sublist opening while <see cref="ItemOpen"/> is
        /// set is parented under this node.
        /// </summary>
        public uint? LastItemIdx;
    }

    private sealed class DefListContext { public uint ListIdx; public string? CurrentTerm; }

    private sealed class FigureContext
    {
        public string? ImgAlt; public string? ImgSrc; public string? ImgWidth; public string? ImgHeight;
        public StringBuilder? Caption; public bool InCaption;
    }

    private readonly string _src;
    private int _pos;
    private readonly DocStructBuilder _builder;

    private readonly StringBuilder _textBuf = new();
    private readonly List<InlineSpan> _inlineStack = new();
    private List<TextAnnotation> _annotations = new();

    private bool _inPre;
    private PreBlock? _preBlock;
    private TableAccumulator? _table;
    private readonly List<ListContext> _listStack = new();
    private bool _inListItem;

    /// <summary>
    /// Number of <c>&lt;table&gt;</c> elements open inside the accumulated table. A nested table is
    /// flattened into the enclosing cell instead of replacing the enclosing table.
    /// </summary>
    private int _nestedTableDepth;
    private readonly StringBuilder _listItemText = new();
    private DefListContext? _defList;
    private bool _inDt;
    private bool _inDd;
    private readonly StringBuilder _dtText = new();
    private readonly StringBuilder _ddText = new();
    private FigureContext? _figure;
    private bool _inHead;
    private List<(string, string)> _metaEntries = new();
    private string? _pendingClasses;

    internal HtmlWalker(string src, DocStructBuilder builder)
    {
        _src = src;
        _builder = builder;
    }

    internal void Walk()
    {
        while (_pos < _src.Length)
        {
            if (StartsWithAt(_pos, "<!--"))
            {
                int end = _src.IndexOf("-->", _pos, StringComparison.Ordinal);
                _pos = end < 0 ? _src.Length : end + 3;
                continue;
            }
            if (_src[_pos] == '<') HandleTag();
            else HandleText();
        }
        FlushParagraph();
    }

    private bool StartsWithAt(int pos, string s) =>
        pos + s.Length <= _src.Length && string.CompareOrdinal(_src, pos, s, 0, s.Length) == 0;

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
        if (_figure is { InCaption: true } fig) { (fig.Caption ??= new()).Append(decoded); return; }

        _textBuf.Append(decoded);
    }

    private void HandleTag()
    {
        int end = _src.IndexOf('>', _pos);
        if (end < 0) { _pos = _src.Length; return; }
        string tagContent = _src.Substring(_pos + 1, end - (_pos + 1));
        _pos = end + 1;

        if (tagContent.StartsWith('!') || tagContent.StartsWith('?')) return;

        bool isClosing = tagContent.StartsWith('/');
        string content = isClosing ? tagContent.Substring(1) : tagContent;
        content = content.TrimEnd('/').Trim();

        var (tagName, attrs) = SplitTagName(content);
        string tag = tagName.ToLowerInvariant();

        if (isClosing) HandleCloseTag(tag);
        else HandleOpenTag(tag, attrs);
    }

    private void HandleOpenTag(string tag, string attrs)
    {
        switch (tag)
        {
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                FlushParagraph();
                _textBuf.Clear();
                _annotations.Clear();
                _pendingClasses = ExtractAttr(attrs, "class");
                break;
            case "p":
                FlushParagraph();
                _pendingClasses = ExtractAttr(attrs, "class");
                break;
            case "br":
                if (_inPre || _preBlock is not null) { _preBlock?.Text.Append('\n'); }
                else if (_inListItem) { _listItemText.Append('\n'); }
                else { _textBuf.Append('\x01'); }
                break;
            case "strong": case "b": PushInline(InlineKindTag.Bold); break;
            case "em": case "i": PushInline(InlineKindTag.Italic); break;
            case "code":
                if (_inPre)
                {
                    string? lang = ExtractLanguageFromClass(ExtractAttr(attrs, "class"));
                    _preBlock = new PreBlock { Language = lang, Text = new() };
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
                    string href = ExtractAttr(attrs, "href") ?? "";
                    string? title = ExtractAttr(attrs, "title");
                    _inlineStack.Add(new InlineSpan(InlineKindTag.Link, CurrentInlineOffset(), href, title));
                    break;
                }
            case "pre":
                FlushParagraph();
                _inPre = true;
                _preBlock = new PreBlock { Language = null, Text = new() };
                break;
            case "blockquote":
                {
                    FlushParagraph();
                    uint idx = _builder.PushQuote();
                    string? cite = ExtractAttr(attrs, "cite");
                    if (cite is not null) _builder.SetAttributes(idx, new() { ["cite"] = cite });
                    break;
                }
            case "ul":
                {
                    // Flush any pending parent <li> text against the still-current (outer) list
                    // before descending, so it does not get misattributed to the list about to be
                    // pushed. The item is flushed *before* the paragraph: while an <li> is open
                    // HandleText buffers into _listItemText, so the item is the live context and
                    // owns the pending annotations, which FlushParagraph would otherwise discard on
                    // its way past an empty paragraph buffer.
                    FlushListItem();
                    FlushParagraph();
                    uint idx = PushListNode(false);
                    _listStack.Add(new ListContext { NodeIdx = idx });
                    break;
                }
            case "ol":
                {
                    FlushListItem();
                    FlushParagraph();
                    uint idx = PushListNode(true);
                    string? startVal = ExtractAttr(attrs, "start");
                    if (startVal is not null) _builder.SetAttributes(idx, new() { ["start"] = startVal });
                    _listStack.Add(new ListContext { NodeIdx = idx });
                    break;
                }
            case "li":
                FlushListItem();
                _inListItem = true;
                _listItemText.Clear();
                if (_listStack.Count > 0)
                {
                    _listStack[^1].ItemOpen = true;
                    _listStack[^1].LastItemIdx = null;
                }
                break;
            case "table":
                if (_table is not null)
                {
                    _nestedTableDepth++;
                    _table.PushText(" ");
                }
                else
                {
                    FlushParagraph();
                    _table = new TableAccumulator();
                }
                break;
            case "tr": case "thead": case "tbody": case "tfoot":
                if (tag == "tr" && _nestedTableDepth == 0 && _table is not null) _table.OpenRow();
                break;
            case "th": case "td":
                if (_nestedTableDepth > 0)
                {
                    _table?.PushText(" ");
                }
                else if (_table is not null)
                {
                    // Clamped at parse time so an out-of-range attribute (a hostile
                    // colspan="4294967295", say) never enters CellMeta/GridCell at all, on top of
                    // the same clamp GridFlatten applies when it consumes these values. The bounds
                    // are the HTML Living Standard's own caps on these attributes.
                    uint colSpan = Math.Clamp(ParseUintOr(ExtractAttr(attrs, "colspan"), 1), 1u, (uint)GridFlatten.MaxColSpan);
                    uint rowSpan = Math.Clamp(ParseUintOr(ExtractAttr(attrs, "rowspan"), 1), 1u, (uint)GridFlatten.MaxRowSpan);
                    _table.OpenCell(colSpan, rowSpan, tag == "th");
                }
                break;
            case "img":
                {
                    string? alt = ExtractAttr(attrs, "alt");
                    string? src = ExtractAttr(attrs, "src");
                    string? width = ExtractAttr(attrs, "width");
                    string? height = ExtractAttr(attrs, "height");
                    if (_figure is not null)
                    {
                        _figure.ImgAlt = alt; _figure.ImgSrc = src; _figure.ImgWidth = width; _figure.ImgHeight = height;
                    }
                    else
                    {
                        FlushParagraph();
                        uint idx = _builder.PushImageWithSrc(alt, src);
                        if (width is not null || height is not null)
                        {
                            var d = new Dictionary<string, string>();
                            if (width is not null) d["width"] = width;
                            if (height is not null) d["height"] = height;
                            _builder.SetAttributes(idx, d);
                        }
                    }
                    break;
                }
            case "figure":
                FlushParagraph();
                _figure = new FigureContext();
                break;
            case "figcaption":
                if (_figure is not null) { _figure.InCaption = true; _figure.Caption = new(); }
                break;
            case "dl":
                {
                    FlushParagraph();
                    uint idx = _builder.PushDefinitionList();
                    _defList = new DefListContext { ListIdx = idx, CurrentTerm = null };
                    break;
                }
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
                _metaEntries = new();
                break;
            case "meta":
                if (_inHead)
                {
                    string? name = ExtractAttr(attrs, "name");
                    string? contentVal = ExtractAttr(attrs, "content");
                    if (name is not null && contentVal is not null) _metaEntries.Add((name, contentVal));
                }
                break;
            case "script": case "style":
                {
                    string closeTag = $"</{tag}>";
                    int closePos = _src.IndexOf(closeTag, _pos, StringComparison.Ordinal);
                    if (closePos >= 0)
                    {
                        string block = _src.Substring(_pos, closePos - _pos);
                        _pos = closePos + closeTag.Length;
                        if (block.Trim().Length > 0) _builder.PushRawBlock(tag, block.Trim());
                    }
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
            // "span" | "html" | "body" | "title" | "link" and unknown: pass through.
            default:
                break;
        }
    }

    private void HandleCloseTag(string tag)
    {
        switch (tag)
        {
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    byte level = byte.TryParse(tag.AsSpan(1), out var lv) ? lv : (byte)1;
                    string text = NormalizeWhitespace(_textBuf.ToString()).Trim();
                    if (text.Length > 0)
                    {
                        uint idx = _builder.PushHeading(level, text);
                        if (_pendingClasses is { } classes)
                        {
                            _builder.SetAttributes(idx, new() { ["class"] = classes });
                            _pendingClasses = null;
                        }
                    }
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
                    if (text.Length > 0) _builder.PushCode(text, _preBlock.Language);
                    _preBlock = null;
                }
                _inPre = false;
                break;
            case "blockquote":
                FlushParagraph();
                _builder.ExitContainer();
                break;
            case "ul": case "ol":
                FlushListItem();
                if (_listStack.Count > 0) _listStack.RemoveAt(_listStack.Count - 1);
                // Content can resume in the enclosing <li> after a sublist closes
                // (<li>before<ul>…</ul>after</li>). Without restoring the flag that text falls
                // through to the paragraph buffer and is emitted as a bare paragraph instead of
                // staying list content.
                _inListItem = _listStack.Count > 0 && _listStack[^1].ItemOpen;
                break;
            case "li":
                FlushListItem();
                if (_listStack.Count > 0) _listStack[^1].ItemOpen = false;
                break;
            case "table":
                if (_nestedTableDepth > 0)
                {
                    _nestedTableDepth--;
                }
                else if (_table is not null)
                {
                    _table.CloseCell();
                    _table.CloseRow();
                    if (_table.Rows.Count > 0) EmitTableWithSpans(_table.Rows);
                    _table = null;
                }
                break;
            case "tr":
                if (_nestedTableDepth == 0 && _table is not null) { _table.CloseCell(); _table.CloseRow(); }
                break;
            case "th": case "td":
                if (_nestedTableDepth == 0) _table?.CloseCell();
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
                if (_figure is { } fig)
                {
                    _figure = null;
                    string? desc = fig.Caption?.ToString().Trim();
                    if (string.IsNullOrEmpty(desc)) desc = fig.ImgAlt;
                    uint idx = _builder.PushImageWithSrc(desc, fig.ImgSrc);
                    if (fig.ImgWidth is not null || fig.ImgHeight is not null)
                    {
                        var d = new Dictionary<string, string>();
                        if (fig.ImgWidth is not null) d["width"] = fig.ImgWidth;
                        if (fig.ImgHeight is not null) d["height"] = fig.ImgHeight;
                        _builder.SetAttributes(idx, d);
                    }
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
                    _metaEntries = new();
                    _builder.PushMetadataBlock(entries);
                }
                break;
            case "div": case "section": case "article": case "main": case "aside":
            case "header": case "footer": case "nav": case "details": case "summary":
                FlushParagraph();
                break;
            default:
                break;
        }
    }

    private void EmitTableWithSpans(List<List<CellMeta>> rows)
    {
        uint numRows = (uint)rows.Count;
        uint numCols = rows.Count == 0 ? 0 : (uint)rows.Max(r => r.Sum(c => (int)c.ColSpan));
        bool hasSpans = rows.Any(r => r.Any(c => c.ColSpan > 1 || c.RowSpan > 1));

        if (!hasSpans)
        {
            var simple = rows.Select(r => r.Select(c => c.Text).ToList()).ToList();
            _builder.PushTableFromCells(simple);
            return;
        }

        var grid = new TableGrid { Rows = numRows, Cols = numCols };
        for (int r = 0; r < rows.Count; r++)
        {
            uint colOffset = 0;
            foreach (var cell in rows[r])
            {
                grid.Cells.Add(new GridCell
                {
                    Content = cell.Text,
                    Row = (uint)r,
                    Col = colOffset,
                    RowSpan = cell.RowSpan,
                    ColSpan = cell.ColSpan,
                    IsHeader = cell.IsHeader,
                });
                colOffset += cell.ColSpan;
            }
        }
        _builder.PushTable(grid);
    }

    // --- inline helpers ---

    private uint CurrentInlineOffset() =>
        _inListItem
            ? (uint)Encoding.UTF8.GetByteCount(_listItemText.ToString())
            : (uint)Encoding.UTF8.GetByteCount(_textBuf.ToString());

    private void PushInline(InlineKindTag kind) =>
        _inlineStack.Add(new InlineSpan(kind, CurrentInlineOffset(), null, null));

    private void PopInline(InlineKindTag expected)
    {
        int idx = _inlineStack.FindLastIndex(s => s.Kind == expected);
        if (idx < 0) return;
        var span = _inlineStack[idx];
        _inlineStack.RemoveAt(idx);
        uint end = CurrentInlineOffset();
        if (end > span.TextStart)
        {
            var kind = expected switch
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
            _annotations.Add(new TextAnnotation { Start = span.TextStart, End = end, Kind = kind });
        }
    }

    private void PopInlineLink()
    {
        int idx = _inlineStack.FindLastIndex(s => s.Kind == InlineKindTag.Link);
        if (idx < 0) return;
        var span = _inlineStack[idx];
        _inlineStack.RemoveAt(idx);
        uint end = CurrentInlineOffset();
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

    // --- flush helpers ---

    private void FlushParagraph()
    {
        string text = NormalizeWhitespace(_textBuf.ToString());
        if (text.Length > 0)
        {
            var anns = _annotations;
            _annotations = new();
            uint idx = _builder.PushParagraph(text, anns);
            if (_pendingClasses is { } classes)
            {
                _builder.SetAttributes(idx, new() { ["class"] = classes });
                _pendingClasses = null;
            }
        }
        _textBuf.Clear();
        _annotations.Clear();
        _inlineStack.Clear();
    }

    /// <summary>
    /// Create the <c>List</c> node for a <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> start tag, parented at
    /// the level the markup actually nests it at.
    /// </summary>
    /// <remarks>
    /// A sublist is a child of the <c>&lt;li&gt;</c> it is written inside, so that a consumer
    /// walking the tree renders it before the item's trailing text rather than after the whole
    /// outer list. Going through <c>PushList</c> instead parents under the section/container stack,
    /// which makes every sublist a root-level sibling.
    /// <para>
    /// Two shapes have no item node to hang the sublist on: <c>&lt;li&gt;&lt;ul&gt;…</c> (the item
    /// has no text of its own, so no <c>ListItem</c> was emitted) and a <c>&lt;ul&gt;</c> sitting
    /// directly inside another <c>&lt;ul&gt;</c> with no <c>&lt;li&gt;</c> open. Both fall back to
    /// the enclosing <c>List</c> node, which keeps the sublist inside the list subtree without
    /// minting an empty item.
    /// </para>
    /// </remarks>
    private uint PushListNode(bool ordered)
    {
        if (_listStack.Count == 0) return _builder.PushList(ordered);
        var ctx = _listStack[^1];
        uint parent = ctx.ItemOpen ? ctx.LastItemIdx ?? ctx.NodeIdx : ctx.NodeIdx;
        return _builder.PushNestedList(parent, ordered);
    }

    /// <summary>
    /// Emit the buffered <c>&lt;li&gt;</c> text as a <c>ListItem</c> and reset the inline state
    /// that belonged to it.
    /// </summary>
    /// <remarks>
    /// The annotation buffer is taken (not just read) and the inline stack is cleared, for the same
    /// reason <c>FlushParagraph</c> does both: <c>PopInline</c> measures spans against
    /// <c>_listItemText</c>, which this method empties. Anything still referring to it after the
    /// flush — a completed annotation left behind, or a span whose closing tag has not arrived yet —
    /// would resolve against whatever text is buffered next and annotate an unrelated node at
    /// meaningless offsets.
    /// </remarks>
    private void FlushListItem()
    {
        if (!_inListItem) return;
        _inListItem = false;
        string text = NormalizeWhitespace(_listItemText.ToString());
        var annotations = _annotations;
        _annotations = new List<TextAnnotation>();
        if (text.Length > 0 && _listStack.Count > 0)
        {
            uint itemIdx = _builder.PushListItem(_listStack[^1].NodeIdx, text, annotations);
            _listStack[^1].LastItemIdx = itemIdx;
        }
        _listItemText.Clear();
        _inlineStack.Clear();
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
                    _builder.PushDefinitionItem(_defList.ListIdx, term, definition);
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

    // --- utilities (ported) ---

    private static (string Name, string Attrs) SplitTagName(string content)
    {
        content = content.Trim();
        for (int i = 0; i < content.Length; i++)
        {
            if (char.IsWhiteSpace(content[i]))
                return (content.Substring(0, i), content.Substring(i + 1));
        }
        return (content, "");
    }

    private static string? ExtractAttr(string? attrs, string name)
    {
        if (attrs is null) return null;
        string search = name + "=";
        int searchFrom = 0;
        int idx;
        while (true)
        {
            int candidate = attrs.IndexOf(search, searchFrom, StringComparison.Ordinal);
            if (candidate < 0) return null;
            if (candidate == 0 || !IsAsciiAlphanumeric(attrs[candidate - 1])) { idx = candidate; break; }
            searchFrom = candidate + 1;
        }
        string afterEq = attrs.Substring(idx + search.Length).TrimStart();
        if (afterEq.Length == 0) return null;
        char quote = afterEq[0];
        if (quote == '"' || quote == '\'')
        {
            string rest = afterEq.Substring(1);
            int endq = rest.IndexOf(quote);
            if (endq < 0) return null;
            return rest.Substring(0, endq);
        }
        else
        {
            int end = 0;
            while (end < afterEq.Length && !char.IsWhiteSpace(afterEq[end]) && afterEq[end] != '>') end++;
            return afterEq.Substring(0, end);
        }
    }

    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static string? ExtractLanguageFromClass(string? cls)
    {
        if (cls is null) return null;
        foreach (var c in cls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (c.StartsWith("language-", StringComparison.Ordinal)) return c.Substring("language-".Length);
            if (c.StartsWith("lang-", StringComparison.Ordinal)) return c.Substring("lang-".Length);
        }
        return null;
    }

    private static uint ParseUintOr(string? s, uint fallback) =>
        s is not null && uint.TryParse(s, out var v) ? v : fallback;

    internal static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        var outSb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '&')
            {
                var entity = new StringBuilder();
                bool overflow = false;
                while (i < s.Length)
                {
                    char ec = s[i++];
                    if (ec == ';') break;
                    entity.Append(ec);
                    if (entity.Length > 10)
                    {
                        outSb.Append('&');
                        outSb.Append(entity);
                        entity.Clear();
                        overflow = true;
                        break;
                    }
                }
                if (entity.Length == 0)
                {
                    if (!overflow) { /* '&' with no entity body: matched ';' immediately or EOF */ }
                    continue;
                }
                AppendEntity(outSb, entity.ToString());
            }
            else outSb.Append(c);
        }
        return outSb.ToString();
    }

    private static void AppendEntity(StringBuilder outSb, string entity)
    {
        switch (entity)
        {
            case "amp": outSb.Append('&'); break;
            case "lt": outSb.Append('<'); break;
            case "gt": outSb.Append('>'); break;
            case "quot": outSb.Append('"'); break;
            case "apos": outSb.Append('\''); break;
            case "nbsp": outSb.Append(' '); break;
            case "copy": outSb.Append('©'); break;
            case "reg": outSb.Append('®'); break;
            case "trade": outSb.Append('™'); break;
            case "mdash": outSb.Append('—'); break;
            case "ndash": outSb.Append('–'); break;
            case "laquo": outSb.Append('«'); break;
            case "raquo": outSb.Append('»'); break;
            case "hellip": outSb.Append('…'); break;
            case "eacute": outSb.Append('é'); break;
            case "egrave": outSb.Append('è'); break;
            case "ecirc": outSb.Append('ê'); break;
            case "euml": outSb.Append('ë'); break;
            case "aacute": outSb.Append('á'); break;
            case "agrave": outSb.Append('à'); break;
            case "acirc": outSb.Append('â'); break;
            case "auml": outSb.Append('ä'); break;
            case "iacute": outSb.Append('í'); break;
            case "ocirc": outSb.Append('ô'); break;
            case "ouml": outSb.Append('ö'); break;
            case "uuml": outSb.Append('ü'); break;
            case "ntilde": outSb.Append('ñ'); break;
            case "ccedil": outSb.Append('ç'); break;
            case "ldquo": outSb.Append('“'); break;
            case "rdquo": outSb.Append('”'); break;
            case "lsquo": outSb.Append('‘'); break;
            case "rsquo": outSb.Append('’'); break;
            case "bull": outSb.Append('•'); break;
            case "middot": outSb.Append('·'); break;
            case "euro": outSb.Append('€'); break;
            case "pound": outSb.Append('£'); break;
            case "yen": outSb.Append('¥'); break;
            case "times": outSb.Append('×'); break;
            case "divide": outSb.Append('÷'); break;
            case "plusmn": outSb.Append('±'); break;
            default:
                if (entity.StartsWith('#'))
                {
                    string num = entity.Substring(1);
                    int? cp = null;
                    if (num.StartsWith('x') || num.StartsWith('X'))
                    {
                        if (int.TryParse(num.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var hv)) cp = hv;
                    }
                    else if (int.TryParse(num, out var dv)) cp = dv;
                    if (cp is int code && code >= 0 && code <= 0x10FFFF && !(code >= 0xD800 && code <= 0xDFFF))
                    {
                        outSb.Append(char.ConvertFromUtf32(code));
                        return;
                    }
                }
                outSb.Append('&');
                outSb.Append(entity);
                outSb.Append(';');
                break;
        }
    }

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
            else if (IsAsciiWhitespace(c))
            {
                if (!lastWasSpace) { outSb.Append(' '); lastWasSpace = true; }
            }
            else { outSb.Append(c); lastWasSpace = false; }
        }
        if (outSb.Length > 0 && outSb[^1] == ' ') outSb.Remove(outSb.Length - 1, 1);
        return outSb.ToString();
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
}
