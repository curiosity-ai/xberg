namespace Xberg.Internal.WordPerfect;

/// <summary>
/// The kinds of event a WordPerfect parse emits.
/// </summary>
/// <remarks>
/// This mirrors the event stream upstream gets out of libwpd's listener callbacks, so the
/// extractor that consumes it is a straight port rather than a reinterpretation.
/// </remarks>
internal enum WpdEventKind
{
    Text,
    Tab,
    Space,
    LineBreak,
    ParagraphEnd,
    ListItemStart,
    ListItemEnd,
    HeadingStart,
    BoldStart, BoldEnd,
    ItalicStart, ItalicEnd,
    UnderlineStart, UnderlineEnd,
    StrikethroughStart, StrikethroughEnd,
    SuperscriptStart, SuperscriptEnd,
    SubscriptStart, SubscriptEnd,
    TableStart, RowStart, CellStart, CoveredCell, CellEnd, RowEnd, TableEnd,
    HeaderStart, HeaderEnd,
    FooterStart, FooterEnd,
    NoteStart, NoteEnd,
    AsideStart, AsideEnd,
    LinkStart, LinkEnd,
    Field,
}

/// <summary>
/// One event in the ordered, properly-nested stream a WordPerfect parse produces.
/// </summary>
/// <remarks>
/// A single type with optional payload rather than a discriminated hierarchy: the stream is walked
/// once by a switch, and the payload fields that matter differ per kind but never overlap.
/// </remarks>
internal sealed record WpdEvent(WpdEventKind Kind)
{
    /// <summary>Literal text, a field placeholder, an aside kind, or a link target.</summary>
    public string Text { get; init; } = "";

    /// <summary>Whether the enclosing list is ordered, for <see cref="WpdEventKind.ListItemStart"/>.</summary>
    public bool Ordered { get; init; }

    /// <summary>1-based nesting depth, or a heading level of 1 to 6.</summary>
    public byte Level { get; init; }

    /// <summary>1-based position within an ordered list; zero for an unordered one.</summary>
    public uint Counter { get; init; }

    /// <summary>Whether a row was flagged as a header row.</summary>
    public bool Header { get; init; }

    /// <summary>Whether a note is an endnote rather than a footnote.</summary>
    public bool Endnote { get; init; }

    /// <summary>Absolute grid column, or -1 when the parse did not report one.</summary>
    public int Column { get; init; } = -1;

    /// <summary>Columns this cell spans, at least 1.</summary>
    public uint ColSpan { get; init; } = 1;

    /// <summary>Rows this cell spans, at least 1.</summary>
    public uint RowSpan { get; init; } = 1;

    public static WpdEvent Literal(string text) => new(WpdEventKind.Text) { Text = text };
    public static WpdEvent Simple(WpdEventKind kind) => new(kind);
}

/// <summary>Document metadata a WordPerfect parse recovered.</summary>
internal sealed class WpdMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }

    /// <summary>Every key/value pair found, in the order the document reported them.</summary>
    public List<(string Key, string Value)> Raw { get; } = new();
}

/// <summary>A parsed WordPerfect document: an ordered event stream plus metadata.</summary>
internal sealed class WpdDocument
{
    public List<WpdEvent> Events { get; } = new();
    public WpdMetadata Metadata { get; } = new();
}

/// <summary>Raised when a document cannot be parsed as WordPerfect at all.</summary>
internal sealed class WpdParseException : Exception
{
    public WpdParseException(string message) : base(message) { }
}
