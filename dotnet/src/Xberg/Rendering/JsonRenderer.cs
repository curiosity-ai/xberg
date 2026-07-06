using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xberg.Types;

namespace Xberg.Rendering;

/// <summary>
/// JSON tree renderer for <see cref="InternalDocument"/>. Produces a heading-driven section
/// tree serialized as <c>{"type":"section"|"paragraph"|...}</c>. Ported verbatim from `rendering/json.rs`.
/// Uses relaxed escaping so the emitted string matches serde_json (which does not escape &lt; &gt; &amp; /).
/// </summary>
public static class JsonRenderer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private sealed class OpenSection
    {
        public string Heading = "";
        public byte Level;
        public JsonArray Body = new();
    }

    private sealed class OpenList
    {
        public bool Ordered;
        public JsonArray Items = new();
    }

    public static string Render(InternalDocument doc)
    {
        var jsonDoc = BuildJsonDocument(doc);
        return jsonDoc.ToJsonString(WriteOptions);
    }

    private static JsonObject BuildJsonDocument(InternalDocument doc)
    {
        string? title = null;
        var sectionStack = new List<OpenSection>();
        var rootBody = new JsonArray();
        var state = new RenderState();
        OpenList? openList = null;
        JsonArray? openBlockquote = null;

        foreach (var elem in doc.Elements)
        {
            if (!RenderCommon.IsBodyElement(elem)) continue;

            if (RenderCommon.IsContainerEnd(elem))
            {
                if (elem.Kind.Tag == ElementKindTag.ListEnd)
                {
                    if (openList is not null)
                    {
                        PushToCurrent(rootBody, sectionStack, ref openBlockquote, ListNode(openList));
                        openList = null;
                    }
                }
                else if (elem.Kind.Tag == ElementKindTag.QuoteEnd)
                {
                    if (openBlockquote is not null)
                    {
                        var bq = openBlockquote;
                        openBlockquote = null;
                        JsonArray? none = null;
                        PushToCurrent(rootBody, sectionStack, ref none, BlockquoteNode(bq));
                    }
                }
                RenderCommon.HandleContainerEnd(elem.Kind, state);
                continue;
            }

            switch (elem.Kind.Tag)
            {
                case ElementKindTag.Title:
                    if (title is null && elem.Text.Length > 0) title = elem.Text;
                    break;

                case ElementKindTag.Heading:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    CloseSectionsToLevel(sectionStack, rootBody, elem.Kind.Level);
                    sectionStack.Add(new OpenSection { Heading = elem.Text, Level = elem.Kind.Level, Body = new() });
                    break;

                case ElementKindTag.Paragraph:
                    if (elem.Text.Length == 0) continue;
                    PushToCurrent(rootBody, sectionStack, ref openBlockquote, ParagraphNode(elem.Text));
                    break;

                case ElementKindTag.ListStart:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    state.PushContainer(NestingKind.ListKind(elem.Kind.Ordered, 0), elem.Depth);
                    openList = new OpenList { Ordered = elem.Kind.Ordered };
                    break;

                case ElementKindTag.ListItem:
                    if (openList is not null)
                        openList.Items.Add(elem.Text);
                    else
                    {
                        var single = new OpenList { Ordered = elem.Kind.Ordered };
                        single.Items.Add(elem.Text);
                        PushToCurrent(rootBody, sectionStack, ref openBlockquote, ListNode(single));
                    }
                    break;

                case ElementKindTag.Code:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    PushToCurrent(rootBody, sectionStack, ref openBlockquote, CodeNode(elem.Text, RenderCommon.GetLanguage(elem)));
                    break;

                case ElementKindTag.Formula:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    PushToCurrent(rootBody, sectionStack, ref openBlockquote, FormulaNode(elem.Text));
                    break;

                case ElementKindTag.Table:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    {
                        int ti = (int)elem.Kind.TableIndex;
                        if (ti < doc.Tables.Count)
                            PushToCurrent(rootBody, sectionStack, ref openBlockquote, TableNode(doc.Tables[ti]));
                    }
                    break;

                case ElementKindTag.Image:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    {
                        int ii = (int)elem.Kind.ImageIndex;
                        ExtractedImage? image = ii < doc.Images.Count ? doc.Images[ii] : null;
                        string? alt = image?.Description;
                        string? src = null;
                        if (image is not null)
                        {
                            src = image.Data.Length > 0
                                ? $"image_{elem.Kind.ImageIndex}.{image.Format}"
                                : image.SourcePath;
                        }
                        PushToCurrent(rootBody, sectionStack, ref openBlockquote, ImageNode(alt, src));
                    }
                    break;

                case ElementKindTag.QuoteStart:
                    FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);
                    state.PushContainer(NestingKind.BlockQuote, elem.Depth);
                    openBlockquote = new JsonArray();
                    break;

                case ElementKindTag.OcrText:
                    if (elem.Text.Length > 0)
                        PushToCurrent(rootBody, sectionStack, ref openBlockquote, ParagraphNode(elem.Text));
                    break;

                // Everything else skipped.
            }
        }

        FlushList(ref openList, rootBody, sectionStack, ref openBlockquote);

        if (openBlockquote is not null)
        {
            var bq = openBlockquote;
            openBlockquote = null;
            JsonArray? none = null;
            PushToCurrent(rootBody, sectionStack, ref none, BlockquoteNode(bq));
        }

        CloseSectionsToLevel(sectionStack, rootBody, 0);

        var result = new JsonObject();
        if (title is not null) result["title"] = title;
        result["body"] = rootBody;
        return result;
    }

    private static void PushToCurrent(JsonArray rootBody, List<OpenSection> sectionStack,
        ref JsonArray? openBlockquote, JsonNode node)
    {
        if (openBlockquote is not null)
        {
            openBlockquote.Add(node);
            return;
        }
        if (sectionStack.Count > 0)
            sectionStack[^1].Body.Add(node);
        else
            rootBody.Add(node);
    }

    private static void CloseSectionsToLevel(List<OpenSection> sectionStack, JsonArray rootBody, byte targetLevel)
    {
        while (sectionStack.Count > 0)
        {
            var top = sectionStack[^1];
            if (top.Level >= targetLevel)
            {
                sectionStack.RemoveAt(sectionStack.Count - 1);
                var node = new JsonObject
                {
                    ["type"] = "section",
                    ["heading"] = top.Heading,
                    ["level"] = top.Level,
                    ["body"] = top.Body,
                };
                if (sectionStack.Count > 0)
                    sectionStack[^1].Body.Add(node);
                else
                    rootBody.Add(node);
            }
            else break;
        }
    }

    private static void FlushList(ref OpenList? openList, JsonArray rootBody,
        List<OpenSection> sectionStack, ref JsonArray? openBlockquote)
    {
        if (openList is not null)
        {
            var node = ListNode(openList);
            openList = null;
            PushToCurrent(rootBody, sectionStack, ref openBlockquote, node);
        }
    }

    private static JsonObject ParagraphNode(string text) => new() { ["type"] = "paragraph", ["text"] = text };

    private static JsonObject FormulaNode(string text) => new() { ["type"] = "formula", ["text"] = text };

    private static JsonObject CodeNode(string text, string? language)
    {
        var node = new JsonObject { ["type"] = "code", ["text"] = text };
        if (language is not null) node["language"] = language;
        return node;
    }

    private static JsonObject ListNode(OpenList list) => new()
    {
        ["type"] = "list",
        ["ordered"] = list.Ordered,
        ["items"] = list.Items,
    };

    private static JsonObject BlockquoteNode(JsonArray body) => new() { ["type"] = "blockquote", ["body"] = body };

    private static JsonObject ImageNode(string? alt, string? src)
    {
        var node = new JsonObject { ["type"] = "image" };
        if (alt is not null) node["alt"] = alt;
        if (src is not null) node["src"] = src;
        return node;
    }

    private static JsonObject TableNode(Table table)
    {
        var headers = new JsonArray();
        var rows = new JsonArray();
        if (table.Cells.Count > 0)
        {
            foreach (var h in table.Cells[0]) headers.Add(h);
            for (int r = 1; r < table.Cells.Count; r++)
            {
                var rowArr = new JsonArray();
                foreach (var c in table.Cells[r]) rowArr.Add(c);
                rows.Add(rowArr);
            }
        }
        // caption is always None in the port.
        return new JsonObject { ["type"] = "table", ["headers"] = headers, ["rows"] = rows };
    }
}
