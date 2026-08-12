// RTL (Arabic/Hebrew) visual-vs-logical correction, ported from pdf_oxide
// (pdf_oxide-0.3.73/src/text/bidi.rs :: detect_visual_order_run /
// reverse_rtl_keep_numbers, and extractors/text.rs "Step 3b: RTL text
// correction"). PDF producers emit RTL text either in visual order (glyphs
// drawn left-to-right) or logical order (drawn right-to-left); visual-order
// runs must be reversed to logical order for extraction output.
namespace Xberg.Internal.Pdf;

internal static class PdfBidi
{
    internal enum RunOrder { Visual, Logical, Ambiguous }

    // rtl_detector::is_rtl_text — any Arabic/Hebrew script range.
    internal static bool IsRtlText(int cp) =>
        (cp >= 0x0600 && cp <= 0x06FF) ||
        (cp >= 0x0590 && cp <= 0x05FF) ||
        (cp >= 0x0750 && cp <= 0x077F) ||
        (cp >= 0x08A0 && cp <= 0x08FF) ||
        (cp >= 0xFB50 && cp <= 0xFDFF) ||
        (cp >= 0xFE70 && cp <= 0xFEFF);

    // rtl_detector::is_arabic_letter
    private static bool IsArabicLetter(int cp) =>
        (cp >= 0x0621 && cp <= 0x063A) ||
        (cp >= 0x0641 && cp <= 0x064A) ||
        (cp >= 0x0750 && cp <= 0x076D) ||
        (cp >= 0x08A0 && cp <= 0x08B4) ||
        (cp >= 0x08B6 && cp <= 0x08BD);

    // rtl_detector::is_hebrew_letter (U+05D0–U+05EA base letters + U+05EF–U+05F2)
    private static bool IsHebrewLetter(int cp) =>
        (cp >= 0x05D0 && cp <= 0x05EA) || (cp >= 0x05EF && cp <= 0x05F2);

    /// <summary>Per show-op cluster RTL correction (extractors/text.rs step 3b):
    /// reverse visual-order RTL text to logical order, gated by the geometric
    /// x-monotonicity detector with a last_x&gt;first_x fallback.</summary>
    internal static string CorrectRtlClusterOrder(string text, List<(char c, double x)> charsWithX)
    {
        if (text.Length <= 1 || charsWithX.Count < 2) return text;
        bool hasRtl = false;
        foreach (var ch in text)
            if (IsRtlText(ch)) { hasRtl = true; break; }
        if (!hasRtl) return text;

        switch (DetectVisualOrderRun(charsWithX))
        {
            case RunOrder.Visual:
                return ReverseRtlKeepNumbers(text);
            case RunOrder.Logical:
                return text;
            default:
                // Short/ambiguous cluster: pre-v0.3.54 heuristic — glyphs placed
                // left-to-right (last_x > first_x) means visual order.
                return charsWithX[^1].x > charsWithX[0].x ? ReverseRtlKeepNumbers(text) : text;
        }
    }

    /// <summary>Port of text::bidi::detect_visual_order_run.</summary>
    internal static RunOrder DetectVisualOrderRun(List<(char c, double x)> charsWithX)
    {
        // Arabic Presentation Forms → owned by another pass; ambiguous.
        foreach (var (c, _) in charsWithX)
        {
            int cp = c;
            if ((cp >= 0xFB50 && cp <= 0xFDFF) || (cp >= 0xFE70 && cp <= 0xFEFF))
                return RunOrder.Ambiguous;
        }

        var rtl = new List<double>();
        foreach (var (c, x) in charsWithX)
            if (IsArabicLetter(c) || IsHebrewLetter(c)) rtl.Add(x);
        if (rtl.Count < 4) return RunOrder.Ambiguous;

        const double KernTol = 0.5;
        int asc = 0, desc = 0;
        for (int i = 0; i + 1 < rtl.Count; i++)
        {
            double dx = rtl[i + 1] - rtl[i];
            if (dx > KernTol) asc++;
            else if (dx < -KernTol) desc++;
        }
        int total = asc + desc;
        if (total == 0) return RunOrder.Ambiguous;
        if (10 * asc > 9 * total) return RunOrder.Visual;
        if (10 * desc > 9 * total) return RunOrder.Logical;
        return RunOrder.Ambiguous;
    }

    /// <summary>Port of text::bidi::reverse_rtl_keep_numbers — reverse the run
    /// while keeping embedded number sequences (digit (sep digit)*) forward.</summary>
    internal static string ReverseRtlKeepNumbers(string s)
    {
        var chars = s.ToCharArray();
        int n = chars.Length;
        static bool IsDigitCh(char c) =>
            (c >= '0' && c <= '9') || (c >= '٠' && c <= '٩') || (c >= '۰' && c <= '۹');
        static bool IsSep(char c) => c is '.' or ',' or ':' or '٫' or '٬';

        var inNum = new bool[n];
        int i = 0;
        while (i < n)
        {
            if (IsDigitCh(chars[i]))
            {
                int start = i;
                int j = i + 1;
                while (true)
                {
                    if (j < n && IsDigitCh(chars[j])) j++;
                    else if (j + 1 < n && IsSep(chars[j]) && IsDigitCh(chars[j + 1])) j += 2;
                    else break;
                }
                for (int k = start; k < j; k++) inNum[k] = true;
                i = j;
            }
            else i++;
        }

        var outChars = new List<char>(n);
        i = n;
        while (i > 0)
        {
            i--;
            if (inNum[i])
            {
                int end = i + 1;
                while (i > 0 && inNum[i - 1]) i--;
                for (int k = i; k < end; k++) outChars.Add(chars[k]);
            }
            else outChars.Add(chars[i]);
        }
        return new string(outChars.ToArray());
    }
}
