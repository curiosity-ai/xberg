// Ported from crates/xberg/src/extractors/pdf/mod.rs (+ pdf/oxide/{text,metadata}.rs).
// Pure-managed PDF extractor: parses the document, walks pages, extracts text in
// reading order, and reads the info-dictionary metadata. No native dependencies,
// no OCR (image-only pages yield whatever native extraction finds).
//
// Backend lives under Xberg.Internal.Pdf (xref/trailer, objects, streams + filters,
// content-stream tokenizer, text-showing operators, font encoding/ToUnicode, page tree).
using Xberg.Core;
using Xberg.Internal.Pdf;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Native PDF extractor. Ports Rust `PdfExtractor` (native, non-OCR path).</summary>
public sealed class PdfExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/pdf" };

    public int Priority => 50;

    // Per-document wall-clock guard so pathological files cannot hang extraction.
    private const int MaxSecondsPerDocument = 25;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        byte[] bytes = content.ToArray();
        long deadline = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(MaxSecondsPerDocument).Ticks;

        PdfDocument pdf;
        try { pdf = PdfDocument.Open(bytes); }
        catch (Exception e) { throw new InvalidDataException($"pdf parse failed: {e.Message}", e); }

        int pageCount = pdf.PageCount;
        if (pageCount == 0)
            throw new InvalidDataException("pdf has no readable pages");

        // --- Text extraction (native, column/row-aware assembly per page) ---
        string nativeText = ExtractText(pdf, deadline);

        // --- Metadata ---
        var meta = PdfMetadataExtractor.Extract(pdf);

        // --- Build InternalDocument ---
        // For Markdown/Djot/HTML the Rust path pre-renders a *structured* document with
        // heading detection (pdf/structure/pipeline.rs). Plain/Json use the flat native
        // text split. Mirror that: structured elements only when the output format needs them.
        InternalDocument doc;
        bool needsStructured = config.OutputFormat.Equals(OutputFormat.Markdown)
            || config.OutputFormat.Equals(OutputFormat.Djot)
            || config.OutputFormat.Equals(OutputFormat.Html);
        InternalDocument? structured = null;
        if (needsStructured)
        {
            try { structured = BuildStructured(pdf, deadline); }
            catch { structured = null; }
        }

        if (structured is not null && structured.Elements.Count > 0)
        {
            doc = structured;
        }
        else
        {
            doc = new InternalDocument("pdf");
            foreach (var paragraph in nativeText.Split("\n\n"))
            {
                var trimmed = paragraph.Trim();
                if (trimmed.Length > 0)
                    doc.PushElement(InternalElement.TextElement(ElementKind.Paragraph, trimmed, 0));
            }
        }
        doc.MimeType = mimeType;

        doc.Metadata = new Metadata
        {
            Title = meta.Title,
            Subject = meta.Subject,
            Authors = meta.Authors,
            Keywords = meta.Keywords,
            CreatedAt = meta.CreatedAt,
            ModifiedAt = meta.ModifiedAt,
            CreatedBy = meta.CreatedBy,
            OcrUsed = false,
            Format = new FormatMetadata
            {
                FormatType = "pdf",
                Payload = new Xberg.Types.PdfMetadata
                {
                    PdfVersion = meta.PdfVersion,
                    Producer = meta.Producer,
                    IsEncrypted = meta.IsEncrypted,
                    Width = meta.Width,
                    Height = meta.Height,
                    PageCount = meta.PageCount,
                },
            },
        };
        doc.Metadata.Additional["extraction_method"] =
            System.Text.Json.JsonSerializer.SerializeToElement("native");

        if (meta.IsEncrypted && pdf.Decryptor is null && nativeText.Length == 0)
            doc.ProcessingWarnings.Add(new ProcessingWarning
            {
                Source = "pdf",
                Message = "PDF is encrypted and could not be decrypted with the empty password; text unavailable.",
            });

        return doc;
    }

    // Extract per-page font-metric segments (ColumnAware order) and run the structure
    // pipeline to produce a heading-aware InternalDocument.
    private static InternalDocument? BuildStructured(PdfDocument pdf, long deadline)
    {
        int pageCount = pdf.PageCount;
        var allPageSegments = new List<List<SegmentData>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            if (DateTime.UtcNow.Ticks > deadline) return null;
            try
            {
                byte[] contentBytes = pdf.GetPageContent(i);
                if (contentBytes.Length == 0) { allPageSegments.Add(new()); continue; }
                var resources = pdf.Resolve(pdf.Pages[i].Get("Resources")).AsDict();
                var extractor = new PdfContentExtractor(pdf, deadline);
                var spans = extractor.Extract(contentBytes, resources);
                var lines = PdfPageText.BuildLineSegments(spans);
                allPageSegments.Add(PdfStructure.SegmentsFromLines(lines));
            }
            catch { allPageSegments.Add(new()); }
        }
        return PdfStructure.Build(allPageSegments);
    }

    private static string ExtractText(PdfDocument pdf, long deadline)
    {
        var sb = new System.Text.StringBuilder();
        int pageCount = pdf.PageCount;
        for (int i = 0; i < pageCount; i++)
        {
            if (DateTime.UtcNow.Ticks > deadline) break;
            string pageText;
            try
            {
                byte[] contentBytes = pdf.GetPageContent(i);
                if (contentBytes.Length == 0) { pageText = ""; }
                else
                {
                    var resources = pdf.Resolve(pdf.Pages[i].Get("Resources")).AsDict();
                    var extractor = new PdfContentExtractor(pdf, deadline);
                    var spans = extractor.Extract(contentBytes, resources);
                    pageText = PdfPageText.Assemble(spans);
                }
            }
            catch { pageText = ""; }

            if (i > 0) sb.Append("\n\n");
            sb.Append(PdfPageText.FixControlChars(pageText));
        }
        return sb.ToString();
    }
}
