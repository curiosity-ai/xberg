using System.Text;

namespace Xberg.Internal.WordPerfect;

/// <summary>
/// Accumulates parser callbacks into the event stream.
/// </summary>
/// <remarks>
/// <para>
/// Its job is buffering: a parser emits one character at a time, and the extractor wants runs of
/// text. Characters accumulate until something that is not a character arrives, which is what
/// turns thousands of per-character calls into one text event per run.
/// </para>
/// <para>
/// It also suppresses the trailing paragraph end that every WordPerfect document carries. A
/// document ends with a hard return, and emitting it would append an empty final paragraph to
/// every extraction.
/// </para>
/// </remarks>
internal sealed class WpdEventSink
{
    private readonly List<WpdEvent> _events;
    private readonly StringBuilder _pending = new();

    /// <summary>
    /// While set, everything the parser emits is discarded.
    /// </summary>
    /// <remarks>
    /// WordPerfect keeps deleted text in the file, bracketed as invalid, so that an undo can
    /// bring it back. It is still there to be read, and reading it puts text the author removed
    /// into the extraction.
    /// </remarks>
    public bool Discarding { get; set; }

    /// <summary>
    /// Whether an automatic number is being read.
    /// </summary>
    /// <remarks>
    /// A note's anchor number, a list item's number and a page number are all written into the
    /// document as literal characters, bracketed by codes that say what they are. They belong to
    /// the thing that owns them rather than to the sentence around them, and the renderer
    /// produces its own — so reading them leaves bare digits glued to the neighbouring words.
    /// </remarks>
    private bool _inNumberText;

    /// <summary>Whether anything the parser reports right now should be thrown away.</summary>
    private bool Suppressed => Discarding || _inNumberText;

    /// <summary>Begin an automatic number, whose characters belong to no sentence.</summary>
    public void NumberTextOn()
    {
        if (Discarding) return;
        Flush();
        _inNumberText = true;
    }

    /// <summary>End an automatic number, discarding the characters it held.</summary>
    public void NumberTextOff()
    {
        if (Discarding) return;
        _inNumberText = false;
        _pending.Clear();
    }

    public WpdEventSink(List<WpdEvent> events) => _events = events;

    /// <summary>Begin a note: what follows up to <see cref="NoteOff"/> is its anchor number.</summary>
    public void NoteOn()
    {
        NumberTextOn();
    }

    /// <summary>
    /// Open the note itself, dropping the anchor number that preceded it.
    /// </summary>
    /// <remarks>
    /// The note opens here rather than at <see cref="NoteOn"/> because that is where the body
    /// follows: WordPerfect places the anchor number first and the note's text after it.
    /// </remarks>
    public void NoteBegin(bool endnote)
    {
        if (Discarding) return;
        NumberTextOff();
        Emit(new WpdEvent(WpdEventKind.NoteStart) { Endnote = endnote });
        _paragraphStarted = false;
    }

    /// <summary>Close the note opened by <see cref="NoteBegin"/>.</summary>
    /// <remarks>
    /// A note's body is a document of its own, so its last line ends where the note does even
    /// when no return was typed there — the same close every sub-document gets.
    /// </remarks>
    public void NoteFinish()
    {
        if (Discarding) return;
        if (_paragraphStarted) ParagraphEnd();
        Emit(WpdEvent.Simple(WpdEventKind.NoteEnd));
        _paragraphStarted = false;
    }

    /// <summary>
    /// Turn an inline attribute on or off.
    /// </summary>
    /// <remarks>
    /// The five formats spell their attribute codes differently but mean the same six things,
    /// so they all arrive here rather than each building its own pair of events.
    /// </remarks>
    public void AttributeChange(WpdEventKind start, WpdEventKind end, bool on) =>
        Emit(WpdEvent.Simple(on ? start : end));

    /// <summary>Append one character to the current run.</summary>
    public void Character(char value)
    {
        if (Suppressed) return;
        _pending.Append(value);
        _paragraphStarted = true;
    }

    /// <summary>Append one Unicode scalar, which may need a surrogate pair.</summary>
    public void Scalar(int scalar)
    {
        if (Suppressed || scalar <= 0) return;
        if (!System.Text.Rune.IsValid(scalar)) return;
        _pending.Append(new System.Text.Rune(scalar).ToString());
        _paragraphStarted = true;
    }

    /// <summary>Append a whole run at once.</summary>
    public void Text(string text)
    {
        if (Suppressed || text.Length == 0) return;
        _pending.Append(text);
        _paragraphStarted = true;
    }

    public void Tab()
    {
        if (Suppressed) return;
        Flush();
        _events.Add(WpdEvent.Simple(WpdEventKind.Tab));
        _paragraphStarted = true;
    }

    /// <summary>
    /// Whether the current paragraph has any content yet.
    /// </summary>
    /// <remarks>
    /// A tab before any text is an indent rather than a tab stop, so where a paragraph begins is
    /// what decides whether the tab reaches the output at all.
    /// </remarks>
    private bool _paragraphStarted;

    /// <summary>Whether the current paragraph already has content.</summary>
    public bool ParagraphStarted => _paragraphStarted;

    public void LineBreak() { if (Suppressed) return; Flush(); _events.Add(WpdEvent.Simple(WpdEventKind.LineBreak)); }

    public void ParagraphEnd()
    {
        if (Suppressed) return;
        Flush();
        _events.Add(WpdEvent.Simple(WpdEventKind.ParagraphEnd));
        _paragraphStarted = false;
    }

    public void Open(WpdEventKind kind) { if (Suppressed) return; Flush(); _events.Add(WpdEvent.Simple(kind)); }

    public void Close(WpdEventKind kind) { if (Suppressed) return; Flush(); _events.Add(WpdEvent.Simple(kind)); }

    public void Emit(WpdEvent e) { if (Suppressed) return; Flush(); _events.Add(e); }

    /// <summary>Push any buffered characters out as one text event.</summary>
    public void Flush()
    {
        if (_pending.Length == 0) return;
        _events.Add(WpdEvent.Literal(_pending.ToString()));
        _pending.Clear();
    }

    // ---------------------------------------------------------------------
    // Table state
    // ---------------------------------------------------------------------
    //
    // WordPerfect files carry no table nesting: a row code and a cell code are
    // standalone, and whether one opens a table, continues a row or closes the
    // previous cell is state the reader has to keep. libwpd keeps it in its content
    // listener rather than in any of the five format parsers, and so does this — a
    // parser reports "a row here, a cell there" and the bracketing is derived once.
    //
    // The 6.x parser drives this; the Macintosh parsers bracket their own tables, which
    // carry no column definition for the padding below to work against.

    private bool _tableOpened;
    private bool _rowOpened;
    private bool _cellOpened;

    /// <summary>Whether the open row has had no cell yet, so it needs a filler on close.</summary>
    private bool _rowWithoutCell;

    /// <summary>Whether a header row was already seen: only the first one counts.</summary>
    private bool _wasHeaderRow;

    /// <summary>Grid column the next cell lands in, or -1 outside a row.</summary>
    private int _column = -1;

    /// <summary>
    /// Per grid column, how many further rows a vertical span still covers.
    /// </summary>
    /// <remarks>
    /// One entry per column of the table definition. A cell spanning three rows leaves 2 in
    /// each column it covers, and every later row consumes one — which is how a covered grid
    /// slot is told apart from a genuinely absent cell.
    /// </remarks>
    private readonly List<int> _rowsToSkip = new();

    /// <summary>Whether the parser is currently inside a table.</summary>
    public bool TableOpen => _tableOpened;

    /// <summary>
    /// Begin a table definition, discarding whatever the previous one left behind.
    /// </summary>
    /// <remarks>
    /// A definition precedes the table itself and names its columns one at a time; the row
    /// and cell codes come later, in the body.
    /// </remarks>
    public void DefineTable()
    {
        if (Suppressed) return;
        if (_paragraphStarted) ParagraphEnd();
        _rowsToSkip.Clear();
    }

    /// <summary>Add one column to the definition being built.</summary>
    public void DefineTableColumn()
    {
        if (Suppressed) return;
        _rowsToSkip.Add(0);
    }

    /// <summary>Open the table the definition described.</summary>
    public void StartTable()
    {
        if (Suppressed) return;
        EndTable();
        Emit(WpdEvent.Simple(WpdEventKind.TableStart));
        _tableOpened = true;
        _column = -1;
    }

    /// <summary>Begin a row, closing any row still open.</summary>
    public void InsertRow(bool header)
    {
        if (Suppressed || !_tableOpened) return;
        if (_rowOpened) CloseRow();

        _column = 0;
        bool isHeader = header && !_wasHeaderRow;
        if (isHeader) _wasHeaderRow = true;
        Emit(new WpdEvent(WpdEventKind.RowStart) { Header = isHeader });
        _rowOpened = true;
        _rowWithoutCell = true;
    }

    /// <summary>Begin a cell, closing any cell still open.</summary>
    public void InsertCell(int colSpan, int rowSpan)
    {
        if (Suppressed || !_tableOpened || !_rowOpened) return;
        if (_cellOpened) CloseCell();

        // Step over grid slots a cell in an earlier row still covers.
        while (_column < _rowsToSkip.Count && _rowsToSkip[_column] > 0)
        {
            _rowsToSkip[_column]--;
            _column++;
        }

        Emit(new WpdEvent(WpdEventKind.CellStart)
        {
            Column = _column,
            ColSpan = (uint)Math.Max(colSpan, 1),
            RowSpan = (uint)Math.Max(rowSpan, 1),
        });
        _cellOpened = true;
        _rowWithoutCell = false;
        _paragraphStarted = false;

        // Claim the slots this cell covers, both across and down.
        int remaining = colSpan;
        while (_column < _rowsToSkip.Count && remaining > 0)
        {
            _rowsToSkip[_column] = Math.Max(rowSpan - 1, 0);
            _column++;
            remaining--;
        }
    }

    /// <summary>Record a grid slot a cell in an earlier row covers.</summary>
    public void InsertCoveredCell()
    {
        if (Suppressed || !_tableOpened || !_rowOpened) return;
        if (_cellOpened) CloseCell();
        Emit(new WpdEvent(WpdEventKind.CoveredCell) { Column = _column });
        _rowWithoutCell = false;
    }

    /// <summary>Close the table, and with it any open row and cell.</summary>
    public void EndTable()
    {
        if (Suppressed) return;
        if (_tableOpened)
        {
            if (_rowOpened) CloseRow();
            Emit(WpdEvent.Simple(WpdEventKind.TableEnd));
        }
        _column = -1;
        _tableOpened = false;
        _wasHeaderRow = false;
        if (_paragraphStarted) ParagraphEnd();
    }

    private void CloseCell()
    {
        if (!_cellOpened) return;

        // Every cell closes a paragraph, even an empty one: a cell's body is a document of its
        // own, and its last line ends where the cell does rather than running into the next.
        ParagraphEnd();
        Emit(WpdEvent.Simple(WpdEventKind.CellEnd));
        _cellOpened = false;
    }

    private void CloseRow()
    {
        if (!_rowOpened) return;

        // Pad the row out to the definition's column count: a slot a vertical span covers is
        // consumed, and one the document simply left off gets an empty cell.
        while (_column < _rowsToSkip.Count)
        {
            if (_rowsToSkip[_column] == 0)
            {
                InsertCell(1, 1);
                CloseCell();
            }
            else
            {
                _rowsToSkip[_column]--;
                _column++;
            }
        }

        if (_cellOpened) CloseCell();
        if (_rowWithoutCell)
        {
            _rowWithoutCell = false;
            Emit(WpdEvent.Simple(WpdEventKind.CoveredCell));
        }
        Emit(WpdEvent.Simple(WpdEventKind.RowEnd));
        _rowOpened = false;
    }

    /// <summary>Flush the last run and drop the document's terminating paragraph end.</summary>
    public void Finish()
    {
        if (_tableOpened) EndTable();
        Flush();
        while (_events.Count > 0 && _events[^1].Kind == WpdEventKind.ParagraphEnd)
            _events.RemoveAt(_events.Count - 1);
    }
}
