// Ported from pdf_oxide `document.rs`: `extract_spans_impl` (15460), `load_fonts` (19130),
// `page_cannot_have_text` (9629), `may_contain_text` (9578), `extract_chars_impl` (16508)
// and `extract_page_text_with_options` (15979).
//
// This is the page-level driver above the extractor: it decides whether a page can carry
// text at all, loads its fonts, runs the extractor over the content stream twice — once for
// spans, once for the glyph positions those spans are stamped with — then drops off-page
// spans and applies reading order.
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

        // Stamp the char extractor's own per-glyph x-origins onto the finished spans, so
        // everything that decomposes a span through `to_chars` sees spec-aligned positions
        // rather than a prefix-sum of nominal widths. Runs last, on the post-processed
        // spans, so the alignment sees the same text the consumers do.
        if (spans.Count > 0)
        {
            OxCharXOffsets.Stamp(doc, pageIndex, spans, ExtractChars(doc, pageIndex, page));
        }

        OxReadingOrder.DropOffpageSpans(spans, (float)llx, (float)lly, (float)urx, (float)ury);
        result.Spans = OxReadingOrder.OrderSpansColumnAware(spans);

        // `PageText.chars` is left empty: the per-glyph list is consumed by the stamp above
        // and nothing downstream asks for it whole.
        return result;
    }

    /// <summary>
    /// One page's glyphs, in the order `extract_chars_impl` (document.rs:16508) leaves them:
    /// top-to-bottom, then left-to-right.
    /// </summary>
    private static List<OxTextChar> ExtractChars(PdfDocument doc, int pageIndex, PdfDict page)
    {
        var prepared = NewPageExtractor(doc, pageIndex, page);
        if (prepared is null) return new List<OxTextChar>();

        List<OxTextChar> chars;
        try { chars = prepared.Value.Extractor.ExtractOwned(prepared.Value.Content); }
        catch { return new List<OxTextChar>(); }

        OxSpanCompare.SortStable(chars, (a, b) =>
        {
            int yCmp = OxSpanCompare.SafeFloatCmp(b.Bbox.Y, a.Bbox.Y);
            return yCmp != 0 ? yCmp : OxSpanCompare.SafeFloatCmp(a.Bbox.X, b.Bbox.X);
        });
        return chars;
    }

    /// <summary>Raw spans for one page, before reading order.</summary>
    private static List<OxTextSpan> ExtractSpans(PdfDocument doc, int pageIndex, PdfDict page)
    {
        var prepared = NewPageExtractor(doc, pageIndex, page);
        if (prepared is null) return new List<OxTextSpan>();

        try { return prepared.Value.Extractor.ExtractTextSpans(prepared.Value.Content); }
        catch { return new List<OxTextSpan>(); }
    }

    /// <summary>
    /// An extractor loaded with the page's fonts, resources and layer exclusions, plus the
    /// page's decoded content stream — everything both extraction passes need before they
    /// diverge. Null when the page cannot carry text at all.
    /// </summary>
    private static (OxTextExtractor Extractor, byte[] Content)? NewPageExtractor(
        PdfDocument doc, int pageIndex, PdfDict page)
    {
        if (PageCannotHaveText(doc, page)) return null;

        byte[] content;
        try { content = doc.GetPageContent(pageIndex); }
        catch { return null; }
        if (content.Length == 0 || !OxTextExtractor.MayContainText(content)) return null;

        var extractor = new OxTextExtractor();
        extractor.SetPageIndex(pageIndex);
        extractor.SetExcludedLayers(PdfOptionalContent.DefaultOffOcgs(doc));

        var resources = page.Get("Resources");
        if (resources is not null)
        {
            extractor.SetResources(resources);
            extractor.SetDocument(doc);
            extractor.LoadFontsForResources(extractor, resources);
        }

        return (extractor, content);
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
