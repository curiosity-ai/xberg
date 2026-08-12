namespace Xberg.Internal.Djot;

/// <summary>
/// Event kinds emitted by <see cref="DjotParser"/>. Mirrors the subset of the Rust
/// <c>jotdown</c> <c>Event</c>/<c>Container</c> variants that the Djot extractor
/// (<c>crates/xberg/src/extractors/djot_format/extractor.rs</c> + <c>parsing/table_extraction.rs</c>)
/// matches on. Smart-punctuation events (smart quotes, dashes, ellipsis) are intentionally
/// <b>not</b> represented: jotdown emits them as distinct non-<c>Str</c> events that both
/// <c>build_internal_document</c> and <c>extract_tables_from_events</c> ignore, so the parser
/// simply drops those characters from the <c>Str</c> stream (identical observable result).
/// </summary>
internal enum DjotEventKind
{
    StartHeading,
    EndHeading,
    StartParagraph,
    EndParagraph,
    StartStrong,
    EndStrong,
    StartEmphasis,
    EndEmphasis,
    StartDelete,
    EndDelete,
    StartVerbatim,
    EndVerbatim,
    StartLink,
    EndLink,
    StartImage,
    EndImage,
    StartCodeBlock,
    EndCodeBlock,
    StartRawBlock,
    EndRawBlock,
    StartBlockquote,
    EndBlockquote,
    StartList,
    EndList,
    StartListItem,
    EndListItem,
    StartMath,
    EndMath,
    StartFootnote,
    EndFootnote,
    FootnoteReference,
    StartTable,
    EndTable,
    StartTableRow,
    EndTableRow,
    StartTableCell,
    EndTableCell,
    Str,
    Softbreak,
    Hardbreak,
}

/// <summary>A single Djot parse event with an optional payload. Kept close to the shape the
/// Rust extractor consumes so <c>DjotExtractor.BuildInternalDocument</c> can port the Rust
/// <c>match</c> arms directly.</summary>
internal sealed class DjotEvent
{
    public DjotEventKind Kind { get; init; }

    /// <summary>Text payload (Str content, code-block language, raw-block format, footnote label/name).</summary>
    public string Text { get; init; } = "";

    /// <summary>Heading level (1-6).</summary>
    public byte Level { get; init; }

    /// <summary>List ordered flag.</summary>
    public bool Ordered { get; init; }

    /// <summary>Link/image URL (source).</summary>
    public string? Url { get; init; }

    /// <summary>Display-mode flag for math.</summary>
    public bool Display { get; init; }

    public static DjotEvent Simple(DjotEventKind kind) => new() { Kind = kind };
    public static DjotEvent Text_(string text) => new() { Kind = DjotEventKind.Str, Text = text };
}
