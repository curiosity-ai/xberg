// Ported from crates/xberg/src/extractors/rtf/metadata.rs
// Metadata extraction from the RTF `\info` block plus computed text statistics.

using System.Text;

namespace Xberg.Internal.Rtf;

/// <summary>
/// A metadata value: string, integer, or list-of-strings (for authors). Mirrors the
/// serde_json Value shapes produced by the Rust extractor.
/// </summary>
internal readonly struct RtfMetaValue
{
    public enum ValueKind { Str, Num, StrList }
    public ValueKind Kind { get; }
    public string? Str { get; }
    public long Num { get; }
    public List<string>? List { get; }

    private RtfMetaValue(ValueKind kind, string? str, long num, List<string>? list)
    {
        Kind = kind; Str = str; Num = num; List = list;
    }

    public static RtfMetaValue OfString(string s) => new(ValueKind.Str, s, 0, null);
    public static RtfMetaValue OfNumber(long n) => new(ValueKind.Num, null, n, null);
    public static RtfMetaValue OfList(List<string> l) => new(ValueKind.StrList, null, 0, l);
}

internal static class RtfMetadata
{
    /// <summary>Parse a `\creatim`/`\revtim` segment into ISO 8601. Mirrors Rust `parse_rtf_datetime`.</summary>
    public static string? ParseRtfDatetime(string segment)
    {
        int? year = null, month = null, day = null, hour = null, minute = null;

        var chars = new CharCursor(segment);
        while (true)
        {
            int ch = chars.Peek();
            if (ch < 0) break;
            if (ch != '\\')
            {
                chars.Next();
                continue;
            }
            chars.Next();
            var (word, value) = RtfEncoding.ParseRtfControlWord(chars);
            if (value is int v)
            {
                switch (word)
                {
                    case "yr": year = v; break;
                    case "mo": month = v; break;
                    case "dy": day = v; break;
                    case "hr": hour = v; break;
                    case "min": minute = v; break;
                }
            }
        }

        if (year is null) return null;
        int mo = Math.Max(1, month ?? 1);
        int dy = Math.Max(1, day ?? 1);
        int hr = Math.Max(0, hour ?? 0);
        int mi = Math.Max(0, minute ?? 0);

        return $"{year.Value:D4}-{mo:D2}-{dy:D2}T{hr:D2}:{mi:D2}:00Z";
    }

    /// <summary>
    /// Extract metadata from the `\info` block and augment with computed statistics.
    /// Mirrors Rust `extract_rtf_metadata`.
    /// </summary>
    public static Dictionary<string, RtfMetaValue> ExtractRtfMetadata(string rtfContent, string extractedText)
    {
        var metadata = new Dictionary<string, RtfMetaValue>();

        int start = rtfContent.IndexOf("{\\info", StringComparison.Ordinal);
        if (start >= 0)
        {
            string slice = rtfContent.Substring(start);

            // Find the balanced end of the info group.
            int depth = 0;
            bool ended = false;
            var block = new StringBuilder();
            foreach (var ch in slice)
            {
                block.Append(ch);
                if (ch == '{')
                {
                    depth += 1;
                }
                else if (ch == '}')
                {
                    if (depth == 0) { ended = false; break; }
                    depth -= 1;
                    if (depth == 0) { ended = true; break; }
                }
            }
            string infoBlock = ended ? block.ToString() : slice;

            // Split into top-level nested segments (seg_depth == 2).
            var segments = new List<string>();
            int segDepth = 0;
            var current = new StringBuilder();
            bool inSegment = false;
            foreach (var ch in infoBlock)
            {
                if (ch == '{')
                {
                    segDepth += 1;
                    if (segDepth == 2)
                    {
                        inSegment = true;
                        current.Clear();
                        continue;
                    }
                }
                else if (ch == '}')
                {
                    if (segDepth == 2 && inSegment)
                    {
                        segments.Add(current.ToString());
                        inSegment = false;
                    }
                    segDepth = segDepth > 0 ? segDepth - 1 : 0;
                    continue;
                }

                if (inSegment)
                    current.Append(ch);
            }

            foreach (var segment in segments)
            {
                if (!segment.StartsWith('\\'))
                    continue;

                string cleanedSegment = segment.StartsWith("\\*\\", StringComparison.Ordinal)
                    ? ReplaceFirst(segment, "\\*\\", "\\")
                    : segment;

                var chars = new CharCursor(cleanedSegment);
                chars.Next(); // skip leading backslash
                var (keyword, numeric) = RtfEncoding.ParseRtfControlWord(chars);
                var remaining = new StringBuilder();
                while (true)
                {
                    int c = chars.Next();
                    if (c < 0) break;
                    RtfChars.AppendCp(remaining, c);
                }
                string trimmed = remaining.ToString().Trim();

                switch (keyword)
                {
                    case "author" when trimmed.Length > 0:
                        metadata["created_by"] = RtfMetaValue.OfString(trimmed);
                        metadata["authors"] = RtfMetaValue.OfList(new List<string> { trimmed });
                        break;
                    case "operator" when trimmed.Length > 0:
                        metadata["modified_by"] = RtfMetaValue.OfString(trimmed);
                        break;
                    case "title" when trimmed.Length > 0:
                        metadata["title"] = RtfMetaValue.OfString(trimmed);
                        break;
                    case "subject" when trimmed.Length > 0:
                        metadata["subject"] = RtfMetaValue.OfString(trimmed);
                        break;
                    case "generator" when trimmed.Length > 0:
                        metadata["generator"] = RtfMetaValue.OfString(trimmed);
                        break;
                    case "creatim":
                    {
                        var dt = ParseRtfDatetime(trimmed);
                        if (dt is not null)
                            metadata["created_at"] = RtfMetaValue.OfString(dt);
                        break;
                    }
                    case "revtim":
                    {
                        var dt = ParseRtfDatetime(trimmed);
                        if (dt is not null)
                            metadata["modified_at"] = RtfMetaValue.OfString(dt);
                        break;
                    }
                    case "version":
                    {
                        var val = numeric ?? (int.TryParse(trimmed, out var p) ? p : (int?)null);
                        if (val is int rv)
                            metadata["revision"] = RtfMetaValue.OfString(rv.ToString());
                        break;
                    }
                    case "nofpages":
                        InsertNumeric(metadata, "page_count", numeric, trimmed);
                        break;
                    case "nofwords":
                        InsertNumeric(metadata, "word_count", numeric, trimmed);
                        break;
                    case "nofchars":
                        InsertNumeric(metadata, "character_count", numeric, trimmed);
                        break;
                    case "lines":
                        InsertNumeric(metadata, "line_count", numeric, trimmed);
                        break;
                    case "paragraphs":
                        InsertNumeric(metadata, "paragraph_count", numeric, trimmed);
                        break;
                }
            }
        }

        string cleanedText = extractedText.Trim();
        if (cleanedText.Length > 0)
        {
            long wordCount = cleanedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (!metadata.ContainsKey("word_count"))
                metadata["word_count"] = RtfMetaValue.OfNumber(wordCount);

            long characterCount = cleanedText.EnumerateRunes().Count();
            if (!metadata.ContainsKey("character_count"))
                metadata["character_count"] = RtfMetaValue.OfNumber(characterCount);

            long lineCount = LinesCount(cleanedText);
            if (!metadata.ContainsKey("line_count"))
                metadata["line_count"] = RtfMetaValue.OfNumber(lineCount);

            long paragraphCount = CountParagraphs(cleanedText);
            if (!metadata.ContainsKey("paragraph_count"))
                metadata["paragraph_count"] = RtfMetaValue.OfNumber(paragraphCount);
        }

        return metadata;
    }

    private static void InsertNumeric(Dictionary<string, RtfMetaValue> map, string key, int? numeric, string trimmed)
    {
        var val = numeric ?? (int.TryParse(trimmed, out var p) ? p : (int?)null);
        if (val is int n)
            map[key] = RtfMetaValue.OfNumber(n);
    }

    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        int idx = s.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0) return s;
        return s.Substring(0, idx) + newValue + s.Substring(idx + oldValue.Length);
    }

    /// <summary>Mirror of Rust `str::lines().count()`.</summary>
    private static long LinesCount(string text)
    {
        if (text.Length == 0) return 0;
        long count = 1;
        foreach (var c in text)
            if (c == '\n') count++;
        if (text.EndsWith('\n')) count--;
        return count;
    }

    private static long CountParagraphs(string text)
    {
        long count = 0;
        int start = 0;
        int idx;
        while ((idx = text.IndexOf("\n\n", start, StringComparison.Ordinal)) >= 0)
        {
            if (text.Substring(start, idx - start).Trim().Length > 0) count++;
            start = idx + 2;
        }
        if (text.Substring(start).Trim().Length > 0) count++;
        return count;
    }
}
