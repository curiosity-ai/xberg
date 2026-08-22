// Ported from crates/xberg/src/extractors/pst.rs.
//
// Turns an Outlook personal-folders archive into one document: the messages of every folder,
// flattened into paragraphs under a heading per folder, with the message count carried in both
// the format metadata and the additional metadata bag.

using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Email;
using Xberg.Internal.Pst;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Outlook personal folders (.pst) extractor. Reads the store's folder tree and emits each
/// message the way the .eml and .msg paths render one — a header block followed by the body —
/// preceded by a heading naming the folder the messages came from.
/// </summary>
public sealed class PstExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.ms-outlook-pst" };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var (messages, warnings) = PstExtraction.ExtractMessages(content);

        var doc = new InternalDocument("pst") { MimeType = mimeType };
        PushMessagesAsElements(doc, messages);

        var first = messages.Count > 0 ? messages[0] : null;
        doc.Metadata = new Metadata
        {
            Format = new FormatMetadata { FormatType = "pst", Payload = new PstMetadata { MessageCount = messages.Count } },
            Subject = first?.Subject,
            CreatedAt = first?.Date,
        };
        doc.Metadata.Additional["message_count"] = JsonSerializer.SerializeToElement(messages.Count);

        foreach (var warning in warnings) doc.ProcessingWarnings.Add(warning);
        return doc;
    }

    /// <summary>
    /// Flatten messages into paragraphs, emitting a heading whenever the folder path changes from
    /// the previous message, so the folder hierarchy stays visible instead of collapsing into one
    /// unlabelled run of paragraphs.
    /// </summary>
    private static void PushMessagesAsElements(InternalDocument doc, List<EmailExtractionResult> messages)
    {
        string? lastFolderPath = null;

        foreach (var message in messages)
        {
            if (message.Metadata.TryGetValue("folder_path", out var folderPath) && lastFolderPath != folderPath)
            {
                doc.PushElement(InternalElement.TextElement(ElementKind.Heading(1), folderPath, 0));
                lastFolderPath = folderPath;
            }

            // The rendered text embeds the body verbatim, and for PST that body is PR_BODY straight
            // out of the message store, where Outlook writes CRLF. Normalize before splitting or
            // the whole body collapses into a single paragraph.
            string text = TextTransform.NormalizeLineEndings(BuildEmailTextOutput(message));
            if (text.Length == 0) continue;

            foreach (string paragraph in text.Split("\n\n"))
            {
                string trimmed = paragraph.Trim();
                if (trimmed.Length > 0)
                    doc.PushElement(InternalElement.TextElement(ElementKind.Paragraph, trimmed, 0));
            }
        }
    }

    /// <summary>
    /// The plain-text rendering of one message: the same header lines the .eml path emits, in the
    /// same order, then the body. Mirrors Rust <c>build_email_text_output</c>.
    /// </summary>
    private static string BuildEmailTextOutput(EmailExtractionResult result)
    {
        var parts = new List<string>(16);

        void AddMeta(string label, string key)
        {
            if (result.Metadata.TryGetValue(key, out var value)) parts.Add($"{label}: {value}");
        }

        if (result.Subject is { } subject) parts.Add($"Subject: {subject}");
        if (result.FromEmail is { } from) parts.Add($"From: {from}");
        if (result.ToEmails.Count > 0) parts.Add($"To: {string.Join(", ", result.ToEmails)}");
        if (result.CcEmails.Count > 0) parts.Add($"CC: {string.Join(", ", result.CcEmails)}");
        if (result.BccEmails.Count > 0) parts.Add($"BCC: {string.Join(", ", result.BccEmails)}");
        AddMeta("Reply-To", "reply_to");
        if (result.Date is { } date) parts.Add($"Date: {date}");
        if (result.MessageId is { } messageId) parts.Add($"Message-ID: {messageId}");
        AddMeta("In-Reply-To", "in_reply_to");
        AddMeta("References", "references");
        AddMeta("List-Id", "list_id");
        AddMeta("List-Unsubscribe", "list_unsubscribe");
        parts.Add(result.Content);

        return string.Join("\n", parts);
    }
}
