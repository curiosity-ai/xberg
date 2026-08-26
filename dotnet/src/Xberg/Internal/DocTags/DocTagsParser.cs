using Xberg.Types;

namespace Xberg.Internal.DocTags;

/// <summary>
/// Parse a Docling DocTags stream into an <see cref="InternalDocument"/>, ported from Rust
/// <c>extraction/doctags.rs</c>. The inverse of <see cref="DocTagsRenderer"/>.
/// </summary>
/// <remarks>
/// Two properties of the format drive the design. There is no escaping, which
/// <see cref="DocTagsVocabulary"/> handles. And location tokens are page-relative: <c>loc_*</c>
/// values are normalised onto a 0–500 grid and the original page size is not recoverable, so
/// pages are reconstructed as <see cref="DocTagsVocabulary.LocGrid"/> squares — which makes
/// re-emitting a parsed document reproduce the original tokens exactly.
/// </remarks>
internal static class DocTagsParser
{
    /// <summary><c>ProcessingWarning.Source</c> for every warning raised while parsing.</summary>
    private const string WarningSource = "doctags";

    public static InternalDocument Parse(string input)
    {
        var tokens = DocTagsVocabulary.Tokenize(input);
        var builder = new InternalDocumentBuilder("doctags");
        uint page = 1;
        uint pagesSeen = 1;
        int index = 0;
        bool strayTextWarned = false;

        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Kind != DocTagKind.Open)
            {
                if (token.Kind == DocTagKind.Close
                    && (token.Value == "ordered_list" || token.Value == "unordered_list"))
                    builder.EndList();
                else if (token.Kind == DocTagKind.Text)
                    PushStrayText(builder, token.Value, page, ref strayTextWarned);
                index++;
                continue;
            }

            string name = token.Value;
            switch (name)
            {
                case "doctag":
                    index++;
                    break;
                case "page_break":
                    builder.PushPageBreak();
                    page++;
                    pagesSeen = Math.Max(pagesSeen, page);
                    index++;
                    break;
                case "ordered_list":
                case "unordered_list":
                    builder.PushList(name == "ordered_list");
                    index++;
                    break;
                default:
                    if (DocTagsVocabulary.IsPaired(name))
                    {
                        int close = MatchingClose(tokens, index);
                        var inner = tokens.GetRange(index + 1, Math.Min(close, tokens.Count) - (index + 1));
                        PushElement(builder, name, inner, page);
                        index = close + 1;
                    }
                    else index++;
                    break;
            }
        }

        var doc = builder.Build();
        doc.Metadata.Pages = ReconstructedPages(pagesSeen);
        doc.MimeType = DocTagsMime.MimeType;
        return doc;
    }

    /// <summary>
    /// Index of the <c>Close</c> matching the <c>Open</c> at <paramref name="openAt"/>, or the
    /// end of the slice.
    /// </summary>
    /// <remarks>
    /// The stack holds tag <em>names</em>, not just a depth. A stray <c>&lt;/text&gt;</c> inside
    /// an unrelated <c>&lt;otsl&gt;…&lt;/otsl&gt;</c> region would decrement a name-blind counter
    /// and close the wrong element early, truncating or misattributing its content.
    /// </remarks>
    private static int MatchingClose(List<DocTagToken> tokens, int openAt)
    {
        if (openAt >= tokens.Count || tokens[openAt].Kind != DocTagKind.Open) return tokens.Count;

        var stack = new List<string> { tokens[openAt].Value };
        for (int i = openAt + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == DocTagKind.Open && DocTagsVocabulary.IsPaired(t.Value))
                stack.Add(t.Value);
            // A close that does not name the innermost open tag belongs to some other element,
            // so it is ignored rather than decrementing this element's nesting.
            else if (t.Kind == DocTagKind.Close && DocTagsVocabulary.IsPaired(t.Value)
                     && stack.Count > 0 && stack[^1] == t.Value)
            {
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) return i;
            }
        }
        return tokens.Count;
    }

    /// <summary>
    /// Read a leading group of four location tokens, returning the box and how many tokens it
    /// consumed.
    /// </summary>
    /// <remarks>
    /// DocTags orders them left, top, right, bottom on a top-left origin; a
    /// <see cref="BoundingBox"/> is PDF space with a bottom-left origin, so the vertical axis is
    /// flipped back here. A hostile or corrupt <c>&lt;loc_nan&gt;</c> is rejected rather than
    /// clamped, so it cannot become a plausible-looking box.
    /// </remarks>
    private static (BoundingBox? Box, int Consumed) TakeLocation(List<DocTagToken> tokens)
    {
        var values = new List<double>(4);
        for (int i = 0; i < Math.Min(4, tokens.Count); i++)
        {
            var t = tokens[i];
            if (t.Kind != DocTagKind.Open || !t.Value.StartsWith("loc_", StringComparison.Ordinal)) break;
            if (!double.TryParse(t.Value[4..], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double value)) break;
            if (!double.IsFinite(value) || value < 0.0 || value > DocTagsVocabulary.LocGrid) break;
            values.Add(value);
        }
        if (values.Count != 4) return (null, 0);

        return (new BoundingBox
        {
            X0 = values[0],
            Y0 = DocTagsVocabulary.LocGrid - values[3],
            X1 = values[2],
            Y1 = DocTagsVocabulary.LocGrid - values[1],
        }, 4);
    }

    /// <summary>Concatenate a token slice's content, ignoring nested tags.</summary>
    private static string TextOf(IEnumerable<DocTagToken> tokens)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in tokens)
            if (t.Kind == DocTagKind.Text) sb.Append(t.Value);
        return sb.ToString().Trim();
    }

    /// <summary>Locate a nested <c>&lt;caption&gt;</c> within an element's inner tokens.</summary>
    private static (int Start, int End)? FindCaption(List<DocTagToken> tokens)
    {
        int start = tokens.FindIndex(t => t.Kind == DocTagKind.Open && t.Value == "caption");
        if (start < 0) return null;
        return (start, MatchingClose(tokens, start));
    }

    /// <summary>
    /// Expand an OTSL cell stream into the flat grid a <see cref="Table"/> stores.
    /// </summary>
    /// <remarks>
    /// Merge tokens repeat the content they continue — <c>lcel</c> the cell to its left,
    /// <c>ucel</c> the one above, <c>xcel</c> either — because the flat grid has nowhere to
    /// record a span. Rows are padded so the grid stays rectangular.
    /// </remarks>
    private static List<List<string>> ParseOtslCells(List<DocTagToken> tokens)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();

        int index = 0;
        while (index < tokens.Count)
        {
            var t = tokens[index];
            if (t.Kind == DocTagKind.Open && t.Value == "nl")
            {
                rows.Add(row);
                row = new List<string>();
                index++;
            }
            else if (t.Kind == DocTagKind.Open && Array.IndexOf(DocTagsVocabulary.OtslCells, t.Value) >= 0)
            {
                string content = index + 1 < tokens.Count && tokens[index + 1].Kind == DocTagKind.Text
                    ? tokens[index + 1].Value.Trim()
                    : "";
                string value = t.Value switch
                {
                    "lcel" => row.Count > 0 ? row[^1] : "",
                    "ucel" => Above(rows, row.Count) ?? "",
                    "xcel" => (row.Count > 0 ? row[^1] : null) ?? Above(rows, row.Count) ?? "",
                    _ => content,
                };
                row.Add(value);
                index++;
            }
            // A caption ends the cell stream.
            else if (t.Kind == DocTagKind.Open && t.Value == "caption") break;
            else index++;
        }

        if (row.Count > 0) rows.Add(row);

        int width = 0;
        foreach (var r in rows) width = Math.Max(width, r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        return rows;

        static string? Above(List<List<string>> rows, int column) =>
            rows.Count > 0 && column < rows[^1].Count ? rows[^1][column] : null;
    }

    /// <summary>
    /// Reconstruct page metadata as <see cref="DocTagsVocabulary.LocGrid"/> squares.
    /// </summary>
    /// <remarks>
    /// The true page size is not in the stream, and using the grid itself as the page is what
    /// makes a re-emit reproduce the original <c>&lt;loc_*&gt;</c> values exactly.
    /// </remarks>
    private static PageStructure ReconstructedPages(uint count) => new()
    {
        TotalCount = count,
        UnitType = PageUnitType.Page,
        Pages = Enumerable.Range(1, (int)count)
            .Select(n => (object)new PageInfoDto((uint)n)
            {
                Dimensions = (DocTagsVocabulary.LocGrid, DocTagsVocabulary.LocGrid),
            })
            .ToList(),
    };

    /// <summary>
    /// Keep text found outside any recognised tag rather than discarding it.
    /// </summary>
    /// <remarks>
    /// A truncated stream — or a plain-text file misrouted here — may carry no recognised
    /// wrapper tags at all, and dropping that text would turn a malformed input into an empty
    /// extraction that claims to have succeeded. Whitespace-only runs between tags, which is what
    /// DocTags puts between elements, are not content and are ignored.
    /// </remarks>
    private static void PushStrayText(
        InternalDocumentBuilder builder, string text, uint page, ref bool warned)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        builder.PushParagraph(trimmed, new List<TextAnnotation>(), page, null);
        if (!warned)
        {
            builder.AddWarning(new ProcessingWarning
            {
                Source = WarningSource,
                Message = "Text appeared outside any recognised DocTags tag; it was kept as a plain "
                          + "paragraph, but the page, table or list structure it belonged to could not "
                          + "be reconstructed",
            });
            warned = true;
        }
    }

    /// <summary>Push one parsed element, plus its caption when it has one.</summary>
    private static void PushElement(
        InternalDocumentBuilder builder, string name, List<DocTagToken> inner, uint page)
    {
        var (bbox, consumed) = TakeLocation(inner);
        var body = inner.GetRange(consumed, inner.Count - consumed);

        (string Text, BoundingBox? Box)? caption = null;
        var found = FindCaption(body);
        if (found is { } c)
        {
            var captionInner = body.GetRange(c.Start + 1, Math.Min(c.End, body.Count) - (c.Start + 1));
            var (captionBox, captionConsumed) = TakeLocation(captionInner);
            caption = (TextOf(captionInner.GetRange(captionConsumed, captionInner.Count - captionConsumed)),
                       captionBox);
        }
        var content = body.GetRange(0, found?.Start ?? body.Count);

        uint? element;
        if (name == "otsl")
        {
            var cells = ParseOtslCells(content);
            // Docling emits table regions it found no cells in. There is no table there, and an
            // invented empty one would not survive a re-emit, so it is dropped.
            element = cells.Count == 0 ? null : builder.PushTableFromCells(cells, page, bbox);
        }
        else if (name == "picture")
        {
            element = builder.PushImage(null, new ExtractedImage(), page, bbox);
        }
        else if (name == "code")
        {
            string? language = null;
            foreach (var t in content)
            {
                if (t.Kind != DocTagKind.Open) continue;
                if (t.Value.Length > 1 && t.Value[0] == '_' && t.Value[^1] == '_')
                {
                    string trimmed = t.Value.Trim('_');
                    if (trimmed.Length > 0 && trimmed != "unknown") { language = trimmed; break; }
                }
            }
            element = builder.PushCode(TextOf(content), language, page, bbox);
        }
        else if (name == "formula") element = builder.PushFormula(TextOf(content), page, bbox);
        else if (name == "title") element = builder.PushTitle(TextOf(content), page, bbox);
        else if (name == "list_item")
            element = builder.PushListItem(TextOf(content), false, new List<TextAnnotation>(), page, bbox);
        else if (name == "footnote")
        {
            uint index = builder.PushFootnoteDefinition(TextOf(content), "", page);
            builder.SetLayer(index, ContentLayer.Footnote);
            element = index;
        }
        else if (name is "page_header" or "page_footer")
        {
            uint index = builder.PushParagraph(TextOf(content), new List<TextAnnotation>(), page, bbox);
            builder.SetLayer(index, name == "page_header" ? ContentLayer.Header : ContentLayer.Footer);
            element = index;
        }
        else if (name is "checkbox_selected" or "checkbox_unselected")
        {
            string marker = name == "checkbox_selected" ? "[x]" : "[ ]";
            string text = TextOf(content);
            text = text.Length == 0 ? marker : $"{marker} {text}";
            element = builder.PushParagraph(text, new List<TextAnnotation>(), page, bbox);
        }
        else if (name.StartsWith("section_header_level_", StringComparison.Ordinal))
        {
            byte level = byte.TryParse(name["section_header_level_".Length..], out var parsed) ? parsed : (byte)1;
            element = builder.PushHeading(Math.Clamp(level, (byte)1, (byte)6), TextOf(content), page, bbox);
        }
        // `text` and anything else that wraps prose.
        else element = builder.PushParagraph(TextOf(content), new List<TextAnnotation>(), page, bbox);

        if (caption is { } cap && cap.Text.Length > 0)
        {
            uint captionIndex = builder.PushParagraph(
                cap.Text, new List<TextAnnotation>(), page, cap.Box);
            // A caption whose target was dropped still carries text, so it stays as an ordinary
            // paragraph rather than going down with the target.
            if (element is { } target)
                builder.PushRelationship(captionIndex, RelationshipTarget.FromIndex(target),
                                         RelationshipKind.Caption);
        }
    }
}

/// <summary>The MIME types a DocTags stream is served under.</summary>
public static class DocTagsMime
{
    public const string MimeType = "text/vnd.docling.doctags";
    public const string ApplicationMimeType = "application/vnd.docling.doctags";
}
