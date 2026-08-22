// Ported from crates/xberg/src/extraction/email.rs (EmailExtractionResult / EmailAttachment
// data model and the `mail_parser::Message` accessors used by parse_eml_content).
// Native MIME parser — System.Net.Mail is insufficient for this.
namespace Xberg.Internal.Email;

/// <summary>A single MIME part (leaf or multipart container) in the parsed tree.</summary>
internal sealed class MimePart
{
    /// <summary>Unfolded headers in order (name kept verbatim, value unfolded).</summary>
    public List<(string Name, string Value)> Headers { get; } = new();

    /// <summary>Lower-cased "type/subtype" (defaults to "text/plain" when absent).</summary>
    public string ContentType { get; set; } = "text/plain";
    public string ContentTypeMain => ContentType.Split('/')[0];
    public string ContentSubtype
    {
        get
        {
            int i = ContentType.IndexOf('/');
            return i >= 0 ? ContentType[(i + 1)..] : "";
        }
    }

    public string? Boundary { get; set; }
    public string? Charset { get; set; }
    public string? ContentTypeName { get; set; }
    public string? TransferEncoding { get; set; }
    public string? Disposition { get; set; }
    public string? DispositionFilename { get; set; }

    public bool IsMultipart => ContentType.StartsWith("multipart/", StringComparison.Ordinal) && Boundary is not null;

    /// <summary>Child parts (multipart only).</summary>
    public List<MimePart> Children { get; } = new();

    /// <summary>Raw body of a leaf, preserved as a Latin-1 string (each char == one byte).</summary>
    public string BodyLatin1 { get; set; } = "";

    /// <summary>
    /// The part sits inside a multipart whose boundary never closes it, so its body ran to the
    /// end of the message. The transfer decoder gives up on such a part and the raw bytes are
    /// kept instead, which is also what demotes the part out of the message body.
    /// </summary>
    public bool IsEncodingProblem { get; set; }

    public string? GetHeader(string name)
    {
        foreach (var (n, v) in Headers)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>The best attachment filename: Content-Disposition filename, then Content-Type name.</summary>
    public string? AttachmentName => DispositionFilename ?? ContentTypeName;

    /// <summary>Transfer-decoded raw bytes of this leaf part (raw when the decode gave up).</summary>
    public byte[] DecodedBytes() =>
        ContentTransferDecoder.Decode(IsEncodingProblem ? null : TransferEncoding, BodyLatin1);

    /// <summary>Transfer- + charset-decoded text of this leaf part.</summary>
    public string DecodedText() => CharsetDecoder.Decode(Charset, DecodedBytes());
}

/// <summary>Attachment metadata + data. Mirrors Rust `EmailAttachment`.</summary>
internal sealed class EmailAttachment
{
    public string? Name { get; set; }
    public string? Filename { get; set; }
    public string? MimeType { get; set; }
    public int? Size { get; set; }
    public bool IsImage { get; set; }
    public byte[]? Data { get; set; }
}

/// <summary>Result of parsing an .eml message. Mirrors Rust `EmailExtractionResult`.</summary>
internal sealed class EmailExtractionResult
{
    public string? Subject { get; set; }
    public string? FromEmail { get; set; }
    public List<string> ToEmails { get; set; } = new();
    public List<string> CcEmails { get; set; } = new();
    public List<string> BccEmails { get; set; } = new();
    public string? Date { get; set; }
    public string? MessageId { get; set; }
    public string? PlainText { get; set; }
    public string? HtmlContent { get; set; }
    public string Content { get; set; } = "";
    public List<EmailAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// Messages attached as Message objects rather than as bytes, fully parsed, flattened across
    /// every nesting depth. An `.msg` may carry another `.msg` inside it, and such an attachment
    /// has no binary stream to extract — only a storage to descend into.
    /// </summary>
    public List<EmailExtractionResult> NestedEmbeddedMessages { get; set; } = new();

    /// <summary>Ordered raw string metadata (matches the Rust HashMap contents; order irrelevant).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}
