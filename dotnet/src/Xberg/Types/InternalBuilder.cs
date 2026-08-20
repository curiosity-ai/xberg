using System.Text;

namespace Xberg.Types;

/// <summary>
/// Ergonomic builder for constructing <see cref="InternalDocument"/> instances.
/// Ports Rust `InternalDocumentBuilder`: tracks nesting depth for list/quote containers
/// and generates deterministic BLAKE3 element IDs. The ID index comes from an internal
/// push counter (mirrors Rust `node_count`/`next_index`).
/// </summary>
public sealed class InternalDocumentBuilder
{
    private readonly InternalDocument _doc;
    private ushort _depth;
    private uint _nodeCount;

    public InternalDocumentBuilder(string sourceFormat) => _doc = new InternalDocument(sourceFormat);

    // Exposed for tests that assert on builder internals.
    public ushort Depth => _depth;
    public uint NodeCount => _nodeCount;

    public void SourceFormat(string format) => _doc.SourceFormat = format;
    public void SetMetadata(Metadata metadata) => _doc.Metadata = metadata;
    public void SetMimeType(string mimeType) => _doc.MimeType = mimeType;
    public void AddWarning(ProcessingWarning warning) => _doc.ProcessingWarnings.Add(warning);
    public void PushUri(ExtractedUri uri) => _doc.PushUri(uri);

    public InternalDocument Build() => _doc;

    public uint PushHeading(byte level, string text, uint? page, BoundingBox? bbox)
    {
        string anchor = Slugify(text);
        var kind = ElementKind.Heading(level);
        ushort depth = Math.Max(_depth, (ushort)(level > 0 ? level - 1 : 0));
        var elem = MakeElement(kind, text, depth, page, bbox, anchor);
        return _doc.PushElement(elem);
    }

    public uint PushParagraph(string text, List<TextAnnotation> annotations, uint? page, BoundingBox? bbox) =>
        PushSimple(ElementKind.Paragraph, text, page, bbox, annotations, null, null);

    public void PushList(bool ordered) => PushContainerStart(ElementKind.ListStart(ordered), null, null);
    public void EndList() => PushContainerEnd(ElementKind.ListEnd);

    public uint PushListItem(string text, bool ordered, List<TextAnnotation> annotations, uint? page, BoundingBox? bbox) =>
        PushSimple(ElementKind.ListItem(ordered), text, page, bbox, annotations, null, null);

    public uint PushTable(Table table, uint? page, BoundingBox? bbox)
    {
        uint tableIndex = _doc.PushTable(table);
        return PushSimple(ElementKind.Table(tableIndex), "", page, bbox, new(), null, null);
    }

    public uint PushTableFromCells(IReadOnlyList<List<string>> cells, uint? page, BoundingBox? bbox)
    {
        var table = new Table
        {
            Cells = cells.Select(r => new List<string>(r)).ToList(),
            Markdown = CellsToMarkdown(cells),
            PageNumber = page ?? 0,
            BoundingBox = null,
        };
        return PushTable(table, page, bbox);
    }

    public uint PushImage(string? description, ExtractedImage image, uint? page, BoundingBox? bbox)
    {
        uint imageIndex = _doc.PushImage(image);
        return PushSimple(ElementKind.Image(imageIndex), description ?? "", page, bbox, new(), null, null);
    }

    public uint PushCode(string text, string? language, uint? page, BoundingBox? bbox)
    {
        var attrs = language is null ? null : SingleAttr("language", language);
        return PushSimple(ElementKind.Code, text, page, bbox, new(), attrs, null);
    }

    public uint PushFormula(string text, uint? page, BoundingBox? bbox) =>
        PushSimple(ElementKind.Formula, text, page, bbox, new(), null, null);

    public uint PushFootnoteRef(string marker, string key, uint? page)
    {
        uint idx = PushSimple(ElementKind.FootnoteRef, marker, page, null, new(), null, key);
        _doc.PushRelationship(new Relationship
        {
            Source = idx,
            Target = RelationshipTarget.FromKey(key),
            Kind = RelationshipKind.FootnoteReference,
        });
        return idx;
    }

    public uint PushFootnoteDefinition(string text, string key, uint? page) =>
        PushSimple(ElementKind.FootnoteDefinition, text, page, null, new(), null, key);

    public uint PushCommentDefinition(string text, string key, uint? page) =>
        PushSimple(ElementKind.CommentDefinition, text, page, null, new(), null, key);

    public uint PushCommentRef(string marker, string key, uint? page) =>
        PushSimple(ElementKind.CommentRef, marker, page, null, new(), null, key);

    public uint PushCitation(string text, string key, uint? page) =>
        PushSimple(ElementKind.Citation, text, page, null, new(), null, key);

    public void PushQuoteStart() => PushContainerStart(ElementKind.QuoteStart, null, null);
    public void PushQuoteEnd() => PushContainerEnd(ElementKind.QuoteEnd);

    public void PushPageBreak()
    {
        var elem = MakeElement(ElementKind.PageBreak, "", 0, null, null, null);
        _doc.PushElement(elem);
    }

    public uint PushSlide(uint number, string? title, uint? page)
    {
        var attrs = title is null ? null : SingleAttr("title", title);
        return PushSimple(ElementKind.Slide(number), title ?? "", page, null, new(), attrs, null);
    }

    public uint PushAdmonition(string kind, string? title, uint? page)
    {
        var attrs = new Dictionary<string, string> { ["kind"] = kind };
        if (title is not null) attrs["title"] = title;
        return PushSimple(ElementKind.Admonition, title ?? kind, page, null, new(), attrs, null);
    }

    public uint PushRawBlock(string format, string content, uint? page) =>
        PushSimple(ElementKind.RawBlock, content, page, null, new(), SingleAttr("format", format), null);

    public uint PushMetadataBlock(IReadOnlyList<(string Key, string Value)> entries, uint? page)
    {
        var attrs = new Dictionary<string, string>();
        foreach (var (k, v) in entries) attrs[k] = v;
        string text = string.Join("\n", entries.Select(e => $"{e.Key}: {e.Value}"));
        return PushSimple(ElementKind.MetadataBlock, text, page, null, new(), attrs, null);
    }

    public uint PushTitle(string text, uint? page, BoundingBox? bbox) =>
        PushSimple(ElementKind.Title, text, page, bbox, new(), null, null);

    public uint PushDefinitionTerm(string text, uint? page) =>
        PushSimple(ElementKind.DefinitionTerm, text, page, null, new(), null, null);

    public uint PushDefinitionDescription(string text, uint? page) =>
        PushSimple(ElementKind.DefinitionDescription, text, page, null, new(), null, null);

    public void PushGroupStart(string? label, uint? page)
    {
        var attrs = label is null ? null : SingleAttr("label", label);
        PushContainerStart(ElementKind.GroupStart, page, attrs);
    }

    public void PushGroupEnd() => PushContainerEnd(ElementKind.GroupEnd);

    public void PushRelationship(uint source, RelationshipTarget target, RelationshipKind kind) =>
        _doc.PushRelationship(new Relationship { Source = source, Target = target, Kind = kind });

    public void SetAnchor(uint index, string anchor)
    {
        if (index < _doc.Elements.Count) _doc.Elements[(int)index].Anchor = anchor;
    }

    public void SetLayer(uint index, ContentLayer layer)
    {
        if (index < _doc.Elements.Count) _doc.Elements[(int)index].Layer = layer;
    }

    public void SetAttributes(uint index, Dictionary<string, string> attributes)
    {
        if (index < _doc.Elements.Count) _doc.Elements[(int)index].Attributes = attributes;
    }

    public void SetAnnotations(uint index, List<TextAnnotation> annotations)
    {
        if (index < _doc.Elements.Count) _doc.Elements[(int)index].Annotations = annotations;
    }

    public void SetText(uint index, string text)
    {
        if (index < _doc.Elements.Count) _doc.Elements[(int)index].Text = text;
    }

    public uint PushElement(InternalElement element)
    {
        _nodeCount++;
        return _doc.PushElement(element);
    }

    /// <summary>
    /// Append another document's contents to this one, rebasing every index it carries.
    /// <para>
    /// A document assembled elsewhere numbers its tables, images and elements from zero, so its
    /// elements' table and image indices — and its relationships' element indices — have to be
    /// shifted by what this document already holds, or they would point at the wrong rows.
    /// </para>
    /// </summary>
    public void AppendDocument(InternalDocument other)
    {
        uint tableOffset = (uint)_doc.Tables.Count;
        uint imageOffset = (uint)_doc.Images.Count;
        uint elementOffset = (uint)_doc.Elements.Count;

        _doc.Tables.AddRange(other.Tables);
        _doc.Images.AddRange(other.Images);
        _doc.Uris.AddRange(other.Uris);

        foreach (var element in other.Elements)
        {
            if (element.Kind.Tag == ElementKindTag.Table)
                element.Kind = ElementKind.Table(element.Kind.TableIndex + tableOffset);
            else if (element.Kind.Tag == ElementKindTag.Image && element.Kind.ImageIndex != uint.MaxValue)
                element.Kind = ElementKind.Image(element.Kind.ImageIndex + imageOffset);
            PushElement(element);
        }

        foreach (var relationship in other.Relationships)
        {
            relationship.Source += elementOffset;
            if (relationship.Target.Index is uint index)
                relationship.Target = RelationshipTarget.FromIndex(index + elementOffset);
            _doc.PushRelationship(relationship);
        }
    }

    // --- container helpers ---

    private void PushContainerStart(ElementKind kind, uint? page, Dictionary<string, string>? attrs)
    {
        PushSimple(kind, "", page, null, new(), attrs, null);
        _depth++;
    }

    private void PushContainerEnd(ElementKind kind)
    {
        _depth = (ushort)(_depth > 0 ? _depth - 1 : 0);
        PushSimple(kind, "", null, null, new(), null, null);
    }

    // --- internal helpers ---

    private uint NextIndex()
    {
        uint idx = _nodeCount;
        _nodeCount++;
        return idx;
    }

    private InternalElement MakeElement(ElementKind kind, string text, ushort depth, uint? page, BoundingBox? bbox, string? anchor)
    {
        uint idx = NextIndex();
        return new InternalElement
        {
            Id = InternalElementId.Generate(kind.Discriminant(), text, page, idx),
            Kind = kind,
            Text = text,
            Depth = depth,
            Page = page,
            Bbox = bbox,
            Layer = ContentLayer.Body,
            Anchor = anchor,
        };
    }

    private uint PushSimple(ElementKind kind, string text, uint? page, BoundingBox? bbox,
        List<TextAnnotation> annotations, Dictionary<string, string>? attributes, string? anchor)
    {
        var elem = MakeElement(kind, text, _depth, page, bbox, anchor);
        elem.Annotations = annotations;
        elem.Attributes = attributes;
        return _doc.PushElement(elem);
    }

    private static Dictionary<string, string> SingleAttr(string key, string val) => new() { [key] = val };

    /// <summary>
    /// Render cells as a GFM pipe table.
    /// </summary>
    /// <remarks>
    /// Every row is padded to the widest row's column count: a pipe table's columns are positional,
    /// so a short row would otherwise leave the cells after it reading under the wrong headings.
    /// The delimiter row follows the first row whether or not any rows follow, since a table with
    /// one row is still a table and without the delimiter it is not one at all.
    /// </remarks>
    internal static string CellsToMarkdown(IReadOnlyList<List<string>> cells)
    {
        if (cells.Count == 0) return "";
        int numCols = cells.Max(r => r.Count);
        if (numCols == 0) return "";

        var md = new StringBuilder();
        for (int rowIdx = 0; rowIdx < cells.Count; rowIdx++)
        {
            var row = cells[rowIdx];
            md.Append('|');
            for (int col = 0; col < numCols; col++)
            {
                md.Append(' ');
                if (col < row.Count) md.Append(row[col]);
                md.Append(" |");
            }
            md.Append('\n');
            if (rowIdx == 0)
            {
                md.Append('|');
                for (int i = 0; i < numCols; i++) md.Append(" --- |");
                md.Append('\n');
            }
        }
        return md.ToString();
    }

    internal static string Slugify(string text)
    {
        var result = new StringBuilder(text.Length);
        bool prevDash = true; // treat start as dash to avoid leading dash
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                result.Append(c.ToString().ToLowerInvariant());
                prevDash = false;
            }
            else if (!prevDash)
            {
                result.Append('-');
                prevDash = true;
            }
        }
        if (result.Length > 0 && result[^1] == '-')
            result.Remove(result.Length - 1, 1);
        return result.ToString();
    }
}
