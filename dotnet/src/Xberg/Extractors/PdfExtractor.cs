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

        // --- Single per-page content pass: page text + font-metric segments ---
        // Content-stream parsing dominates cost, so parse each page exactly once and
        // derive both the assembled page text (plain/json) and the SegmentData used for
        // tables + heading structure (md/html) from the same spans.
        bool needsStructured = config.OutputFormat.Equals(OutputFormat.Markdown)
            || config.OutputFormat.Equals(OutputFormat.Djot)
            || config.OutputFormat.Equals(OutputFormat.Html);

        string nativeText = ExtractTextAndSegments(pdf, deadline, out var pageSegments);

        // --- Metadata ---
        var meta = PdfMetadataExtractor.Extract(pdf);

        // --- Tables (text-layer heuristic tier) ---
        // Mirrors the Rust three-tier detector, minus pdf_oxide's native/bordered
        // ruling-line grid passes (no managed equivalent). Extract regardless of
        // output format — tables live in the result independent of `content`.
        List<Xberg.Types.Table> tables;
        try { tables = PdfTableReconstruct.ExtractHeuristicTables(pageSegments, allowSingleColumn: false); }
        catch { tables = new List<Xberg.Types.Table>(); }

        // --- Build InternalDocument ---
        // For Markdown/Djot/HTML the Rust path pre-renders a *structured* document with
        // heading detection (pdf/structure/pipeline.rs). Plain/Json use the flat native
        // text split. Mirror that: structured elements only when the output format needs them.
        InternalDocument doc;
        InternalDocument? structured = null;
        if (needsStructured)
        {
            try { structured = PdfStructure.Build(pageSegments); }
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
        doc.Tables = tables;

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

    // Single per-page pass: parse each page's content stream once, then derive both the
    // assembled page text (returned, joined by blank lines) and the font-metric
    // SegmentData grid (out param) used for tables and heading structure. Mirrors Rust
    // `oxide::text::extract_text` + `oxide::hierarchy::extract_all_segments` sharing spans.
    private static string ExtractTextAndSegments(PdfDocument pdf, long deadline, out List<List<SegmentData>> pageSegments)
    {
        int pageCount = pdf.PageCount;
        pageSegments = new List<List<SegmentData>>(pageCount);
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < pageCount; i++)
        {
            // Match the previous text path: once the wall-clock guard trips, stop
            // emitting pages entirely rather than appending empty tails.
            if (DateTime.UtcNow.Ticks > deadline) break;

            string pageText = "";
            List<SegmentData> segs = new();
            try
            {
                byte[] contentBytes = pdf.GetPageContent(i);
                if (contentBytes.Length != 0)
                {
                    var resources = pdf.Resolve(pdf.Pages[i].Get("Resources")).AsDict();
                    var extractor = new PdfContentExtractor(pdf, deadline);
                    var spans = extractor.Extract(contentBytes, resources);
                    (pageText, var lines) = PdfPageText.AssembleWithLines(spans);
                    segs = PdfStructure.SegmentsFromLines(lines);
                }
            }
            catch { pageText = ""; segs = new(); }

            pageSegments.Add(segs);
            if (i > 0) sb.Append("\n\n");
            sb.Append(PdfPageText.FixControlChars(pageText));
        }
        return sb.ToString();
    }
}
