// Word-level extraction, ported from pdf_oxide-0.3.77:
//   document.rs:16623  Document::extract_words_inner
//   layout/text_block.rs:216  TextSpan::to_chars
//   layout/text_block.rs:723  Word::from_chars
//   layout/clustering.rs:268  cluster_chars_into_words (the non-`ml` build, which is
//                             what xberg's `default-features = false` dependency selects)
//   layout/document_analyzer.rs:166/373  median char width and the word-gap threshold
//
// This is the granularity the table detector is fed: `extract_tables_with_config` calls
// `extract_words`, not `extract_spans`, so a table cell's text is assembled from words,
// not from show-operator runs. The difference is not cosmetic — the clustering re-sorts
// a span's glyphs by geometry before grouping them, so a run whose glyphs were drawn out
// of positional order reaches the cell in geometric order rather than emission order.
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xberg.Internal.PdfOxide.Text;

namespace Xberg.Internal.PdfOxide.Layout;

/// <summary>A cluster of glyphs with no whitespace and no gap wider than the word threshold.</summary>
internal sealed class OxWord
{
    public string Text = "";
    public OxRect Bbox;
    public float AvgFontSize;
    public string DominantFont = "";
    public bool IsBold;
    public bool IsItalic;
    public int? Mcid;
    public int Sequence;
    public float RotationDegrees;
    public int CharCount;
}

internal static class OxWordExtraction
{
    /// <summary>
    /// One positioned glyph, as <c>TextSpan::to_chars</c> reports it. Held separately from
    /// <see cref="OxTextChar"/> because Rust keys these on Unicode scalars and that type
    /// carries a UTF-16 unit; only the fields the word pipeline reads are kept.
    /// </summary>
    internal readonly struct OxGlyph
    {
        public readonly int CodePoint;
        public readonly OxRect Bbox;
        public readonly string FontName;
        public readonly float FontSize;
        public readonly OxFontWeight FontWeight;
        public readonly bool IsItalic;
        public readonly int? Mcid;
        public readonly float RotationDegrees;

        public OxGlyph(int codePoint, OxRect bbox, OxTextSpan span)
        {
            CodePoint = codePoint;
            Bbox = bbox;
            FontName = span.FontName;
            FontSize = span.FontSize;
            FontWeight = span.FontWeight;
            IsItalic = span.IsItalic;
            Mcid = span.Mcid;
            RotationDegrees = span.RotationDegrees;
        }
    }

    /// <summary>
    /// A span's glyphs with their own positions (text_block.rs:216).
    /// </summary>
    /// <remarks>
    /// The captured per-glyph origins are preferred over prefix-summing the nominal widths,
    /// which drifts because those widths omit TJ kerning (§9.4.3). They are only trusted when
    /// they cover every glyph and all land inside the span's own box; otherwise the widths,
    /// then a uniform division, stand in.
    /// </remarks>
    internal static List<OxGlyph> ToChars(OxTextSpan span)
    {
        var runes = span.Text.EnumerateRunes().ToList();
        int charCount = runes.Count;
        if (charCount == 0)
        {
            return new List<OxGlyph>();
        }

        var result = new List<OxGlyph>(charCount);

        const float OffsetBboxTolerance = 0.5f;
        bool offsetsFitBbox = span.CharXOffsets.All(x =>
            !float.IsNaN(x) && !float.IsInfinity(x)
            && x >= span.Bbox.X - OffsetBboxTolerance
            && x <= span.Bbox.X + span.Bbox.Width + OffsetBboxTolerance);

        if (span.CharXOffsets.Count == charCount && offsetsFitBbox)
        {
            bool hasWidths = span.CharWidths.Count == charCount;
            for (int i = 0; i < charCount; i++)
            {
                float charX = span.CharXOffsets[i];
                float w = hasWidths
                    ? span.CharWidths[i]
                    : i + 1 < charCount
                        ? MathF.Max(span.CharXOffsets[i + 1] - charX, 0.0f)
                        : MathF.Max(span.Bbox.X + span.Bbox.Width - charX, 0.0f);
                result.Add(MakeChar(span, runes[i], charX, w));
            }
            return result;
        }

        if (span.CharWidths.Count == charCount)
        {
            float x = span.Bbox.X;
            for (int i = 0; i < charCount; i++)
            {
                float w = span.CharWidths[i];
                result.Add(MakeChar(span, runes[i], x, w));
                x += w;
            }
            return result;
        }

        float uniform = span.Bbox.Width / charCount;
        for (int i = 0; i < charCount; i++)
        {
            result.Add(MakeChar(span, runes[i], span.Bbox.X + uniform * i, uniform));
        }
        return result;
    }

    private static OxGlyph MakeChar(OxTextSpan span, Rune rune, float charX, float width) =>
        new(rune.Value, new OxRect(charX, span.Bbox.Y, width, span.Bbox.Height), span);

    /// <summary>
    /// Group a span's glyphs into words by spatial proximity (clustering.rs:268).
    /// </summary>
    /// <remarks>
    /// The glyphs are re-sorted by centre Y (descending) then centre X before grouping, so
    /// glyphs the producer drew out of positional order are clustered — and read back — in
    /// geometric order, not emission order.
    /// </remarks>
    internal static List<List<int>> ClusterCharsIntoWords(IReadOnlyList<OxGlyph> chars, float epsilon)
    {
        var clusters = new List<List<int>>();
        if (chars.Count == 0)
        {
            return clusters;
        }
        if (chars.Count == 1)
        {
            clusters.Add(new List<int> { 0 });
            return clusters;
        }

        var indices = Enumerable.Range(0, chars.Count).ToList();
        OxSpanCompare.SortStable(indices, (a, b) =>
        {
            int yCmp = OxSpanCompare.SafeFloatCmp(chars[b].Bbox.Center.Y, chars[a].Bbox.Center.Y);
            return yCmp != 0 ? yCmp : OxSpanCompare.SafeFloatCmp(chars[a].Bbox.Center.X, chars[b].Bbox.Center.X);
        });

        // Lines first: glyphs within half a font size vertically.
        var lines = new List<List<int>>();
        var currentLine = new List<int> { indices[0] };
        float lineY = chars[indices[0]].Bbox.Center.Y;
        for (int i = 1; i < indices.Count; i++)
        {
            int idx = indices[i];
            float y = chars[idx].Bbox.Center.Y;
            float fontHalf = chars[idx].FontSize * 0.5f;
            if (MathF.Abs(y - lineY) < MathF.Max(fontHalf, chars[currentLine[0]].FontSize * 0.5f))
            {
                currentLine.Add(idx);
            }
            else
            {
                lines.Add(currentLine);
                currentLine = new List<int> { idx };
                lineY = y;
            }
        }
        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        // Then by X proximity within each line; the line is already X-sorted.
        foreach (var line in lines)
        {
            var cluster = new List<int> { line[0] };
            for (int i = 1; i < line.Count; i++)
            {
                int idx = line[i];
                int prevIdx = cluster[^1];
                float xGap = MathF.Max(chars[idx].Bbox.Left - chars[prevIdx].Bbox.Right, 0.0f);
                if (xGap <= epsilon)
                {
                    cluster.Add(idx);
                }
                else
                {
                    OxSpanCompare.SortStable(cluster, (a, b) => OxSpanCompare.SafeFloatCmp(chars[a].Bbox.X, chars[b].Bbox.X));
                    clusters.Add(cluster);
                    cluster = new List<int> { idx };
                }
            }
            OxSpanCompare.SortStable(cluster, (a, b) => OxSpanCompare.SafeFloatCmp(chars[a].Bbox.X, chars[b].Bbox.X));
            clusters.Add(cluster);
        }

        return clusters;
    }

    /// <summary>The median of a page's glyph widths (document_analyzer.rs:166).</summary>
    private static float MedianCharWidth(IReadOnlyList<OxGlyph> chars)
    {
        if (chars.Count == 0)
        {
            return 6.0f;
        }
        var widths = chars.Select(c => c.Bbox.Width).ToList();
        widths.Sort((a, b) => OxSpanCompare.SafeFloatCmp(a, b));
        return widths[widths.Count / 2];
    }

    /// <summary>
    /// The page's words in reading order (document.rs:16623), the granularity the table
    /// detector consumes.
    /// </summary>
    internal static List<OxWord> ExtractWords(IReadOnlyList<OxTextSpan> spans)
    {
        var words = new List<OxWord>();
        if (spans.Count == 0)
        {
            return words;
        }

        // Materialised once: the clustering loop below reads the same glyphs the page
        // statistics were computed from.
        var allChars = new List<OxGlyph>();
        var spanRanges = new List<(int Start, int End)>(spans.Count);
        foreach (var s in spans)
        {
            int start = allChars.Count;
            allChars.AddRange(ToChars(s));
            spanRanges.Add((start, allChars.Count));
        }
        if (allChars.Count == 0)
        {
            return words;
        }

        // A space runs 25-35% of a character width, so 30% of the page's median separates
        // inter-word gaps from kerning without any per-font knowledge.
        float wordGapThreshold = MedianCharWidth(allChars) * 0.3f;

        // A word index the merge below must not absorb into its predecessor: the span it came
        // from opened a hard boundary (a table cell, a column).
        var splitBoundaryWordIndices = new HashSet<int>();
        // A rotated run advances along its own axis but reports a bbox flattened onto X, so its
        // box overlaps unrelated perpendicular columns; merging into or out of one fuses a whole
        // column into a single token.
        var rotatedWordIndices = new HashSet<int>();

        for (int spanIdx = 0; spanIdx < spans.Count; spanIdx++)
        {
            var span = spans[spanIdx];
            (int start, int end) = spanRanges[spanIdx];
            if (end <= start)
            {
                continue;
            }
            var spanChars = new List<OxGlyph>(end - start);
            for (int i = start; i < end; i++)
            {
                spanChars.Add(allChars[i]);
            }

            // Clustering per span, not across the page: a PDF span is usually a word or a line
            // fragment, so its own glyphs are the safe neighbourhood.
            var clusters = ClusterCharsIntoWords(spanChars, wordGapThreshold);

            int firstWordIdx = words.Count;
            foreach (var clusterIndices in clusters)
            {
                var current = new List<OxGlyph>();
                foreach (int ci in clusterIndices)
                {
                    var c = spanChars[ci];
                    if (Rune.IsWhiteSpace(new Rune(c.CodePoint)) || c.CodePoint == '\n' || c.CodePoint == '\r')
                    {
                        if (current.Count > 0)
                        {
                            words.Add(FromChars(current, span.Sequence));
                            current = new List<OxGlyph>();
                        }
                    }
                    else
                    {
                        current.Add(c);
                    }
                }
                if (current.Count > 0)
                {
                    words.Add(FromChars(current, span.Sequence));
                }
            }

            if (span.SplitBoundaryBefore && words.Count > firstWordIdx)
            {
                splitBoundaryWordIndices.Add(firstWordIdx);
            }
            if (span.RotationDegrees != 0.0f)
            {
                for (int i = firstWordIdx; i < words.Count; i++)
                {
                    rotatedWordIndices.Add(i);
                }
            }
        }

        return MergeAdjacentWords(words, splitBoundaryWordIndices, rotatedWordIndices);
    }

    /// <summary>
    /// Fuse words whose boxes abut or overlap on one line (document.rs:16778). Producers —
    /// tagged CJK documents especially — encode typographically adjacent glyphs as separate
    /// marked-content runs, which would otherwise never match a ground-truth token.
    /// </summary>
    private static List<OxWord> MergeAdjacentWords(
        List<OxWord> words,
        HashSet<int> splitBoundaryWordIndices,
        HashSet<int> rotatedWordIndices)
    {
        var merged = new List<OxWord>(words.Count);
        // Carried alongside `merged` rather than re-derived: the predecessor grows on every
        // merge, so rescanning it would make a chain of k merges cost O(k^2) characters. The
        // test is an `any` over the characters, so it composes by OR.
        var mergedRtl = new List<bool>(words.Count);
        bool prevRotated = false;

        for (int idx = 0; idx < words.Count; idx++)
        {
            var word = words[idx];
            bool curRotated = rotatedWordIndices.Contains(idx);
            bool wordRtl = LooksRtl(word.Text);

            if (!curRotated && !prevRotated && !splitBoundaryWordIndices.Contains(idx) && merged.Count > 0)
            {
                var prev = merged[^1];
                float gap = word.Bbox.X - (prev.Bbox.X + prev.Bbox.Width);
                float yDiff = MathF.Abs(word.Bbox.Y - prev.Bbox.Y);
                float deltaX = word.Bbox.X - prev.Bbox.X;
                float lineH = MathF.Max(prev.Bbox.Height, word.Bbox.Height);
                float fontSize = MathF.Max(MathF.Max(prev.AvgFontSize, word.AvgFontSize), 1.0f);
                bool notRtl = !mergedRtl[^1] && !wordRtl;

                // `gap` has no lower bound, so a word that backtracks far behind its
                // predecessor's origin also satisfies the gap test. Displayed math draws a
                // fraction's denominator after the relation sign that follows the numerator,
                // which lands exactly there; left unguarded the chain collapses a whole
                // equation into one word. RTL's leftward flow is ordinary reading order.
                bool isMathBacktrack = yDiff > 1.0f && deltaX <= 0.5f && gap < -fontSize && notRtl;

                // A line wrap can land at nearly the same Y as the line above (sub-point
                // baseline drift), but it always resets X toward the left margin — an order of
                // magnitude further than the math backtrack above, and unreachable by any
                // same-line construct, so it is rejected whatever the Y difference.
                bool isLineWrapReset = deltaX < -5.0f * fontSize && notRtl;

                if (yDiff <= lineH * 0.5f && gap <= fontSize * 0.15f && !isMathBacktrack && !isLineWrapReset)
                {
                    float prevN = prev.CharCount;
                    float wordN = word.CharCount;
                    prev.Bbox = prev.Bbox.Union(word.Bbox);
                    prev.AvgFontSize = (prev.AvgFontSize * prevN + word.AvgFontSize * wordN) / (prevN + wordN);
                    if (wordN > prevN)
                    {
                        prev.DominantFont = word.DominantFont;
                    }
                    prev.IsBold |= word.IsBold;
                    prev.IsItalic |= word.IsItalic;
                    if (prev.Mcid != word.Mcid)
                    {
                        prev.Mcid = null;
                    }
                    prev.Text += word.Text;
                    prev.CharCount += word.CharCount;
                    mergedRtl[^1] = mergedRtl[^1] || wordRtl;
                    continue;
                }
            }

            merged.Add(word);
            mergedRtl.Add(wordRtl);
            prevRotated = curRotated;
        }

        return merged;
    }

    private static bool LooksRtl(string text)
    {
        foreach (Rune r in text.EnumerateRunes())
        {
            if (ScriptSignals.IsRtlText(r.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One word from its glyphs (text_block.rs:723).</summary>
    private static OxWord FromChars(List<OxGlyph> chars, int sequence)
    {
        var sb = new StringBuilder(chars.Count);
        foreach (var c in chars)
        {
            sb.Append(new Rune(c.CodePoint).ToString());
        }

        OxRect bbox = chars[0].Bbox;
        float fontSum = 0.0f;
        var fontCounts = new Dictionary<string, int>();
        bool isBold = false, isItalic = false;
        foreach (var c in chars)
        {
            bbox = bbox.Union(c.Bbox);
            fontSum += c.FontSize;
            fontCounts.TryGetValue(c.FontName, out int n);
            fontCounts[c.FontName] = n + 1;
            isBold |= c.FontWeight == OxFontWeight.Bold;
            isItalic |= c.IsItalic;
        }

        string dominantFont = "";
        int best = -1;
        foreach (var (name, n) in fontCounts)
        {
            if (n > best)
            {
                best = n;
                dominantFont = name;
            }
        }

        int? mcid = chars[0].Mcid;
        if (mcid is not null && chars.Any(c => c.Mcid != mcid))
        {
            mcid = null;
        }

        // A word's glyphs share one rendering matrix in practice; a mixed-rotation cluster has
        // no single frame to describe it, so it reads as upright.
        float rotation = chars[0].RotationDegrees;
        if (chars.Any(c => c.RotationDegrees != rotation))
        {
            rotation = 0.0f;
        }

        return new OxWord
        {
            Text = sb.ToString(),
            Bbox = bbox,
            AvgFontSize = fontSum / chars.Count,
            DominantFont = dominantFont,
            IsBold = isBold,
            IsItalic = isItalic,
            Mcid = mcid,
            Sequence = sequence,
            RotationDegrees = rotation,
            CharCount = chars.Count,
        };
    }
}
