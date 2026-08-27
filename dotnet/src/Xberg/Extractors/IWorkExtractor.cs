// Ported from crates/xberg/src/extractors/iwork/{pages,keynote,numbers}.rs.
//
// All three formats are the same package: a ZIP of Snappy-framed `.iwa` protobuf archives
// (see Internal/IWork/IwaContainer.cs). Pages and Keynote read text off the wire without a
// schema; Numbers walks the reverse-engineered table schema and falls back to the same flat
// scan when that finds nothing.

using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Internal.IWork;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Apple Pages (.pages), modern iWork format (2013+). Mirrors Rust <c>PagesExtractor</c>.</summary>
public sealed class PagesExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-iwork-pages-sffpages" };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        // The outer container is validated before any package member is read.
        using var archive = ZipBombValidator.OpenValidated(stream, config.SecurityLimits);

        var data = ParsePages(archive);
        var doc = Build(data);
        doc.MimeType = mimeType;
        foreach (var warning in data.Warnings) doc.ProcessingWarnings.Add(warning);
        return doc;
    }

    private sealed class PagesData
    {
        public List<string> DocumentTexts = new();
        public List<string> SupplementaryTexts = new();
        public Metadata Metadata = new();
        public List<ProcessingWarning> Warnings = new();
    }

    /// <summary>
    /// Pages keeps its body in <c>Index/Document.iwa</c> (plus Section/Text archives) and its
    /// comments and embedded data records elsewhere; the two groups stay separate so the body
    /// leads the output.
    /// </summary>
    private static PagesData ParsePages(ZipArchive archive)
    {
        var data = new PagesData { Metadata = IwaContainer.ExtractMetadataFromZip(archive) };
        var paths = IwaContainer.CollectIwaPaths(archive);

        var docPaths = new List<string>();
        var otherPaths = new List<string>();
        foreach (var path in paths)
        {
            string filename = IWorkPaths.FileName(path);
            if (filename.StartsWith("Document", StringComparison.Ordinal)
                || filename.StartsWith("Section", StringComparison.Ordinal)
                || filename.StartsWith("Text", StringComparison.Ordinal))
                docPaths.Add(path);
            else
                otherPaths.Add(path);
        }

        if (docPaths.Count == 0)
        {
            docPaths = paths;
            otherPaths.Clear();
        }

        var docTexts = IWorkPaths.ReadTexts(archive, docPaths, data.Warnings);
        var otherTexts = IWorkPaths.ReadTexts(archive, otherPaths, data.Warnings);

        data.DocumentTexts = IwaContainer.DedupText(docTexts);
        data.SupplementaryTexts = IwaContainer.DedupText(otherTexts)
            .Where(text => !data.DocumentTexts.Contains(text))
            .ToList();
        return data;
    }

    /// <summary>
    /// A short leading line that does not close a sentence becomes the title; the same shape
    /// further down becomes a heading, and everything else a paragraph.
    /// </summary>
    private static InternalDocument Build(PagesData data)
    {
        var builder = new InternalDocumentBuilder("pages");
        if (data.Metadata.Title is not null || data.Metadata.Authors is not null)
            builder.SetMetadata(data.Metadata);

        var texts = data.DocumentTexts;
        int startIndex = 0;
        if (texts.Count > 0)
        {
            string trimmed = texts[0].Trim();
            if (trimmed.Length > 0 && IsLikelyTitle(trimmed) && texts.Count > 1)
            {
                builder.PushTitle(trimmed, null, null);
                startIndex = 1;
            }
        }

        for (int i = startIndex; i < texts.Count; i++)
        {
            string trimmed = texts[i].Trim();
            if (trimmed.Length == 0) continue;
            if (IsLikelyHeading(trimmed)) builder.PushHeading(2, trimmed, null, null);
            else builder.PushParagraph(trimmed, new List<TextAnnotation>(), null, null);
        }

        if (data.SupplementaryTexts.Count > 0)
        {
            if (data.DocumentTexts.Count > 0) builder.PushHeading(2, "Annotations", null, null);
            foreach (var text in data.SupplementaryTexts)
            {
                string trimmed = text.Trim();
                if (trimmed.Length > 0) builder.PushParagraph(trimmed, new List<TextAnnotation>(), null, null);
            }
        }

        return builder.Build();
    }

    /// <summary>Short, no sentence-terminating punctuation, at least one letter, single line.</summary>
    private static bool IsLikelyTitle(string text) =>
        IwaContainer.Utf8Length(text) <= 100
        && !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?')
        && text.EnumerateRunes().Any(Rune.IsLetter)
        && !text.Contains('\n');

    /// <summary>Short, unterminated, single line, opening on a capital or digit, at most ten words.</summary>
    private static bool IsLikelyHeading(string text)
    {
        if (IwaContainer.Utf8Length(text) > 80) return false;
        if (text.EndsWith('.') || text.EndsWith(',') || text.Contains('\n')) return false;
        var first = text.EnumerateRunes().FirstOrDefault();
        if (!Rune.IsUpper(first) && !(first.IsAscii && Rune.IsDigit(first))) return false;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 10;
    }
}

/// <summary>Apple Keynote (.key), modern iWork format (2013+). Mirrors Rust <c>KeynoteExtractor</c>.</summary>
public sealed class KeynoteExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-iwork-keynote-sffkey" };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        // The outer container is validated before any package member is read.
        using var archive = ZipBombValidator.OpenValidated(stream, config.SecurityLimits);

        var data = ParseKeynote(archive, config.SecurityLimits);
        var doc = Build(data);
        doc.MimeType = mimeType;
        foreach (var warning in data.Warnings) doc.ProcessingWarnings.Add(warning);
        return doc;
    }

    private sealed class KeynoteData
    {
        public List<List<string>> SlideTexts = new();
        public List<string> OtherTexts = new();
        public Metadata Metadata = new();
        public List<ProcessingWarning> Warnings = new();
    }

    private static KeynoteData ParseKeynote(ZipArchive archive, SecurityLimits? limits = null)
    {
        var data = new KeynoteData { Metadata = IwaContainer.ExtractMetadataFromZip(archive) };
        var paths = IwaContainer.CollectIwaPaths(archive);

        bool IsSlide(string path)
        {
            string filename = IWorkPaths.FileName(path);
            return filename.StartsWith("Slide", StringComparison.Ordinal)
                && !filename.StartsWith("MasterSlide", StringComparison.Ordinal);
        }

        var slidePaths = paths.Where(IsSlide).OrderBy(p => p, StringComparer.Ordinal).ToList();
        // Reject a deck whose slide count exceeds the configured ceiling before any member is
        // decompressed and walked. The Index/Slide-*.iwa entries are already enumerated here, so
        // the count is exact.
        DocumentLimits.EnforcePageCount(slidePaths.Count, limits);
        var otherPaths = paths.Where(p => !IsSlide(p)).ToList();

        // Each slide keeps its own text, deduped only within itself: a footer or title
        // legitimately repeated across slides must survive on every slide it appears on.
        foreach (var path in slidePaths)
        {
            if (IWorkPaths.ReadMember(archive, path, data.Warnings) is not { } decompressed) continue;
            var deduped = IwaContainer.DedupText(IwaContainer.ExtractTextFromProto(decompressed));
            if (deduped.Count > 0) data.SlideTexts.Add(deduped);
        }

        // Text already shown on a slide is only worth repeating in "Additional Content" if it
        // says something new.
        var seenInSlides = new HashSet<string>(data.SlideTexts.SelectMany(s => s), StringComparer.Ordinal);
        var otherRaw = IWorkPaths.ReadTexts(archive, otherPaths, data.Warnings);
        data.OtherTexts = IwaContainer.DedupText(otherRaw).Where(seenInSlides.Add).ToList();
        return data;
    }

    private static InternalDocument Build(KeynoteData data)
    {
        var builder = new InternalDocumentBuilder("keynote");
        if (data.Metadata.Title is not null || data.Metadata.Authors is not null)
            builder.SetMetadata(data.Metadata);

        for (int index = 0; index < data.SlideTexts.Count; index++)
        {
            var slideLines = data.SlideTexts[index];
            if (slideLines.Count == 0) continue;

            builder.PushSlide((uint)(index + 1), slideLines[0].Trim(), null);
            for (int line = 1; line < slideLines.Count; line++)
            {
                string trimmed = slideLines[line].Trim();
                if (trimmed.Length > 0) builder.PushParagraph(trimmed, new List<TextAnnotation>(), null, null);
            }
        }

        if (data.OtherTexts.Count > 0)
        {
            if (data.SlideTexts.Count > 0) builder.PushHeading(2, "Additional Content", null, null);
            foreach (var text in data.OtherTexts)
            {
                string trimmed = text.Trim();
                if (trimmed.Length > 0) builder.PushParagraph(trimmed, new List<TextAnnotation>(), null, null);
            }
        }

        return builder.Build();
    }
}

/// <summary>Apple Numbers (.numbers), modern iWork format (2013+). Mirrors Rust <c>NumbersExtractor</c>.</summary>
public sealed class NumbersExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-iwork-numbers-sffnumbers" };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        // The outer container is validated before any package member is read.
        using var archive = ZipBombValidator.OpenValidated(stream, config.SecurityLimits);

        var data = NumbersParser.Parse(archive);
        var doc = Build(data);
        doc.MimeType = mimeType;
        foreach (var warning in data.Warnings) doc.ProcessingWarnings.Add(warning);
        return doc;
    }

    /// <summary>
    /// Cell values stay a table rather than becoming flat paragraphs. A sheet name is its own
    /// heading above the tables it holds, matching the xlsx/ods convention of headinging a
    /// sheet independently of its content rather than folding it into a table title.
    /// </summary>
    private static InternalDocument Build(NumbersData data)
    {
        var builder = new InternalDocumentBuilder("numbers");
        if (data.Metadata.Title is not null || data.Metadata.Authors is not null)
            builder.SetMetadata(data.Metadata);

        string? lastSheetName = null;
        foreach (var table in data.Tables)
        {
            if (table.Cells.Count == 0) continue;

            if (table.SheetName is { } sheetName)
            {
                if (lastSheetName != sheetName)
                {
                    builder.PushHeading(1, sheetName, null, null);
                    lastSheetName = sheetName;
                }
            }
            else
            {
                lastSheetName = null;
            }

            builder.PushHeading(table.SheetName is not null ? (byte)2 : (byte)1, table.Name, null, null);
            builder.PushTableFromCells(table.Cells, null, null);
        }

        return builder.Build();
    }
}

/// <summary>Path and member helpers shared by the three iWork extractors.</summary>
internal static class IWorkPaths
{
    /// <summary>The last path segment, or the whole path when it has no separator.</summary>
    public static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// Decompress one member, or record a warning naming it. A member that cannot be read is
    /// skipped rather than failing the document, but it never vanishes silently.
    /// </summary>
    public static byte[]? ReadMember(ZipArchive archive, string path, List<ProcessingWarning> warnings)
    {
        try
        {
            return IwaContainer.ReadIwaFile(archive, path);
        }
        catch (Exception error) when (error is IwaFormatException or InvalidDataException)
        {
            IwaContainer.PushMemberParseWarning(warnings, path, error);
            return null;
        }
    }

    public static List<string> ReadTexts(ZipArchive archive, List<string> paths, List<ProcessingWarning> warnings)
    {
        var texts = new List<string>();
        foreach (var path in paths)
        {
            if (ReadMember(archive, path, warnings) is { } decompressed)
                texts.AddRange(IwaContainer.ExtractTextFromProto(decompressed));
        }
        return texts;
    }
}
