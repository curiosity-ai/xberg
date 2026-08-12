// Ported from crates/xberg/src/extractors/rtf/formatting.rs
// Whitespace normalization with a byte-offset mapping so formatting spans can be
// remapped from pre-normalized to post-normalized text. All offsets are UTF-8 bytes.

using System.Text;

namespace Xberg.Internal.Rtf;

internal static class RtfFormatting
{
    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// Normalize whitespace and produce a sorted (old_byte, new_byte) mapping covering
    /// byte boundaries in the input. Mirrors Rust `normalize_whitespace_with_mapping`.
    /// </summary>
    public static (string Text, List<(int Old, int New)> Mapping) NormalizeWhitespaceWithMapping(string s)
    {
        var mapping = new List<(int, int)>();

        // Step 1: identify surviving lines (trim each, collapse blank runs).
        var keptLines = new List<(string Trimmed, int StartInS)>();
        bool lastBlank = false;
        int lineStart = 0;
        foreach (var line in s.Split('\n'))
        {
            string trimmed = line.Trim();
            int trimOffset = lineStart + (Bytes(line) - Bytes(line.TrimStart()));
            if (trimmed.Length == 0)
            {
                if (!lastBlank && keptLines.Count > 0)
                {
                    keptLines.Add(("", lineStart));
                    lastBlank = true;
                }
            }
            else
            {
                lastBlank = false;
                keptLines.Add((trimmed, trimOffset));
            }
            lineStart += Bytes(line) + 1; // +1 for the consumed '\n'
        }
        // Trim trailing blank lines.
        while (keptLines.Count > 0 && keptLines[^1].Trimmed.Length == 0)
            keptLines.RemoveAt(keptLines.Count - 1);

        // Step 2: build joined string tracking (old_byte, new_byte) per char.
        var joined = new StringBuilder(s.Length);
        int newPos = 0;
        for (int li = 0; li < keptLines.Count; li++)
        {
            if (li > 0)
            {
                joined.Append('\n');
                newPos += 1;
            }
            int oldPos = keptLines[li].StartInS;
            foreach (var rune in keptLines[li].Trimmed.EnumerateRunes())
            {
                mapping.Add((oldPos, newPos));
                RtfChars.AppendCp(joined, rune.Value);
                int len = RtfChars.Utf8Len(rune.Value);
                oldPos += len;
                newPos += len;
            }
        }
        mapping.Add((Bytes(s), newPos)); // sentinel

        // Step 3: collapse runs of spaces within lines.
        var result = new StringBuilder(joined.Length);
        var mapping2 = new List<(int, int)>();
        bool lastWasSpace = false;
        int joinedByte = 0;
        int resultByte = 0;
        foreach (var rune in joined.ToString().EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp == '\n')
            {
                mapping2.Add((joinedByte, resultByte));
                result.Append('\n');
                joinedByte += 1;
                resultByte += 1;
                lastWasSpace = false;
            }
            else if (cp == ' ' || cp == '\t')
            {
                if (!lastWasSpace)
                {
                    mapping2.Add((joinedByte, resultByte));
                    result.Append(' ');
                    resultByte += 1;
                    lastWasSpace = true;
                }
                joinedByte += RtfChars.Utf8Len(cp);
            }
            else
            {
                mapping2.Add((joinedByte, resultByte));
                RtfChars.AppendCp(result, cp);
                int len = RtfChars.Utf8Len(cp);
                joinedByte += len;
                resultByte += len;
                lastWasSpace = false;
            }
        }
        mapping2.Add((joinedByte, resultByte));

        // Step 4: trim, remove spaces before punctuation / after pipe.
        string resultStr = result.ToString();
        string trimmedResult = resultStr.Trim();
        int trimStart = Bytes(resultStr) - Bytes(resultStr.TrimStart());

        var charsVec = new List<int>();
        foreach (var rune in trimmedResult.EnumerateRunes())
            charsVec.Add(rune.Value);

        var cleaned = new StringBuilder(trimmedResult.Length);
        var mapping3 = new List<(int, int)>();
        int trimmedByte = 0;
        int cleanedByte = 0;
        int ci = 0;
        while (ci < charsVec.Count)
        {
            bool skip;
            if (charsVec[ci] == ' '
                && ci + 1 < charsVec.Count
                && IsPunct(charsVec[ci + 1])
                && (ci == 0 || charsVec[ci - 1] != ' '))
            {
                skip = true;
            }
            else
            {
                skip = charsVec[ci] == ' ' && ci > 0 && charsVec[ci - 1] == '|';
            }
            if (skip)
            {
                trimmedByte += RtfChars.Utf8Len(charsVec[ci]);
                ci += 1;
                continue;
            }
            mapping3.Add((trimmedByte, cleanedByte));
            RtfChars.AppendCp(cleaned, charsVec[ci]);
            int len = RtfChars.Utf8Len(charsVec[ci]);
            trimmedByte += len;
            cleanedByte += len;
            ci += 1;
        }
        mapping3.Add((trimmedByte, cleanedByte));

        // Compose: s -> joined (mapping), joined -> result (mapping2),
        //          result -> trimmed (subtract trim_start), trimmed -> cleaned (mapping3).
        var finalMapping = new List<(int, int)>(mapping.Count);
        foreach (var (sOff, joinedOff) in mapping)
        {
            int resultOff = ApplyMapping(mapping2, joinedOff);
            if (resultOff < trimStart)
            {
                finalMapping.Add((sOff, 0));
                continue;
            }
            int trimmedOff = resultOff - trimStart;
            int cleanedOff = ApplyMapping(mapping3, trimmedOff);
            finalMapping.Add((sOff, cleanedOff));
        }

        return (cleaned.ToString(), finalMapping);
    }

    private static bool IsPunct(int cp) =>
        cp == '.' || cp == ',' || cp == ';' || cp == ':' || cp == '!' || cp == '?' || cp == '|';

    /// <summary>Look up a byte offset in a sorted mapping via binary search + interpolation.</summary>
    private static int ApplyMapping(List<(int Old, int New)> mapping, int offset)
    {
        if (mapping.Count == 0) return offset;

        // Binary search by Old key; mirrors Rust binary_search_by_key semantics.
        int lo = 0, hi = mapping.Count; // hi is exclusive insertion bound
        int found = -1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int key = mapping[mid].Old;
            if (key == offset) { found = mid; break; }
            if (key < offset) lo = mid + 1;
            else hi = mid;
        }

        if (found >= 0)
            return mapping[found].New;

        int i = lo; // insertion point (Err(i))
        if (i == 0)
            return mapping[0].New;
        if (i >= mapping.Count)
        {
            var (lastOld, lastNew) = mapping[^1];
            if (offset >= lastOld)
                return lastNew + (offset - lastOld);
            return lastNew;
        }
        var (oldLo, newLo) = mapping[i - 1];
        int delta = offset - oldLo;
        return newLo + delta;
    }

    /// <summary>Map a byte offset from pre-normalized to post-normalized text.</summary>
    public static int MapOffset(List<(int Old, int New)> mapping, int offset) => ApplyMapping(mapping, offset);

    /// <summary>
    /// Normalize whitespace in a string (no mapping). Mirrors Rust `normalize_whitespace`.
    /// </summary>
    public static string NormalizeWhitespace(string s)
    {
        var lines = new List<string>();
        bool lastBlank = false;
        foreach (var line in s.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (!lastBlank && lines.Count > 0)
                {
                    lines.Add("");
                    lastBlank = true;
                }
            }
            else
            {
                lastBlank = false;
                lines.Add(trimmed);
            }
        }
        while (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        string joined = string.Join("\n", lines);

        var result = new StringBuilder(joined.Length);
        bool lastWasSpace = false;
        foreach (var ch in joined)
        {
            if (ch == '\n')
            {
                result.Append('\n');
                lastWasSpace = false;
            }
            else if (ch == ' ' || ch == '\t')
            {
                if (!lastWasSpace)
                {
                    result.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                result.Append(ch);
                lastWasSpace = false;
            }
        }

        string res = result.ToString().Trim();
        var cleaned = new StringBuilder(res.Length);
        var chars = res.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == ' '
                && i + 1 < chars.Length
                && IsPunct(chars[i + 1])
                && (i == 0 || chars[i - 1] != ' '))
            {
                i += 1;
                continue;
            }
            if (chars[i] == ' ' && i > 0 && chars[i - 1] == '|')
            {
                i += 1;
                continue;
            }
            cleaned.Append(chars[i]);
            i += 1;
        }
        return cleaned.ToString();
    }
}
