// Assembles positioned spans into page text, mirroring the xberg oxide layer
// (crates/xberg/src/pdf/oxide/text.rs :: extract_page_text_column_aware) and the
// control-char cleanup in crates/xberg/src/pdf/text.rs.
using System.Text;

namespace Xberg.Internal.Pdf;

public static class PdfPageText
{
    /// <summary>Sort spans into reading order and assemble into text.</summary>
    public static string Assemble(List<TextSpan> spans)
    {
        if (spans.Count == 0) return "";
        var ordered = ReadingOrder(spans);

        // Median height for paragraph-break detection.
        var heights = ordered.Select(s => s.Height).OrderBy(h => h).ToList();
        double medianHeight = heights.Count == 0 ? 1.0 : heights[heights.Count / 2];
        if (medianHeight <= 0) medianHeight = 1.0;
        double paragraphGap = medianHeight * 1.5;

        var sb = new StringBuilder();
        TextSpan? prev = null;
        foreach (var span in ordered)
        {
            if (prev != null)
            {
                double prevEndX = prev.X + prev.Width;
                double yGap = Math.Abs(prev.Y - span.Y);
                double effHeight = Math.Max(Math.Max(span.Height, prev.Height), span.FontSize * 0.5);
                bool sameLine = yGap < effHeight * 0.5;
                if (sameLine)
                {
                    double xGap = span.X - prevEndX;
                    if (xGap > span.FontSize * 0.15) sb.Append(' ');
                }
                else if (yGap > paragraphGap) sb.Append("\n\n");
                else sb.Append('\n');
            }
            sb.Append(span.Text);
            prev = span;
        }
        return sb.ToString();
    }

    // Row-aware reading order — ports pdf_oxide row_aware_span_cmp (ROW_BAND_TOLERANCE_PT = 3.0):
    // quantize Y into fixed 3pt bands (descending = top-first), then X ascending within a band.
    // (pdf_oxide's ColumnAware path additionally applies an XY-cut column split, which is not
    // ported here; single-column pages match, multi-column ordering may differ.)
    private const double RowBandTolerancePt = 3.0;

    private static List<TextSpan> ReadingOrder(List<TextSpan> spans)
    {
        var indexed = new List<(TextSpan s, int i)>(spans.Count);
        for (int i = 0; i < spans.Count; i++) indexed.Add((spans[i], i));
        indexed.Sort((a, b) =>
        {
            int c = RowCompare(a.s, b.s);
            return c != 0 ? c : a.i.CompareTo(b.i); // stable
        });
        var result = new List<TextSpan>(spans.Count);
        foreach (var (s, _) in indexed) result.Add(s);
        return result;
    }

    private static int RowCompare(TextSpan a, TextSpan b)
    {
        int bandA = (int)Math.Round(a.Y / RowBandTolerancePt, MidpointRounding.AwayFromZero);
        int bandB = (int)Math.Round(b.Y / RowBandTolerancePt, MidpointRounding.AwayFromZero);
        if (bandA != bandB) return bandB.CompareTo(bandA); // larger Y first
        return a.X.CompareTo(b.X);
    }

    /// <summary>Port of fix_pdf_control_chars (crates/xberg/src/pdf/text.rs).</summary>
    public static string FixControlChars(string text)
    {
        bool has = false;
        foreach (char c in text)
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { has = true; break; }
        if (!has) return text;

        var chars = text.ToCharArray();
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            if (ch == (char)0x02) continue;               // STX: dropped (ambiguous)
            if (ch == (char)0x03)                         // ETX: "ft" ligature after a letter
            {
                bool prevAlpha = i > 0 && char.IsLetter(chars[i - 1]);
                if (prevAlpha) sb.Append("ft");
                continue;
            }
            if (ch >= (char)0x01 && ch <= (char)0x1F && ch != '\t' && ch != '\n' && ch != '\r')
                continue;                                 // residual C0 control -> dropped
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
