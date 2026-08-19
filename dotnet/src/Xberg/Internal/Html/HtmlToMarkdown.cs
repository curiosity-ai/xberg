using System.Text;

namespace Xberg.Internal.Html;

/// <summary>
/// Port of the `html-to-markdown-rs` crate's Tier-2 markdown conversion pipeline
/// (converter/main.rs + block/inline handlers), restricted to xberg's default options:
/// ATX headings, `*` emphasis, bullets "-*+", 2-space list indent, no escaping,
/// backtick code fences, inline links, Angle url style, padded tables,
/// extract_metadata=true (YAML frontmatter), Standard preprocessing preset.
/// The output is stored as `InternalDocument.PreRenderedContent` (after the xberg-side
/// `normalize_html_markdown` pass) so the pipeline returns it verbatim for markdown output.
/// </summary>
internal static class HtmlToMarkdown
{
    // ── public entry ─────────────────────────────────────────────────────────
    public static string Convert(string html)
    {
        html = html.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\0", "");
        var root = HtmlDom.Parse(html);

        var sb = new StringBuilder();
        string front = ExtractFrontmatter(root);
        sb.Append(front);

        var ctx = new Ctx();
        foreach (var child in root.Children)
            WalkNode(child, sb, ctx);

        string outp = sb.ToString();
        outp = TrimLineEndWhitespace(outp);
        outp = CollapseExcessBlankLines(outp);
        return outp;
    }

    // ── context (mirrors converter::Context) ────────────────────────────────
    internal sealed record Ctx
    {
        public bool ConvertAsInline { get; init; }
        public bool InTableCell { get; init; }
        public bool InListItem { get; init; }
        public bool InList { get; init; }
        public bool InOrderedList { get; init; }
        public int ListCounter { get; init; }
        public int ListDepth { get; init; }
        public int UlDepth { get; init; }
        public bool LooseList { get; init; }
        public bool PrevItemHadBlocks { get; init; }
        public bool InHeading { get; init; }
        public bool InCode { get; init; }
        public bool InParagraph { get; init; }
        public int BlockContentStart { get; init; }
        public int BlockquoteDepth { get; init; }
        public int InlineDepth { get; init; }
        public bool InStrong { get; init; }
        public bool InLink { get; init; }
        public bool MeasureWidthOnly { get; init; }
        // When set, every <table> handled contributes a 2D cell grid to this sink (mirrors the
        // crate's structure collector `push_table_data`): nested tables are emitted before their
        // parent, in the order the two cell-walks (render + collect) encounter them.
        public Action<List<List<string>>>? TableEmit { get; init; }
        // When set, every <img> handled reports its (alt, src) to this sink (mirrors the crate's
        // structure collector `push_image`), so images inside table cells become image nodes.
        public Action<string?, string>? ImageEmit { get; init; }
    }

    // ── table grid emission (structure collector) ────────────────────────────
    /// <summary>
    /// Walk a parsed &lt;table&gt; node and emit a 2D cell grid for it and every nested table,
    /// mirroring the html-to-markdown crate's document-structure collector. Each grid is passed
    /// to <paramref name="emit"/>; nested tables are emitted before their enclosing table.
    /// </summary>
    public static void EmitTableTree(HNode table, Action<List<List<string>>> emit,
        Action<string?, string>? imageEmit = null)
    {
        var ctx = new Ctx { TableEmit = emit, ImageEmit = imageEmit };
        var dummy = new StringBuilder();
        HandleTableWithContext(table, dummy, ctx);
    }

    /// <summary>
    /// collect_table_grid: the table's cells on a 2D grid, each rendered under an in-cell context.
    /// <para>
    /// Column positions are resolved by <see cref="Tables.GridFlatten"/> rather than by advancing
    /// through each row independently. Advancing per row ignores the columns a rowspan started
    /// earlier still covers, which slides every cell beneath one leftwards and out from under its
    /// header.
    /// </para>
    /// </summary>
    private static List<List<string>> CollectGrid(HNode table, Ctx ctx)
    {
        var rows = new List<IReadOnlyList<GridCellSpan>>();
        foreach (var row in TableRows(table))
        {
            var cells = new List<GridCellSpan>();
            foreach (var cell in CollectTableCells(row))
            {
                var (colspan, rowspan) = GetColspanRowspan(cell);
                cells.Add(new GridCellSpan(RenderCellForGrid(cell, ctx), colspan, rowspan));
            }
            rows.Add(cells);
        }

        return Tables.GridFlatten.FlattenSpannedRows(rows, c => c.ColSpan, c => c.RowSpan, c => c.Content);
    }

    private readonly record struct GridCellSpan(string Content, int ColSpan, int RowSpan);

    // Cell content for the grid: walk children under in-cell context, normalize (keep newlines),
    // trim. Mirrors collect_grid_row's `normalize_whitespace(walk_node(children)).trim()`.
    private static string RenderCellForGrid(HNode cell, Ctx ctx)
    {
        var buf = new StringBuilder();
        var cctx = ctx with { InTableCell = true };
        foreach (var c in cell.Children) WalkNode(c, buf, cctx);
        return NormalizeWhitespaceKeepNewlines(buf.ToString()).Trim();
    }

    // ── frontmatter (head_metadata.rs + main_helpers::extract_head_metadata) ─
    private static string ExtractFrontmatter(HNode root)
    {
        var head = FindHead(root);
        if (head is null) return "";
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in head.Children)
        {
            if (child.Tag is null) continue;
            switch (child.Tag)
            {
                case "meta":
                {
                    string? name = child.Attr("name");
                    string? property = child.Attr("property");
                    string? content = child.Attr("content");
                    if (content is not null)
                    {
                        if (name is not null) metadata[$"meta-{name}"] = content;
                        if (property is not null) metadata[$"meta-{property}"] = content;
                    }
                    break;
                }
                case "title":
                {
                    var t = new StringBuilder();
                    foreach (var tc in child.Children)
                        if (tc.Tag is null && !tc.IsComment) t.Append(tc.Text);
                    string title = t.ToString().Trim();
                    if (title.Length > 0) metadata["title"] = title;
                    break;
                }
                case "link":
                {
                    string? rel = child.Attr("rel");
                    string? href = child.Attr("href");
                    if (rel is not null && href is not null && rel.Contains("canonical", StringComparison.Ordinal))
                        metadata["canonical"] = href;
                    break;
                }
                case "base":
                {
                    string? href = child.Attr("href");
                    if (href is not null) metadata["base"] = href;
                    break;
                }
            }
        }
        if (metadata.Count == 0) return "";
        var sb = new StringBuilder("---\n");
        foreach (var (k, v) in metadata) sb.Append(k).Append(": ").Append(v).Append('\n');
        sb.Append("---\n");
        return sb.ToString();
    }

    private static HNode? FindHead(HNode node)
    {
        // Depth-first, document order, first non-empty <head> preferred but we
        // take the first head that yields metadata (mirrors extract_head_metadata's
        // "keep searching for a later one" only when empty; simplified: first head).
        if (node.Tag == "head") return node;
        foreach (var c in node.Children)
        {
            if (c.Tag is null) continue;
            var r = FindHead(c);
            if (r is not null) return r;
        }
        return null;
    }

    // ── main walker (converter/main.rs walk_node) ────────────────────────────
    internal static void WalkNode(HNode node, StringBuilder output, Ctx ctx)
    {
        if (node.IsComment) return;
        if (node.Tag is null)
        {
            ProcessTextNode(node, output, ctx);
            return;
        }

        string tag = node.Tag;

        // should_drop_for_preprocessing (Standard preset, remove_navigation + remove_forms)
        if (ShouldDrop(tag, node))
        {
            TrimTrailingWhitespace(output);
            return;
        }

        // hidden attribute stripping (strip_hidden_elements runs pre-parse in Rust)
        if (node.Attr("hidden") is not null) return;

        switch (tag)
        {
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                HandleHeading(tag, node, output, ctx);
                break;
            case "p":
                HandleParagraph(node, output, ctx);
                break;
            case "strong": case "b":
                HandleStrong(node, output, ctx);
                break;
            case "em": case "i":
                HandleEmphasis(node, output, ctx);
                break;
            case "code":
                HandleInlineCode(node, output, ctx);
                break;
            case "kbd": case "samp":
                HandleKbdSamp(node, output, ctx);
                break;
            case "del": case "s": case "strike":
                HandleStrikethrough(node, output, ctx);
                break;
            case "mark":     // HighlightStyle::DoubleEqual (default) → ==…==
            case "ins":      // inserted text → ==…== (marks.rs handle_inserted)
                HandleHighlight(node, output, ctx);
                break;
            case "u": case "small": case "abbr": case "bdi": case "bdo":
            case "rb": case "rtc":
                WalkChildren(node, output, ctx);
                break;
            case "sub": case "sup":
                HandleSubSup(node, output, ctx);
                break;
            case "var": case "dfn": case "cite":
                HandleEmphasis(node, output, ctx);
                break;
            case "q":
                HandleQ(node, output, ctx);
                break;
            case "span":
                HandleSpan(node, output, ctx);
                break;
            case "a":
                HandleLink(node, output, ctx);
                break;
            case "img":
                HandleImg(node, output, ctx);
                break;
            case "br":
                HandleBr(output, ctx);
                break;
            case "hr":
                HandleHr(node, output, ctx);
                break;
            case "div":
                HandleDiv(node, output, ctx);
                break;
            case "pre":
                HandlePre(node, output, ctx);
                break;
            case "blockquote":
                HandleBlockquote(node, output, ctx);
                break;
            case "table":
                // During an outer table's width-measurement pre-pass, don't recurse into the
                // nested table handler (which would emit its grid and launch its own pre-pass);
                // fall back to descendant text content, matching the crate (issue #406).
                if (ctx.MeasureWidthOnly) { output.Append(TextContent(node)); break; }
                HandleTableWithContext(node, output, ctx);
                break;
            case "caption":
                break; // handled inside table
            case "ul":
                HandleList(node, output, ctx, ordered: false);
                break;
            case "ol":
                HandleList(node, output, ctx, ordered: true);
                break;
            case "li":
                HandleLi(node, output, ctx);
                break;
            case "dl":
                HandleDl(node, output, ctx);
                break;
            case "dt":
                HandleDt(node, output, ctx);
                break;
            case "dd":
                HandleDd(node, output, ctx);
                break;
            case "article": case "section": case "aside": case "header":
            case "footer": case "main": case "nav":
                HandleSectioning(node, output, ctx);
                break;
            case "figure":
                HandleFigure(node, output, ctx);
                break;
            case "figcaption":
                HandleFigcaption(node, output, ctx);
                break;
            case "details": case "dialog":
                HandleDetails(node, output, ctx);
                break;
            case "summary":
                HandleSummary(node, output, ctx);
                break;
            case "time": case "data":
                WalkChildren(node, output, ctx);
                break;
            case "wbr": case "thead": case "tbody": case "tfoot": case "tr": case "th": case "td":
            case "source": case "track": case "param": case "col": case "colgroup":
                break; // no-op outside table context
            case "head": case "script": case "style": case "template": case "noscript":
            case "meta": case "link": case "base": case "title":
                break; // metadata / non-content
            case "html": case "body":
                WalkChildren(node, output, ctx);
                break;
            case "audio": case "video": case "picture": case "iframe": case "svg": case "math":
            case "object": case "embed": case "canvas": case "map": case "area":
                break; // media handlers: not ported (no output for defaults)
            case "form": case "fieldset": case "legend": case "label": case "input":
            case "textarea": case "select": case "option": case "optgroup": case "button":
            case "progress": case "meter": case "output": case "datalist":
                break; // forms removed by Standard preprocessing / no output
            default:
                HandleUnknown(node, output, ctx);
                break;
        }
    }

    private static void WalkChildren(HNode node, StringBuilder output, Ctx ctx)
    {
        foreach (var c in node.Children) WalkNode(c, output, ctx);
    }

    // ── preprocessing drop (preprocessing_helpers::should_drop_for_preprocessing) ─
    private static bool ShouldDrop(string tag, HNode node)
    {
        if (tag == "form") return true;
        if (tag == "nav") return true;
        if (tag is "header" or "footer" or "aside") return HasNavigationHint(node);
        return false;
    }

    private static readonly string[] NavKeywords =
    {
        "nav", "navigation", "navbar", "breadcrumbs", "breadcrumb", "toc", "sidebar",
        "sidenav", "menu", "menubar", "mainmenu", "subnav", "tabs", "tablist", "toolbar",
        "pager", "pagination", "skipnav", "skip-link", "skiplinks", "site-nav", "site-menu",
        "site-header", "site-footer", "topbar", "bottombar", "masthead", "vector-nav",
        "vector-header", "vector-footer",
    };

    private static bool HasNavigationHint(HNode node)
    {
        string? role = node.Attr("role");
        if (role is not null && role is "navigation" or "menubar" or "tablist" or "toolbar") return true;
        string? aria = node.Attr("aria-label");
        if (aria is not null)
        {
            string lower = aria.ToLowerInvariant();
            foreach (var kw in new[] { "navigation", "menu", "contents", "table of contents", "toc" })
                if (lower.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return AttrTokenMatches(node.Attr("class")) || AttrTokenMatches(node.Attr("id"));
    }

    private static bool AttrTokenMatches(string? value)
    {
        if (value is null) return false;
        foreach (var token in value.Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new StringBuilder(token.Length);
            foreach (char c in token) sb.Append(c is '_' or ':' or '.' or '/' ? '-' : char.ToLowerInvariant(c));
            if (Array.IndexOf(NavKeywords, sb.ToString()) >= 0) return true;
        }
        return false;
    }

    // ── text node (converter/text_node.rs, Normalized whitespace mode, no escaping) ─
    private static void ProcessTextNode(HNode node, StringBuilder output, Ctx ctx)
    {
        string text = HtmlWalker.DecodeEntities(node.Text);
        if (text.Length == 0) return;

        bool hadNewlines = text.Contains('\n');

        if (text.Trim().Length == 0)
        {
            if (ctx.InCode) { output.Append(text); return; }

            if (hadNewlines)
            {
                if (output.Length == 0) return;
                if (!EndsWith(output, "\n\n"))
                {
                    string? nextTag = NextSiblingTag(node);
                    if (nextTag is not null && IsInlineElement(nextTag))
                    {
                        if (!EndsWith(output, " ") && !EndsWith(output, "\n")) output.Append(' ');
                    }
                }
                return;
            }

            if (PrevSiblingIsInlineTag(node) && NextSiblingIsInlineTag(node))
            {
                if (HasMoreThanOneChar(text))
                {
                    if (!EndsWith(output, " ")) output.Append(' ');
                }
                else output.Append(text);
            }
            else output.Append(text);
            return;
        }

        string processed;
        if (ctx.InCode)
        {
            processed = text;
        }
        else if (ctx.InTableCell)
        {
            string normalized = NormalizeWhitespaceKeepNewlines(text);
            processed = EscapeCellText(normalized);
        }
        else
        {
            bool hasDoubleNewline = text.Contains("\n\n");
            bool hasTrailingSingleNewline = text.EndsWith('\n') && !text.EndsWith("\n\n");

            string normalized = NormalizeWhitespaceKeepNewlines(text);
            var (prefix, suffix, core) = Chomp(normalized);

            bool skipPrefix = EndsWith(output, "\n\n")
                || EndsWith(output, "* ")
                || EndsWith(output, "- ")
                || EndsWith(output, ". ")
                || EndsWith(output, "] ")
                || (EndsWith(output, "\n") && prefix == " ")
                || (EndsWith(output, " ") && prefix == " " && !PrevSiblingIsInlineTag(node));

            var final = new StringBuilder();
            if (!skipPrefix && prefix.Length > 0) final.Append(prefix);
            final.Append(core); // no escaping with default options
            if (suffix.Length > 0) final.Append(suffix);
            else if (hasTrailingSingleNewline)
            {
                int safeStart = Math.Min(ctx.BlockContentStart, output.Length);
                bool atParagraphBreak = EndsWithAt(output, safeStart, "\n\n");
                if (!atParagraphBreak)
                {
                    if (hasDoubleNewline) final.Append('\n');
                    else
                    {
                        string? nextTag = NextSiblingTag(node);
                        if (nextTag == "span") { }
                        else if (ctx.InlineDepth > 0 || ctx.ConvertAsInline || ctx.InParagraph) final.Append(' ');
                        else final.Append('\n');
                    }
                }
            }
            processed = final.ToString();
        }

        if (ctx.InListItem && processed.Contains("\n\n"))
        {
            string indent = new string(' ', 4 * ctx.ListDepth);
            bool first = true;
            foreach (var part in processed.Split("\n\n"))
            {
                if (!first) { output.Append("\n\n"); output.Append(indent); }
                first = false;
                output.Append(part.Trim());
            }
        }
        else output.Append(processed);
    }

    // did the current block (starting at blockStart) end with pattern?
    private static bool EndsWithAt(StringBuilder sb, int blockStart, string pattern)
    {
        int len = sb.Length - blockStart;
        if (len < pattern.Length) return false;
        return EndsWith(sb, pattern);
    }

    // ── heading (block/heading.rs) ────────────────────────────────────────────
    private static void HandleHeading(string tag, HNode node, StringBuilder output, Ctx ctx)
    {
        int level = tag[^1] - '0';
        if (level is < 1 or > 6) level = 1;

        bool needsLeadingSep = !ctx.InTableCell && !ctx.InListItem && !ctx.ConvertAsInline
            && ctx.BlockquoteDepth == 0 && output.Length > 0 && !EndsWith(output, "\n\n");
        if (needsLeadingSep)
        {
            TrimTrailingWhitespace(output);
            output.Append("\n\n");
        }

        var text = new StringBuilder();
        var hctx = ctx with { InHeading = true, ConvertAsInline = true };
        foreach (var c in node.Children) WalkNode(c, text, hctx);

        string trimmed = text.ToString().Trim();
        if (trimmed.Length == 0) return;
        string normalized = NormalizeHeadingText(trimmed);
        PushHeading(output, ctx, level, normalized);
    }

    private static string NormalizeHeadingText(string text)
    {
        if (!text.Contains('\n') && !text.Contains('\r')) return text;
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char ch in text)
        {
            if (ch is '\n' or '\r') { if (sb.Length > 0) pendingSpace = true; }
            else if ((ch == ' ' || ch == '\t') && pendingSpace) { }
            else
            {
                if (pendingSpace)
                {
                    if (sb.Length == 0 || sb[^1] != ' ') sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    internal static void PushHeading(StringBuilder output, Ctx ctx, int level, string text)
    {
        if (text.Length == 0) return;
        if (ctx.ConvertAsInline) { output.Append(text); return; }
        if (ctx.InTableCell)
        {
            bool isTableContinuation = output.Length > 0 && !EndsWith(output, "|") && !EndsWith(output, " ") && !EndsWith(output, "<br>");
            if (isTableContinuation) output.Append("<br>");
            output.Append(text);
            return;
        }
        if (ctx.InListItem)
        {
            if (EndsWith(output, "\n"))
            {
                int indentLevel = ctx.ListDepth > 0 ? 4 * ctx.ListDepth : 0;
                if (indentLevel > 0) output.Append(' ', indentLevel);
            }
            else if (!EndsWith(output, " ") && output.Length > 0) output.Append(' ');
        }
        else if (output.Length > 0 && !EndsWith(output, "\n\n"))
        {
            if (EndsWith(output, "\n")) output.Append('\n');
            else { TrimTrailingWhitespace(output); output.Append("\n\n"); }
        }

        string suffix = ctx.InListItem || ctx.BlockquoteDepth > 0 ? "\n" : "\n\n";
        output.Append('#', level).Append(' ').Append(text).Append(suffix);
    }

    // ── paragraph (block/paragraph.rs) ────────────────────────────────────────
    private static void HandleParagraph(HNode node, StringBuilder output, Ctx ctx)
    {
        int contentStart = output.Length;

        bool isTableContinuation = ctx.InTableCell && output.Length > 0 && !EndsWith(output, "|") && !EndsWith(output, "<br>");
        bool isListContinuation = ctx.InListItem && output.Length > 0
            && !EndsWith(output, "* ") && !EndsWith(output, "- ") && !EndsWith(output, ". ");
        bool afterCodeBlock = EndsWith(output, "```\n");
        bool needsLeadingSep = !ctx.InTableCell && !ctx.InListItem && !ctx.ConvertAsInline
            && ctx.BlockquoteDepth == 0 && output.Length > 0 && !EndsWith(output, "\n\n") && !afterCodeBlock;

        if (isTableContinuation)
        {
            TrimTrailingWhitespace(output);
            output.Append("<br>");
        }
        else if (isListContinuation)
        {
            if (!EndsWith(output, " ") && !EndsWith(output, "\n")) output.Append(' ');
            output.Append(' ', 4 * ctx.ListDepth);
        }
        else if (needsLeadingSep)
        {
            TrimTrailingWhitespace(output);
            output.Append("\n\n");
        }

        var pctx = ctx with { InParagraph = true, BlockContentStart = output.Length };

        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Tag is null && !child.IsComment && child.Text.Trim().Length == 0
                && i > 0 && i < children.Count - 1
                && IsEmptyInlineElement(children[i - 1]) && IsEmptyInlineElement(children[i + 1]))
                continue;
            WalkNode(child, output, pctx);
        }

        bool hasContent = output.Length > contentStart;
        if (hasContent && !ctx.ConvertAsInline && !ctx.InTableCell)
            output.Append("\n\n");
    }

    private static bool IsEmptyInlineElement(HNode n) =>
        n.Tag is "br" or "hr" or "img" or "input" or "meta" or "link";

    // ── div (block/div.rs) ───────────────────────────────────────────────────
    private static void HandleDiv(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }

        int contentStart = output.Length;

        bool isTableContinuation = ctx.InTableCell && output.Length > 0 && !EndsWith(output, "|") && !EndsWith(output, "<br>");
        bool isListContinuation = ctx.InListItem && output.Length > 0
            && !EndsWith(output, "* ") && !EndsWith(output, "- ") && !EndsWith(output, ". ");
        bool needsLeadingSep = !ctx.InTableCell && !ctx.InListItem && !ctx.ConvertAsInline
            && output.Length > 0 && !EndsWith(output, "\n\n");

        if (isTableContinuation)
        {
            TrimTrailingWhitespace(output);
            output.Append("  \n");   // NewlineStyle::Spaces, br_in_tables=false
        }
        else if (isListContinuation)
        {
            if (!EndsWith(output, "\n")) output.Append('\n');
            output.Append(' ', 2 * ctx.ListDepth);
        }
        else if (needsLeadingSep)
        {
            TrimTrailingWhitespace(output);
            output.Append("\n\n");
        }

        WalkChildren(node, output, ctx);

        bool hasContent = output.Length > contentStart;
        if (!hasContent) return;

        if (contentStart == 0 && StartsWith(output, "\n") && !StartsWith(output, "\n\n"))
            output.Remove(0, 1);
        TrimTrailingWhitespace(output);

        if (ctx.InTableCell) { }
        else if (ctx.InListItem)
        {
            if (isListContinuation)
            {
                if (!EndsWith(output, "\n")) output.Append('\n');
            }
            else if (!EndsWith(output, "\n\n"))
            {
                if (EndsWith(output, "\n")) output.Append('\n');
                else output.Append("\n\n");
            }
        }
        else if (!ctx.ConvertAsInline)
        {
            if (EndsWith(output, "\n\n")) { }
            else if (EndsWith(output, "\n")) output.Append('\n');
            else output.Append("\n\n");
        }
    }

    // ── inline: strong / em (inline/emphasis.rs) ─────────────────────────────
    private static void HandleStrong(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.InCode) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        var sctx = ctx with { InlineDepth = ctx.InlineDepth + 1, InStrong = true };
        foreach (var c in node.Children) WalkNode(c, content, sctx);

        var (prefix, suffix, trimmed) = ChompInline(content.ToString());
        if (content.ToString().Trim().Length > 0)
        {
            output.Append(prefix);
            if (ctx.InStrong) output.Append(trimmed);
            else output.Append("**").Append(trimmed).Append("**");
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (content.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
    }

    private static void HandleEmphasis(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.InCode) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        var ectx = ctx with { InlineDepth = ctx.InlineDepth + 1 };
        foreach (var c in node.Children) WalkNode(c, content, ectx);

        var (prefix, suffix, trimmed) = ChompInline(content.ToString());
        if (content.ToString().Trim().Length > 0)
        {
            output.Append(prefix).Append('*').Append(trimmed).Append('*');
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (content.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
        else
        {
            string? cls = node.Attr("class");
            if (cls is not null && cls.Contains("caret", StringComparison.Ordinal) && !EndsWith(output, " "))
                output.Append(" > ");
        }
    }

    private static void HandleStrikethrough(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.InCode) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        var (prefix, suffix, trimmed) = ChompInline(content.ToString());
        if (content.ToString().Trim().Length > 0)
        {
            output.Append(prefix).Append("~~").Append(trimmed).Append("~~");
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (content.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
    }

    // ins / mark → ==content== (marks.rs handle_inserted / handle_mark, DoubleEqual default)
    private static void HandleHighlight(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.InCode) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        var (prefix, suffix, trimmed) = ChompInline(content.ToString());
        if (content.ToString().Trim().Length > 0)
        {
            output.Append(prefix).Append("==").Append(trimmed).Append("==");
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (content.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
    }

    // sub/sup with empty symbols → passthrough with chomp (typography.rs)
    private static void HandleSubSup(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        if (ctx.InCode) { output.Append(content); return; }
        var (prefix, suffix, trimmed) = ChompInline(content.ToString());
        if (content.ToString().Trim().Length > 0)
        {
            output.Append(prefix).Append(trimmed);
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (content.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
    }

    private static void HandleQ(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length > 0) output.Append('"').Append(trimmed).Append('"');
    }

    private static void HandleSpan(HNode node, StringBuilder output, Ctx ctx)
    {
        // Whitespace normalization: pop a single trailing newline (typography.rs handle_span)
        if (!ctx.InCode && EndsWith(output, "\n") && !EndsWith(output, "\n\n"))
            output.Remove(output.Length - 1, 1);
        WalkChildren(node, output, ctx);
    }

    // ── inline code (inline/code.rs) ─────────────────────────────────────────
    private static void HandleInlineCode(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.InCode) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        var cctx = ctx with { InCode = true };
        foreach (var c in node.Children) WalkNode(c, content, cctx);
        string s = content.ToString();
        if (s.Trim().Length == 0) return;
        RenderCodeWithEscaping(s, output);
    }

    private static void HandleKbdSamp(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        var cctx = ctx.InCode ? ctx : ctx with { InCode = true };
        foreach (var c in node.Children) WalkNode(c, content, cctx);
        string normalized = NormalizeWhitespaceKeepNewlines(content.ToString());
        var (prefix, suffix, trimmed) = ChompInline(normalized);
        if (normalized.Trim().Length > 0)
        {
            output.Append(prefix).Append('`').Append(trimmed).Append('`');
            AppendInlineSuffix(output, suffix, trimmed.Length > 0, node);
        }
        else if (normalized.Length > 0)
        {
            output.Append(prefix);
            AppendInlineSuffix(output, suffix, false, node);
        }
    }

    private static void RenderCodeWithEscaping(string trimmed, StringBuilder output)
    {
        bool containsBacktick = trimmed.Contains('`');
        char? first = trimmed.Length > 0 ? trimmed[0] : null;
        char? last = trimmed.Length > 0 ? trimmed[^1] : null;
        bool allSpaces = trimmed.Length > 0 && trimmed.All(c => c == ' ');
        bool needsDelimiterSpaces = allSpaces
            || first == '`' || last == '`'
            || (first == ' ' && last == ' ' && containsBacktick);

        int numBackticks = 1;
        if (containsBacktick)
        {
            int max = 0, cur = 0;
            foreach (char c in trimmed)
            {
                if (c == '`') { cur++; if (cur > max) max = cur; }
                else cur = 0;
            }
            numBackticks = max == 1 ? 2 : 1;
        }

        output.Append('`', numBackticks);
        if (needsDelimiterSpaces) output.Append(' ');
        output.Append(trimmed);
        if (needsDelimiterSpaces) output.Append(' ');
        output.Append('`', numBackticks);
    }

    // ── link (handlers/link.rs) ──────────────────────────────────────────────
    private static void HandleLink(HNode node, StringBuilder output, Ctx ctx)
    {
        string? hrefRaw = node.Attr("href");
        string? title = node.Attr("title");

        if (hrefRaw is null)
        {
            WalkChildren(node, output, ctx);
            return;
        }

        string href = SanitizeMarkdownUrl(HtmlWalker.DecodeEntities(hrefRaw));

        if (ctx.InLink) { WalkChildren(node, output, ctx); return; }

        string rawText = NormalizeWhitespaceKeepNewlines(TextContent(node)).Trim();

        bool isAutolink = href.Length > 0 && HasUriScheme(href)
            && (rawText == href || (href.StartsWith("mailto:", StringComparison.Ordinal) && rawText == href[7..]));
        if (isAutolink)
        {
            output.Append('<');
            output.Append(href.StartsWith("mailto:", StringComparison.Ordinal) && rawText == href[7..] ? rawText : href);
            output.Append('>');
            return;
        }

        // single heading child → heading wrapping the link
        var single = FindSingleHeadingChild(node);
        if (single is not null)
        {
            var (level, heading) = single.Value;
            var htext = new StringBuilder();
            var hctx = ctx with { InHeading = true, ConvertAsInline = true };
            WalkNode(heading, htext, hctx);
            string trimmedHeading = htext.ToString().Trim();
            if (trimmedHeading.Length > 0)
            {
                var linkBuf = new StringBuilder();
                AppendMarkdownLink(linkBuf, EscapeLinkLabel(trimmedHeading), href, title);
                PushHeading(output, ctx, level, linkBuf.ToString());
                return;
            }
        }

        var (inlineLabel, sawBlock) = CollectLinkLabelText(node);
        string label;
        if (sawBlock)
        {
            var content = new StringBuilder();
            var lctx = ctx with { InlineDepth = ctx.InlineDepth + 1, ConvertAsInline = true, InLink = true };
            foreach (var child in node.Children)
            {
                var childBuf = new StringBuilder();
                WalkNode(child, childBuf, lctx);
                if (childBuf.ToString().Trim().Length > 0 && content.Length > 0
                    && !char.IsWhiteSpace(content[^1])
                    && childBuf.Length > 0 && !char.IsWhiteSpace(childBuf[0]))
                    content.Append(' ');
                content.Append(childBuf);
            }
            label = content.ToString().Trim().Length == 0
                ? NormalizeLinkLabel(inlineLabel)
                : NormalizeLinkLabel(content.ToString());
        }
        else
        {
            var content = new StringBuilder();
            var lctx = ctx with { InlineDepth = ctx.InlineDepth + 1, InLink = true };
            foreach (var child in node.Children) WalkNode(child, content, lctx);
            label = NormalizeLinkLabel(content.ToString());
        }

        if (label.Length == 0 && sawBlock)
            label = NormalizeLinkLabel(NormalizeWhitespaceKeepNewlines(TextContent(node)));
        if (label.Length == 0 && rawText.Length > 0)
            label = NormalizeLinkLabel(rawText);
        if (label.Length == 0 && href.Length > 0 && node.Children.Count > 0)
            label = href;

        if (label == "^" && href.StartsWith('#')) label = "↑";

        AppendMarkdownLink(output, EscapeLinkLabel(label), href, title);
    }

    private static void AppendMarkdownLink(StringBuilder output, string label, string href, string? title)
    {
        output.Append('[').Append(label).Append("](");
        if (href.Length == 0) output.Append("<>");
        else if (href.Contains(' ') || href.Contains('\n')) output.Append('<').Append(href).Append('>');
        else
        {
            int open = href.Count(c => c == '(');
            int close = href.Count(c => c == ')');
            if (open == close) output.Append(href);
            else output.Append(href.Replace("(", "\\(").Replace(")", "\\)"));
        }
        if (title is not null)
        {
            output.Append(" \"");
            output.Append(title.Contains('"') ? title.Replace("\"", "\\\"") : title);
            output.Append('"');
        }
        output.Append(')');
    }

    private static (int level, HNode node)? FindSingleHeadingChild(HNode node)
    {
        (int, HNode)? found = null;
        foreach (var child in node.Children)
        {
            if (child.IsComment) return null;
            if (child.Tag is null)
            {
                if (child.Text.Trim().Length > 0) return null;
                continue;
            }
            int level = child.Tag switch
            {
                "h1" => 1, "h2" => 2, "h3" => 3, "h4" => 4, "h5" => 5, "h6" => 6, _ => 0,
            };
            if (level == 0) return null;
            if (found is not null) return null;
            found = (level, child);
        }
        return found;
    }

    // collect_link_label_text: inline text of children skipping block descendants
    private static readonly HashSet<string> BlockLevelForLabel = new(StringComparer.Ordinal)
    {
        "p", "div", "blockquote", "pre", "table", "ul", "ol", "li", "dl", "dt", "dd",
        "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "aside", "header",
        "footer", "main", "nav", "figure", "figcaption", "hr",
    };

    private static (string text, bool sawBlock) CollectLinkLabelText(HNode node)
    {
        var text = new StringBuilder();
        bool sawBlock = false;
        var stack = new Stack<HNode>();
        for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsComment) continue;
            if (n.Tag is null) { text.Append(HtmlWalker.DecodeEntities(n.Text)); continue; }
            if (BlockLevelForLabel.Contains(n.Tag)) { sawBlock = true; continue; }
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
        return (text.ToString(), sawBlock);
    }

    private static string NormalizeLinkLabel(string label)
    {
        string collapsed = label.Replace('\n', ' ').Replace('\r', ' ');
        return NormalizeWhitespaceKeepNewlines(collapsed).Trim();
    }

    private static string EscapeLinkLabel(string text)
    {
        if (text.Length == 0) return "";
        var result = new StringBuilder(text.Length);
        int backslashCount = 0;
        int bracketDepth = 0;
        foreach (char ch in text)
        {
            if (ch == '\\') { result.Append('\\'); backslashCount++; continue; }
            bool isEscaped = backslashCount % 2 == 1;
            backslashCount = 0;
            if (ch == '[' && !isEscaped) { bracketDepth++; result.Append('['); }
            else if (ch == ']' && !isEscaped)
            {
                if (bracketDepth == 0) result.Append('\\');
                else bracketDepth--;
                result.Append(']');
            }
            else result.Append(ch);
        }
        return result.ToString();
    }

    internal static bool HasUriScheme(string href)
    {
        if (href.Length == 0 || !char.IsAsciiLetter(href[0])) return false;
        for (int i = 1; i < href.Length; i++)
        {
            char c = href[i];
            if (c == ':') return true;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return false;
        }
        return false;
    }

    private static string SanitizeMarkdownUrl(string url)
    {
        int mid = url.IndexOf("](", StringComparison.Ordinal);
        if (mid < 0) return url;
        if (!url[..mid].Contains('[')) return url;
        int parenStart = mid + 2;
        int relEnd = url.IndexOf(')', parenStart);
        if (relEnd < 0 || parenStart >= relEnd) return url;
        return url[parenStart..relEnd];
    }

    // ── image (handlers/image.rs) ────────────────────────────────────────────
    private static void HandleImg(HNode node, StringBuilder output, Ctx ctx)
    {
        string src = SanitizeMarkdownUrl(node.Attr("src") ?? "");
        string alt = node.Attr("alt") ?? "";
        string? title = node.Attr("title");

        // Structure-collector side effect: report every <img> so cell images become nodes
        // (the crate's push_image runs unconditionally, once per handler invocation).
        ctx.ImageEmit?.Invoke(alt.Length == 0 ? null : alt, src);

        bool shouldUseAltText = ctx.ConvertAsInline || ctx.InHeading;
        if (shouldUseAltText) { output.Append(alt); return; }

        output.Append("![").Append(alt).Append("](");
        if (src.Length == 0) output.Append("<>");
        else if (src.Contains(' ') || src.Contains('\n')) output.Append('<').Append(src).Append('>');
        else
        {
            int open = src.Count(c => c == '(');
            int close = src.Count(c => c == ')');
            if (open == close) output.Append(src);
            else output.Append(src.Replace("(", "\\(").Replace(")", "\\)"));
        }
        if (title is not null) output.Append(" \"").Append(title).Append('"');
        output.Append(')');
    }

    // ── br / hr ──────────────────────────────────────────────────────────────
    private static void HandleBr(StringBuilder output, Ctx ctx)
    {
        if (ctx.InHeading)
        {
            TrimTrailingWhitespace(output);
            output.Append("  ");
        }
        else if (output.Length == 0 || EndsWith(output, "\n")) output.Append('\n');
        else output.Append("  \n");
    }

    private static void HandleHr(HNode node, StringBuilder output, Ctx ctx)
    {
        if (output.Length > 0)
        {
            string? prevTag = PrevSiblingTag(node);
            string lastLine = LastNonEmptyLine(output);
            bool lastLineIsBlockquote = lastLine.TrimStart().StartsWith('>');
            bool needsBlankLine = !ctx.InParagraph && prevTag != "blockquote" && !lastLineIsBlockquote;

            if (prevTag == "blockquote" && EndsWith(output, "\n\n"))
                output.Remove(output.Length - 1, 1);
            else if (ctx.InParagraph || !needsBlankLine)
            {
                if (!EndsWith(output, "\n")) output.Append('\n');
            }
            else
            {
                TrimTrailingWhitespace(output);
                if (EndsWith(output, "\n"))
                {
                    if (!EndsWith(output, "\n\n")) output.Append('\n');
                }
                else output.Append("\n\n");
            }
        }
        output.Append("---\n");
    }

    private static string LastNonEmptyLine(StringBuilder sb)
    {
        string s = sb.ToString();
        int end = s.Length;
        while (end > 0)
        {
            int start = s.LastIndexOf('\n', end - 1);
            string line = s[(start + 1)..end];
            if (line.Trim().Length > 0) return line;
            if (start < 0) break;
            end = start;
        }
        return "";
    }

    // ── pre (block/preformatted.rs) ──────────────────────────────────────────
    private static void HandlePre(HNode node, StringBuilder output, Ctx ctx)
    {
        var cctx = ctx with { InCode = true };
        string? language = ExtractLanguageFromPre(node);

        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, cctx);
        string s = content.ToString();
        if (s.Length == 0) return;

        int leading = 0; while (leading < s.Length && s[leading] == '\n') leading++;
        int trailing = 0; while (trailing < s.Length && s[s.Length - 1 - trailing] == '\n') trailing++;
        string core = s.Trim('\n');
        bool whitespaceOnly = core.Trim().Length == 0;

        string coreText = leading > 0 ? DedentCodeBlock(core) : core;
        string processed;
        if (whitespaceOnly)
        {
            processed = new string('\n', leading) + coreText + new string('\n', trailing);
        }
        else
        {
            processed = coreText + new string('\n', trailing);
        }

        // Backticks style
        if (!ctx.ConvertAsInline && output.Length > 0 && !EndsWith(output, "\n\n"))
        {
            if (EndsWith(output, "\n")) output.Append('\n');
            else output.Append("\n\n");
        }
        output.Append("```");
        if (language is not null) output.Append(language);
        output.Append('\n');
        output.Append(processed.TrimEnd('\n'));
        output.Append('\n');
        output.Append("```").Append("\n\n");
    }

    private static string? ExtractLanguageFromPre(HNode node)
    {
        string? FromClass(string? cls)
        {
            if (cls is null) return null;
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (c.StartsWith("language-", StringComparison.Ordinal)) return c["language-".Length..];
                if (c.StartsWith("lang-", StringComparison.Ordinal)) return c["lang-".Length..];
            }
            return null;
        }
        var fromPre = FromClass(node.Attr("class"));
        if (fromPre is not null) return fromPre;
        foreach (var child in node.Children)
        {
            if (child.Tag == "code")
                return FromClass(child.Attr("class"));
        }
        return null;
    }

    private static string DedentCodeBlock(string content)
    {
        var lines = content.Split('\n');
        int minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            int ind = 0;
            while (ind < line.Length && char.IsWhiteSpace(line[ind])) ind++;
            if (ind < minIndent) minIndent = ind;
        }
        if (minIndent == int.MaxValue) minIndent = 0;
        var sb = new StringBuilder(content.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            int cut = 0, remaining = minIndent;
            while (remaining > 0 && cut < line.Length && char.IsWhiteSpace(line[cut])) { cut++; remaining--; }
            sb.Append(line[cut..]);
        }
        return sb.ToString();
    }

    // ── blockquote (handlers/blockquote.rs) ──────────────────────────────────
    private static void HandleBlockquote(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }

        var bctx = ctx with { BlockquoteDepth = ctx.BlockquoteDepth + 1 };
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, bctx);

        string trimmedContent = content.ToString().Trim();
        if (trimmedContent.Length == 0) return;

        if (ctx.BlockquoteDepth > 0) output.Append("\n\n\n");
        else if (output.Length > 0)
        {
            if (EndsWith(output, "\n\n")) output.Remove(output.Length - 1, 1);
            else if (!EndsWith(output, "\n")) output.Append("\n\n");
        }

        foreach (var line in trimmedContent.Split('\n'))
        {
            output.Append("> ").Append(line.Trim()).Append('\n');
        }
        output.Append('\n');
    }

    // ── lists (list/*.rs) ────────────────────────────────────────────────────
    private static void HandleList(HNode node, StringBuilder output, Ctx ctx, bool ordered)
    {
        AddListLeadingSeparator(output, ctx);

        int nestedDepth = ctx.InList && !ctx.InListItem ? ctx.ListDepth + 1 : ctx.ListDepth;
        bool isLoose = IsLooseList(node);

        int start = 1;
        if (ordered && int.TryParse(node.Attr("start"), out var s)) start = s;

        // process_list_children
        int counter = start;
        var listCtx = ctx with
        {
            InOrderedList = ordered,
            ListCounter = ordered ? counter : 0,
            InList = true,
            ListDepth = nestedDepth,
            UlDepth = ordered ? ctx.UlDepth : ctx.UlDepth + 1,
            LooseList = isLoose,
            PrevItemHadBlocks = false,
        };

        foreach (var child in node.Children)
        {
            if (child.Tag is null && !child.IsComment && child.Text.Trim().Length == 0) continue;
            if (ordered) listCtx = listCtx with { ListCounter = counter };
            WalkNode(child, output, listCtx);
            if (ordered && child.Tag == "li") counter++;
        }

        AddNestedListTrailingSeparator(output, ctx);
    }

    private static void AddListLeadingSeparator(StringBuilder output, Ctx ctx)
    {
        if (ctx.InTableCell)
        {
            bool isTableContinuation = output.Length > 0 && !EndsWith(output, "|") && !EndsWith(output, " ") && !EndsWith(output, "<br>");
            if (isTableContinuation) output.Append("<br>");
            return;
        }
        if (output.Length > 0 && !ctx.InList)
        {
            bool needsNewline = !EndsWith(output, "\n\n") && !EndsWith(output, "* ")
                && !EndsWith(output, "- ") && !EndsWith(output, ". ");
            if (needsNewline) output.Append("\n\n");
            return;
        }
        if (ctx.InListItem && output.Length > 0)
        {
            bool needsNewline = !EndsWith(output, "\n") && !EndsWith(output, "* ")
                && !EndsWith(output, "- ") && !EndsWith(output, ". ");
            if (needsNewline)
            {
                TrimTrailingWhitespace(output);
                output.Append('\n');
            }
        }
    }

    private static void AddNestedListTrailingSeparator(StringBuilder output, Ctx ctx)
    {
        if (!ctx.InListItem) return;
        if (ctx.LooseList)
        {
            if (!EndsWith(output, "\n\n"))
            {
                if (!EndsWith(output, "\n")) output.Append('\n');
                output.Append('\n');
            }
        }
        else if (!EndsWith(output, "\n")) output.Append('\n');
    }

    private static bool IsLooseList(HNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Tag != "li") continue;
            foreach (var liChild in child.Children)
                if (liChild.Tag == "p") return true;
        }
        return false;
    }

    private static void HandleLi(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ListDepth > 0)
            output.Append(' ', ctx.ListDepth * 2);

        bool hasBlockChildren = false;
        foreach (var child in node.Children)
        {
            if (child.Tag is "p" or "div" or "blockquote" or "pre" or "table" or "hr" or "dl")
            {
                hasBlockChildren = true;
                break;
            }
        }

        var liCtx = ctx with { InListItem = true, ListDepth = ctx.ListDepth + 1 };

        // task lists: find checkbox
        var checkbox = FindCheckbox(node);
        if (checkbox is not null)
        {
            output.Append("- ").Append(checkbox.Value.check ? "[x]" : "[ ]");
            var taskText = new StringBuilder();
            RenderLiContentSkippingCheckbox(node, taskText, liCtx, checkbox.Value.node);
            output.Append(' ');
            string trimmedTask = taskText.ToString().Trim();
            if (trimmedTask.Length > 0) output.Append(trimmedTask);
        }
        else
        {
            if (!ctx.InTableCell)
            {
                if (ctx.InOrderedList) output.Append(ctx.ListCounter).Append(". ");
                else
                {
                    const string bullets = "-*+";
                    int idx = ctx.UlDepth > 0 ? (ctx.UlDepth - 1) % bullets.Length : 0;
                    output.Append(bullets[idx]).Append(' ');
                }
            }

            foreach (var child in node.Children)
                WalkNode(child, output, liCtx);

            TrimTrailingWhitespace(output);
        }

        if (!ctx.InTableCell)
        {
            if (hasBlockChildren || ctx.LooseList || ctx.PrevItemHadBlocks)
            {
                if (!EndsWith(output, "\n\n"))
                {
                    if (EndsWith(output, "\n")) output.Append('\n');
                    else output.Append("\n\n");
                }
            }
            else if (!EndsWith(output, "\n")) output.Append('\n');
        }
    }

    private static (bool check, HNode node)? FindCheckbox(HNode node)
    {
        if (node.Tag == "input" && node.Attr("type") == "checkbox")
            return (node.Attr("checked") is not null, node);
        foreach (var c in node.Children)
        {
            if (c.Tag is null) continue;
            var r = FindCheckbox(c);
            if (r is not null) return r;
        }
        return null;
    }

    private static void RenderLiContentSkippingCheckbox(HNode node, StringBuilder output, Ctx ctx, HNode checkbox)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child, checkbox)) continue;
            if (ContainsNode(child, checkbox))
                RenderLiContentSkippingCheckbox(child, output, ctx, checkbox);
            else WalkNode(child, output, ctx);
        }
    }

    private static bool ContainsNode(HNode node, HNode target)
    {
        if (ReferenceEquals(node, target)) return true;
        foreach (var c in node.Children)
            if (ContainsNode(c, target)) return true;
        return false;
    }

    // ── definition lists (semantic/definition_list.rs) ───────────────────────
    private static void HandleDl(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length == 0) return;
        if (output.Length > 0 && !EndsWith(output, "\n\n")) output.Append("\n\n");
        output.Append(trimmed).Append("\n\n");
    }

    private static void HandleDt(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length == 0) return;
        output.Append(trimmed);
        if (!ctx.ConvertAsInline) output.Append('\n');
    }

    private static void HandleDd(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length == 0) return;
        output.Append(trimmed);
        if (!ctx.ConvertAsInline) output.Append("\n\n");
    }

    // ── sectioning / figure / details (semantic/*.rs) ────────────────────────
    private static void HandleSectioning(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string s = content.ToString();
        if (s.Trim().Length == 0) return;
        if (output.Length > 0 && !EndsWith(output, "\n\n")) output.Append("\n\n");
        output.Append(s);
        if (s.EndsWith('\n') && !s.EndsWith("\n\n")) output.Append('\n');
        else if (!s.EndsWith('\n')) output.Append("\n\n");
    }

    private static void HandleFigure(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }
        if (output.Length > 0 && !EndsWith(output, "\n\n")) output.Append("\n\n");
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string s = content.ToString().Replace("\n![", "![").Replace(" ![", "![");
        string trimmed = s.Trim(' ', '\t', '\n');
        if (trimmed.Length == 0) return;
        output.Append(trimmed);
        if (!EndsWith(output, "\n")) output.Append('\n');
        if (!EndsWith(output, "\n\n")) output.Append('\n');
    }

    private static void HandleFigcaption(HNode node, StringBuilder output, Ctx ctx)
    {
        var text = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, text, ctx);
        string trimmed = text.ToString().Trim();
        if (trimmed.Length == 0) return;
        if (output.Length > 0)
        {
            if (EndsWith(output, "```\n")) output.Append('\n');
            else
            {
                TrimTrailingWhitespace(output);
                if (EndsWith(output, "\n") && !EndsWith(output, "\n\n")) output.Append('\n');
                else if (!EndsWith(output, "\n")) output.Append("\n\n");
            }
        }
        output.Append('*').Append(trimmed).Append("*\n\n");
    }

    private static void HandleDetails(HNode node, StringBuilder output, Ctx ctx)
    {
        if (ctx.ConvertAsInline) { WalkChildren(node, output, ctx); return; }
        var content = new StringBuilder();
        foreach (var c in node.Children) WalkNode(c, content, ctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length == 0) return;
        if (output.Length > 0 && !EndsWith(output, "\n\n")) output.Append("\n\n");
        output.Append(trimmed).Append("\n\n");
    }

    private static void HandleSummary(HNode node, StringBuilder output, Ctx ctx)
    {
        var content = new StringBuilder();
        var sctx = ctx with { InStrong = true };
        foreach (var c in node.Children) WalkNode(c, content, sctx);
        string trimmed = content.ToString().Trim();
        if (trimmed.Length == 0) return;
        if (ctx.ConvertAsInline) output.Append(trimmed);
        else output.Append("**").Append(trimmed).Append("**\n\n");
    }

    // ── unknown elements (block/unknown.rs) ──────────────────────────────────
    private static void HandleUnknown(HNode node, StringBuilder output, Ctx ctx)
    {
        int lenBefore = output.Length;
        bool hadTrailingSpace = EndsWith(output, " ");
        WalkChildren(node, output, ctx);
        int lenAfter = output.Length;
        if (lenAfter > lenBefore)
        {
            string added = output.ToString(lenBefore, lenAfter - lenBefore);
            bool isCodeBlock = added.StartsWith("    ", StringComparison.Ordinal)
                || added.StartsWith("```", StringComparison.Ordinal)
                || added.StartsWith("~~~", StringComparison.Ordinal);
            if (added.Trim().Length == 0 && !isCodeBlock)
            {
                output.Length = lenBefore;
                if (!hadTrailingSpace && added.Contains(' ')) output.Append(' ');
            }
        }
    }

    // ── tables (block/table/*.rs) ────────────────────────────────────────────
    private sealed class TableScan
    {
        public readonly List<int> RowCounts = new();
        public bool HasSpan, HasHeader, HasCaption, HasText;
        public int NestedTableCount, LinkCount;
    }

    private static void ScanTableNode(HNode node, bool isRoot, TableScan scan)
    {
        if (node.IsComment) return;
        if (node.Tag is null)
        {
            if (!scan.HasText && HtmlWalker.DecodeEntities(node.Text).Trim().Length > 0)
                scan.HasText = true;
            return;
        }
        switch (node.Tag)
        {
            case "a": scan.LinkCount++; break;
            case "caption": scan.HasCaption = true; break;
            case "th": scan.HasHeader = true; break;
            case "img":
                if (node.Attr("src") is not null || node.Attr("alt") is not null) scan.HasText = true;
                break;
            case "table":
                if (!isRoot) scan.NestedTableCount++;
                break;
            case "tr":
            {
                int cellCount = 0;
                foreach (var child in node.Children)
                {
                    if (child.Tag is "td" or "th")
                    {
                        cellCount += GetColspan(child);
                        if (child.Attr("colspan") is not null || child.Attr("rowspan") is not null)
                            scan.HasSpan = true;
                    }
                }
                scan.RowCounts.Add(cellCount);
                break;
            }
        }
        foreach (var c in node.Children) ScanTableNode(c, false, scan);
    }

    private static int GetColspan(HNode cell)
    {
        if (int.TryParse(cell.Attr("colspan"), out var v) && v >= 0)
            return v == 0 ? 1 : Math.Min(v, 1000);
        return 1;
    }

    private static (int colspan, int rowspan) GetColspanRowspan(HNode cell)
    {
        int cs = 1, rs = 1;
        if (int.TryParse(cell.Attr("colspan"), out var c) && c >= 0) cs = c == 0 ? 1 : Math.Min(c, 1000);
        if (int.TryParse(cell.Attr("rowspan"), out var r) && r >= 0) rs = r == 0 ? 1 : Math.Min(r, 1000);
        return (cs, rs);
    }

    private static List<HNode> CollectTableCells(HNode row)
    {
        var cells = new List<HNode>();
        foreach (var c in row.Children)
            if (c.Tag is "td" or "th") cells.Add(c);
        return cells;
    }

    private static IEnumerable<HNode> TableRows(HNode table)
    {
        foreach (var child in table.Children)
        {
            if (child.Tag is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Children)
                    if (row.Tag == "tr") yield return row;
            }
            else if (child.Tag == "tr") yield return child;
        }
    }

    private static void HandleTableWithContext(HNode node, StringBuilder output, Ctx ctx)
    {
        var tableOutput = new StringBuilder();
        HandleTable(node, tableOutput, ctx);

        // Structure-collector side effect: after rendering (which already emitted any nested
        // tables encountered in cells), collect this table's grid (re-walking cells emits those
        // nested tables a second time), then emit this table — nested-before-parent order.
        if (ctx.TableEmit is not null)
        {
            var grid = CollectGrid(node, ctx);
            ctx.TableEmit(grid);
        }

        if (ctx.InListItem)
        {
            bool hasCaption = StartsWith(tableOutput, "*");
            if (!hasCaption)
            {
                TrimTrailingWhitespace(output);
                if (output.Length > 0 && !EndsWith(output, "\n")) output.Append('\n');
            }
            output.Append(IndentTableForList(tableOutput.ToString(), ctx.ListDepth));
        }
        else
        {
            if (output.Length > 0 && !EndsWith(output, "\n\n"))
            {
                if (EndsWith(output, "\n")) output.Append('\n');
                else output.Append("\n\n");
            }
            output.Append(tableOutput);
            if (!EndsWith(output, "\n\n"))
            {
                if (EndsWith(output, "\n")) output.Append('\n');
                else output.Append("\n\n");
            }
        }
    }

    private static string IndentTableForList(string table, int listDepth)
    {
        string indent = new string(' ', 2 * (listDepth > 0 ? 2 * listDepth - 1 : 0));
        var sb = new StringBuilder();
        foreach (var line in table.Split('\n'))
        {
            if (line.Length > 0) sb.Append(indent).Append(line);
            sb.Append('\n');
        }
        // Remove the final extra newline that Split introduced
        if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        return sb.ToString();
    }

    private static void HandleTable(HNode node, StringBuilder output, Ctx ctx)
    {
        var scan = new TableScan();
        ScanTableNode(node, true, scan);

        var distinctCounts = scan.RowCounts.Where(c => c > 0).Distinct().ToList();
        bool hasBorderZero = node.Attr("border") == "0";
        bool looksLikeLayout = scan.NestedTableCount > 1 || distinctCounts.Count > 1 || (scan.HasSpan && hasBorderZero);
        bool isBlankTable = !scan.HasText;
        int rowCount = scan.RowCounts.Count;

        if (!scan.HasHeader && !scan.HasCaption
            && (looksLikeLayout || isBlankTable || (rowCount <= 2 && scan.LinkCount >= 3)))
        {
            if (isBlankTable && scan.LinkCount == 0) return;
            foreach (var child in node.Children)
            {
                if (child.Tag is "thead" or "tbody" or "tfoot")
                {
                    foreach (var row in child.Children)
                        if (row.Tag == "tr") AppendLayoutRow(row, output, ctx);
                }
                else if (child.Tag == "tr") AppendLayoutRow(child, output, ctx);
                else if (child.Tag is "colgroup" or "col") { }
                else if (child.Tag is not null) WalkNode(child, output, ctx);
            }
            if (!EndsWith(output, "\n")) output.Append('\n');
            return;
        }

        int totalCols = TableTotalColumns(node);

        // width pre-pass
        var colWidths = new List<int>();
        var prepassRowspan = new int?[totalCols];
        var prepassCtx = ctx with { MeasureWidthOnly = true };
        foreach (var row in TableRows(node))
            CollectRowCellWidths(row, prepassCtx, colWidths, prepassRowspan);

        var rowspanTracker = new int?[totalCols];
        int rowIndex = 0;
        int? firstRowCols = null;

        foreach (var child in node.Children)
        {
            switch (child.Tag)
            {
                case "caption":
                {
                    var text = new StringBuilder();
                    foreach (var gc in child.Children) WalkNode(gc, text, ctx);
                    string trimmedCap = text.ToString().Trim();
                    if (trimmedCap.Length > 0)
                    {
                        output.Append('*').Append(trimmedCap.Replace("-", "\\-")).Append("*\n\n");
                    }
                    break;
                }
                case "thead": case "tbody": case "tfoot":
                {
                    foreach (var row in child.Children)
                    {
                        if (row.Tag != "tr") continue;
                        if (firstRowCols is null)
                        {
                            int cols = CollectTableCells(row).Sum(GetColspan);
                            firstRowCols = Math.Clamp(cols, 1, 1000);
                        }
                        ConvertTableRow(row, output, ctx, rowIndex, scan.HasSpan, rowspanTracker,
                            totalCols, firstRowCols.Value, colWidths);
                        rowIndex++;
                    }
                    break;
                }
                case "tr":
                {
                    if (firstRowCols is null)
                    {
                        int cols = CollectTableCells(child).Sum(GetColspan);
                        firstRowCols = Math.Clamp(cols, 1, 1000);
                    }
                    ConvertTableRow(child, output, ctx, rowIndex, scan.HasSpan, rowspanTracker,
                        totalCols, firstRowCols.Value, colWidths);
                    rowIndex++;
                    break;
                }
                case "colgroup": case "col":
                    break;
                default:
                    if (child.Tag is not null) WalkNode(child, output, ctx);
                    break;
            }
        }
    }

    private static int TableTotalColumns(HNode table)
    {
        int maxCols = 0;
        foreach (var row in TableRows(table))
        {
            int colCount = CollectTableCells(row).Sum(GetColspan);
            if (colCount > maxCols) maxCols = colCount;
        }
        return Math.Clamp(maxCols, 1, 1000);
    }

    private static void AppendLayoutRow(HNode row, StringBuilder output, Ctx ctx)
    {
        var rowText = new StringBuilder();
        foreach (var cell in row.Children)
        {
            if (cell.Tag is not ("td" or "th")) continue;
            var cellText = new StringBuilder();
            var cellCtx = ctx with { ConvertAsInline = true };
            foreach (var c in cell.Children) WalkNode(c, cellText, cellCtx);
            string content = NormalizeWhitespaceKeepNewlines(cellText.ToString());
            if (content.Trim().Length > 0)
            {
                if (rowText.Length > 0) rowText.Append(' ');
                rowText.Append(content.Trim());
            }
        }
        string trimmed = rowText.ToString().Trim();
        if (trimmed.Length == 0) return;
        if (output.Length > 0 && !EndsWith(output, "\n")) output.Append('\n');
        string formatted = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..].TrimStart() : trimmed;
        output.Append("- ").Append(formatted).Append('\n');
    }

    private static void CollectRowCellWidths(HNode row, Ctx ctx, List<int> colWidths, int?[] rowspanTracker)
    {
        var cells = CollectTableCells(row);
        int col = 0;
        int cellIdx = 0;
        while (true)
        {
            while (col < rowspanTracker.Length && rowspanTracker[col] is int remaining && remaining > 0)
            {
                rowspanTracker[col] = remaining - 1 == 0 ? null : remaining - 1;
                col++;
            }
            if (cellIdx >= cells.Count) break;
            var cell = cells[cellIdx++];
            string text = CellTextContent(cell, ctx);
            int width = Math.Min(text.Length, 200); // char count
            while (colWidths.Count <= col) colWidths.Add(0);
            if (width > colWidths[col]) colWidths[col] = width;
            var (colspan, rowspan) = GetColspanRowspan(cell);
            if (rowspan > 1 && col < rowspanTracker.Length)
                rowspanTracker[col] = rowspan - 1;
            col += colspan;
        }
    }

    private static string CellTextContent(HNode cell, Ctx ctx)
    {
        string text = RenderCellContent(cell, ctx);
        text = text.Trim();
        return text.Contains('\n') ? text.Replace('\n', ' ') : text;
    }

    private static string RenderCellContent(HNode cell, Ctx ctx)
    {
        // Always walk children under an in-cell context (mirrors the crate's convert_table_cell,
        // which routes every cell through walk_node). Text nodes decode entities and escape
        // *_| there, so `&nbsp;`-only cells collapse to empty exactly like the grid path.
        var buf = new StringBuilder();
        var cctx = ctx with { InTableCell = true };
        foreach (var c in cell.Children) WalkNode(c, buf, cctx);
        return buf.ToString();
    }

    private static string EscapeCellText(string text)
    {
        // Always escape * and _ inside table cells; also escape | (escape_misc=false path).
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '*' or '_' or '|') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void ConvertTableRow(HNode row, StringBuilder output, Ctx ctx, int rowIndex,
        bool hasSpan, int?[] rowspanTracker, int totalCols, int headerCols, List<int> colWidths)
    {
        var rowText = new StringBuilder();
        var cells = CollectTableCells(row);
        var cellCtx = ctx with { InTableCell = true };

        if (hasSpan)
        {
            int colIndex = 0;
            int cellIdx = 0;
            while (true)
            {
                if (colIndex < totalCols && rowspanTracker[colIndex] is int remaining && remaining > 0)
                {
                    rowText.Append(' ');
                    if (colIndex < colWidths.Count) rowText.Append(' ', colWidths[colIndex]);
                    rowText.Append(" |");
                    rowspanTracker[colIndex] = remaining - 1 == 0 ? null : remaining - 1;
                    colIndex++;
                    continue;
                }
                if (cellIdx >= cells.Count) break;
                var cell = cells[cellIdx++];
                int? colWidth = colIndex < colWidths.Count ? colWidths[colIndex] : null;
                ConvertTableCell(cell, rowText, cellCtx, colWidth);
                var (colspan, rowspan) = GetColspanRowspan(cell);
                if (rowspan > 1 && colIndex < totalCols)
                    rowspanTracker[colIndex] = rowspan - 1;
                colIndex += colspan;
            }
        }
        else
        {
            for (int i = 0; i < cells.Count; i++)
            {
                int? colWidth = i < colWidths.Count ? colWidths[i] : null;
                ConvertTableCell(cells[i], rowText, cellCtx, colWidth);
            }
        }

        output.Append('|').Append(rowText).Append('\n');

        if (rowIndex == 0)
        {
            int cols = Math.Clamp(headerCols, 1, 1000);
            output.Append("| ");
            for (int i = 0; i < cols; i++)
            {
                if (i > 0) output.Append(" | ");
                int dashCount = Math.Max(i < colWidths.Count ? colWidths[i] : 0, 3);
                output.Append('-', dashCount);
            }
            output.Append(" |\n");
        }
    }

    private static void ConvertTableCell(HNode cell, StringBuilder output, Ctx cellCtx, int? colWidth)
    {
        string text = RenderCellContent(cell, cellCtx).Trim();
        string textForOutput = text.Contains('\n') ? text.Replace('\n', ' ') : text;
        int colspan = GetColspan(cell);
        output.Append(' ').Append(textForOutput);
        if (colWidth is int w)
        {
            int len = textForOutput.Length;
            if (len < w) output.Append(' ', w - len);
        }
        for (int i = 0; i < colspan; i++) output.Append(" |");
    }

    // ── sibling helpers ──────────────────────────────────────────────────────
    private static string? NextSiblingTag(HNode node)
    {
        var parent = node.Parent;
        if (parent is null) return null;
        var siblings = parent.Children;
        for (int i = node.Index + 1; i < siblings.Count; i++)
        {
            var s = siblings[i];
            if (s.IsComment) continue;
            if (s.Tag is not null) return s.Tag;
            if (s.Text.Trim().Length > 0) return null;
        }
        return null;
    }

    private static string? PrevSiblingTag(HNode node)
    {
        var parent = node.Parent;
        if (parent is null) return null;
        var siblings = parent.Children;
        for (int i = node.Index - 1; i >= 0; i--)
        {
            var s = siblings[i];
            if (s.IsComment) continue;
            if (s.Tag is not null) return s.Tag;
            if (s.Text.Trim().Length > 0) return null;
        }
        return null;
    }

    private static bool PrevSiblingIsInlineTag(HNode node)
    {
        var parent = node.Parent;
        if (parent is null) return false;
        var siblings = parent.Children;
        for (int i = node.Index - 1; i >= 0; i--)
        {
            var s = siblings[i];
            if (s.IsComment) continue;
            if (s.Tag is not null) return IsInlineElement(s.Tag);
            if (s.Text.Trim().Length > 0) return false;
        }
        return false;
    }

    private static bool NextSiblingIsInlineTag(HNode node)
    {
        var parent = node.Parent;
        if (parent is null) return false;
        var siblings = parent.Children;
        for (int i = node.Index + 1; i < siblings.Count; i++)
        {
            var s = siblings[i];
            if (s.IsComment) continue;
            if (s.Tag is not null) return IsInlineElement(s.Tag);
            if (s.Text.Trim().Length > 0) return false;
        }
        return false;
    }

    private static bool NextSiblingIsWhitespaceText(HNode node)
    {
        var parent = node.Parent;
        if (parent is null) return false;
        var siblings = parent.Children;
        for (int i = node.Index + 1; i < siblings.Count; i++)
        {
            var s = siblings[i];
            if (s.IsComment) continue;
            if (s.Tag is not null) return false;
            return s.Text.Trim().Length == 0;
        }
        return false;
    }

    private static void AppendInlineSuffix(StringBuilder output, string suffix, bool hasCoreContent, HNode node)
    {
        if (suffix.Length == 0) return;
        if (suffix == " " && hasCoreContent && NextSiblingIsWhitespaceText(node)) return;
        output.Append(suffix);
    }

    internal static bool IsInlineElement(string tag) => tag switch
    {
        "a" or "abbr" or "b" or "bdi" or "bdo" or "br" or "cite" or "code" or "data" or "dfn"
        or "em" or "i" or "kbd" or "mark" or "q" or "rp" or "rt" or "ruby" or "s" or "samp"
        or "small" or "span" or "strong" or "sub" or "sup" or "time" or "u" or "var" or "wbr"
        or "del" or "ins" or "img" or "map" or "area" or "audio" or "video" or "picture"
        or "source" or "track" or "embed" or "object" or "param" or "input" or "label"
        or "button" or "select" or "textarea" or "output" or "progress" or "meter" => true,
        _ => false,
    };

    // ── text helpers ─────────────────────────────────────────────────────────
    private static string TextContent(HNode node)
    {
        var sb = new StringBuilder();
        AppendTextContent(node, sb);
        return HtmlWalker.DecodeEntities(sb.ToString());
    }

    private static void AppendTextContent(HNode node, StringBuilder sb)
    {
        foreach (var c in node.Children)
        {
            if (c.IsComment) continue;
            if (c.Tag is null) sb.Append(c.Text);
            else AppendTextContent(c, sb);
        }
    }

    private static bool HasMoreThanOneChar(string s) => s.Length > 1;

    // normalize_whitespace: collapse spaces/tabs/unicode spaces, preserve newlines
    internal static string NormalizeWhitespaceKeepNewlines(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool prevWasSpace = false;
        foreach (char ch in text)
        {
            bool isSpace = ch == ' ' || ch == '\t' || IsUnicodeSpace(ch);
            if (isSpace)
            {
                if (!prevWasSpace) { sb.Append(' '); prevWasSpace = true; }
            }
            else { sb.Append(ch); prevWasSpace = false; }
        }
        return sb.ToString();
    }

    private static bool IsUnicodeSpace(char ch) => ch is '\u00A0' or '\u1680'
        or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000';

    // text::chomp
    private static (string prefix, string suffix, string core) Chomp(string text)
    {
        if (text.Length == 0) return ("", "", "");
        string prefix = char.IsWhiteSpace(text[0]) ? " " : "";
        string suffix;
        string core;
        if (text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            suffix = "\n\n";
            core = text.TrimEnd('\n').Trim();
            // Rust trims "\n\n" suffix then trims — same effect
        }
        else if (text.EndsWith(' ') || text.EndsWith('\t'))
        {
            suffix = " ";
            core = text.Trim();
        }
        else
        {
            suffix = "";
            core = text.Trim();
        }
        return (prefix, suffix, core);
    }

    // utility::content::chomp_inline
    private static (string prefix, string suffix, string core) ChompInline(string text)
    {
        if (text.Length == 0) return ("", "", "");
        string prefix = text[0] is ' ' or '\t' ? " " : "";
        bool hasTrailingLinebreak = text.EndsWith("  \n", StringComparison.Ordinal) || text.EndsWith("\\\n", StringComparison.Ordinal);
        string suffix = hasTrailingLinebreak
            ? (text.EndsWith("  \n", StringComparison.Ordinal) ? "  \n" : "\\\n")
            : (text.EndsWith(' ') || text.EndsWith('\t') ? " " : "");
        string trimmed;
        if (hasTrailingLinebreak)
        {
            string stripped = text.EndsWith("  \n", StringComparison.Ordinal) ? text[..^3]
                : text.EndsWith("\\\n", StringComparison.Ordinal) ? text[..^2] : text;
            trimmed = stripped.Trim();
        }
        else trimmed = text.Trim();
        return (prefix, suffix, trimmed);
    }

    internal static void TrimTrailingWhitespace(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t')) sb.Length--;
    }

    private static bool EndsWith(StringBuilder sb, string s)
    {
        if (sb.Length < s.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (sb[sb.Length - s.Length + i] != s[i]) return false;
        return true;
    }

    private static bool StartsWith(StringBuilder sb, string s)
    {
        if (sb.Length < s.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (sb[i] != s[i]) return false;
        return true;
    }

    // main_helpers::trim_line_end_whitespace — preserve exactly-two-space hard breaks
    internal static string TrimLineEndWhitespace(string output)
    {
        if (output.Length == 0) return output;
        var cleaned = new StringBuilder(output.Length);
        var lines = output.Split('\n');
        foreach (var lineRaw in lines)
        {
            string line = lineRaw;
            string suffixNl = "\n";
            if (line.EndsWith("  ", StringComparison.Ordinal))
            {
                line = line[..^2];
                suffixNl = "  \n";
            }
            cleaned.Append(line.TrimEnd(' ', '\t')).Append(suffixNl);
        }
        string result = cleaned.ToString();
        string trimmed = result.TrimEnd('\n');
        if (trimmed.Length == 0) return "";
        return trimmed + "\n";
    }

    // main_helpers::collapse_excess_blank_lines
    internal static string CollapseExcessBlankLines(string output)
    {
        if (!output.Contains("\n\n\n")) return output;
        var cleaned = new StringBuilder(output.Length);
        int consecutive = 0;
        foreach (char ch in output)
        {
            if (ch == '\n')
            {
                consecutive++;
                if (consecutive <= 2) cleaned.Append(ch);
            }
            else { consecutive = 0; cleaned.Append(ch); }
        }
        return cleaned.ToString();
    }

    /// <summary>
    /// Port of xberg's `normalize_html_markdown` (extractors/html.rs): setext → ATX,
    /// strip trailing whitespace, blank line before ATX headings, single trailing newline.
    /// </summary>
    public static string NormalizeHtmlMarkdown(string raw)
    {
        var lines = SplitLines(raw);
        var pass1 = new List<string>(lines.Count);
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[i];
            string lineTrimmed = line.TrimEnd();
            if (i + 1 < lines.Count)
            {
                string next = lines[i + 1].Trim();
                bool isSetextH1 = next.Length > 0 && next.All(c => c == '=');
                bool isSetextH2 = next.Length > 0 && next.All(c => c == '-')
                    && lineTrimmed.Trim().Length > 0 && !lineTrimmed.Trim().StartsWith('|');
                if (isSetextH1)
                {
                    pass1.Add("# " + lineTrimmed.Trim());
                    i += 2;
                    continue;
                }
                if (isSetextH2)
                {
                    pass1.Add("## " + lineTrimmed.Trim());
                    i += 2;
                    continue;
                }
            }
            pass1.Add(lineTrimmed);
            i++;
        }

        var result = new StringBuilder(raw.Length);
        for (int idx = 0; idx < pass1.Count; idx++)
        {
            string line = pass1[idx];
            bool isAtx = line.StartsWith('#');
            if (isAtx && idx > 0 && pass1[idx - 1].Length > 0) result.Append('\n');
            result.Append(line).Append('\n');
        }

        string s = result.ToString();
        string trimmedEnd = s.TrimEnd();
        if (trimmedEnd.Length == 0) return "";
        return trimmedEnd + "\n";
    }

    // Rust `str::lines()`: split on \n, dropping a trailing empty segment; \r\n handled too.
    private static List<string> SplitLines(string s)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                int end = i > start && s[i - 1] == '\r' ? i - 1 : i;
                list.Add(s[start..end]);
                start = i + 1;
            }
        }
        if (start < s.Length) list.Add(s[start..]);
        return list;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Minimal lenient HTML DOM (mirrors the `tl` parser semantics used by the crate:
// no HTML5 tree corrections, unclosed tags nest until parent close, li/dt/dd
// implied-close normalization applied as in `normalize_unclosed_list_items`).
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class HNode
{
    public string? Tag;                   // null → text node
    public string Text = "";              // raw (entities NOT decoded)
    public string AttrString = "";
    public bool IsComment;
    public readonly List<HNode> Children = new();
    public HNode? Parent;
    public int Index;

    private Dictionary<string, string?>? _attrCache;

    public string? Attr(string name)
    {
        if (Tag is null) return null;
        _attrCache ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_attrCache.TryGetValue(name, out var cached)) return cached;
        string? v = HtmlWalker.ExtractAttr(AttrString, name);
        _attrCache[name] = v;
        return v;
    }
}

internal static class HtmlDom
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextTags = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea",
    };

    public static HNode Parse(string src)
    {
        var root = new HNode { Tag = "#root" };
        var stack = new List<HNode> { root };
        int pos = 0;
        int n = src.Length;

        void AddChild(HNode child)
        {
            var parent = stack[^1];
            child.Parent = parent;
            child.Index = parent.Children.Count;
            parent.Children.Add(child);
        }

        while (pos < n)
        {
            if (src[pos] != '<')
            {
                int lt = src.IndexOf('<', pos);
                if (lt < 0) lt = n;
                AddChild(new HNode { Tag = null, Text = src[pos..lt] });
                pos = lt;
                continue;
            }

            // comment
            if (pos + 3 < n && src[pos + 1] == '!' && src[pos + 2] == '-' && src[pos + 3] == '-')
            {
                int end = src.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                pos = end < 0 ? n : end + 3;
                continue;
            }
            // doctype / PI
            if (pos + 1 < n && (src[pos + 1] == '!' || src[pos + 1] == '?'))
            {
                int gtd = src.IndexOf('>', pos);
                pos = gtd < 0 ? n : gtd + 1;
                continue;
            }

            int gt = FindTagEnd(src, pos);
            if (gt < 0) { AddChild(new HNode { Tag = null, Text = src[pos..] }); break; }
            string tagContent = src[(pos + 1)..gt];
            pos = gt + 1;

            bool isClosing = tagContent.StartsWith('/');
            string content = isClosing ? tagContent[1..] : tagContent;
            bool selfClosing = content.TrimEnd().EndsWith('/');
            content = content.TrimEnd('/').Trim();
            var (name, attrs) = HtmlWalker.SplitTagName(content);
            string tag = name.ToLowerInvariant();
            if (tag.Length == 0) continue;

            if (isClosing)
            {
                // pop to matching open tag if present
                for (int i = stack.Count - 1; i >= 1; i--)
                {
                    if (stack[i].Tag == tag)
                    {
                        stack.RemoveRange(i, stack.Count - i);
                        break;
                    }
                }
                continue;
            }

            // implied close of li/dt/dd (normalize_unclosed_list_items) and p-in-p leniency
            if (tag is "li")
            {
                for (int i = stack.Count - 1; i >= 1; i--)
                {
                    if (stack[i].Tag == "li") { stack.RemoveRange(i, stack.Count - i); break; }
                    if (stack[i].Tag is "ul" or "ol") break;
                }
            }
            else if (tag is "dt" or "dd")
            {
                for (int i = stack.Count - 1; i >= 1; i--)
                {
                    if (stack[i].Tag is "dt" or "dd") { stack.RemoveRange(i, stack.Count - i); break; }
                    if (stack[i].Tag == "dl") break;
                }
            }

            var node = new HNode { Tag = tag, AttrString = attrs };
            AddChild(node);

            if (VoidTags.Contains(tag) || selfClosing) continue;

            if (RawTextTags.Contains(tag))
            {
                // consume raw text until matching close tag
                string closeStart = "</" + tag;
                int searchFrom = pos;
                int close;
                while (true)
                {
                    close = src.IndexOf(closeStart, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) { close = n; break; }
                    int k = close + closeStart.Length;
                    while (k < n && char.IsWhiteSpace(src[k])) k++;
                    if (k < n && src[k] == '>') break;
                    searchFrom = close + 1;
                }
                if (tag == "textarea" && close > pos)
                {
                    var text = new HNode { Tag = null, Text = src[pos..Math.Min(close, n)] };
                    text.Parent = node;
                    text.Index = 0;
                    node.Children.Add(text);
                }
                if (close >= n) { pos = n; }
                else
                {
                    int gtc = src.IndexOf('>', close);
                    pos = gtc < 0 ? n : gtc + 1;
                }
                continue;
            }

            stack.Add(node);
        }

        return root;
    }

    // Find the '>' terminating a tag, honoring quoted attribute values.
    private static int FindTagEnd(string src, int lt)
    {
        int i = lt + 1;
        char quote = '\0';
        while (i < src.Length)
        {
            char c = src[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
            i++;
        }
        return -1;
    }
}
