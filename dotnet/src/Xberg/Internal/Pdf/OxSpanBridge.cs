// The join between the ported pdf_oxide span producer and the port's existing page-text
// assembly (`PdfPageText.AssembleWithLines`, which ports xberg's own `assemble_page_text`).
//
// Upstream is a single Rust program, so the two halves share one `TextSpan`. Here the
// producer is a port of pdf_oxide's type and the consumer a port of xberg's, so the span
// crosses one boundary — this is that boundary, and nothing else should convert between
// the two span types.
using System.Collections.Generic;
using Xberg.Internal.PdfOxide;

namespace Xberg.Internal.Pdf;

internal static class OxSpanBridge
{
    /// <summary>The extractor's spans in the shape the page-text assembler consumes.</summary>
    public static List<TextSpan> ToPdfSpans(IReadOnlyList<OxTextSpan> spans)
    {
        var converted = new List<TextSpan>(spans.Count);
        foreach (var s in spans)
        {
            converted.Add(new TextSpan
            {
                Text = s.Text,
                X = s.Bbox.X,
                Y = s.Bbox.Y,
                Width = s.Bbox.Width,
                Height = s.Bbox.Height,
                FontSize = s.FontSize,
                FontName = s.FontName,
                // pdf_oxide grades weight on the CSS scale; the assembler only asks
                // whether the run is bold, and semibold and up reads as bold.
                IsBold = (int)s.FontWeight >= 600,
                IsItalic = s.IsItalic,
                IsMonospace = s.IsMonospace,
                RotationDegrees = s.RotationDegrees,
                TextRiseRatio = s.TextRise,
                Sequence = s.Sequence,
            });
        }
        return converted;
    }
}
