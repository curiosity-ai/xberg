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

    public WpdEventSink(List<WpdEvent> events) => _events = events;

    /// <summary>Append one character to the current run.</summary>
    public void Character(char value) => _pending.Append(value);

    /// <summary>Append one Unicode scalar, which may need a surrogate pair.</summary>
    public void Scalar(int scalar)
    {
        if (scalar <= 0) return;
        if (System.Text.Rune.IsValid(scalar)) _pending.Append(new System.Text.Rune(scalar).ToString());
    }

    /// <summary>Append a whole run at once.</summary>
    public void Text(string text) => _pending.Append(text);

    public void Tab() { Flush(); _events.Add(WpdEvent.Simple(WpdEventKind.Tab)); }

    public void LineBreak() { Flush(); _events.Add(WpdEvent.Simple(WpdEventKind.LineBreak)); }

    public void ParagraphEnd() { Flush(); _events.Add(WpdEvent.Simple(WpdEventKind.ParagraphEnd)); }

    public void Open(WpdEventKind kind) { Flush(); _events.Add(WpdEvent.Simple(kind)); }

    public void Close(WpdEventKind kind) { Flush(); _events.Add(WpdEvent.Simple(kind)); }

    public void Emit(WpdEvent e) { Flush(); _events.Add(e); }

    /// <summary>Push any buffered characters out as one text event.</summary>
    public void Flush()
    {
        if (_pending.Length == 0) return;
        _events.Add(WpdEvent.Literal(_pending.ToString()));
        _pending.Clear();
    }

    /// <summary>Flush the last run and drop the document's terminating paragraph end.</summary>
    public void Finish()
    {
        Flush();
        while (_events.Count > 0 && _events[^1].Kind == WpdEventKind.ParagraphEnd)
            _events.RemoveAt(_events.Count - 1);
    }
}
