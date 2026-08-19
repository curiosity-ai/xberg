using System.Text;

namespace Xberg.Internal.Commonmark;

/// <summary>Event kinds emitted by <see cref="MarkdownParser"/>, mirroring the subset of
/// pulldown-cmark <c>Event</c>/<c>Tag</c> variants that the Rust Markdown extractor consumes.</summary>
public enum MdEventKind
{
    StartHeading, EndHeading,
    StartParagraph, EndParagraph,
    StartStrong, EndStrong,
    StartEmphasis, EndEmphasis,
    StartStrikethrough, EndStrikethrough,
    StartLink, EndLink,
    StartCodeBlock, EndCodeBlock,
    StartBlockQuote, EndBlockQuote,
    StartList, EndList,
    StartItem, EndItem,
    StartTable, EndTable,
    StartTableRow, EndTableRow,
    StartTableCell, EndTableCell,
    StartImage, EndImage,
    StartFootnoteDefinition, EndFootnoteDefinition,
    Code, Text, SoftBreak, HardBreak, FootnoteReference, Html, TaskListMarker,
    InlineMath, DisplayMath,
}

public struct MdEvent
{
    public MdEventKind Kind;
    public string Text;          // Text/Code/Html payload, footnote name/label
    public byte Level;           // heading level
    public bool Ordered;         // list ordered
    public string Url;           // link/image dest
    public string? LinkTitle;    // link title
    public bool Checked;         // task list marker

    public static MdEvent Simple(MdEventKind k) => new() { Kind = k, Text = "", Url = "" };
    public static MdEvent WithText(MdEventKind k, string t) => new() { Kind = k, Text = t, Url = "" };
}

/// <summary>
/// A pragmatic CommonMark/GFM block+inline parser producing <see cref="MdEvent"/> streams
/// compatible with the Rust Markdown extractor's event handling. It covers ATX headings,
/// paragraphs, fenced code blocks, blockquotes, bullet/ordered lists (with nesting), GFM pipe
/// tables, inline emphasis/strong/strikethrough/code, links, images, autolinks and footnotes.
///
/// NOTE: This is not a byte-exact port of pulldown-cmark; adversarial edge cases (complex
/// emphasis flanking, reference links, HTML blocks) may differ. It targets the mainstream
/// constructs that dominate the fixture corpus.
/// </summary>
public static class MarkdownParser
{
    [ThreadStatic] private static Dictionary<string, (string Url, string? Title)>? _refs;

    public static List<MdEvent> Parse(string text)
    {
        var lines = SplitLines(text);
        _refs = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        ScanRefDefs(lines);
        var ev = new List<MdEvent>();
        ParseBlocks(lines, 0, lines.Count, ev);
        return ev;
    }

    private static void ScanRefDefs(List<string> lines)
    {
        bool inFence = false; char fenceChar = ' '; int fenceLen = 0;
        foreach (var line in lines)
        {
            string ts = line.TrimStart();
            int indent = line.Length - ts.Length;
            if (inFence)
            {
                if (indent <= 3 && ts.Length >= fenceLen && ts[0] == fenceChar
                    && ts.TakeWhile(c => c == fenceChar).Count() >= fenceLen
                    && ts.TrimEnd(fenceChar).Trim().Length == 0)
                    inFence = false;
                continue;
            }
            if (indent <= 3 && (ts.StartsWith("```") || ts.StartsWith("~~~")))
            {
                fenceChar = ts[0];
                fenceLen = ts.TakeWhile(c => c == fenceChar).Count();
                inFence = true;
                continue;
            }
            if (TryParseRefDef(line, out string key, out string url, out string? title))
                _refs!.TryAdd(key, (url, title));
        }
    }

    private static string NormalizeRefLabel(string s)
    {
        var parts = s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static bool TryParseRefDef(string line, out string key, out string url, out string? title)
    {
        key = ""; url = ""; title = null;
        string ts = line.TrimStart();
        int indent = line.Length - ts.Length;
        if (indent > 3 || ts.Length == 0 || ts[0] != '[') return false;
        int close = -1;
        for (int k = 1; k < ts.Length; k++)
        {
            if (ts[k] == '\\') { k++; continue; }
            if (ts[k] == ']') { close = k; break; }
            if (ts[k] == '[') return false;
        }
        if (close < 0 || close + 1 >= ts.Length || ts[close + 1] != ':') return false;
        string label = ts.Substring(1, close - 1);
        if (label.Trim().Length == 0) return false;

        int p = close + 2;
        while (p < ts.Length && (ts[p] == ' ' || ts[p] == '\t')) p++;
        var sb = new StringBuilder();
        if (p < ts.Length && ts[p] == '<')
        {
            p++;
            while (p < ts.Length && ts[p] != '>') { sb.Append(ts[p]); p++; }
            if (p < ts.Length) p++;
        }
        else
        {
            while (p < ts.Length && !char.IsWhiteSpace(ts[p])) { sb.Append(ts[p]); p++; }
        }
        if (sb.Length == 0) return false;
        url = sb.ToString();

        int save = p;
        while (p < ts.Length && (ts[p] == ' ' || ts[p] == '\t')) p++;
        if (p < ts.Length && (ts[p] == '"' || ts[p] == '\'' || ts[p] == '('))
        {
            char cl = ts[p] == '(' ? ')' : ts[p];
            p++;
            var tb = new StringBuilder();
            while (p < ts.Length && ts[p] != cl)
            {
                if (ts[p] == '\\' && p + 1 < ts.Length) { tb.Append(ts[p + 1]); p += 2; continue; }
                tb.Append(ts[p]); p++;
            }
            if (p < ts.Length) { p++; title = tb.ToString(); }
            int after = p;
            while (after < ts.Length && char.IsWhiteSpace(ts[after])) after++;
            if (after != ts.Length) { title = null; p = save; }
        }
        else p = save;

        while (p < ts.Length && char.IsWhiteSpace(ts[p])) p++;
        if (p != ts.Length) return false;
        key = NormalizeRefLabel(label);
        return true;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            int end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text.Substring(start, end - start));
        }
        return lines;
    }

    private static bool IsBlank(string s) => s.Trim().Length == 0;

    /// <summary>Number of leading ASCII block-indentation characters (space/tab only).
    /// Unlike <see cref="string.TrimStart()"/>, Unicode whitespace such as U+00A0 (nbsp) is
    /// NOT treated as indentation — per CommonMark it is ordinary inline content. Counting it
    /// as indentation caused a non-terminating loop when the indented-code branch was entered
    /// on an nbsp-indented line but the inner scanner (ASCII-space only) refused to advance.</summary>
    private static int AsciiIndentLen(string s)
    {
        int k = 0;
        while (k < s.Length && (s[k] == ' ' || s[k] == '\t')) k++;
        return k;
    }

    private static void ParseBlocks(List<string> lines, int lo, int hi, List<MdEvent> ev)
    {
        int i = lo;
        int stuckAt = -1;
        while (i < hi)
        {
            // Strict-progress safety net: if a full iteration ever returns to the top without
            // advancing the cursor, force a single-line advance so no input can hang the parser.
            if (i == stuckAt) { i++; stuckAt = -1; continue; }
            stuckAt = i;

            string line = lines[i];
            if (IsBlank(line)) { i++; continue; }

            int indent = AsciiIndentLen(line);
            string trimmedStart = line.Substring(indent);

            // Footnote definition: [^label]: content (+ indented continuation lines).
            if (indent <= 3 && trimmedStart.StartsWith("[^", StringComparison.Ordinal))
            {
                int fc = trimmedStart.IndexOf(']');
                if (fc > 2 && fc + 1 < trimmedStart.Length && trimmedStart[fc + 1] == ':')
                {
                    string flabel = trimmedStart.Substring(2, fc - 2);
                    if (flabel.Length > 0 && flabel.IndexOf(' ') < 0 && flabel.IndexOf('\t') < 0)
                    {
                        i = ParseFootnoteDef(lines, i, hi, ev, indent, flabel, fc + 2);
                        continue;
                    }
                }
            }

            // Link reference definition — collected in ScanRefDefs, dropped from block stream.
            if (indent <= 3 && trimmedStart.StartsWith("[") && TryParseRefDef(line, out _, out _, out _))
            {
                i++;
                continue;
            }

            // Indented code block (>= 4 spaces of indentation). Cannot interrupt a paragraph,
            // and we are at a block boundary here.
            if (indent >= 4 || (line.Length > 0 && line[0] == '\t'))
            {
                var codeBuf = new StringBuilder();
                int lastNonBlank = -1;
                var collected = new List<string>();
                while (i < hi)
                {
                    string cl = lines[i];
                    if (IsBlank(cl)) { collected.Add(""); i++; continue; }
                    string cts = cl.TrimStart(' ');
                    int cind = cl.Length - cts.Length;
                    if (cind >= 4) { collected.Add(cl.Substring(4)); lastNonBlank = collected.Count - 1; i++; }
                    else if (cl.Length > 0 && cl[0] == '\t') { collected.Add(cl.Substring(1)); lastNonBlank = collected.Count - 1; i++; }
                    else break;
                }
                for (int k = 0; k <= lastNonBlank; k++) { codeBuf.Append(collected[k]); codeBuf.Append('\n'); }
                ev.Add(new MdEvent { Kind = MdEventKind.StartCodeBlock, Text = "", Url = "" });
                if (codeBuf.Length > 0) ev.Add(MdEvent.WithText(MdEventKind.Code, codeBuf.ToString()));
                ev.Add(MdEvent.Simple(MdEventKind.EndCodeBlock));
                continue;
            }

            // Fenced code block
            if (indent <= 3 && (trimmedStart.StartsWith("```") || trimmedStart.StartsWith("~~~")))
            {
                char fenceChar = trimmedStart[0];
                int fenceLen = 0;
                while (fenceLen < trimmedStart.Length && trimmedStart[fenceLen] == fenceChar) fenceLen++;
                // pulldown-cmark exposes the full (trimmed) info string, not just the first word.
                string info = trimmedStart.Substring(fenceLen).Trim();
                ev.Add(new MdEvent { Kind = MdEventKind.StartCodeBlock, Text = info, Url = "" });
                var code = new StringBuilder();
                i++;
                bool closed = false;
                while (i < hi)
                {
                    string cl = lines[i];
                    string cls = cl.TrimStart();
                    if (cls.Length >= fenceLen && cls.Take(fenceLen).All(c => c == fenceChar) &&
                        cls.Substring(fenceLen).Trim().Length == 0)
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    code.Append(cl).Append('\n');
                    i++;
                }
                _ = closed;
                if (code.Length > 0) ev.Add(MdEvent.WithText(MdEventKind.Code, code.ToString()));
                ev.Add(MdEvent.Simple(MdEventKind.EndCodeBlock));
                continue;
            }

            // ATX heading
            if (indent <= 3 && trimmedStart.StartsWith("#"))
            {
                int h = 0;
                while (h < trimmedStart.Length && trimmedStart[h] == '#') h++;
                if (h >= 1 && h <= 6 && (h == trimmedStart.Length || trimmedStart[h] == ' ' || trimmedStart[h] == '\t'))
                {
                    string content = trimmedStart.Substring(h).Trim();
                    // Strip optional closing sequence of #'s.
                    content = StripAtxClosing(content);
                    ev.Add(new MdEvent { Kind = MdEventKind.StartHeading, Level = (byte)h, Text = "", Url = "" });
                    ParseInlines(content, ev);
                    ev.Add(MdEvent.Simple(MdEventKind.EndHeading));
                    i++;
                    continue;
                }
            }

            // Thematic break (skip: not represented as an element by the extractor)
            if (indent <= 3 && IsThematicBreak(trimmedStart))
            {
                i++;
                continue;
            }

            // Block-level raw HTML. pulldown-cmark emits it as an Html event with no enclosing
            // block open; the extractor records it as a raw block rather than dropping it.
            if (indent <= 3 && trimmedStart.StartsWith("<"))
            {
                int newI = TryHtmlBlock(lines, i, hi);
                if (newI > i)
                {
                    ev.Add(MdEvent.WithText(MdEventKind.Html, string.Join("\n", lines.GetRange(i, newI - i))));
                    i = newI; continue;
                }
            }

            // Blockquote
            if (indent <= 3 && trimmedStart.StartsWith(">"))
            {
                int j = i;
                var inner = new List<string>();
                while (j < hi)
                {
                    string bl = lines[j];
                    string bs = bl.TrimStart();
                    if (bs.StartsWith(">"))
                    {
                        string rest = bs.Substring(1);
                        if (rest.StartsWith(" ")) rest = rest.Substring(1);
                        inner.Add(rest);
                        j++;
                    }
                    else if (IsBlank(bl)) break;
                    else { inner.Add(bl); j++; } // lazy continuation
                }
                ev.Add(MdEvent.Simple(MdEventKind.StartBlockQuote));
                ParseBlocks(inner, 0, inner.Count, ev);
                ev.Add(MdEvent.Simple(MdEventKind.EndBlockQuote));
                i = j;
                continue;
            }

            // GFM table: current line has a pipe and next line is a matching delimiter row.
            if (indent <= 3 && line.Contains('|') && i + 1 < hi && IsTableStart(line, lines[i + 1]))
            {
                i = ParseTable(lines, i, hi, ev);
                continue;
            }

            // List
            if (TryListMarker(trimmedStart, out bool ordered, out int markerLen))
            {
                i = ParseList(lines, i, hi, ev, indent, ordered);
                continue;
            }

            // Paragraph: consecutive non-blank lines until a blank or block-starting line.
            var paraLines = new List<string>();
            while (i < hi && !IsBlank(lines[i]))
            {
                string pl = lines[i];
                string pts = pl.TrimStart();
                int pind = pl.Length - pts.Length;
                if (paraLines.Count > 0)
                {
                    // Paragraph interruption rules (simplified).
                    if (pind <= 3 && (pts.StartsWith("#") || pts.StartsWith("```") || pts.StartsWith("~~~")
                        || pts.StartsWith(">") || IsThematicBreak(pts)))
                        break;
                    if (pind <= 3 && pts.StartsWith("<") && TryHtmlBlock(lines, i, hi) > i)
                        break;
                    // A GFM table interrupts a paragraph when the current line is a header row
                    // immediately followed by a valid delimiter row.
                    if (pind <= 3 && pl.Contains('|') && i + 1 < hi && IsTableStart(pl, lines[i + 1]))
                        break;
                    // A list item can interrupt a paragraph (bullets always; ordered only when
                    // starting at 1), provided the item is non-empty.
                    if (pind <= 3 && TryListMarker(pts, out bool po, out int pml))
                    {
                        bool interrupts = pts.Substring(pml).Trim().Length > 0;
                        if (interrupts && po)
                        {
                            int d = 0; while (d < pts.Length && char.IsDigit(pts[d])) d++;
                            interrupts = d > 0 && int.TryParse(pts.Substring(0, d), out int num) && num == 1;
                        }
                        if (interrupts) break;
                    }
                }
                paraLines.Add(pl);
                i++;
            }
            string paraText = JoinInline(paraLines);
            ev.Add(MdEvent.Simple(MdEventKind.StartParagraph));
            ParseInlines(paraText, ev);
            ev.Add(MdEvent.Simple(MdEventKind.EndParagraph));
        }
    }

    private static string StripAtxClosing(string s)
    {
        int end = s.Length;
        while (end > 0 && s[end - 1] == '#') end--;
        if (end < s.Length && (end == 0 || s[end - 1] == ' ' || s[end - 1] == '\t'))
            return s.Substring(0, end).TrimEnd();
        return s;
    }

    private static bool IsThematicBreak(string s)
    {
        s = s.Replace(" ", "").Replace("\t", "");
        if (s.Length < 3) return false;
        char c = s[0];
        if (c != '*' && c != '-' && c != '_') return false;
        return s.All(ch => ch == c);
    }

    private static bool TryListMarker(string trimmedStart, out bool ordered, out int markerLen)
    {
        ordered = false; markerLen = 0;
        if (trimmedStart.Length == 0) return false;
        char c0 = trimmedStart[0];
        if (c0 == '-' || c0 == '*' || c0 == '+')
        {
            // A bare marker (no following content) is a valid empty list item in CommonMark.
            if (trimmedStart.Length == 1) { markerLen = 1; return true; }
            if (trimmedStart[1] == ' ' || trimmedStart[1] == '\t') { markerLen = 2; return true; }
            return false;
        }
        // ordered
        int k = 0;
        while (k < trimmedStart.Length && char.IsDigit(trimmedStart[k])) k++;
        if (k > 0 && k <= 9 && k < trimmedStart.Length && (trimmedStart[k] == '.' || trimmedStart[k] == ')'))
        {
            if (k + 1 == trimmedStart.Length) { ordered = true; markerLen = k + 1; return true; }
            if (trimmedStart[k + 1] == ' ' || trimmedStart[k + 1] == '\t') { ordered = true; markerLen = k + 2; return true; }
        }
        return false;
    }

    private static int ParseList(List<string> lines, int start, int hi, List<MdEvent> ev, int baseIndent, bool ordered)
    {
        ev.Add(new MdEvent { Kind = MdEventKind.StartList, Ordered = ordered, Text = "", Url = "" });
        int i = start;
        while (i < hi)
        {
            string line = lines[i];
            if (IsBlank(line)) { i++; continue; }
            string ts = line.TrimStart();
            int indent = line.Length - ts.Length;
            if (indent > baseIndent + 3 && i > start)
            {
                // deeply-indented continuation handled inside item collection
            }
            if (indent < baseIndent || !TryListMarker(ts, out bool o2, out int mlen) || o2 != ordered)
            {
                if (indent <= baseIndent) break;
                // Not a new marker at this level; stop.
                break;
            }

            // Collect this item's lines: marker line + subsequent lines more indented than content start.
            int contentIndent = indent + mlen;
            string first = ts.Substring(mlen);
            var itemLines = new List<string> { first };
            i++;
            while (i < hi)
            {
                string cl = lines[i];
                if (IsBlank(cl)) { itemLines.Add(""); i++; continue; }
                string cts = cl.TrimStart();
                int cind = cl.Length - cts.Length;
                if (cind >= contentIndent) { itemLines.Add(cl.Substring(Math.Min(contentIndent, cl.Length))); i++; continue; }
                if (TryListMarker(cts, out _, out _) && cind >= baseIndent) break; // next item
                if (cind <= baseIndent) break;
                itemLines.Add(cts); i++; // lazy
            }
            // Trim trailing blanks.
            while (itemLines.Count > 0 && itemLines[^1].Trim().Length == 0) itemLines.RemoveAt(itemLines.Count - 1);

            ev.Add(MdEvent.Simple(MdEventKind.StartItem));
            // Task list marker
            string itemJoined = string.Join("\n", itemLines);
            var taskMatch = System.Text.RegularExpressions.Regex.Match(itemJoined, @"^\[( |x|X)\]\s+");
            bool nestedOnly = false;
            _ = nestedOnly;
            if (HasBlockStructure(itemLines))
            {
                ParseBlocks(itemLines, 0, itemLines.Count, ev);
            }
            else
            {
                if (taskMatch.Success)
                {
                    ev.Add(new MdEvent { Kind = MdEventKind.TaskListMarker, Checked = taskMatch.Groups[1].Value != " ", Text = "", Url = "" });
                    itemJoined = itemJoined.Substring(taskMatch.Length);
                }
                string itemText = JoinInline(SplitLines(itemJoined)).Trim();
                ParseInlines(itemText, ev);
            }
            ev.Add(MdEvent.Simple(MdEventKind.EndItem));
        }
        ev.Add(MdEvent.Simple(MdEventKind.EndList));
        return i;
    }

    /// <summary>Parses a footnote definition (<c>[^label]: ...</c>) starting at
    /// <paramref name="start"/>. The first line's trailing text plus any subsequent lines indented
    /// by at least four columns form the definition body (parsed recursively as blocks). Emits
    /// <see cref="MdEventKind.StartFootnoteDefinition"/>/<see cref="MdEventKind.EndFootnoteDefinition"/>.</summary>
    private static int ParseFootnoteDef(List<string> lines, int start, int hi, List<MdEvent> ev,
        int baseIndent, string label, int firstContentCol)
    {
        const int contIndent = 4;
        string firstLine = lines[start].Substring(baseIndent);
        string rest = firstContentCol <= firstLine.Length
            ? firstLine.Substring(firstContentCol).TrimStart(' ', '\t')
            : "";
        var content = new List<string> { rest };
        int i = start + 1;
        while (i < hi)
        {
            string cl = lines[i];
            if (IsBlank(cl)) { content.Add(""); i++; continue; }
            if (AsciiIndentLen(cl) >= contIndent) { content.Add(cl.Substring(contIndent)); i++; continue; }
            break;
        }
        while (content.Count > 0 && content[^1].Trim().Length == 0) content.RemoveAt(content.Count - 1);

        ev.Add(new MdEvent { Kind = MdEventKind.StartFootnoteDefinition, Text = label, Url = "" });
        ParseBlocks(content, 0, content.Count, ev);
        ev.Add(MdEvent.Simple(MdEventKind.EndFootnoteDefinition));
        return i;
    }

    private static bool HasBlockStructure(List<string> itemLines)
    {
        // Detect sub-lists or multiple blocks (blank line separation) inside an item.
        bool sawBlank = false;
        for (int k = 0; k < itemLines.Count; k++)
        {
            string l = itemLines[k];
            if (l.Trim().Length == 0) { sawBlank = true; continue; }
            string ts = l.TrimStart();
            if (TryListMarker(ts, out _, out _) && k > 0) return true;
            if (ts.StartsWith("```") || ts.StartsWith("~~~") || ts.StartsWith(">")) return true;
            if (sawBlank) return true;
        }
        return false;
    }

    /// <summary>A GFM table starts only when the delimiter row is well-formed AND has the same
    /// number of cells as the header row (pulldown-cmark / GFM rule).</summary>
    private static bool IsTableStart(string header, string delimiter)
    {
        if (!header.Contains('|') && !delimiter.Contains('|')) return false;
        var hcells = SplitTableRow(header);
        var dcells = SplitTableRow(delimiter);
        if (dcells.Count != hcells.Count || dcells.Count == 0) return false;
        foreach (var d in dcells)
        {
            string s = d.Trim();
            if (s.Length == 0) return false;
            int k = 0;
            if (s[k] == ':') k++;
            int dashes = 0;
            while (k < s.Length && s[k] == '-') { k++; dashes++; }
            if (k < s.Length && s[k] == ':') k++;
            if (k != s.Length || dashes < 1) return false;
        }
        return true;
    }

    private static int ParseTable(List<string> lines, int start, int hi, List<MdEvent> ev)
    {
        ev.Add(MdEvent.Simple(MdEventKind.StartTable));

        // Header row
        ev.Add(MdEvent.Simple(MdEventKind.StartTableRow));
        foreach (var cell in SplitTableRow(lines[start]))
        {
            ev.Add(MdEvent.Simple(MdEventKind.StartTableCell));
            ParseInlines(cell, ev);
            ev.Add(MdEvent.Simple(MdEventKind.EndTableCell));
        }
        ev.Add(MdEvent.Simple(MdEventKind.EndTableRow));

        int i = start + 2; // skip delimiter row
        while (i < hi)
        {
            string line = lines[i];
            if (IsBlank(line) || !line.Contains('|')) break;
            ev.Add(MdEvent.Simple(MdEventKind.StartTableRow));
            foreach (var cell in SplitTableRow(line))
            {
                ev.Add(MdEvent.Simple(MdEventKind.StartTableCell));
                ParseInlines(cell, ev);
                ev.Add(MdEvent.Simple(MdEventKind.EndTableCell));
            }
            ev.Add(MdEvent.Simple(MdEventKind.EndTableRow));
            i++;
        }
        ev.Add(MdEvent.Simple(MdEventKind.EndTable));
        return i;
    }

    private static List<string> SplitTableRow(string line)
    {
        string s = line.Trim();
        if (s.StartsWith("|")) s = s.Substring(1);
        if (s.EndsWith("|") && !s.EndsWith("\\|")) s = s.Substring(0, s.Length - 1);
        var cells = new List<string>();
        var cur = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '|') { cur.Append('|'); i++; }
            else if (c == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    // ---- inline parsing (CommonMark delimiter-stack) --------------------

    private enum NType { Text, Opaque, Delim, Open, Close, SmartQuote }

    private sealed class Node
    {
        public NType T;
        public string S = "";          // Text
        public List<MdEvent>? Ev;      // Opaque (already-resolved sub-events)
        public char C;                 // Delim char
        public int Count;              // Delim remaining count
        public int OrigCount;          // Delim original count
        public bool CanOpen;
        public bool CanClose;
        public MdEventKind Mark;       // Open/Close marker
    }

    /// <summary>
    /// Render a run of <paramref name="count"/> hyphens as em/en dashes, matching
    /// pulldown-cmark: 2 is an en dash, 3 an em dash, and longer runs split into the
    /// em/en mix that divides the run evenly.
    /// </summary>
    private static string SmartDashes(int count)
    {
        if (count == 2) return "\u2013";
        if (count == 3) return "\u2014";
        int ems, ens;
        switch (count % 6)
        {
            case 0:
            case 3: ems = count / 3; ens = 0; break;
            case 2:
            case 4: ems = 0; ens = count / 2; break;
            case 1: ems = count / 3 - 1; ens = 2; break;
            default: ems = count / 3; ens = 1; break;
        }
        return new string('\u2014', ems) + new string('\u2013', ens);
    }

    /// <summary>
    /// Turn each recorded quote delimiter into its curly form. A single quote defaults to the
    /// closing form, and retroactively opens the last candidate when it can close; a double
    /// quote closes only when one is open. Mirrors pulldown-cmark's MaybeSmartQuote pass.
    /// </summary>
    private static void ResolveSmartQuotes(LinkedList<Node> nodes)
    {
        Node? singleQuoteOpen = null;
        bool doubleQuoteOpen = false;

        for (var n = nodes.First; n is not null; n = n.Next)
        {
            var node = n.Value;
            if (node.T != NType.SmartQuote) continue;

            if (node.C == '\'')
            {
                if (singleQuoteOpen is not null && node.CanClose)
                {
                    singleQuoteOpen.S = "\u2018";
                    singleQuoteOpen = null;
                }
                else if (node.CanOpen)
                {
                    singleQuoteOpen = node;
                }
                node.S = "\u2019";
            }
            else
            {
                if (node.CanClose && doubleQuoteOpen)
                {
                    doubleQuoteOpen = false;
                    node.S = "\u201d";
                }
                else
                {
                    if (node.CanOpen && !doubleQuoteOpen) doubleQuoteOpen = true;
                    node.S = "\u201c";
                }
            }
        }
    }

    private static void ParseInlines(string text, List<MdEvent> ev)
    {
        var nodes = Tokenize(text);
        ResolveSmartQuotes(nodes);
        ProcessEmphasis(nodes);
        foreach (var node in nodes)
        {
            switch (node.T)
            {
                case NType.Text: if (node.S.Length > 0) ev.Add(MdEvent.WithText(MdEventKind.Text, node.S)); break;
                case NType.Opaque: ev.AddRange(node.Ev!); break;
                case NType.Delim: if (node.Count > 0) ev.Add(MdEvent.WithText(MdEventKind.Text, new string(node.C, node.Count))); break;
                case NType.SmartQuote: ev.Add(MdEvent.WithText(MdEventKind.Text, node.S)); break;
                case NType.Open:
                case NType.Close: ev.Add(MdEvent.Simple(node.Mark)); break;
            }
        }
    }

    /// <summary>
    /// The offset of the delimiter closing a math span opened at <paramref name="from"/>, or -1.
    /// </summary>
    /// <remarks>
    /// Math cannot contain a bare <c>$</c>, so the first one found either closes the span or
    /// invalidates it. An inline span closes on a <c>$</c> not preceded by whitespace; a display
    /// span closes only on another <c>$$</c>, whatever precedes it.
    /// </remarks>
    private static int FindClosingMath(string text, int from, int run)
    {
        for (int j = from; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] != '$') continue;

            if (run == 2)
                return j + 1 < text.Length && text[j + 1] == '$' ? j : -1;

            return j > 0 && !IsAsciiWhitespace(text[j - 1]) ? j : -1;
        }
        return -1;
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static LinkedList<Node> Tokenize(string text)
    {
        var nodes = new LinkedList<Node>();
        var buf = new StringBuilder();
        void Flush() { if (buf.Length > 0) { nodes.AddLast(new Node { T = NType.Text, S = buf.ToString() }); buf.Clear(); } }

        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            char c = text[i];

            // Backslash escape.
            if (c == '\\' && i + 1 < n)
            {
                char nx = text[i + 1];
                if (IsAsciiPunct(nx)) { buf.Append(nx); i += 2; continue; }
                buf.Append('\\'); i++; continue;
            }

            // HTML entity.
            if (c == '&' && TryDecodeEntity(text, i, out string dec, out int eadv))
            {
                buf.Append(dec); i += eadv; continue;
            }

            // Inline code span.
            if (c == '`')
            {
                int ticks = 0;
                while (i + ticks < n && text[i + ticks] == '`') ticks++;
                int close = FindClosingTicks(text, i + ticks, ticks);
                if (close >= 0)
                {
                    Flush();
                    string code = text.Substring(i + ticks, close - (i + ticks)).Replace('\n', ' ');
                    code = StripCodeSpaces(code);
                    nodes.AddLast(new Node { T = NType.Opaque, Ev = new List<MdEvent> { MdEvent.WithText(MdEventKind.Code, code) } });
                    i = close + ticks;
                    continue;
                }
                buf.Append('`', ticks); i += ticks; continue;
            }

            // Math. A run of one `$` is inline, two is display; longer runs are not delimiters.
            // The opening delimiter must not be followed by whitespace, which is what keeps a
            // lone `$` in prose — a price, say — from opening a span.
            if (c == '$')
            {
                int run = i + 1 < n && text[i + 1] == '$' ? 2 : 1;
                bool canOpen = i + 1 < n && !IsAsciiWhitespace(text[i + 1]);
                if (canOpen)
                {
                    int mclose = FindClosingMath(text, i + run, run);
                    if (mclose >= 0)
                    {
                        Flush();
                        string body = text.Substring(i + run, mclose - (i + run));
                        var kind = run == 2 ? MdEventKind.DisplayMath : MdEventKind.InlineMath;
                        nodes.AddLast(new Node
                        {
                            T = NType.Opaque,
                            Ev = new List<MdEvent> { MdEvent.WithText(kind, body) },
                        });
                        i = mclose + run;
                        continue;
                    }
                }
                buf.Append('$', run); i += run; continue;
            }

            // Image.
            if (c == '!' && i + 1 < n && text[i + 1] == '[')
            {
                if (TryParseLink(text, i + 1, out int ls, out int le, out string url, out string? title, out int end))
                {
                    Flush();
                    var sub = new List<MdEvent> { new MdEvent { Kind = MdEventKind.StartImage, Url = url, LinkTitle = title, Text = "" } };
                    ParseInlines(text.Substring(ls, le - ls), sub);
                    sub.Add(MdEvent.Simple(MdEventKind.EndImage));
                    nodes.AddLast(new Node { T = NType.Opaque, Ev = sub });
                    i = end;
                    continue;
                }
            }

            // Link / footnote reference.
            if (c == '[')
            {
                if (i + 1 < n && text[i + 1] == '^')
                {
                    int fe = text.IndexOf(']', i + 2);
                    if (fe > i + 2)
                    {
                        string name = text.Substring(i + 2, fe - (i + 2));
                        if (!name.Contains(' '))
                        {
                            Flush();
                            nodes.AddLast(new Node { T = NType.Opaque, Ev = new List<MdEvent> { MdEvent.WithText(MdEventKind.FootnoteReference, name) } });
                            i = fe + 1;
                            continue;
                        }
                    }
                }
                if (TryParseLink(text, i, out int ls, out int le, out string url, out string? title, out int end))
                {
                    Flush();
                    var sub = new List<MdEvent> { new MdEvent { Kind = MdEventKind.StartLink, Url = url, LinkTitle = title, Text = "" } };
                    ParseInlines(text.Substring(ls, le - ls), sub);
                    sub.Add(MdEvent.Simple(MdEventKind.EndLink));
                    nodes.AddLast(new Node { T = NType.Opaque, Ev = sub });
                    i = end;
                    continue;
                }
            }

            // Autolink / inline HTML.
            if (c == '<')
            {
                int gt = text.IndexOf('>', i + 1);
                if (gt > i + 1)
                {
                    string inner = text.Substring(i + 1, gt - (i + 1));
                    if (Scanners.Scheme(inner) is not null && !inner.Contains(' '))
                    {
                        Flush();
                        nodes.AddLast(new Node
                        {
                            T = NType.Opaque,
                            Ev = new List<MdEvent>
                            {
                                new MdEvent { Kind = MdEventKind.StartLink, Url = inner, LinkTitle = null, Text = "" },
                                MdEvent.WithText(MdEventKind.Text, inner),
                                MdEvent.Simple(MdEventKind.EndLink),
                            },
                        });
                        i = gt + 1; continue;
                    }
                    if (IsEmailAutolink(inner))
                    {
                        Flush();
                        nodes.AddLast(new Node
                        {
                            T = NType.Opaque,
                            Ev = new List<MdEvent>
                            {
                                new MdEvent { Kind = MdEventKind.StartLink, Url = "mailto:" + inner, LinkTitle = null, Text = "" },
                                MdEvent.WithText(MdEventKind.Text, inner),
                                MdEvent.Simple(MdEventKind.EndLink),
                            },
                        });
                        i = gt + 1; continue;
                    }
                }
                int htmlLen = ScanInlineHtml(text, i);
                if (htmlLen > 0) { Flush(); i += htmlLen; continue; } // inline HTML dropped
                buf.Append('<'); i++; continue;
            }

            // Smart punctuation (pulldown-cmark ENABLE_SMART_PUNCTUATION): "..." becomes an
            // ellipsis and a run of two or more hyphens becomes em/en dashes.
            if (c == '.' && i + 2 < n && text[i + 1] == '.' && text[i + 2] == '.')
            {
                buf.Append('\u2026'); i += 3; continue;
            }
            if (c == '-')
            {
                int dashes = 0;
                while (i + dashes < n && text[i + dashes] == '-') dashes++;
                if (dashes >= 2)
                {
                    buf.Append(SmartDashes(dashes));
                    i += dashes; continue;
                }
            }
            if (c == '\'' || c == '"')
            {
                char qBefore = i > 0 ? text[i - 1] : '\n';
                char qAfter = (i + 1) < n ? text[i + 1] : '\n';
                bool atStart = i == 0, atEnd = (i + 1) >= n;

                // delim_run_can_open / delim_run_can_close for a single-character quote run.
                bool canOpen;
                if (atEnd || IsFlWhite(qAfter)) canOpen = false;
                else if (atStart) canOpen = true;
                else canOpen = IsFlWhite(qBefore)
                    || (IsFlPunct(qBefore) && (c != '\'' || (qBefore != ']' && qBefore != ')')));

                bool canClose;
                if (atStart || IsFlWhite(qBefore)) canClose = false;
                else if (atEnd) canClose = true;
                else canClose = IsFlWhite(qAfter) || IsFlPunct(qAfter);

                Flush();
                nodes.AddLast(new Node { T = NType.SmartQuote, C = c, CanOpen = canOpen, CanClose = canClose });
                i++; continue;
            }

            // Emphasis / strong / strikethrough delimiter run.
            if (c == '*' || c == '_' || c == '~')
            {
                int run = 0;
                while (i + run < n && text[i + run] == c) run++;
                if (c == '~' && run > 2) { buf.Append(c, run); i += run; continue; }

                char before = i > 0 ? text[i - 1] : '\n';
                char after = (i + run) < n ? text[i + run] : '\n';
                bool bWhite = IsFlWhite(before), aWhite = IsFlWhite(after);
                bool bPunct = !bWhite && IsFlPunct(before);
                bool aPunct = !aWhite && IsFlPunct(after);
                bool left = !aWhite && (!aPunct || bWhite || bPunct);
                bool right = !bWhite && (!bPunct || aWhite || aPunct);
                bool canOpen, canClose;
                if (c == '_') { canOpen = left && (!right || bPunct); canClose = right && (!left || aPunct); }
                else { canOpen = left; canClose = right; }

                Flush();
                nodes.AddLast(new Node { T = NType.Delim, C = c, Count = run, OrigCount = run, CanOpen = canOpen, CanClose = canClose });
                i += run; continue;
            }

            buf.Append(c); i++;
        }
        Flush();
        return nodes;
    }

    private static void ProcessEmphasis(LinkedList<Node> nodes)
    {
        var openersBottom = new Dictionary<string, LinkedListNode<Node>?>();

        static LinkedListNode<Node>? FindDelim(LinkedListNode<Node>? from)
        {
            for (var x = from; x != null; x = x.Next) if (x.Value.T == NType.Delim) return x;
            return null;
        }

        var closer = FindDelim(nodes.First);
        while (closer != null)
        {
            var cd = closer.Value;
            if (!(cd.T == NType.Delim && cd.CanClose)) { closer = FindDelim(closer.Next); continue; }
            char ch = cd.C;
            string key = ch.ToString() + (cd.CanOpen ? "1" : "0") + (cd.OrigCount % 3);
            LinkedListNode<Node>? bottom = openersBottom.TryGetValue(key, out var bb) ? bb : null;

            var opener = closer.Previous;
            bool found = false;
            while (opener != null && opener != bottom)
            {
                var od = opener.Value;
                if (od.T == NType.Delim && od.CanOpen && od.C == ch)
                {
                    bool odd = false;
                    if (ch != '~' && (od.CanClose || cd.CanOpen)
                        && (cd.OrigCount + od.OrigCount) % 3 == 0
                        && !(od.OrigCount % 3 == 0 && cd.OrigCount % 3 == 0))
                        odd = true;
                    if (!odd) { found = true; break; }
                }
                opener = opener.Previous;
            }

            if (found)
            {
                var od = opener!.Value;
                int use = ch == '~' ? Math.Min(Math.Min(od.Count, cd.Count), 2) : ((od.Count >= 2 && cd.Count >= 2) ? 2 : 1);
                MdEventKind ok, ck;
                if (ch == '~') { ok = MdEventKind.StartStrikethrough; ck = MdEventKind.EndStrikethrough; }
                else if (use == 2) { ok = MdEventKind.StartStrong; ck = MdEventKind.EndStrong; }
                else { ok = MdEventKind.StartEmphasis; ck = MdEventKind.EndEmphasis; }

                for (var m = opener.Next; m != null && m != closer; m = m.Next)
                {
                    if (m.Value.T == NType.Delim) { var mv = m.Value; mv.S = new string(mv.C, mv.Count); mv.T = NType.Text; }
                }

                od.Count -= use; cd.Count -= use;
                nodes.AddAfter(opener, new Node { T = NType.Open, Mark = ok });
                nodes.AddBefore(closer, new Node { T = NType.Close, Mark = ck });
                if (od.Count == 0) nodes.Remove(opener);
                if (cd.Count == 0) { var nx = closer.Next; nodes.Remove(closer); closer = FindDelim(nx); }
            }
            else
            {
                openersBottom[key] = closer.Previous;
                closer = FindDelim(closer.Next);
            }
        }
    }

    private static int FindClosingTicks(string text, int from, int ticks)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int run = 0;
                while (i + run < text.Length && text[i + run] == '`') run++;
                if (run == ticks) return i;
                i += run;
            }
            else i++;
        }
        return -1;
    }

    private static string StripCodeSpaces(string s)
    {
        if (s.Length >= 2 && s[0] == ' ' && s[^1] == ' ')
        {
            bool allSpace = true;
            foreach (var c in s) if (c != ' ') { allSpace = false; break; }
            if (!allSpace) return s.Substring(1, s.Length - 2);
        }
        return s;
    }

    private static string JoinInline(List<string> lines)
    {
        var parts = new List<string>(lines.Count);
        for (int k = 0; k < lines.Count; k++)
        {
            string s = lines[k].Trim();
            if (k < lines.Count - 1)
            {
                int bs = 0, p = s.Length;
                while (p > 0 && s[p - 1] == '\\') { bs++; p--; }
                if ((bs & 1) == 1) s = s.Substring(0, s.Length - 1);
            }
            parts.Add(s);
        }
        return string.Join(" ", parts);
    }

    private static bool IsFlWhite(char c) => c == '\n' || char.IsWhiteSpace(c);
    private static bool IsFlPunct(char c) => IsAsciiPunct(c) || char.IsPunctuation(c) || char.IsSymbol(c);
    private static bool IsAsciiAlpha(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsEmailAutolink(string s)
    {
        int at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        if (s.Contains(' ')) return false;
        foreach (char c in s.Substring(0, at))
            if (!(IsAsciiAlpha(c) || char.IsDigit(c) || ".!#$%&'*+/=?^_`{|}~-".IndexOf(c) >= 0)) return false;
        string domain = s.Substring(at + 1);
        if (domain.Length == 0 || domain[0] == '.' || domain[^1] == '.') return false;
        foreach (char c in domain)
            if (!(IsAsciiAlpha(c) || char.IsDigit(c) || c == '.' || c == '-')) return false;
        return domain.Contains('.');
    }

    private static bool TryDecodeEntity(string t, int i, out string dec, out int adv)
    {
        dec = ""; adv = 0;
        int semi = t.IndexOf(';', i + 1);
        if (semi < 0 || semi == i + 1 || semi - i > 32) return false;
        string body = t.Substring(i + 1, semi - (i + 1));
        if (body.Length == 0) return false;
        if (body[0] == '#')
        {
            int code;
            try
            {
                code = (body.Length > 1 && (body[1] == 'x' || body[1] == 'X'))
                    ? Convert.ToInt32(body.Substring(2), 16)
                    : int.Parse(body.Substring(1));
            }
            catch { return false; }
            if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) code = 0xFFFD;
            try { dec = char.ConvertFromUtf32(code); } catch { dec = "�"; }
            adv = semi - i + 1;
            return true;
        }
        if (NamedEntities.TryGetValue(body, out var v)) { dec = v; adv = semi - i + 1; return true; }
        return false;
    }

    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["AMP"] = "&", ["lt"] = "<", ["LT"] = "<", ["gt"] = ">", ["GT"] = ">",
        ["quot"] = "\"", ["QUOT"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["copy"] = "©", ["COPY"] = "©", ["reg"] = "®", ["REG"] = "®",
        ["trade"] = "™", ["TRADE"] = "™", ["hellip"] = "…",
        ["mdash"] = "—", ["ndash"] = "–", ["lsquo"] = "‘", ["rsquo"] = "’",
        ["ldquo"] = "“", ["rdquo"] = "”", ["laquo"] = "«", ["raquo"] = "»",
        ["deg"] = "°", ["plusmn"] = "±", ["times"] = "×", ["divide"] = "÷",
        ["frac12"] = "½", ["frac14"] = "¼", ["frac34"] = "¾",
        ["sup2"] = "²", ["sup3"] = "³", ["micro"] = "µ", ["para"] = "¶",
        ["middot"] = "·", ["sect"] = "§", ["bull"] = "•", ["dagger"] = "†",
        ["Dagger"] = "‡", ["euro"] = "€", ["pound"] = "£", ["cent"] = "¢",
        ["yen"] = "¥", ["curren"] = "¤", ["iexcl"] = "¡", ["iquest"] = "¿",
        ["hearts"] = "♥", ["diams"] = "♦", ["clubs"] = "♣", ["spades"] = "♠",
        ["larr"] = "←", ["uarr"] = "↑", ["rarr"] = "→", ["darr"] = "↓",
        ["harr"] = "↔", ["hArr"] = "⇔", ["rArr"] = "⇒", ["lArr"] = "⇐",
        ["ge"] = "≥", ["le"] = "≤", ["ne"] = "≠", ["equiv"] = "≡",
        ["infin"] = "∞", ["radic"] = "√", ["sum"] = "∑", ["prod"] = "∏",
        ["part"] = "∂", ["nabla"] = "∇", ["int"] = "∫", ["asymp"] = "≈",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["pi"] = "π", ["sigma"] = "σ", ["omega"] = "ω", ["mu"] = "μ",
        ["lambda"] = "λ", ["theta"] = "θ", ["phi"] = "φ",
        ["emsp"] = " ", ["ensp"] = " ", ["thinsp"] = " ", ["shy"] = "­",
        ["star"] = "☆", ["check"] = "✓", ["cross"] = "✗", ["prime"] = "′",
        ["Prime"] = "″", ["oline"] = "‾", ["frasl"] = "⁄",
    };

    // ---- inline HTML scanner (returns consumed length or 0) --------------

    private static int ScanInlineHtml(string t, int i)
    {
        int n = t.Length;
        if (i >= n || t[i] != '<' || i + 1 >= n) return 0;
        char c1 = t[i + 1];

        if (c1 == '!')
        {
            if (i + 3 < n && t[i + 2] == '-' && t[i + 3] == '-')
            {
                int end = t.IndexOf("-->", i + 4, StringComparison.Ordinal);
                return end >= 0 ? end + 3 - i : 0;
            }
            if (i + 8 < n && t.Substring(i + 2, 7) == "[CDATA[")
            {
                int end = t.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                return end >= 0 ? end + 3 - i : 0;
            }
            if (i + 2 < n && IsAsciiAlpha(t[i + 2]))
            {
                int gt = t.IndexOf('>', i + 2);
                return gt >= 0 ? gt + 1 - i : 0;
            }
            return 0;
        }
        if (c1 == '?')
        {
            int end = t.IndexOf("?>", i + 2, StringComparison.Ordinal);
            return end >= 0 ? end + 2 - i : 0;
        }

        int p = i + 1;
        bool closing = false;
        if (p < n && t[p] == '/') { closing = true; p++; }
        if (p >= n || !IsAsciiAlpha(t[p])) return 0;
        p++;
        while (p < n && (IsAsciiAlpha(t[p]) || char.IsDigit(t[p]) || t[p] == '-')) p++;

        if (closing)
        {
            while (p < n && IsFlWhite(t[p])) p++;
            return (p < n && t[p] == '>') ? p + 1 - i : 0;
        }

        while (true)
        {
            int ws = p;
            while (p < n && IsFlWhite(t[p])) p++;
            if (p < n && t[p] == '>') return p + 1 - i;
            if (p + 1 < n && t[p] == '/' && t[p + 1] == '>') return p + 2 - i;
            if (p == ws) return 0; // attributes must be preceded by whitespace
            if (p >= n || !(IsAsciiAlpha(t[p]) || t[p] == '_' || t[p] == ':')) return 0;
            p++;
            while (p < n && (IsAsciiAlpha(t[p]) || char.IsDigit(t[p]) || t[p] == '_' || t[p] == ':' || t[p] == '.' || t[p] == '-')) p++;
            int save = p;
            while (p < n && IsFlWhite(t[p])) p++;
            if (p < n && t[p] == '=')
            {
                p++;
                while (p < n && IsFlWhite(t[p])) p++;
                if (p < n && (t[p] == '"' || t[p] == '\''))
                {
                    char q = t[p]; p++;
                    while (p < n && t[p] != q) p++;
                    if (p >= n) return 0;
                    p++;
                }
                else
                {
                    int vs = p;
                    while (p < n && !IsFlWhite(t[p]) && t[p] != '>' && t[p] != '"' && t[p] != '\'' && t[p] != '=' && t[p] != '<' && t[p] != '`') p++;
                    if (p == vs) return 0;
                }
            }
            else p = save;
        }
    }

    // ---- block-level HTML (consumed & dropped) ---------------------------

    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption",
        "center", "col", "colgroup", "dd", "details", "dialog", "dir", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset",
        "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "iframe",
        "legend", "li", "link", "main", "menu", "menuitem", "nav", "noframes", "ol",
        "optgroup", "option", "p", "param", "section", "summary", "table", "tbody", "td",
        "tfoot", "th", "thead", "title", "tr", "track", "ul",
    };

    private static readonly HashSet<string> HtmlType1Tags = new(StringComparer.OrdinalIgnoreCase)
    { "script", "pre", "style", "textarea" };

    /// <summary>Detects and consumes an HTML block starting at <paramref name="i"/>.
    /// Returns the index of the first line after the block, or <paramref name="i"/> if not an HTML block.</summary>
    private static int TryHtmlBlock(List<string> lines, int i, int hi)
    {
        string line = lines[i];
        string ts = line.TrimStart();
        int indent = line.Length - ts.Length;
        if (indent > 3 || ts.Length == 0 || ts[0] != '<') return i;

        // type 2: comment
        if (ts.StartsWith("<!--", StringComparison.Ordinal))
        {
            int j = i; while (j < hi) { if (lines[j].Contains("-->")) return j + 1; j++; }
            return hi;
        }
        // type 3: processing instruction
        if (ts.StartsWith("<?", StringComparison.Ordinal))
        {
            int j = i; while (j < hi) { if (lines[j].Contains("?>")) return j + 1; j++; }
            return hi;
        }
        // type 5: CDATA
        if (ts.StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            int j = i; while (j < hi) { if (lines[j].Contains("]]>")) return j + 1; j++; }
            return hi;
        }
        // type 4: declaration
        if (ts.Length > 2 && ts[1] == '!' && IsAsciiAlpha(ts[2]))
        {
            int j = i; while (j < hi) { if (lines[j].Contains(">")) return j + 1; j++; }
            return hi;
        }

        // Extract tag name.
        int p = 1;
        bool closing = false;
        if (ts.Length > 1 && ts[1] == '/') { closing = true; p = 2; }
        int st = p;
        while (p < ts.Length && (IsAsciiAlpha(ts[p]) || char.IsDigit(ts[p]))) p++;
        if (p == st) return i;
        string name = ts.Substring(st, p - st);
        char nextCh = p < ts.Length ? ts[p] : '\n';

        // type 1: script/pre/style/textarea
        if (!closing && HtmlType1Tags.Contains(name) && (p >= ts.Length || IsFlWhite(nextCh) || nextCh == '>'))
        {
            string closeTag = "</" + name;
            int j = i;
            while (j < hi)
            {
                if (lines[j].IndexOf(closeTag, StringComparison.OrdinalIgnoreCase) >= 0) return j + 1;
                j++;
            }
            return hi;
        }

        // type 6: known block-level tag → ends at blank line
        bool type6Tail = p >= ts.Length || IsFlWhite(nextCh) || nextCh == '>' || ts.Substring(p).StartsWith("/>", StringComparison.Ordinal);
        if (HtmlBlockTags.Contains(name) && type6Tail)
        {
            int j = i;
            while (j < hi && !IsBlank(lines[j])) j++;
            return j;
        }

        // type 7: a complete tag occupying the whole line → ends at blank line
        int htmlLen = ScanInlineHtml(ts, 0);
        if (htmlLen > 0 && ts.Substring(htmlLen).Trim().Length == 0)
        {
            int j = i;
            while (j < hi && !IsBlank(lines[j])) j++;
            return j;
        }

        return i;
    }

    private static bool TryParseLink(string text, int bracket, out int labelStart, out int labelEnd,
        out string url, out string? title, out int end)
    {
        labelStart = labelEnd = end = 0; url = ""; title = null;
        // text[bracket] == '['
        int depth = 0;
        int i = bracket;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') depth++;
            else if (c == ']') { depth--; if (depth == 0) break; }
        }
        if (i >= text.Length || text[i] != ']') return false;
        labelStart = bracket + 1;
        labelEnd = i;
        int p = i + 1;
        if (p >= text.Length || text[p] != '(')
        {
            // Reference-style link: [label][ref], [label][], or shortcut [label].
            if (_refs is null || _refs.Count == 0) return false;
            string labelText = text.Substring(labelStart, labelEnd - labelStart);
            string refKey;
            int refEnd;
            if (p < text.Length && text[p] == '[')
            {
                int q = p + 1;
                var rb = new StringBuilder();
                while (q < text.Length && text[q] != ']')
                {
                    if (text[q] == '\\' && q + 1 < text.Length) { rb.Append(text[q]); rb.Append(text[q + 1]); q += 2; continue; }
                    rb.Append(text[q]); q++;
                }
                if (q >= text.Length) return false;
                string refText = rb.ToString();
                refKey = NormalizeRefLabel(refText.Length == 0 ? labelText : refText);
                refEnd = q + 1;
            }
            else
            {
                refKey = NormalizeRefLabel(labelText);
                refEnd = labelEnd + 1;
            }
            if (_refs.TryGetValue(refKey, out var def))
            {
                url = def.Url;
                title = def.Title;
                end = refEnd;
                return true;
            }
            return false;
        }
        p++;
        var sb = new StringBuilder();
        // URL (optionally <...>)
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (p < text.Length && text[p] == '<')
        {
            p++;
            while (p < text.Length && text[p] != '>') { sb.Append(text[p]); p++; }
            if (p < text.Length) p++;
        }
        else
        {
            int paren = 0;
            while (p < text.Length && !char.IsWhiteSpace(text[p]) && !(text[p] == ')' && paren == 0))
            {
                if (text[p] == '(') paren++;
                else if (text[p] == ')') paren--;
                sb.Append(text[p]); p++;
            }
        }
        url = sb.ToString();
        // Optional title
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (p < text.Length && (text[p] == '"' || text[p] == '\''))
        {
            char q = text[p]; p++;
            var t = new StringBuilder();
            while (p < text.Length && text[p] != q) { t.Append(text[p]); p++; }
            if (p < text.Length) p++;
            title = t.ToString();
        }
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (p >= text.Length || text[p] != ')') return false;
        end = p + 1;
        return true;
    }

    private static bool IsAsciiPunct(char c) =>
        c is '!' or '"' or '#' or '$' or '%' or '&' or '\'' or '(' or ')' or '*' or '+' or ','
        or '-' or '.' or '/' or ':' or ';' or '<' or '=' or '>' or '?' or '@' or '[' or '\\'
        or ']' or '^' or '_' or '`' or '{' or '|' or '}' or '~';
}
