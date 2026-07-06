using System.Text;
using Xberg.Types;

namespace Xberg.Rendering;

/// <summary>
/// Renders an <see cref="InternalDocument"/> to HTML5.
///
/// DEVIATION FROM RUST: the Rust renderer routes through comrak's `format_html`. This is a
/// direct element-walk HTML writer producing equivalent structure; exact whitespace/attribute
/// output may differ from comrak (documented in PORT_NOTES).
/// </summary>
public static class HtmlRenderer
{
    public static string Render(InternalDocument doc)
    {
        var sb = new StringBuilder(doc.Elements.Count * 96);
        var state = new RenderState();
        bool listOpen = false;
        bool listOrdered = false;

        void CloseList()
        {
            if (listOpen)
            {
                sb.Append(listOrdered ? "</ol>\n" : "</ul>\n");
                listOpen = false;
            }
        }

        foreach (var elem in doc.Elements)
        {
            if (elem.Layer != ContentLayer.Body) continue;

            switch (elem.Kind.Tag)
            {
                case ElementKindTag.ListStart:
                    CloseList();
                    listOpen = true;
                    listOrdered = elem.Kind.Ordered;
                    sb.Append(listOrdered ? "<ol>\n" : "<ul>\n");
                    continue;
                case ElementKindTag.ListEnd:
                    CloseList();
                    continue;
                case ElementKindTag.QuoteStart:
                    sb.Append("<blockquote>\n");
                    continue;
                case ElementKindTag.QuoteEnd:
                    sb.Append("</blockquote>\n");
                    continue;
                case ElementKindTag.GroupStart:
                case ElementKindTag.GroupEnd:
                    continue;
            }

            switch (elem.Kind.Tag)
            {
                case ElementKindTag.Title:
                    if (elem.Text.Length > 0) sb.Append("<h1>").Append(Inline(elem)).Append("</h1>\n");
                    break;
                case ElementKindTag.Heading:
                    if (elem.Text.Length > 0)
                    {
                        int level = Math.Clamp(elem.Kind.Level, (byte)1, (byte)6);
                        sb.Append("<h").Append(level).Append('>').Append(Inline(elem)).Append("</h").Append(level).Append(">\n");
                    }
                    break;
                case ElementKindTag.Paragraph:
                    if (elem.Text.Length > 0) sb.Append("<p>").Append(Inline(elem)).Append("</p>\n");
                    break;
                case ElementKindTag.ListItem:
                    sb.Append("<li>").Append(Inline(elem)).Append("</li>\n");
                    break;
                case ElementKindTag.Code:
                    {
                        string lang = RenderCommon.GetLanguage(elem) ?? "";
                        string cls = lang.Length > 0 ? $" class=\"language-{Escape(lang)}\"" : "";
                        sb.Append("<pre><code").Append(cls).Append('>').Append(Escape(elem.Text)).Append("</code></pre>\n");
                    }
                    break;
                case ElementKindTag.Formula:
                    if (elem.Text.Length > 0) sb.Append("<p>").Append(Escape(elem.Text)).Append("</p>\n");
                    break;
                case ElementKindTag.Table:
                    {
                        int ti = (int)elem.Kind.TableIndex;
                        if (ti < doc.Tables.Count) AppendTable(sb, doc.Tables[ti]);
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
                            sb.Append($"<p><img src=\"{Escape(src)}\" alt=\"{Escape(alt)}\" /></p>\n");
                        }
                    }
                    break;
                case ElementKindTag.Citation:
                case ElementKindTag.Slide:
                case ElementKindTag.OcrText:
                    if (elem.Text.Length > 0) sb.Append("<p>").Append(Inline(elem)).Append("</p>\n");
                    break;
                case ElementKindTag.RawBlock:
                    if (elem.Text.Length > 0) sb.Append(elem.Text).Append('\n');
                    break;
                case ElementKindTag.PageBreak:
                    sb.Append("<hr />\n");
                    break;
            }
        }
        CloseList();
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        if (table.Cells.Count == 0) return;
        sb.Append("<table>\n<thead>\n<tr>\n");
        foreach (var h in table.Cells[0]) sb.Append("<th>").Append(Escape(h)).Append("</th>\n");
        sb.Append("</tr>\n</thead>\n<tbody>\n");
        for (int r = 1; r < table.Cells.Count; r++)
        {
            sb.Append("<tr>\n");
            foreach (var c in table.Cells[r]) sb.Append("<td>").Append(Escape(c)).Append("</td>\n");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
    }

    private static string Inline(InternalElement elem)
    {
        if (elem.Annotations.Count == 0) return Escape(elem.Text);
        return RenderCommon.RenderAnnotatedTextWithPlain(elem.Text, elem.Annotations, EmitInline, Escape);
    }

    private static string EmitInline(string span, AnnotationKind kind)
    {
        string e = Escape(span);
        return kind.Which switch
        {
            AnnotationKind.Tag.Bold => "<strong>" + e + "</strong>",
            AnnotationKind.Tag.Italic => "<em>" + e + "</em>",
            AnnotationKind.Tag.Code => "<code>" + e + "</code>",
            AnnotationKind.Tag.Strikethrough => "<del>" + e + "</del>",
            AnnotationKind.Tag.Underline => "<u>" + e + "</u>",
            AnnotationKind.Tag.Link => "<a href=\"" + Escape(kind.Url ?? "") + "\">" + e + "</a>",
            _ => e,
        };
    }

    private static string Escape(string s)
    {
        if (s.IndexOfAny(new[] { '&', '<', '>', '"' }) < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
