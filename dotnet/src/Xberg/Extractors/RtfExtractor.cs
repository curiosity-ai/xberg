// Ported from crates/xberg/src/extractors/rtf/mod.rs
// Native RTF (Rich Text Format) extractor. Builds an InternalDocument from RTF content:
// paragraphs, headings, lists, tables, images, hyperlinks, footnotes, formatting
// annotations, and header/footer content, plus \info metadata.

using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Rtf;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Native RTF extractor. Ports Rust `RtfExtractor`.</summary>
public sealed class RtfExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/rtf", "text/rtf" };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        // Rust uses String::from_utf8_lossy; UTF8.GetString applies replacement fallback.
        string rtfContent = Encoding.UTF8.GetString(content);
        const bool plain = true; // InternalDocument doesn't need markdown formatting

        var textResult = RtfParser.ExtractTextFromRtf(rtfContent, plain);
        var metadataMap = RtfMetadata.ExtractRtfMetadata(rtfContent, textResult.Text);

        string? title = TakeStr(metadataMap, "title");
        string? subject = TakeStr(metadataMap, "subject");
        List<string>? authors = TakeList(metadataMap, "authors");
        string? createdBy = TakeStr(metadataMap, "created_by");
        string? modifiedBy = TakeStr(metadataMap, "modified_by");
        string? createdAt = TakeStr(metadataMap, "created_at");
        string? modifiedAt = TakeStr(metadataMap, "modified_at");

        var doc = BuildInternalDocument(rtfContent, plain);

        doc.MimeType = mimeType;
        doc.Metadata = new Metadata
        {
            Title = title,
            Subject = subject,
            Authors = authors,
            CreatedBy = createdBy,
            ModifiedBy = modifiedBy,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt,
            Additional = ToAdditional(metadataMap),
        };
        return doc;
    }

    private static InternalDocument BuildInternalDocument(string rtfContent, bool plain)
    {
        var tr = RtfParser.ExtractTextFromRtf(rtfContent, plain);
        string extractedText = tr.Text;
        var tables = tr.Tables;
        var rtfImages = tr.Images;
        var paraMetas = tr.ParaMetas;
        var formatting = tr.Formatting;

        // Headers/footers come from the separate formatting pass.
        var legacyFormatting = RtfParser.ExtractRtfFormatting(rtfContent);
        formatting.HeaderText = legacyFormatting.HeaderText;
        formatting.FooterText = legacyFormatting.FooterText;

        var builder = new InternalDocumentBuilder("rtf");

        byte[] textBytes = Encoding.UTF8.GetBytes(extractedText);

        // Extract URIs from hyperlinks found during RTF parsing.
        foreach (var (start, end, url) in formatting.Hyperlinks)
        {
            if (url.Length == 0) continue;
            string? label = SliceBytes(textBytes, start, end);
            builder.PushUri(new ExtractedUri { Url = url, Label = label, Kind = UriKind.Hyperlink });
        }

        int tableIdx = 0;
        int metaIdx = 0;
        int byteOffset = 0;
        bool inTableRows = false;

        bool inList = false;
        ushort? listId = null;
        byte listDepth = 0;

        void CloseList()
        {
            if (!inList) return;
            for (int i = 0; i <= listDepth; i++) builder.EndList();
            inList = false;
            listId = null;
            listDepth = 0;
        }

        foreach (var paragraph in SplitDoubleNewline(extractedText))
        {
            int paraLen = ByteLen(paragraph);
            string trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
            {
                byteOffset += paraLen + 2;
                metaIdx += 1;
                continue;
            }

            ParagraphMeta meta = metaIdx < paraMetas.Count ? paraMetas[metaIdx] : new ParagraphMeta();
            metaIdx += 1;

            if (meta.IsTable)
            {
                CloseList();
                inTableRows = true;
                byteOffset += paraLen + 2;
                continue;
            }

            if (inTableRows)
            {
                inTableRows = false;
                if (tableIdx < tables.Count)
                {
                    builder.PushTableFromCells(tables[tableIdx].Cells, null, null);
                    tableIdx += 1;
                }
            }

            var lines = trimmed.Split('\n');
            bool isTableLike = lines.Length >= 2 && lines.All(l => l.Contains('|'));
            if (isTableLike && tableIdx < tables.Count)
            {
                CloseList();
                builder.PushTableFromCells(tables[tableIdx].Cells, null, null);
                tableIdx += 1;
                byteOffset += paraLen + 2;
                continue;
            }

            int trimOffset = byteOffset + (paraLen - ByteLen(paragraph.TrimStart()));
            var annotations = RtfParser.SpansToAnnotations(trimOffset, trimOffset + ByteLen(trimmed), formatting);

            if (meta.HeadingLevel > 0 && meta.HeadingLevel <= 6)
            {
                CloseList();
                builder.PushHeading(meta.HeadingLevel, trimmed, null, null);
            }
            else if (meta.ListLevel is byte level)
            {
                ushort? newListId = meta.ListId;
                bool ordered = meta.Ordered;

                if (!inList || listId != newListId)
                {
                    if (inList)
                    {
                        for (int i = 0; i <= listDepth; i++) builder.EndList();
                    }
                    builder.PushList(ordered);
                    for (int i = 0; i < level; i++) builder.PushList(ordered);
                    inList = true;
                    listId = newListId;
                    listDepth = level;
                }
                else if (level > listDepth)
                {
                    for (int i = listDepth; i < level; i++) builder.PushList(ordered);
                    listDepth = level;
                }
                else if (level < listDepth)
                {
                    for (int i = level; i < listDepth; i++) builder.EndList();
                    listDepth = level;
                }

                builder.PushListItem(trimmed, ordered, annotations, null, null);
            }
            else
            {
                CloseList();
                builder.PushParagraph(trimmed, annotations, null, null);
            }

            byteOffset += paraLen + 2;
        }

        CloseList();

        if (inTableRows && tableIdx < tables.Count)
        {
            builder.PushTableFromCells(tables[tableIdx].Cells, null, null);
            tableIdx += 1;
        }

        while (tableIdx < tables.Count)
        {
            builder.PushTableFromCells(tables[tableIdx].Cells, null, null);
            tableIdx += 1;
        }

        for (int i = 0; i < rtfImages.Count; i++)
        {
            var rtfImg = rtfImages[i];
            var image = new ExtractedImage
            {
                Data = rtfImg.Data,
                Format = rtfImg.Format,
                ImageIndex = (uint)i,
                IsMask = false,
                Description = "image",
                // image_kind::classify is skipped: ImageKind / KindConfidence left null.
            };
            builder.PushImage(null, image, null, null);
        }

        if (formatting.HeaderText is string header)
        {
            uint idx = builder.PushParagraph(header, new(), null, null);
            builder.SetLayer(idx, ContentLayer.Header);
        }
        if (formatting.FooterText is string footer)
        {
            uint idx = builder.PushParagraph(footer, new(), null, null);
            builder.SetLayer(idx, ContentLayer.Footer);
        }

        return builder.Build();
    }

    // --- metadata map helpers ---

    private static string? TakeStr(Dictionary<string, RtfMetaValue> map, string key)
    {
        if (map.TryGetValue(key, out var v) && v.Kind == RtfMetaValue.ValueKind.Str)
        {
            map.Remove(key);
            return v.Str;
        }
        map.Remove(key);
        return null;
    }

    private static List<string>? TakeList(Dictionary<string, RtfMetaValue> map, string key)
    {
        if (map.TryGetValue(key, out var v) && v.Kind == RtfMetaValue.ValueKind.StrList)
        {
            map.Remove(key);
            return v.List;
        }
        map.Remove(key);
        return null;
    }

    private static Dictionary<string, JsonElement> ToAdditional(Dictionary<string, RtfMetaValue> map)
    {
        var additional = new Dictionary<string, JsonElement>();
        foreach (var (key, v) in map)
        {
            additional[key] = v.Kind switch
            {
                RtfMetaValue.ValueKind.Num => JsonSerializer.SerializeToElement(v.Num),
                RtfMetaValue.ValueKind.StrList => JsonSerializer.SerializeToElement(v.List),
                _ => JsonSerializer.SerializeToElement(v.Str),
            };
        }
        return additional;
    }

    // --- text helpers ---

    private static int ByteLen(string s) => Encoding.UTF8.GetByteCount(s);

    private static string? SliceBytes(byte[] bytes, int start, int end)
    {
        if (start < 0 || end < start || end > bytes.Length) return null;
        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    private static IEnumerable<string> SplitDoubleNewline(string text)
    {
        int start = 0;
        int idx;
        while ((idx = text.IndexOf("\n\n", start, StringComparison.Ordinal)) >= 0)
        {
            yield return text.Substring(start, idx - start);
            start = idx + 2;
        }
        yield return text.Substring(start);
    }
}
