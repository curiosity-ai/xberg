using System.Text;
using Xberg.Types;

namespace Xberg.Rendering;

/// <summary>
/// Renders an <see cref="InternalDocument"/> to GFM Markdown.
///
/// DEVIATION FROM RUST: the Rust renderer builds a comrak AST and formats it via
/// `format_commonmark`. Porting comrak's exact AST/formatter (~70 KB of `comrak_bridge.rs`)
/// is out of scope for the core spine, so this is a direct element-walk GFM writer. Output is
/// close but may differ from comrak in whitespace/escaping edge cases; the string post-processing
/// helpers (<see cref="UnescapeBackslashSequences"/>, <see cref="ReplaceHtmlEntities"/>,
/// <see cref="CollapseExcessNewlines"/>) are ported faithfully and unit-tested.
/// </summary>
public static class MarkdownRenderer
{
    public static string Render(InternalDocument doc)
    {
        var sb = new StringBuilder(doc.Elements.Count * 80);
        var state = new RenderState();

        foreach (var elem in doc.Elements)
        {
            if (elem.Layer != ContentLayer.Body) continue;

            switch (elem.Kind.Tag)
            {
                case ElementKindTag.ListStart:
                    state.PushContainer(NestingKind.ListKind(elem.Kind.Ordered, 0), elem.Depth);
                    continue;
                case ElementKindTag.QuoteStart:
                    state.PushContainer(NestingKind.BlockQuote, elem.Depth);
                    continue;
                case ElementKindTag.GroupStart:
                    state.PushContainer(NestingKind.Group, elem.Depth);
                    continue;
                case ElementKindTag.ListEnd:
                case ElementKindTag.QuoteEnd:
                case ElementKindTag.GroupEnd:
                    RenderCommon.HandleContainerEnd(elem.Kind, state);
                    continue;
            }

            int bq = state.BlockquoteDepth();

            switch (elem.Kind.Tag)
            {
                case ElementKindTag.Title:
                    if (elem.Text.Length > 0)
                        AppendBlock(sb, "# " + InlineText(elem), bq);
                    break;
                case ElementKindTag.Heading:
                    if (elem.Text.Length > 0)
                    {
                        int level = Math.Clamp(elem.Kind.Level, (byte)1, (byte)6);
                        AppendBlock(sb, new string('#', level) + " " + InlineText(elem), bq);
                    }
                    break;
                case ElementKindTag.Paragraph:
                    if (elem.Text.Length > 0)
                        AppendBlock(sb, InlineText(elem), bq);
                    break;
                case ElementKindTag.ListItem:
                    {
                        int listDepth = Math.Max(0, state.ListDepth() - 1);
                        string indent = new string(' ', listDepth * 2);
                        string marker = elem.Kind.Ordered ? state.NextListNumber() + ". " : "- ";
                        sb.Append(indent).Append(marker).Append(InlineText(elem)).Append('\n');
                    }
                    break;
                case ElementKindTag.Code:
                    {
                        string lang = RenderCommon.GetLanguage(elem) ?? "";
                        AppendBlock(sb, "```" + lang + "\n" + elem.Text.TrimEnd('\n') + "\n```", bq);
                    }
                    break;
                case ElementKindTag.Formula:
                    if (elem.Text.Length > 0)
                        AppendBlock(sb, "$$\n" + elem.Text + "\n$$", bq);
                    break;
                case ElementKindTag.Table:
                    {
                        int ti = (int)elem.Kind.TableIndex;
                        if (ti < doc.Tables.Count)
                        {
                            var table = doc.Tables[ti];
                            string t = table.Cells.Count > 0
                                ? RenderCommon.RenderTableMarkdown(table.Cells)
                                : table.Markdown;
                            if (t.Trim().Length > 0)
                                AppendBlock(sb, t.TrimEnd('\n'), bq);
                        }
                    }
                    break;
                case ElementKindTag.Image:
                    {
                        int ii = (int)elem.Kind.ImageIndex;
                        if (ii < doc.Images.Count)
                        {
                            var img = doc.Images[ii];
                            string alt = img.Description ?? elem.Text;
                            string src = img.Data.Length > 0
                                ? $"image_{elem.Kind.ImageIndex}.{img.Format}"
                                : img.SourcePath ?? "";
                            AppendBlock(sb, $"![{alt}]({src})", bq);
                        }
                    }
                    break;
                case ElementKindTag.Citation:
                    if (elem.Text.Length > 0) AppendBlock(sb, InlineText(elem), bq);
                    break;
                case ElementKindTag.Slide:
                    if (elem.Text.Length > 0) AppendBlock(sb, InlineText(elem), bq);
                    break;
                case ElementKindTag.DefinitionTerm:
                    AppendBlock(sb, InlineText(elem), bq);
                    break;
                case ElementKindTag.DefinitionDescription:
                    AppendBlock(sb, ": " + InlineText(elem), bq);
                    break;
                case ElementKindTag.Admonition:
                    {
                        var title = RenderCommon.GetAdmonitionTitle(elem) ?? RenderCommon.GetAdmonitionKind(elem);
                        AppendBlock(sb, "> **" + title + "**", bq);
                        if (elem.Text.Length > 0) AppendBlock(sb, elem.Text, bq);
                    }
                    break;
                case ElementKindTag.RawBlock:
                    if (elem.Text.Length > 0) AppendBlock(sb, elem.Text, bq);
                    break;
                case ElementKindTag.MetadataBlock:
                    {
                        var entries = RenderCommon.ParseMetadataEntries(elem.Text);
                        if (entries.Count > 0)
                        {
                            var b = new StringBuilder();
                            foreach (var (k, v) in entries) b.Append(k).Append(": ").Append(v).Append('\n');
                            AppendBlock(sb, b.ToString().TrimEnd('\n'), bq);
                        }
                        else if (elem.Text.Length > 0) AppendBlock(sb, elem.Text, bq);
                    }
                    break;
                case ElementKindTag.PageBreak:
                    AppendBlock(sb, "---", bq);
                    break;
                case ElementKindTag.OcrText:
                    if (elem.Text.Length > 0) AppendBlock(sb, elem.Text, bq);
                    break;
            }
        }

        string output = sb.ToString();
        output = ReplaceHtmlEntities(output);
        output = CollapseExcessNewlines(output);

        int trimmedLen = RenderCommon.TrimEndLen(output);
        if (trimmedLen == 0) return "";
        return output.Substring(0, trimmedLen) + "\n";
    }

    private static string InlineText(InternalElement elem)
    {
        if (elem.Annotations.Count == 0) return elem.Text;
        return RenderCommon.RenderAnnotatedText(elem.Text, elem.Annotations, EmitInline);
    }

    private static string EmitInline(string span, AnnotationKind kind) => kind.Which switch
    {
        AnnotationKind.Tag.Bold => "**" + span + "**",
        AnnotationKind.Tag.Italic => "*" + span + "*",
        AnnotationKind.Tag.Code => "`" + span + "`",
        AnnotationKind.Tag.Strikethrough => "~~" + span + "~~",
        AnnotationKind.Tag.Link => "[" + span + "](" + (kind.Url ?? "") + ")",
        _ => span,
    };

    private static void AppendBlock(StringBuilder sb, string block, int bqDepth)
    {
        if (sb.Length > 0) sb.Append('\n');
        RenderCommon.PushWithBq(sb, block, bqDepth);
        if (sb.Length == 0 || sb[^1] != '\n') sb.Append('\n');
    }

    // ------------------------------------------------------------------
    // String post-processing helpers (ported verbatim + unit-tested).
    // ------------------------------------------------------------------

    /// <summary>Drops the backslash before any target char (single pass).</summary>
    public static string UnescapeBackslashSequences(string input, char[] targets)
    {
        int firstHit = -1;
        for (int i = 0; i + 1 < input.Length; i++)
        {
            if (input[i] == '\\' && Array.IndexOf(targets, input[i + 1]) >= 0)
            {
                firstHit = i;
                break;
            }
        }
        if (firstHit < 0) return input;

        var sb = new StringBuilder(input.Length);
        sb.Append(input, 0, firstHit);
        int idx = firstHit;
        while (idx < input.Length)
        {
            if (idx + 1 < input.Length && input[idx] == '\\' && Array.IndexOf(targets, input[idx + 1]) >= 0)
            {
                sb.Append(input[idx + 1]);
                idx += 2;
                continue;
            }
            sb.Append(input[idx]);
            idx++;
        }
        return sb.ToString();
    }

    /// <summary>`&amp;#10;` → space, `&amp;#2;` → removed (single pass).</summary>
    public static string ReplaceHtmlEntities(string input)
    {
        if (!input.Contains("&#10;") && !input.Contains("&#2;")) return input;

        var sb = new StringBuilder(input.Length);
        string rest = input;
        while (rest.Length > 0)
        {
            int pos = rest.IndexOf("&#", StringComparison.Ordinal);
            if (pos < 0)
            {
                sb.Append(rest);
                break;
            }
            sb.Append(rest, 0, pos);
            string after = rest.Substring(pos);
            if (after.StartsWith("&#10;", StringComparison.Ordinal))
            {
                sb.Append(' ');
                rest = after.Substring(5);
            }
            else if (after.StartsWith("&#2;", StringComparison.Ordinal))
            {
                rest = after.Substring(4);
            }
            else
            {
                sb.Append("&#");
                rest = after.Substring(2);
            }
        }
        return sb.ToString();
    }

    /// <summary>Collapses runs of 3+ newlines down to exactly two (single pass).</summary>
    public static string CollapseExcessNewlines(string input)
    {
        if (!input.Contains("\n\n\n")) return input;

        var sb = new StringBuilder(input.Length);
        int newlineRun = 0;
        foreach (var c in input)
        {
            if (c == '\n')
            {
                newlineRun++;
                if (newlineRun <= 2) sb.Append('\n');
            }
            else
            {
                newlineRun = 0;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
