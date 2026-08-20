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

        string nativeText = ExtractTextAndSegments(pdf, deadline, out var pageSegments,
            out var pageWords, out var pagePaths);

        // --- Metadata ---
        var meta = PdfMetadataExtractor.Extract(pdf);
        // Advisory: a document we cannot grade reports no scan evidence.
        var scanDetection = PdfScanDetect.Detect(pdf);

        // --- Tables: native → bordered → heuristic, each tier only on pages the
        // previous one left empty (crates/xberg/src/extractors/pdf/extraction.rs).
        // Extracted regardless of output format — tables live in the result
        // independent of `content`.
        var tables = new List<Xberg.Types.Table>();
        try { tables.AddRange(ExtractRuledTables(pageWords, pagePaths, TableDetectionConfig.Strict(), null)); }
        catch { }
        var nativePages = new HashSet<uint>(tables.Select(t => t.PageNumber));
        try { tables.AddRange(ExtractRuledTables(pageWords, pagePaths, TableDetectionConfig.Bordered(), nativePages)); }
        catch { }
        var coveredPages = new HashSet<uint>(tables.Select(t => t.PageNumber));
        try { tables.AddRange(PdfTableReconstruct.ExtractHeuristicTables(pageSegments, allowSingleColumn: false, coveredPages, pagePaths)); }
        catch { }
        foreach (var table in tables) PdfTableNormalize.RepairConsistentlyMergedNumericColumn(table);

        // --- Build InternalDocument ---
        // For Markdown/Djot/HTML the Rust path pre-renders a *structured* document with
        // heading detection (pdf/structure/pipeline.rs). Plain/Json use the flat native
        // text split. Mirror that: structured elements only when the output format needs them.
        InternalDocument doc;
        InternalDocument? structured = null;
        if (needsStructured)
        {
            try { structured = PdfStructure.Build(pageSegments, ruledTables: tables); }
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
        // Text repair belongs to the structure pipeline (Rust `pdf/structure/pipeline.rs`
        // runs it over the elements that pipeline assembles), not to the flat native-text
        // split: applying it there over-corrects text whose ligatures the Rust plain path
        // keeps, and measurably loses fixtures.
        if (structured is not null && ReferenceEquals(doc, structured))
        {
            foreach (var elem in doc.Elements)
            {
                if (elem.Text.Length == 0) continue;
                elem.Text = PdfTextRepair.Repair(elem.Text);
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
                    ScannedConfidence = (float)scanDetection.Confidence,
                    ScannedPages = scanDetection.ScannedPageNumbers(PdfScanDetect.DefaultScannedMinConfidence),
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
    /// <summary>
    /// Run one ruling-line tier over every page the previous tier left uncovered.
    /// </summary>
    private static List<Xberg.Types.Table> ExtractRuledTables(
        List<List<TableSpan>> pageWords, List<List<PdfPath>> pagePaths,
        TableDetectionConfig config, HashSet<uint>? skipPages)
    {
        var tables = new List<Xberg.Types.Table>();
        for (int i = 0; i < pageWords.Count && i < pagePaths.Count; i++)
        {
            uint pageNumber = (uint)(i + 1);
            if (skipPages is not null && skipPages.Contains(pageNumber)) continue;
            try { tables.AddRange(PdfSpatialTables.DetectPageTables(pageWords[i], pagePaths[i], pageNumber, config)); }
            catch { }
        }
        return tables;
    }

    // SegmentData grid (out param) used for tables and heading structure. Mirrors Rust
    // `oxide::text::extract_text` + `oxide::hierarchy::extract_all_segments` sharing spans.
    private static string ExtractTextAndSegments(
        PdfDocument pdf, long deadline, out List<List<SegmentData>> pageSegments,
        out List<List<TableSpan>> pageWords, out List<List<PdfPath>> pagePaths)
    {
        int pageCount = pdf.PageCount;
        pageSegments = new List<List<SegmentData>>(pageCount);
        pageWords = new List<List<TableSpan>>(pageCount);
        pagePaths = new List<List<PdfPath>>(pageCount);
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < pageCount; i++)
        {
            // Match the previous text path: once the wall-clock guard trips, stop
            // emitting pages entirely rather than appending empty tails.
            if (DateTime.UtcNow.Ticks > deadline) break;

            string pageText = "";
            List<SegmentData> segs = new();
            List<TableSpan> words = new();
            List<PdfPath> paths = new();
            try
            {
                byte[] contentBytes = pdf.GetPageContent(i);
                if (contentBytes.Length != 0)
                {
                    var resources = pdf.Resolve(pdf.Pages[i].Get("Resources")).AsDict();
                    var extractor = new PdfContentExtractor(pdf, deadline);
                    var spans = extractor.Extract(contentBytes, resources);
                    var (mbLlx, _, mbUrx, _) = pdf.GetPageMediaBox(i);
                    (pageText, var lines) = PdfPageText.AssembleWithLines(spans, Math.Abs(mbUrx - mbLlx));
                    segs = PdfStructure.SegmentsFromLines(lines);
                    words = PdfSpatialTables.SpansToWords(spans);
                    paths = extractor.Paths;
                }
                // AcroForm: interactive text-field values are stored as the widget's /V and are
                // not drawn into the content stream when no appearance stream exists. pdf_oxide
                // surfaces them, appended after the page's content text. Mirror that here.
                var formValues = CollectWidgetTextValues(pdf, i);
                if (formValues.Count > 0)
                    pageText = pageText.Length > 0
                        ? pageText + "\n" + string.Join("\n", formValues)
                        : string.Join("\n", formValues);
            }
            catch { pageText = ""; segs = new(); words = new(); paths = new(); }

            pageSegments.Add(segs);
            pageWords.Add(words);
            pagePaths.Add(paths);
            if (i > 0) sb.Append("\n\n");
            sb.Append(PdfPageText.FixControlChars(pageText));
        }
        return sb.ToString();
    }

    // Collect interactive text/choice field values from the widgets on one page, in /Annots order.
    // /V and /FT may be inherited via the /Parent field chain; only string values (text/choice
    // fields) are surfaced — button fields store a /Name in /V and are skipped. De-duplicated by
    // fully-qualified field name so widgets that share a parent field are not counted twice.
    private static List<string> CollectWidgetTextValues(PdfDocument pdf, int pageIndex)
    {
        var values = new List<string>();
        var annots = pdf.Resolve(pdf.Pages[pageIndex].Get("Annots")).AsArray();
        if (annots is null) return values;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in annots.Items)
        {
            var widget = pdf.Resolve(a).AsDict();
            if (widget is null) continue;
            if (pdf.Resolve(widget.Get("Subtype")).AsName() != "Widget") continue;

            string? ft = null;
            byte[]? vbytes = null;
            var names = new List<string>();
            var node = widget;
            for (int guard = 0; node is not null && guard < 32; guard++)
            {
                ft ??= pdf.Resolve(node.Get("FT")).AsName();
                vbytes ??= pdf.Resolve(node.Get("V")).AsStringBytes();
                var t = pdf.Resolve(node.Get("T")).AsStringBytes();
                if (t is not null) names.Insert(0, PdfMetadataExtractor.DecodePdfString(t) ?? "");
                node = pdf.Resolve(node.Get("Parent")).AsDict();
            }

            if (vbytes is null) continue;                       // no value set
            if (ft is not null && ft != "Tx" && ft != "Ch") continue; // not a text/choice field
            string? value = PdfMetadataExtractor.DecodePdfString(vbytes);
            if (string.IsNullOrEmpty(value)) continue;

            string key = names.Count > 0 ? string.Join(".", names) : value;
            if (!seen.Add(key)) continue;
            values.Add(value);
        }
        return values;
    }
}
