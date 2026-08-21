// Ported from crates/xberg/src/extractors/email.rs (EmailExtractor: build_internal_document,
// process_node, metadata wiring, priority) with MIME parsing in
// crates/xberg/src/extraction/email.rs. Native port — System.Net.Mail is insufficient.
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Email;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Email message extractor. Parses .eml (message/rfc822) MIME messages directly.
///
/// The Rust extractor's <c>supported_mime_types()</c> lists both
/// <c>message/rfc822</c> and <c>application/vnd.ms-outlook</c>, but MSG/PST are handled by a
/// separate extractor in this port; advertising ms-outlook here would collide (equal priority)
/// with that extractor, so only the implemented .eml type is advertised. See the port report.
/// </summary>
public sealed class EmailExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "message/rfc822" };

    // Matches Rust `priority()`.
    public int Priority => 50;

    // Keys already represented in EmailMetadata / flattened metadata — excluded from `additional`.
    private static readonly HashSet<string> EmailStructKeys = new(StringComparer.Ordinal)
    {
        "from_email", "from_name", "to_emails", "cc_emails", "bcc_emails", "message_id",
        "attachments", "subject", "date", "email_from", "email_to", "email_cc", "email_bcc",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        if (content.IsEmpty)
            throw new InvalidOperationException("Email content is empty");

        byte[] data = content.ToArray();

        EmailExtractionResult result = mimeType switch
        {
            "message/rfc822" or "text/plain" => MimeParser.ParseEmlContent(data),
            "application/vnd.ms-outlook" => throw new NotSupportedException(
                "MSG (application/vnd.ms-outlook) extraction is handled by a separate extractor"),
            _ => throw new InvalidOperationException($"Unsupported email MIME type: {mimeType}"),
        };

        // additional = raw metadata minus the struct/flattened keys.
        var additional = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in result.Metadata)
        {
            if (!EmailStructKeys.Contains(key))
                additional[key] = JsonSerializer.SerializeToElement(value);
        }

        InternalDocument doc = BuildInternalDocument(result, config);
        doc.MimeType = mimeType;

        string? subject = result.Subject;
        string? createdAt = result.Date;
        // Rust reads from_name from the metadata map, which the EML path never populates → null.
        string? fromName = result.Metadata.TryGetValue("from_name", out var fn) ? fn : null;

        var emailMetadata = new EmailMetadata
        {
            FromEmail = result.FromEmail,
            FromName = fromName,
            ToEmails = result.ToEmails,
            CcEmails = result.CcEmails,
            BccEmails = result.BccEmails,
            MessageId = result.MessageId,
            Attachments = result.Attachments
                .Select(a => a.Filename ?? a.Name)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList(),
        };

        // Map from_name to the standard authors field.
        List<string>? authors = !string.IsNullOrEmpty(fromName) ? new List<string> { fromName! } : null;

        doc.Metadata = new Metadata
        {
            Format = new FormatMetadata { FormatType = "email", Payload = emailMetadata },
            Subject = subject,
            Authors = authors,
            CreatedAt = createdAt,
            Additional = additional,
        };

        // SecurityBudget check intentionally skipped per port scope.
        return doc;
    }

    /// <summary>Port of Rust `EmailExtractor::build_internal_document`.</summary>
    private static InternalDocument BuildInternalDocument(EmailExtractionResult result, ExtractionConfig config)
    {
        var builder = new InternalDocumentBuilder("email");

        // Email headers → metadata block.
        // Subject/From/To/CC/BCC/Date plus, when present, Reply-To, Message-ID, In-Reply-To,
        // References, List-Id and List-Unsubscribe. Headers with no value are omitted rather
        // than rendered as empty lines. Order matches Rust `build_email_text_output`.
        var headerEntries = new List<(string, string)>();
        void AddMeta(string label, string key)
        {
            if (result.Metadata.TryGetValue(key, out var v)) headerEntries.Add((label, v));
        }

        if (result.Subject is { } subject) headerEntries.Add(("Subject", subject));
        if (FormatSender(result) is { } from) headerEntries.Add(("From", from));
        if (result.ToEmails.Count > 0) headerEntries.Add(("To", string.Join(", ", result.ToEmails)));
        if (result.CcEmails.Count > 0) headerEntries.Add(("CC", string.Join(", ", result.CcEmails)));
        if (result.BccEmails.Count > 0) headerEntries.Add(("BCC", string.Join(", ", result.BccEmails)));
        AddMeta("Reply-To", "reply_to");
        if (result.Date is { } date) headerEntries.Add(("Date", date));
        if (result.MessageId is { } messageId) headerEntries.Add(("Message-ID", messageId));
        AddMeta("In-Reply-To", "in_reply_to");
        AddMeta("References", "references");
        AddMeta("List-Id", "list_id");
        AddMeta("List-Unsubscribe", "list_unsubscribe");
        if (headerEntries.Count > 0) builder.PushMetadataBlock(headerEntries, null);

        // Body: walk HTML structure if available, else split plain content into paragraphs.
        if (result.HtmlContent is { } html)
        {
            DocumentStructure htmlDoc = HtmlStructure.Build(html);
            for (int idx = 0; idx < htmlDoc.Nodes.Count; idx++)
            {
                if (htmlDoc.Nodes[idx].Parent is null)
                    ProcessNode(htmlDoc, idx, builder);
            }
        }
        else
        {
            // RFC 5322 mandates CRLF line endings and the transfer decoders hand back the body
            // verbatim, so without normalizing first "\r\n\r\n" never matches the paragraph
            // boundary and every plain-text email collapses into one paragraph (Rust GH#316).
            foreach (string paragraph in TextTransform.NormalizeLineEndings(result.Content).Split("\n\n"))
            {
                string trimmed = paragraph.Trim();
                if (trimmed.Length > 0)
                    builder.PushParagraph(trimmed, new(), null, null);
            }
        }

        // Attachments section.
        if (result.Attachments.Count > 0)
        {
            builder.PushParagraph("Attachments:", new(), null, null);
            foreach (var att in result.Attachments)
            {
                string name = att.Filename ?? att.Name ?? "unnamed";
                int size = att.Size ?? 0;
                builder.PushParagraph($"  {name} ({size}B)", new(), null, null);
            }
        }

        AttachmentInlining.Append(builder, result.Attachments, config);

        return builder.Build();
    }

    /// <summary>
    /// The sender as a header line: both halves of the mailbox when it has a display name, but
    /// never <c>address &lt;address&gt;</c> when the "name" is just the address repeated.
    /// </summary>
    internal static string? FormatSender(EmailExtractionResult result)
    {
        string? name = result.Metadata.TryGetValue("from_name", out var n) && n.Trim().Length != 0 ? n : null;
        string? address = result.FromEmail;

        if (name is not null && address is not null && !name.Equals(address, StringComparison.OrdinalIgnoreCase))
            return $"{name} <{address}>";
        return address ?? name;
    }

    /// <summary>Port of Rust `process_node` — flatten a DocumentStructure node into the builder.</summary>
    private static void ProcessNode(DocumentStructure doc, int nodeIdx, InternalDocumentBuilder builder)
    {
        if (nodeIdx < 0 || nodeIdx >= doc.Nodes.Count) return;
        DocumentNode node = doc.Nodes[nodeIdx];
        NodeContent c = node.Content;

        switch (c.Which)
        {
            case NodeContent.Tag.Paragraph:
                {
                    string trimmed = (c.Text ?? "").Trim();
                    if (trimmed.Length > 0)
                        builder.PushParagraph(trimmed, node.Annotations, null, null);
                    break;
                }
            case NodeContent.Tag.Heading:
                builder.PushHeading(c.Level, c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.Title:
                builder.PushTitle(c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.List:
                builder.PushList(c.Ordered);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.EndList();
                break;
            case NodeContent.Tag.ListItem:
                {
                    bool ordered = false;
                    if (node.Parent is uint p && p < doc.Nodes.Count)
                    {
                        var parent = doc.Nodes[(int)p].Content;
                        if (parent.Which == NodeContent.Tag.List) ordered = parent.Ordered;
                    }
                    builder.PushListItem(c.Text ?? "", ordered, node.Annotations, null, null);
                    foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                    break;
                }
            case NodeContent.Tag.Table:
                {
                    var grid = c.Grid ?? new TableGrid();
                    int rowsN = (int)grid.Rows;
                    int colsN = (int)grid.Cols;
                    var rows = new List<List<string>>(rowsN);
                    for (int r = 0; r < rowsN; r++)
                    {
                        var row = new List<string>(colsN);
                        for (int col = 0; col < colsN; col++) row.Add("");
                        rows.Add(row);
                    }
                    foreach (var cell in grid.Cells)
                    {
                        if (cell.Row < rowsN && colsN > 0 && cell.Col < colsN)
                            rows[(int)cell.Row][(int)cell.Col] = cell.Content;
                    }
                    builder.PushTableFromCells(rows, null, null);
                    break;
                }
            case NodeContent.Tag.Code:
                builder.PushCode(c.Text ?? "", c.Language, null, null);
                break;
            case NodeContent.Tag.Formula:
                builder.PushFormula(c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.MetadataBlock:
                {
                    var entries = (c.Entries ?? new())
                        .Select(e => (e.Length > 0 ? e[0] : "", e.Length > 1 ? e[1] : ""))
                        .ToList();
                    builder.PushMetadataBlock(entries, null);
                    break;
                }
            case NodeContent.Tag.Quote:
                builder.PushQuoteStart();
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.PushQuoteEnd();
                break;
            case NodeContent.Tag.Group:
                builder.PushGroupStart(c.Label, null);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.PushGroupEnd();
                break;
            case NodeContent.Tag.Admonition:
                builder.PushAdmonition(c.Kind ?? "note", c.SlideTitle, null);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                break;
            case NodeContent.Tag.Image:
                {
                    // Only a *missing* description becomes the placeholder. `alt=""` is a
                    // deliberate "this image carries no meaning", and upstream's
                    // `description.as_deref().unwrap_or("[Image]")` keeps it as the empty string.
                    string text = c.Description ?? "[Image]";
                    builder.PushParagraph(text, new(), null, null);
                    break;
                }
            default:
                {
                    string? text = NodeText(c);
                    if (text is not null)
                    {
                        string trimmed = text.Trim();
                        if (trimmed.Length > 0)
                            builder.PushParagraph(trimmed, node.Annotations, null, null);
                    }
                    break;
                }
        }
    }

    /// <summary>Port of Rust `NodeContent::text()` — the fallback used by process_node.</summary>
    private static string? NodeText(NodeContent c) => c.Which switch
    {
        NodeContent.Tag.Title => c.Text,
        NodeContent.Tag.Heading => c.Text,
        NodeContent.Tag.Paragraph => c.Text,
        NodeContent.Tag.ListItem => c.Text,
        NodeContent.Tag.Code => c.Text,
        NodeContent.Tag.Formula => c.Text,
        NodeContent.Tag.Footnote => c.Text,
        NodeContent.Tag.Citation => c.Text,
        NodeContent.Tag.RawBlock => c.RawContent,
        NodeContent.Tag.DefinitionItem => c.Term,
        _ => null,
    };
}
