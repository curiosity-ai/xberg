using System.Text;
using Xberg.Core;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Plain text extractor. Ported from `extractors/text.rs`.</summary>
public sealed class PlainTextExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "text/plain",
        "text/troff",
        "text/x-mdoc",
        "text/x-pod",
        "text/x-dokuwiki",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string text = Encoding.UTF8.GetString(content);
        text = text.TrimEnd('\n').TrimEnd('\r');

        uint lineCount = (uint)CountLines(text);
        uint wordCount = (uint)text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        // Rust counts `text.chars()` — Unicode scalar values, not UTF-8 bytes and not UTF-16
        // code units, so a non-BMP character counts once.
        uint characterCount = (uint)text.EnumerateRunes().Count();

        var doc = BuildInternalDocument(text);
        doc.Metadata = new Metadata
        {
            Format = FormatMetadata.Text(new TextMetadata
            {
                LineCount = lineCount,
                WordCount = wordCount,
                CharacterCount = characterCount,
            }),
        };
        doc.MimeType = mimeType;
        return doc;
    }

    /// <summary>
    /// Split the text into paragraphs on blank lines.
    /// </summary>
    /// <remarks>
    /// Line endings are normalized here rather than left to a later stage: this extractor
    /// populates an InternalDocument directly, which bypasses the paragraph splitter downstream.
    /// Without it "\r\n\r\n" never matches the blank-line boundary and a CRLF document collapses
    /// into one paragraph carrying its carriage returns.
    /// </remarks>
    private static InternalDocument BuildInternalDocument(string text)
    {
        var builder = new InternalDocumentBuilder("text");
        foreach (var paragraph in SplitDoubleNewline(TextTransform.NormalizeLineEndings(text)))
        {
            string trimmed = paragraph.Trim();
            if (trimmed.Length > 0)
                builder.PushParagraph(trimmed, new(), null, null);
        }
        return builder.Build();
    }

    private static IEnumerable<string> SplitDoubleNewline(string text)
    {
        int start = 0;
        int idx;
        while ((idx = text.IndexOf("\n\n", start, StringComparison.Ordinal)) >= 0)
        {
            yield return text.Substring(start, idx - start);
            start = idx + 2;
        }
        yield return text.Substring(start);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        int count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        // Rust `str::lines()` does not count a trailing empty line after a final '\n'.
        if (text.EndsWith('\n')) count--;
        return count;
    }
}
