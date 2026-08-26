using System.Text;

namespace Xberg.Internal.WordPerfect;

/// <summary>
/// Entry point for reading a WordPerfect document: detect the family, then parse it.
/// </summary>
/// <remarks>
/// A managed reimplementation of what libwpd does, rather than a binding to it. WordPerfect is
/// five unrelated binary formats sharing a name, and only 5.0 and later carry a header at all, so
/// detection falls back to structural heuristics for the older two.
/// </remarks>
internal static class WordPerfectReader
{
    /// <summary>Which parser a document needs, or <see cref="WpdFormat.Unknown"/>.</summary>
    public static WpdFormat Detect(byte[] bytes)
    {
        var reader = new WpdReader(bytes);
        if (WpdHeader.TryRead(reader) is { } header) return header.Format;

        // Formats before 5.0 have no header, so the structure itself has to identify them.
        if (Wp42Parser.LooksLikeWp42(bytes)) return WpdFormat.Wp42;
        return WpdFormat.Unknown;
    }

    /// <summary>Parse a WordPerfect document into its event stream.</summary>
    public static WpdDocument Parse(byte[] bytes)
    {
        var header = WpdHeader.TryRead(new WpdReader(bytes));
        return Detect(bytes) switch
        {
            WpdFormat.Wp42 => Wp42Parser.Parse(bytes),
            WpdFormat.Wp5 => Wp5Parser.Parse(bytes, header!),
            WpdFormat.Wp6 => Wp6Parser.Parse(bytes, header!),
            var other => throw new WpdParseException(
                other == WpdFormat.Unknown
                    ? "Failed to read WordPerfect document: unrecognised format"
                    : $"Failed to read WordPerfect document: {other} is not yet supported"),
        };
    }

    /// <summary>
    /// Render an event stream as plain text, one blank line between paragraphs.
    /// </summary>
    /// <remarks>
    /// A convenience for probes and tests. The real extractor builds a structured document
    /// instead, and this must agree with the text that document renders to.
    /// </remarks>
    public static string RenderPlain(WpdDocument document)
    {
        var text = new StringBuilder();
        var paragraph = new StringBuilder();

        void EndParagraph()
        {
            // A paragraph with nothing but whitespace in it is dropped rather than emitted, the
            // same rule the extractor applies: WordPerfect ends every document with a hard return
            // and scatters them between blocks, and keeping them would double every gap.
            string content = paragraph.ToString();
            paragraph.Clear();
            if (content.Trim().Length == 0) return;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(content);
        }

        foreach (var e in document.Events)
        {
            switch (e.Kind)
            {
                case WpdEventKind.Text: paragraph.Append(e.Text); break;
                case WpdEventKind.Tab: paragraph.Append('\t'); break;
                case WpdEventKind.Space: paragraph.Append(' '); break;
                case WpdEventKind.LineBreak: paragraph.Append('\n'); break;
                case WpdEventKind.ParagraphEnd: EndParagraph(); break;
            }
        }

        if (paragraph.Length > 0) EndParagraph();
        return text.ToString();
    }
}
