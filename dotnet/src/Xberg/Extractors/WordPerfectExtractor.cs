using System.Text;

using Xberg.Core;
using Xberg.Internal.WordPerfect;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// WordPerfect (.wpd, .wp) extractor, ported from Rust <c>extractors/wordperfect.rs</c>.
/// </summary>
/// <remarks>
/// Walks the ordered, properly-nested event stream a WordPerfect parse produces and builds an
/// internal document from it. The parse itself is managed rather than a binding to libwpd — see
/// <see cref="WordPerfectReader"/>.
/// </remarks>
public sealed class WordPerfectExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.wordperfect" };

    public int Priority => 50;

    /// <summary>
    /// Upper bound on a cell's reported column span.
    /// </summary>
    /// <remarks>
    /// A corrupt table definition can report an implausible span, which would otherwise drive an
    /// unbounded copy loop when the cell is written out. No real table is this wide.
    /// </remarks>
    private const int MaxTableColSpan = 4096;

    /// <summary>An inline span awaiting its matching close event.</summary>
    private readonly record struct OpenSpan(int Start, AnnotationKind Kind);

    /// <summary>
    /// Accumulates text and in-progress inline spans for one unit of content — a paragraph, a
    /// table cell, or a diverted body such as a header or a note.
    /// </summary>
    private sealed class Ctx
    {
        public readonly StringBuilder Text = new();
        public readonly List<OpenSpan> Spans = new();
        public readonly List<TextAnnotation> Annotations = new();

        public void Append(string text) => Text.Append(text);

        public void OpenSpan(AnnotationKind kind) => Spans.Add(new OpenSpan(Utf8Length(), kind));

        /// <summary>
        /// Annotation offsets are UTF-8 byte offsets, matching the rest of the port's wire format.
        /// </summary>
        private int Utf8Length() => Encoding.UTF8.GetByteCount(Text.ToString());

        /// <summary>Record an annotation covering an open span's range; empty ranges are dropped.</summary>
        private void Record(OpenSpan open)
        {
            int end = Utf8Length();
            if (end > open.Start)
                Annotations.Add(new TextAnnotation
                {
                    Start = (uint)open.Start,
                    End = (uint)end,
                    Kind = open.Kind,
                });
        }

        /// <summary>Close the innermost open span of the same kind.</summary>
        public void CloseSpan(AnnotationKind kind)
        {
            for (int i = Spans.Count - 1; i >= 0; i--)
            {
                if (!SameKind(Spans[i].Kind, kind)) continue;
                var open = Spans[i];
                Spans.RemoveAt(i);
                Record(open);
                return;
            }
        }

        /// <summary>
        /// Close every still-open span at the current end.
        /// </summary>
        /// <remarks>
        /// Not the same as discarding them: a span still open when a boundary arrives — a
        /// paragraph flush, a cell close — covered real text, and dropping it would lose that
        /// formatting silently.
        /// </remarks>
        public void CloseOpenSpans()
        {
            for (int i = 0; i < Spans.Count; i++) Record(Spans[i]);
            Spans.Clear();
        }

        /// <summary>Drain the accumulated text and annotations.</summary>
        public (string Text, List<TextAnnotation> Annotations) Take()
        {
            CloseOpenSpans();
            string text = Text.ToString();
            Text.Clear();
            var annotations = MergeAdjacent(Annotations);
            Annotations.Clear();
            return (text, annotations);
        }
    }

    /// <summary>Two annotation kinds match for span closing and merging.</summary>
    /// <remarks>Links match only when they point at the same target.</remarks>
    private static bool SameKind(AnnotationKind a, AnnotationKind b) =>
        a.Which == b.Which && (a.Which != AnnotationKind.Tag.Link || a.Url == b.Url);

    /// <summary>Why a diverted body was opened, and what to do with its text when it closes.</summary>
    private enum DiversionKind { Header, Footer, Note, InlineNote, Aside }

    private sealed class Diversion
    {
        public DiversionKind Kind;
        public string Key = "";
        public string AsideKind = "";
        public Ctx Ctx = new();
    }

    /// <summary>
    /// Accumulates rows and cells for a table being walked.
    /// </summary>
    /// <remarks>
    /// Column placement is positional rather than taken from the reported column index, which a
    /// parse may not know and report as -1. A covered position — a grid slot merged away by a
    /// span — is filled from the same column in the previous row, so a vertically merged cell's
    /// content still appears in every row it visually covers.
    /// </remarks>
    private sealed class TableBuilder
    {
        public readonly List<List<string>> Rows = new();
        public List<string> CurrentRow = new();
        public bool CurrentRowIsHeader;
        public readonly List<int> HeaderRowIndices = new();
        public bool InCell;
        public Ctx CellCtx = new();
        public int CellColSpan = 1;

        public void StartRow(bool header)
        {
            CurrentRow = new List<string>();
            CurrentRowIsHeader = header;
        }

        public void StartCell(uint colSpan)
        {
            InCell = true;
            CellCtx = new Ctx();
            CellColSpan = (int)Math.Clamp(colSpan, 1, MaxTableColSpan);
        }

        public void EndCell()
        {
            var (text, annotations) = CellCtx.Take();
            // Cells are flat strings, so inline formatting is baked in as markdown rather than
            // dropped — the same choice the DOCX extractor makes.
            string rendered = AnnotationsToMarkdown(text, annotations);
            for (int i = 0; i < CellColSpan; i++) CurrentRow.Add(rendered);
            InCell = false;
        }

        public void CoveredCell()
        {
            int column = CurrentRow.Count;
            string filler = Rows.Count > 0 && column < Rows[^1].Count ? Rows[^1][column] : "";
            CurrentRow.Add(filler);
        }

        public void EndRow()
        {
            if (CurrentRowIsHeader) HeaderRowIndices.Add(Rows.Count);
            Rows.Add(CurrentRow);
            CurrentRow = new List<string>();
        }
    }

    /// <summary>Walks an event stream and pushes elements into a document builder.</summary>
    private sealed class Walker
    {
        private readonly InternalDocumentBuilder _builder;
        private readonly Ctx _main = new();
        private byte? _pendingHeadingLevel;
        private readonly List<bool> _listStack = new();
        private bool? _currentListItemOrdered;
        private TableBuilder? _table;
        private int _tableDepth;
        private readonly List<Diversion> _diversions = new();
        private uint _noteCounter;

        public Walker(InternalDocumentBuilder builder) => _builder = builder;

        private bool InCell => _table is { InCell: true } || _tableDepth > 0;

        private TableBuilder? ActiveTable => _tableDepth > 0 ? null : _table;

        /// <summary>The context text currently flows into.</summary>
        private Ctx Active =>
            _diversions.Count > 0 ? _diversions[^1].Ctx
            : _table is { InCell: true } ? _table.CellCtx
            : _main;

        private void Append(string text) => Active.Append(text);

        /// <summary>
        /// A paragraph break inside a cell or a diversion is only a line break.
        /// </summary>
        /// <remarks>
        /// Cells and diverted bodies are pushed as one unit when they close, so flushing a
        /// paragraph there would emit them out of reading order.
        /// </remarks>
        private void OnParagraphEnd()
        {
            if (InCell || _diversions.Count > 0) { Active.Append("\n"); return; }
            FlushParagraph();
        }

        private void FlushParagraph()
        {
            var (text, annotations) = _main.Take();
            if (text.Trim().Length == 0) { _pendingHeadingLevel = null; return; }

            if (_pendingHeadingLevel is { } level)
            {
                _pendingHeadingLevel = null;
                uint index = _builder.PushHeading(level, text, null, null);
                if (annotations.Count > 0) _builder.SetAnnotations(index, annotations);
            }
            else if (_currentListItemOrdered is { } ordered)
            {
                _builder.PushListItem(text, ordered, annotations, null, null);
            }
            else
            {
                _builder.PushParagraph(text, annotations, null, null);
            }
        }

        private void EnterListItem(bool ordered, byte level)
        {
            int depth = Math.Max(level, (byte)1);
            while (_listStack.Count > depth) { _builder.EndList(); _listStack.RemoveAt(_listStack.Count - 1); }

            if (_listStack.Count < depth)
            {
                while (_listStack.Count < depth) { _builder.PushList(ordered); _listStack.Add(ordered); }
            }
            else if (_listStack.Count > 0 && _listStack[^1] != ordered)
            {
                _builder.EndList();
                _listStack.RemoveAt(_listStack.Count - 1);
                _builder.PushList(ordered);
                _listStack.Add(ordered);
            }

            _currentListItemOrdered = ordered;
        }

        private void ExitListItem()
        {
            // Always drain, even for a whitespace-only item: its recorded annotation would
            // otherwise leak into the next element.
            FlushParagraph();
            _currentListItemOrdered = null;
        }

        /// <summary>
        /// Open a note: a reference at the anchor point, then divert until the note closes.
        /// </summary>
        /// <remarks>
        /// A note anchored inside a cell or another diversion cannot become a standalone
        /// reference element without landing out of reading order, so it folds inline instead.
        /// Spans open across the anchor are reopened afterwards so formatting continues.
        /// </remarks>
        private void EnterNote(bool endnote)
        {
            _noteCounter++;

            if (_diversions.Count > 0 || InCell)
            {
                Append($"[{_noteCounter}]");
                _diversions.Add(new Diversion { Kind = DiversionKind.InlineNote });
                return;
            }

            var openKinds = _main.Spans.Select(s => s.Kind).ToList();
            FlushParagraph();
            foreach (var kind in openKinds) _main.OpenSpan(kind);

            string key = (endnote ? "en" : "fn") + _noteCounter;
            _builder.PushFootnoteRef(_noteCounter.ToString(), key, null);
            _diversions.Add(new Diversion { Kind = DiversionKind.Note, Key = key });
        }

        private void ExitDiversion()
        {
            if (_diversions.Count == 0) return;
            var diversion = _diversions[^1];
            _diversions.RemoveAt(_diversions.Count - 1);

            var (text, annotations) = diversion.Ctx.Take();
            switch (diversion.Kind)
            {
                case DiversionKind.Header when text.Trim().Length > 0:
                    _builder.SetLayer(_builder.PushParagraph(text, annotations, null, null), ContentLayer.Header);
                    break;

                case DiversionKind.Footer when text.Trim().Length > 0:
                    _builder.SetLayer(_builder.PushParagraph(text, annotations, null, null), ContentLayer.Footer);
                    break;

                case DiversionKind.Aside when text.Trim().Length > 0:
                    _builder.SetAttributes(
                        _builder.PushParagraph(text, annotations, null, null),
                        new Dictionary<string, string> { ["aside_kind"] = diversion.AsideKind });
                    break;

                case DiversionKind.Note:
                    _builder.PushFootnoteDefinition(text, diversion.Key, null);
                    break;

                case DiversionKind.InlineNote:
                {
                    // Fold the body back into the surrounding text, which is active again now.
                    // Annotations are dropped: this is a rare nested-note fallback.
                    string body = text.Trim();
                    if (body.Length > 0) { Active.Append(" "); Active.Append(body); }
                    break;
                }
            }
        }

        public void Walk(IReadOnlyList<WpdEvent> events)
        {
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case WpdEventKind.Text: Append(e.Text); break;
                    case WpdEventKind.Tab: Append("\t"); break;
                    case WpdEventKind.Space: Append(" "); break;
                    case WpdEventKind.LineBreak: Append("\n"); break;
                    case WpdEventKind.Field: Append(e.Text); break;
                    case WpdEventKind.ParagraphEnd: OnParagraphEnd(); break;
                    case WpdEventKind.HeadingStart: _pendingHeadingLevel = e.Level; break;

                    case WpdEventKind.BoldStart: Active.OpenSpan(AnnotationKind.Bold); break;
                    case WpdEventKind.BoldEnd: Active.CloseSpan(AnnotationKind.Bold); break;
                    case WpdEventKind.ItalicStart: Active.OpenSpan(AnnotationKind.Italic); break;
                    case WpdEventKind.ItalicEnd: Active.CloseSpan(AnnotationKind.Italic); break;
                    case WpdEventKind.UnderlineStart: Active.OpenSpan(new AnnotationKind { Which = AnnotationKind.Tag.Underline }); break;
                    case WpdEventKind.UnderlineEnd: Active.CloseSpan(new AnnotationKind { Which = AnnotationKind.Tag.Underline }); break;
                    case WpdEventKind.StrikethroughStart: Active.OpenSpan(new AnnotationKind { Which = AnnotationKind.Tag.Strikethrough }); break;
                    case WpdEventKind.StrikethroughEnd: Active.CloseSpan(new AnnotationKind { Which = AnnotationKind.Tag.Strikethrough }); break;
                    case WpdEventKind.SuperscriptStart: Active.OpenSpan(new AnnotationKind { Which = AnnotationKind.Tag.Superscript }); break;
                    case WpdEventKind.SuperscriptEnd: Active.CloseSpan(new AnnotationKind { Which = AnnotationKind.Tag.Superscript }); break;
                    case WpdEventKind.SubscriptStart: Active.OpenSpan(new AnnotationKind { Which = AnnotationKind.Tag.Subscript }); break;
                    case WpdEventKind.SubscriptEnd: Active.CloseSpan(new AnnotationKind { Which = AnnotationKind.Tag.Subscript }); break;
                    case WpdEventKind.LinkStart: Active.OpenSpan(new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = e.Text }); break;
                    case WpdEventKind.LinkEnd: Active.CloseSpan(new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = e.Text }); break;

                    case WpdEventKind.ListItemStart: EnterListItem(e.Ordered, e.Level); break;
                    case WpdEventKind.ListItemEnd: ExitListItem(); break;

                    case WpdEventKind.TableStart:
                        // A table nested inside an open cell keeps the outer one: its text folds
                        // into the enclosing cell rather than clobbering the table being built.
                        if (_table is not null) _tableDepth++;
                        else _table = new TableBuilder();
                        break;

                    case WpdEventKind.RowStart: ActiveTable?.StartRow(e.Header); break;
                    case WpdEventKind.CellStart: ActiveTable?.StartCell(e.ColSpan); break;
                    case WpdEventKind.CoveredCell: ActiveTable?.CoveredCell(); break;
                    case WpdEventKind.CellEnd: ActiveTable?.EndCell(); break;
                    case WpdEventKind.RowEnd: ActiveTable?.EndRow(); break;

                    case WpdEventKind.TableEnd:
                        if (_tableDepth > 0) _tableDepth--;
                        else EndTable();
                        break;

                    case WpdEventKind.HeaderStart:
                        _diversions.Add(new Diversion { Kind = DiversionKind.Header });
                        break;
                    case WpdEventKind.FooterStart:
                        _diversions.Add(new Diversion { Kind = DiversionKind.Footer });
                        break;
                    case WpdEventKind.AsideStart:
                        _diversions.Add(new Diversion { Kind = DiversionKind.Aside, AsideKind = e.Text });
                        break;
                    case WpdEventKind.HeaderEnd:
                    case WpdEventKind.FooterEnd:
                    case WpdEventKind.AsideEnd:
                    case WpdEventKind.NoteEnd:
                        ExitDiversion();
                        break;

                    case WpdEventKind.NoteStart: EnterNote(e.Endnote); break;
                }
            }

            FlushParagraph();
            while (_listStack.Count > 0)
            {
                _builder.EndList();
                _listStack.RemoveAt(_listStack.Count - 1);
            }
            // A table left open by a truncated document still holds rows worth keeping.
            EndTable();
        }

        private void EndTable()
        {
            if (_table is not { } table) return;
            _table = null;
            if (table.Rows.Count == 0) return;

            uint index = _builder.PushTableFromCells(table.Rows, null, null);
            if (table.HeaderRowIndices.Count > 0)
                _builder.SetAttributes(index, new Dictionary<string, string>
                {
                    ["header_rows"] = string.Join(",", table.HeaderRowIndices),
                });
        }
    }

    /// <summary>
    /// Merge adjacent or overlapping annotations of the same kind.
    /// </summary>
    /// <remarks>
    /// Consecutive runs of identical formatting would otherwise produce back-to-back annotation
    /// fragments rather than one span, which every consumer then has to re-join.
    /// </remarks>
    private static List<TextAnnotation> MergeAdjacent(List<TextAnnotation> annotations)
    {
        if (annotations.Count < 2) return new List<TextAnnotation>(annotations);

        var sorted = annotations
            .OrderBy(a => a.Kind.Which)
            .ThenBy(a => a.Start)
            .ToList();

        var merged = new List<TextAnnotation>(sorted.Count);
        int i = 0;
        while (i < sorted.Count)
        {
            var current = sorted[i];
            int j = i + 1;
            while (j < sorted.Count && SameKind(sorted[j].Kind, current.Kind) && sorted[j].Start <= current.End)
            {
                current.End = Math.Max(current.End, sorted[j].End);
                j++;
            }
            merged.Add(current);
            i = j;
        }
        return merged;
    }

    /// <summary>The markdown markers for an annotation kind, if it has an inline form.</summary>
    /// <remarks>
    /// Superscript and subscript have no portable markdown form inside a table cell, so they
    /// render as plain text rather than as something a reader would misread.
    /// </remarks>
    private static (string Open, string Close)? MarkdownMarkers(AnnotationKind kind) => kind.Which switch
    {
        AnnotationKind.Tag.Bold => ("**", "**"),
        AnnotationKind.Tag.Italic => ("*", "*"),
        AnnotationKind.Tag.Strikethrough => ("~~", "~~"),
        AnnotationKind.Tag.Link => ("[", $"]({kind.Url})"),
        _ => null,
    };

    /// <summary>
    /// Render text with its annotations baked in as markdown, for contexts that store flat
    /// strings.
    /// </summary>
    /// <remarks>
    /// Ranges are half-open UTF-8 byte offsets: at each boundary, closings come before openings,
    /// so two spans meeting at a point nest rather than interleave.
    /// </remarks>
    internal static string AnnotationsToMarkdown(string text, IReadOnlyList<TextAnnotation> annotations)
    {
        if (annotations.Count == 0) return text;

        var result = new StringBuilder(text.Length + annotations.Count * 4);

        void Boundary(int position)
        {
            foreach (var a in annotations)
                if (a.End == position && MarkdownMarkers(a.Kind) is { } m) result.Append(m.Close);
            foreach (var a in annotations)
                if (a.Start == position && MarkdownMarkers(a.Kind) is { } m) result.Append(m.Open);
        }

        int offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            Boundary(offset);
            result.Append(rune.ToString());
            offset += rune.Utf8SequenceLength;
        }
        Boundary(offset);
        return result.ToString();
    }

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        return Extract(content.ToArray(), mimeType, config);
    }

    private static InternalDocument Extract(byte[] content, string mimeType, ExtractionConfig config)
    {
        var limits = config.SecurityLimits ?? new SecurityLimits();
        if (content.Length > limits.MaxContentSize)
            throw new ValidationException(
                $"WordPerfect file exceeds size limit ({content.Length} > {limits.MaxContentSize} bytes)");

        var parsed = WordPerfectReader.Parse(content);

        var builder = new InternalDocumentBuilder("wordperfect");
        builder.SetMetadata(new Metadata
        {
            Title = parsed.Metadata.Title,
            Subject = parsed.Metadata.Subject,
            Authors = parsed.Metadata.Author is { } author ? new List<string> { author } : null,
            Keywords = parsed.Metadata.Keywords is { } keywords ? new List<string> { keywords } : null,
        });
        new Walker(builder).Walk(parsed.Events);

        var document = builder.Build();
        if (document.Elements.Count == 0)
            throw new WpdParseException("WordPerfect document produced no extractable content");

        document.MimeType = mimeType;
        return document;
    }
}
