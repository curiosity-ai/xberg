using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Ooxml;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Word document extractor. Ports `extractors/docx.rs` `build_internal_document` over a
/// DOCX parsed by <see cref="DocxReader"/>: emits headings/paragraphs/lists/tables/images and
/// header/footer/footnote layers, plus core/app/custom metadata (<see cref="DocxMetadata"/>).
/// Math (OMML→LaTeX) is not converted.
/// </summary>
public sealed class DocxExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-word.document.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        "application/vnd.ms-word.template.macroEnabled.12",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        using var pkg = new OoxmlPackage(content);
        var doc = DocxReader.Parse(pkg);
        var internalDoc = BuildInternalDocument(doc);
        PopulateImages(doc, internalDoc);
        internalDoc.MimeType = mimeType;
        internalDoc.Metadata = BuildMetadata(pkg);
        return internalDoc;
    }

    // ── build_internal_document (docx.rs) ──────────────────────────────────────
    private static InternalDocument BuildInternalDocument(DocxDocument doc)
    {
        var b = new InternalDocumentBuilder("docx");

        long? currentListNumId = null;
        bool currentListOrdered = false;
        long currentNesting = 0;
        long openListCount = 0;

        void CloseLists()
        {
            if (currentListNumId is not null)
            {
                for (long i = 0; i < openListCount; i++) b.EndList();
                currentListNumId = null;
                openListCount = 0;
            }
        }

        uint drawingIndex = 0;
        foreach (var el in doc.Elements)
        {
            switch (el.Kind)
            {
                case DocElementKind.Paragraph:
                {
                    var para = el.Paragraph!;
                    string text = CollectText(para.Runs);
                    if (text.Length == 0) { CloseLists(); continue; }

                    byte? headingLevel = DocxReader.ResolveHeadingLevel(para.StyleId);
                    bool isQuote = IsQuoteStyle(para.StyleId);

                    if (headingLevel is { } level)
                    {
                        CloseLists();
                        string headingText = text.Length == 0 ? RunsToMarkdown(para.Runs) : text;
                        b.PushHeading(level, headingText, null, null);
                    }
                    else if (isQuote)
                    {
                        CloseLists();
                        b.PushQuoteStart();
                        b.PushParagraph(text, new(), null, null);
                        b.PushQuoteEnd();
                    }
                    else if (para.NumId is { } nid)
                    {
                        long nlvl = para.NumLevel ?? 0;
                        bool isOrdered = doc.NumberingOrdered.TryGetValue((nid, nlvl), out var ord) && ord;
                        if (currentListNumId != nid)
                        {
                            if (currentListNumId is not null)
                                for (long i = 0; i < openListCount; i++) b.EndList();
                            b.PushList(isOrdered);
                            currentListNumId = nid;
                            currentListOrdered = isOrdered;
                            currentNesting = nlvl;
                            openListCount = 1;
                        }
                        else if (nlvl > currentNesting)
                        {
                            for (long i = 0; i < nlvl - currentNesting; i++) { b.PushList(isOrdered); openListCount++; }
                            currentNesting = nlvl;
                        }
                        else if (nlvl < currentNesting)
                        {
                            for (long i = 0; i < currentNesting - nlvl; i++) { b.EndList(); openListCount = Math.Max(0, openListCount - 1); }
                            currentNesting = nlvl;
                        }
                        b.PushListItem(text, currentListOrdered, new(), null, null);
                    }
                    else
                    {
                        CloseLists();
                        b.PushParagraph(text, new(), null, null);
                    }
                    break;
                }
                case DocElementKind.Table:
                {
                    CloseLists();
                    var table = el.Table!;
                    if (!string.IsNullOrEmpty(table.Caption))
                        b.PushParagraph(table.Caption!, new(), null, null);
                    var cells = BuildTableCells(table);
                    if (cells.Count > 0) b.PushTableFromCells(cells, null, null);
                    break;
                }
                case DocElementKind.Drawing:
                {
                    var draw = el.Drawing!;
                    uint idx = drawingIndex++;
                    if (draw.ImageRid is null) break; // textbox shapes etc.
                    CloseLists();
                    var elem = InternalElement.TextElement(ElementKind.Image(idx), draw.Description ?? "", 0);
                    b.PushElement(elem);
                    break;
                }
                case DocElementKind.PageBreak:
                    break;
            }
        }
        CloseLists();

        // Headers / footers
        foreach (var hf in doc.Headers)
        {
            string text = string.Join("\n", hf.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0) { uint idx = b.PushParagraph(text, new(), null, null); b.SetLayer(idx, ContentLayer.Header); }
        }
        foreach (var hf in doc.Footers)
        {
            string text = string.Join("\n", hf.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0) { uint idx = b.PushParagraph(text, new(), null, null); b.SetLayer(idx, ContentLayer.Footer); }
        }

        // Footnotes + endnotes
        foreach (var note in doc.Footnotes.Concat(doc.Endnotes))
        {
            string text = string.Join(" ", note.Paragraphs.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0)
            {
                uint idx = b.PushFootnoteDefinition(text, $"fn{note.Id}", null);
                b.SetLayer(idx, ContentLayer.Footnote);
            }
        }

        return b.Build();
    }

    /// <summary>Build one <see cref="ExtractedImage"/> per drawing (index = image_index) so the
    /// renderers can resolve alt text / source path. Mirrors the Rust extractor's placeholder images.</summary>
    private static void PopulateImages(DocxDocument doc, InternalDocument internalDoc)
    {
        for (int i = 0; i < doc.Drawings.Count; i++)
        {
            var d = doc.Drawings[i];
            string? sourcePath = d.ImageRid is not null && doc.ImageRelationships.TryGetValue(d.ImageRid, out var t) ? t : null;
            string format = sourcePath is not null && sourcePath.Contains('.')
                ? sourcePath[(sourcePath.LastIndexOf('.') + 1)..].ToLowerInvariant()
                : "png";
            internalDoc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i,
                Description = d.Description,
                Format = format,
                SourcePath = sourcePath,
            });
        }
    }

    private static bool IsQuoteStyle(string? style)
    {
        if (style is null) return false;
        var lower = style.ToLowerInvariant();
        return lower is "quote" or "blockquote" or "intenseq" or "intensequote" || lower.Contains("quote");
    }

    /// <summary>Plain text: concatenate non-math run text.</summary>
    private static string CollectText(List<DocxRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.MathLatex is not null) continue;
            sb.Append(run.Text);
        }
        return sb.ToString();
    }

    // ── table cell grid (docx.rs build_internal_document Table arm) ────────────
    private static List<List<string>> BuildTableCells(DocxTable table)
    {
        var cells = new List<List<string>>();
        foreach (var row in table.Rows)
        {
            var rowCells = new List<string>();
            foreach (var cell in row.Cells)
            {
                string text = string.Join(" ", cell.Paragraphs.Select(p => RunsToMarkdown(p.Runs))).Trim();
                for (int s = 0; s < cell.GridSpan; s++) rowCells.Add(text);
            }
            cells.Add(rowCells);
        }
        // Fill vertically merged cells from the row above.
        for (int r = 1; r < table.Rows.Count; r++)
        {
            int col = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                int span = (int)cell.GridSpan;
                if (cell.VMergeContinue)
                    for (int c = col; c < col + span; c++)
                        if (c < cells[r].Count && c < cells[r - 1].Count) cells[r][c] = cells[r - 1][c];
                col += span;
            }
        }
        return cells;
    }

    // ── runs_to_markdown (docx parser) ─────────────────────────────────────────
    private static string RunsToMarkdown(List<DocxRun> runs)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < runs.Count)
        {
            var r = runs[i];
            if (r.MathLatex is not null) { i++; continue; }
            int j = i + 1;
            while (j < runs.Count && runs[j].MathLatex is null && SameGroup(runs[j], r)) j++;
            bool allSameUS = true;
            for (int k = i; k < j; k++)
                if (runs[k].Underline != r.Underline || runs[k].Strike != r.Strike) { allSameUS = false; break; }

            string biOpen = r.Bold && r.Italic ? "***" : r.Bold ? "**" : r.Italic ? "*" : "";
            if (allSameUS)
            {
                var text = new StringBuilder();
                for (int k = i; k < j; k++) text.Append(runs[k].Text);
                if (r.HyperlinkUrl is not null) sb.Append('[');
                if (r.Underline) sb.Append("<u>");
                if (r.Strike) sb.Append("~~");
                sb.Append(biOpen).Append(text).Append(biOpen);
                if (r.Strike) sb.Append("~~");
                if (r.Underline) sb.Append("</u>");
                if (r.HyperlinkUrl is not null) sb.Append("](").Append(r.HyperlinkUrl).Append(')');
            }
            else
            {
                if (r.HyperlinkUrl is not null) sb.Append('[');
                sb.Append(biOpen);
                for (int k = i; k < j; k++)
                {
                    var run = runs[k];
                    if (run.Underline) sb.Append("<u>");
                    if (run.Strike) sb.Append("~~");
                    sb.Append(run.Text);
                    if (run.Strike) sb.Append("~~");
                    if (run.Underline) sb.Append("</u>");
                }
                sb.Append(biOpen);
                if (r.HyperlinkUrl is not null) sb.Append("](").Append(r.HyperlinkUrl).Append(')');
            }
            i = j;
        }
        return sb.ToString();
    }

    private static bool SameGroup(DocxRun a, DocxRun r) =>
        a.Bold == r.Bold && a.Italic == r.Italic && a.HyperlinkUrl == r.HyperlinkUrl;

    // ── metadata ───────────────────────────────────────────────────────────────
    private static Metadata BuildMetadata(OoxmlPackage pkg)
    {
        var core = OfficeMetadata.ExtractCore(pkg);
        var app = OfficeMetadata.ExtractApp(pkg);
        var custom = OfficeMetadata.ExtractCustom(pkg);

        var additional = new Dictionary<string, JsonElement>();
        void AddNum(string key, int? v) { if (v is not null) additional[key] = JsonNumber(v.Value); }
        void AddStr(string key, string? v) { if (v is not null) additional[key] = JsonString(v); }

        AddNum("page_count", app.Pages);
        AddNum("word_count", app.Words);
        AddNum("character_count", app.Characters);
        AddNum("line_count", app.Lines);
        AddNum("paragraph_count", app.Paragraphs);
        AddStr("template", app.Template);
        AddStr("company", app.Company);
        AddNum("total_editing_time_minutes", app.TotalTime);
        AddStr("application", app.Application);
        AddStr("revision", core.Revision);
        AddStr("category", core.Category);
        AddStr("content_status", core.ContentStatus);
        AddStr("description", core.Description);

        var docxMeta = new DocxMetadata
        {
            CoreProperties = CoreToElement(core),
            AppProperties = AppToElement(app),
            CustomProperties = custom.Count > 0 ? custom : new(),
        };

        List<string>? keywords = core.Keywords is { } kw
            ? kw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : null;

        return new Metadata
        {
            Title = core.Title,
            Subject = core.Subject,
            Authors = core.Creator is { } c ? new List<string> { c } : null,
            Keywords = keywords,
            Language = core.Language,
            CreatedAt = core.Created,
            ModifiedAt = core.Modified,
            CreatedBy = core.Creator,
            ModifiedBy = core.LastModifiedBy,
            Format = new FormatMetadata { FormatType = "docx", Payload = docxMeta },
            Additional = additional,
        };
    }

    private static JsonElement CoreToElement(CoreProperties c) => DictToElement(new()
    {
        ["title"] = c.Title, ["subject"] = c.Subject, ["creator"] = c.Creator, ["keywords"] = c.Keywords,
        ["description"] = c.Description, ["last_modified_by"] = c.LastModifiedBy, ["revision"] = c.Revision,
        ["created"] = c.Created, ["modified"] = c.Modified, ["category"] = c.Category,
        ["content_status"] = c.ContentStatus, ["language"] = c.Language, ["identifier"] = c.Identifier,
        ["version"] = c.Version, ["last_printed"] = c.LastPrinted,
    });

    private static JsonElement AppToElement(AppProperties a) => DictToElement(new()
    {
        ["application"] = a.Application, ["app_version"] = a.AppVersion, ["template"] = a.Template,
        ["total_time"] = a.TotalTime, ["pages"] = a.Pages, ["words"] = a.Words, ["characters"] = a.Characters,
        ["characters_with_spaces"] = a.CharactersWithSpaces, ["lines"] = a.Lines, ["paragraphs"] = a.Paragraphs,
        ["company"] = a.Company, ["doc_security"] = a.DocSecurity, ["scale_crop"] = a.ScaleCrop,
        ["links_up_to_date"] = a.LinksUpToDate, ["shared_doc"] = a.SharedDoc, ["hyperlinks_changed"] = a.HyperlinksChanged,
    });

    /// <summary>Serialize a key→value map to a JsonElement, omitting null values (canonicalizer
    /// strips nulls on both sides, so non-null-only is equivalent).</summary>
    private static JsonElement DictToElement(Dictionary<string, object?> map)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var (k, v) in map)
            {
                if (v is null) continue;
                w.WritePropertyName(k);
                switch (v)
                {
                    case string s: w.WriteStringValue(s); break;
                    case int n: w.WriteNumberValue(n); break;
                    case bool bo: w.WriteBooleanValue(bo); break;
                    default: w.WriteStringValue(v.ToString()); break;
                }
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement JsonString(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    private static JsonElement JsonNumber(int n) =>
        JsonDocument.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();
}
