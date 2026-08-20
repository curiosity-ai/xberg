// Ported from pdf_oxide `document.rs`: `extract_spans_impl` (15460), `load_fonts` (19130),
// `page_cannot_have_text` (9629), `may_contain_text` (9578) and
// `extract_page_text_with_options` (15979).
//
// This is the page-level driver above the extractor: it decides whether a page can carry
// text at all, loads its fonts, runs the extractor over the content stream, then drops
// off-page spans and applies reading order.
//
// Font loading and the `BT`/`Do` pre-scan live on the extractor itself, ported alongside
// the operator dispatch that also needs them for Form XObjects.
using System;
using System.Collections.Generic;
using System.Linq;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Fonts;
using Xberg.Internal.PdfOxide.Layout;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxPageExtractor
{
    /// <summary>
    /// One page's spans and glyphs, ordered as `ReadingOrder::ColumnAware` leaves them —
    /// the shape `extract_page_text_with_options` returns.
    /// </summary>
    public static OxPageText ExtractPageText(PdfDocument doc, int pageIndex)
    {
        var page = pageIndex >= 0 && pageIndex < doc.PageCount ? doc.Pages[pageIndex] : null;
        var (llx, lly, urx, ury) = doc.GetPageMediaBox(pageIndex);
        var result = new OxPageText { PageWidth = (float)urx, PageHeight = (float)ury };
        if (page is null) return result;

        var spans = ExtractSpans(doc, pageIndex, page);
        OxReadingOrder.DropOffpageSpans(spans, (float)llx, (float)lly, (float)urx, (float)ury);
        result.Spans = OxReadingOrder.OrderSpansColumnAware(spans);

        // `PageText.chars` is left empty: xberg assembles page text from spans alone, and
        // `TextSpan::to_chars` exists upstream only for callers that want per-glyph boxes.
        return result;
    }

    /// <summary>Raw spans for one page, before reading order.</summary>
    private static List<OxTextSpan> ExtractSpans(PdfDocument doc, int pageIndex, PdfDict page)
    {
        if (PageCannotHaveText(doc, page)) return new List<OxTextSpan>();

        byte[] content;
        try { content = doc.GetPageContent(pageIndex); }
        catch { return new List<OxTextSpan>(); }
        if (content.Length == 0 || !OxTextExtractor.MayContainText(content)) return new List<OxTextSpan>();

        var extractor = new OxTextExtractor();
        extractor.SetPageIndex(pageIndex);

        var resources = page.Get("Resources");
        if (resources is not null)
        {
            extractor.SetResources(resources);
            extractor.SetDocument(doc);
            extractor.LoadFontsForResources(extractor, resources);
        }

        try { return extractor.ExtractTextSpans(content); }
        catch { return new List<OxTextSpan>(); }
    }

    /// <summary>
    /// Whether the page definitely cannot produce text, so its content stream need not be
    /// decompressed at all. Conservative: anything unreadable counts as "might have text".
    /// </summary>
    private static bool PageCannotHaveText(PdfDocument doc, PdfDict page)
    {
        if (page.Get("Resources") is not { } resourcesObj) return true;
        var res = Ox.Dict(doc, resourcesObj);
        if (res is null) return false;

        if (Ox.GetDict(doc, res, "Font") is { } fonts && fonts.Map.Count > 0) return false;

        // A Form XObject can carry text of its own, so its presence rules nothing out.
        if (Ox.GetDict(doc, res, "XObject") is { } xobjects)
            foreach (var entry in xobjects.Map.Values)
            {
                var sub = doc.Resolve(entry);
                if (sub is PdfStream st && Ox.GetName(doc, st.Dict, "Subtype") == "Form") return false;
                if (sub.AsDict() is { } d && Ox.GetName(doc, d, "Subtype") == "Form") return false;
            }

        return true;
    }

}
