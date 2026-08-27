using System.Text;

namespace Xberg.Internal.Html;

/// <summary>
/// Port of `html-to-markdown-rs`'s `converter/plain_text.rs`.
/// </summary>
/// <remarks>
/// When xberg asks for <c>OutputFormat.Plain</c> the crate still runs the markdown walk — that is
/// what fills the document structure — but replaces the returned text with this walker's output
/// (`converter/main.rs`: <c>let output = if is_plain_text { extract_plain_text(..) } else { .. }</c>).
/// The text only reaches the document when the structure came back empty, which is the one case
/// where the extractor falls back to "the whole conversion as a single paragraph".
/// </remarks>
internal static class HtmlPlainText
{
    /// <summary>Tags whose content is skipped entirely.</summary>
    private static readonly HashSet<string> SkipTags = new(StringComparer.Ordinal)
    {
        "script", "style", "head", "template", "noscript", "svg", "math",
    };

    /// <summary>Block-level tags separated by blank lines.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "section", "article",
        "aside", "main", "nav", "header", "footer", "figure", "figcaption", "details",
        "summary", "address", "hgroup", "search",
    };

    /// <summary>Which list, if any, an <c>&lt;li&gt;</c> takes its marker from.</summary>
    private enum ListKind { None, Unordered, Ordered }

    private sealed class ListContext
    {
        public ListKind Kind;
        public int NextIndex = 1;
    }

    public static string Extract(HNode root)
    {
        var buf = new StringBuilder(1024);
        var listCtx = new ListContext { Kind = ListKind.None };
        foreach (var child in root.Children) Walk(child, buf, false, listCtx);
        return NormalizePlainOutput(buf.ToString());
    }

    private static void Walk(HNode node, StringBuilder buf, bool inPre, ListContext listCtx)
    {
        if (node.IsComment) return;

        if (node.Tag is null)
        {
            string decoded = HtmlWalker.DecodeEntitiesFull(node.Text, node.CanonicalAttrs);
            if (inPre) { buf.Append(decoded); return; }
            string normalized = NormalizeWhitespace(decoded);
            if (normalized.Length == 0) return;
            if (normalized == " " && EndsWith(buf, '\n')) return;
            buf.Append(normalized);
            return;
        }

        string tag = node.Tag.ToLowerInvariant();
        if (SkipTags.Contains(tag)) return;
        if (HtmlToMarkdown.ShouldDropForPreprocessing(
                tag, node.Attr("role"), node.Attr("aria-label"), node.Attr("class"), node.Attr("id")))
            return;

        switch (tag)
        {
            case "br":
                buf.Append('\n');
                break;
            case "hr":
                EnsureBlankLine(buf);
                break;
            case "pre":
                EnsureBlankLine(buf);
                WalkChildren(node, buf, true, listCtx);
                EnsureBlankLine(buf);
                break;
            case "img":
            {
                string? alt = node.Attr("alt");
                if (!string.IsNullOrEmpty(alt)) buf.Append(alt);
                break;
            }
            case "table":
                EnsureBlankLine(buf);
                WalkTable(node, buf);
                EnsureBlankLine(buf);
                break;
            case "ul":
                EnsureNewline(buf);
                WalkChildren(node, buf, false, new ListContext { Kind = ListKind.Unordered });
                EnsureNewline(buf);
                break;
            case "ol":
            {
                int start = int.TryParse(node.Attr("start"), out int s) ? s : 1;
                EnsureNewline(buf);
                WalkChildren(node, buf, false, new ListContext { Kind = ListKind.Ordered, NextIndex = start });
                EnsureNewline(buf);
                break;
            }
            case "li":
                EnsureNewline(buf);
                // An <li> outside any list still gets a bullet — upstream's ListContext::None arm.
                if (listCtx.Kind == ListKind.Ordered) buf.Append(listCtx.NextIndex++).Append(". ");
                else buf.Append("- ");
                WalkChildren(node, buf, false, listCtx);
                EnsureNewline(buf);
                break;
            default:
                if (BlockTags.Contains(tag))
                {
                    EnsureBlankLine(buf);
                    WalkChildren(node, buf, inPre, listCtx);
                    EnsureBlankLine(buf);
                }
                else
                {
                    WalkChildren(node, buf, inPre, listCtx);
                }
                break;
        }
    }

    private static void WalkChildren(HNode node, StringBuilder buf, bool inPre, ListContext listCtx)
    {
        foreach (var child in node.Children) Walk(child, buf, inPre, listCtx);
    }

    /// <summary>Cells are tab-separated, rows newline-separated.</summary>
    private static void WalkTable(HNode table, StringBuilder buf)
    {
        var rows = new List<HNode>();
        CollectDescendants(table, "tr", rows);

        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) buf.Append('\n');
            bool firstCell = true;
            foreach (var cell in rows[r].Children)
            {
                if (cell.Tag is null) continue;
                if (!cell.Tag.Equals("th", StringComparison.OrdinalIgnoreCase)
                    && !cell.Tag.Equals("td", StringComparison.OrdinalIgnoreCase)) continue;
                if (!firstCell) buf.Append('\t');
                firstCell = false;
                var cellBuf = new StringBuilder();
                WalkChildren(cell, cellBuf, false, new ListContext { Kind = ListKind.None });
                buf.Append(cellBuf.ToString().Trim());
            }
        }
    }

    /// <summary>Descendants named <paramref name="target"/>, not descending into a match.</summary>
    private static void CollectDescendants(HNode node, string target, List<HNode> result)
    {
        foreach (var child in node.Children)
        {
            if (child.Tag is null) continue;
            if (child.Tag.Equals(target, StringComparison.OrdinalIgnoreCase)) result.Add(child);
            else CollectDescendants(child, target, result);
        }
    }

    private static void EnsureBlankLine(StringBuilder buf)
    {
        if (buf.Length == 0) return;
        while (buf.Length > 0 && (buf[^1] == ' ' || buf[^1] == '\t')) buf.Length--;
        int newlines = 0;
        while (newlines < buf.Length && buf[buf.Length - 1 - newlines] == '\n') newlines++;
        for (int i = newlines; i < 2; i++) buf.Append('\n');
    }

    private static void EnsureNewline(StringBuilder buf)
    {
        if (buf.Length == 0) return;
        if (buf[^1] != '\n') buf.Append('\n');
    }

    private static bool EndsWith(StringBuilder buf, char c) => buf.Length > 0 && buf[^1] == c;

    /// <summary>
    /// `text::normalize_whitespace`: runs of spaces, tabs and Unicode spaces collapse to one
    /// ASCII space; newlines are left alone.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool prevWasSpace = false;
        foreach (char ch in text)
        {
            bool isSpace = ch is ' ' or '\t' || IsUnicodeSpace(ch);
            if (isSpace)
            {
                if (!prevWasSpace) { sb.Append(' '); prevWasSpace = true; }
            }
            else { sb.Append(ch); prevWasSpace = false; }
        }
        return sb.ToString();
    }

    /// <summary>`text::is_unicode_space` — the crate's own list, not <c>char.IsWhiteSpace</c>.</summary>
    private static bool IsUnicodeSpace(char ch) => ch is ' ' or ' '
        or (>= ' ' and <= ' ') or ' ' or ' ' or '　';

    /// <summary>
    /// Trim each line's end, fold runs of blank lines to one, and end with a single newline.
    /// </summary>
    private static string NormalizePlainOutput(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool lastWasBlank = false;
        foreach (var raw in EnumerateLines(input))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (!lastWasBlank) { sb.Append('\n'); lastWasBlank = true; }
            }
            else { sb.Append(line).Append('\n'); lastWasBlank = false; }
        }
        while (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        if (sb.Length > 0) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Rust's <c>str::lines()</c>: split on \n, drop a trailing \r, no trailing empty.</summary>
    private static IEnumerable<string> EnumerateLines(string s)
    {
        int start = 0;
        while (start < s.Length)
        {
            int nl = s.IndexOf('\n', start);
            if (nl < 0) { yield return s[start..]; yield break; }
            int end = nl > start && s[nl - 1] == '\r' ? nl - 1 : nl;
            yield return s[start..end];
            start = nl + 1;
        }
    }
}
