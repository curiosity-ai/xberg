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
    public static List<MdEvent> Parse(string text)
    {
        var lines = SplitLines(text);
        var ev = new List<MdEvent>();
        ParseBlocks(lines, 0, lines.Count, ev);
        return ev;
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

    private static void ParseBlocks(List<string> lines, int lo, int hi, List<MdEvent> ev)
    {
        int i = lo;
        while (i < hi)
        {
            string line = lines[i];
            if (IsBlank(line)) { i++; continue; }

            string trimmedStart = line.TrimStart();
            int indent = line.Length - trimmedStart.Length;

            // Fenced code block
            if (indent <= 3 && (trimmedStart.StartsWith("```") || trimmedStart.StartsWith("~~~")))
            {
                char fenceChar = trimmedStart[0];
                int fenceLen = 0;
                while (fenceLen < trimmedStart.Length && trimmedStart[fenceLen] == fenceChar) fenceLen++;
                string info = trimmedStart.Substring(fenceLen).Trim();
                string lang = info.Length == 0 ? "" : info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                ev.Add(new MdEvent { Kind = MdEventKind.StartCodeBlock, Text = lang, Url = "" });
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

            // GFM table: current line has a pipe and next line is a delimiter row.
            if (line.Contains('|') && i + 1 < hi && IsTableDelimiter(lines[i + 1]))
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
                    if (line.Contains('|') && i + 1 < hi && false) { }
                }
                paraLines.Add(pl);
                i++;
            }
            string paraText = string.Join(" ", paraLines.Select(l => l.Trim()));
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
            if (trimmedStart.Length >= 2 && (trimmedStart[1] == ' ' || trimmedStart[1] == '\t'))
            {
                markerLen = 2;
                return true;
            }
            return false;
        }
        // ordered
        int k = 0;
        while (k < trimmedStart.Length && char.IsDigit(trimmedStart[k])) k++;
        if (k > 0 && k <= 9 && k < trimmedStart.Length && (trimmedStart[k] == '.' || trimmedStart[k] == ')')
            && k + 1 < trimmedStart.Length && (trimmedStart[k + 1] == ' ' || trimmedStart[k + 1] == '\t'))
        {
            ordered = true;
            markerLen = k + 2;
            return true;
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
                string itemText = string.Join(" ", SplitLines(itemJoined).Select(l => l.Trim())).Trim();
                ParseInlines(itemText, ev);
            }
            ev.Add(MdEvent.Simple(MdEventKind.EndItem));
        }
        ev.Add(MdEvent.Simple(MdEventKind.EndList));
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

    private static bool IsTableDelimiter(string line)
    {
        string s = line.Trim();
        if (s.Length == 0) return false;
        // Must consist of |, -, :, spaces and contain at least one -
        bool hasDash = false;
        foreach (char c in s)
        {
            if (c == '-') hasDash = true;
            else if (c != '|' && c != ':' && c != ' ' && c != '\t') return false;
        }
        return hasDash;
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

    // ---- inline parsing --------------------------------------------------

    private static void ParseInlines(string text, List<MdEvent> ev)
    {
        int i = 0;
        var pending = new StringBuilder();

        void Flush()
        {
            if (pending.Length > 0) { ev.Add(MdEvent.WithText(MdEventKind.Text, pending.ToString())); pending.Clear(); }
        }

        while (i < text.Length)
        {
            char c = text[i];

            // Escapes
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunct(text[i + 1]))
            {
                pending.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // Inline code span
            if (c == '`')
            {
                int ticks = 0;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                int close = FindClosingTicks(text, i + ticks, ticks);
                if (close >= 0)
                {
                    Flush();
                    string code = text.Substring(i + ticks, close - (i + ticks));
                    code = code.Trim(' ');
                    ev.Add(MdEvent.WithText(MdEventKind.Code, code));
                    i = close + ticks;
                    continue;
                }
            }

            // Image
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryParseLink(text, i + 1, out int labelStart, out int labelEnd, out string url, out string? title, out int end))
                {
                    Flush();
                    ev.Add(new MdEvent { Kind = MdEventKind.StartImage, Url = url, LinkTitle = title, Text = "" });
                    ParseInlines(text.Substring(labelStart, labelEnd - labelStart), ev);
                    ev.Add(MdEvent.Simple(MdEventKind.EndImage));
                    i = end;
                    continue;
                }
            }

            // Link
            if (c == '[')
            {
                // Footnote reference [^name]
                if (i + 1 < text.Length && text[i + 1] == '^')
                {
                    int fe = text.IndexOf(']', i + 2);
                    if (fe > i + 2)
                    {
                        string name = text.Substring(i + 2, fe - (i + 2));
                        if (!name.Contains(' '))
                        {
                            Flush();
                            ev.Add(MdEvent.WithText(MdEventKind.FootnoteReference, name));
                            i = fe + 1;
                            continue;
                        }
                    }
                }
                if (TryParseLink(text, i, out int ls, out int le, out string url, out string? title, out int end))
                {
                    Flush();
                    ev.Add(new MdEvent { Kind = MdEventKind.StartLink, Url = url, LinkTitle = title, Text = "" });
                    ParseInlines(text.Substring(ls, le - ls), ev);
                    ev.Add(MdEvent.Simple(MdEventKind.EndLink));
                    i = end;
                    continue;
                }
            }

            // Autolink <url>
            if (c == '<')
            {
                int gt = text.IndexOf('>', i + 1);
                if (gt > i + 1)
                {
                    string inner = text.Substring(i + 1, gt - (i + 1));
                    if (Scanners.Scheme(inner) is not null && !inner.Contains(' '))
                    {
                        Flush();
                        ev.Add(new MdEvent { Kind = MdEventKind.StartLink, Url = inner, LinkTitle = null, Text = "" });
                        ev.Add(MdEvent.WithText(MdEventKind.Text, inner));
                        ev.Add(MdEvent.Simple(MdEventKind.EndLink));
                        i = gt + 1;
                        continue;
                    }
                }
            }

            // Strong / emphasis / strikethrough
            if (c == '*' || c == '_' || c == '~')
            {
                int run = 0;
                while (i + run < text.Length && text[i + run] == c) run++;

                if (c == '~' && run >= 2)
                {
                    int close = FindDelimiter(text, i + 2, "~~");
                    if (close >= 0)
                    {
                        Flush();
                        ev.Add(MdEvent.Simple(MdEventKind.StartStrikethrough));
                        ParseInlines(text.Substring(i + 2, close - (i + 2)), ev);
                        ev.Add(MdEvent.Simple(MdEventKind.EndStrikethrough));
                        i = close + 2;
                        continue;
                    }
                }
                if (c == '*' || c == '_')
                {
                    if (run >= 2)
                    {
                        string delim = new string(c, 2);
                        int close = FindDelimiter(text, i + 2, delim);
                        if (close >= 0)
                        {
                            Flush();
                            ev.Add(MdEvent.Simple(MdEventKind.StartStrong));
                            ParseInlines(text.Substring(i + 2, close - (i + 2)), ev);
                            ev.Add(MdEvent.Simple(MdEventKind.EndStrong));
                            i = close + 2;
                            continue;
                        }
                    }
                    {
                        int close = FindDelimiter(text, i + 1, c.ToString());
                        if (close >= 0 && close > i + 1)
                        {
                            Flush();
                            ev.Add(MdEvent.Simple(MdEventKind.StartEmphasis));
                            ParseInlines(text.Substring(i + 1, close - (i + 1)), ev);
                            ev.Add(MdEvent.Simple(MdEventKind.EndEmphasis));
                            i = close + 1;
                            continue;
                        }
                    }
                }
            }

            pending.Append(c);
            i++;
        }
        Flush();
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

    private static int FindDelimiter(string text, int from, string delim)
    {
        int i = from;
        while (i <= text.Length - delim.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == '`')
            {
                // skip code spans
                int ticks = 0; while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                int close = FindClosingTicks(text, i + ticks, ticks);
                if (close >= 0) { i = close + ticks; continue; }
            }
            if (string.CompareOrdinal(text, i, delim, 0, delim.Length) == 0)
            {
                // For single-char delimiter, ensure not part of a longer run mismatch.
                return i;
            }
            i++;
        }
        return -1;
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
        if (p >= text.Length || text[p] != '(') return false;
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
