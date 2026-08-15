using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the .eml path: sender formatting and the inlining of attachment text.
/// </summary>
public class EmailExtractorTests
{
    private const string EmlMime = "message/rfc822";

    private static string Extract(string eml, OutputFormat fmt)
    {
        var doc = new EmailExtractor().Extract(
            Encoding.UTF8.GetBytes(eml), EmlMime, new ExtractionConfig { OutputFormat = fmt });
        return Derive.DeriveExtractionResult(doc, includeDocumentStructure: false, fmt).Content;
    }

    /// <summary>A multipart message with one base64 attachment of the given type.</summary>
    private static string WithAttachment(string filename, string contentType, string base64Body) =>
        "From: Ada Lovelace <ada@example.com>\r\n" +
        "To: bob@example.com\r\n" +
        "Subject: See attached\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"BOUND\"\r\n" +
        "\r\n" +
        "--BOUND\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "Covering note.\r\n" +
        "\r\n" +
        "--BOUND\r\n" +
        $"Content-Type: {contentType}; name=\"{filename}\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n" +
        $"Content-Disposition: attachment; filename=\"{filename}\"\r\n" +
        "\r\n" +
        base64Body + "\r\n" +
        "--BOUND--\r\n";

    /// <summary>
    /// Naming an attachment says nothing about what it holds. For a message whose body is a
    /// covering note, the attachment is the document.
    /// </summary>
    [Fact]
    public void AttachmentTextIsInlinedUnderAHeadingNamingIt()
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes("The quarterly numbers are attached."));
        string plain = Extract(WithAttachment("report.txt", "text/plain", body), OutputFormat.Plain);

        Assert.Contains("Covering note.", plain);
        Assert.Contains("report.txt", plain);
        Assert.Contains("The quarterly numbers are attached.", plain);
    }

    /// <summary>The heading is a real level-2 heading, not a paragraph that looks like one.</summary>
    [Fact]
    public void InlinedAttachmentHeadingIsAtLevelTwo()
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Inner text."));
        string markdown = Extract(WithAttachment("notes.txt", "text/plain", body), OutputFormat.Markdown);

        Assert.Contains("## notes.txt", markdown);
        Assert.Contains("Inner text.", markdown);
    }

    /// <summary>
    /// An image carries no text to inline. Under a structured output format an empty document
    /// still renders as a non-empty envelope, so emptiness cannot be judged on the rendered
    /// string alone.
    /// </summary>
    [Fact]
    public void ImageAttachmentsAreListedButNotInlined()
    {
        // A one-pixel PNG: real enough to be detected by content, and carrying no text.
        string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        string json = Extract(WithAttachment("pixel.png", "image/png", png), OutputFormat.Json);

        // Listed in the attachments section...
        Assert.Contains("pixel.png", json);
        // ...but contributing no section of its own, and never an empty document envelope.
        Assert.DoesNotContain("\"heading\":\"pixel.png\"", json);
        Assert.DoesNotContain("{\\\"body\\\":[{\\\"type\\\":\\\"image\\\"}]}", json);
    }

    [Fact]
    public void SenderKeepsBothHalvesOfTheMailbox()
    {
        string plain = Extract(
            "From: Michael Elkins <elkins@aero.org>\r\nSubject: Hi\r\n\r\nBody.\r\n", OutputFormat.Plain);
        Assert.Contains("From: Michael Elkins <elkins@aero.org>", plain);
    }

    [Fact]
    public void SenderWithoutADisplayNameIsJustTheAddress()
    {
        string plain = Extract("From: elkins@aero.org\r\nSubject: Hi\r\n\r\nBody.\r\n", OutputFormat.Plain);
        Assert.Contains("From: elkins@aero.org", plain);
        Assert.DoesNotContain("<elkins@aero.org>", plain);
    }

    /// <summary>A name that merely repeats the address must not become `address &lt;address&gt;`.</summary>
    [Fact]
    public void SenderDoesNotRepeatTheAddressAsItsOwnName()
    {
        string plain = Extract(
            "From: elkins@aero.org <elkins@aero.org>\r\nSubject: Hi\r\n\r\nBody.\r\n", OutputFormat.Plain);
        Assert.Contains("From: elkins@aero.org", plain);
        Assert.DoesNotContain("elkins@aero.org <elkins@aero.org>", plain);
    }

    /// <summary>An encoded-word display name is decoded before it reaches the header line.</summary>
    [Fact]
    public void SenderDisplayNameIsDecodedFromItsEncodedWord()
    {
        string plain = Extract(
            "From: =?utf-8?B?QW5kcsOp?= <andre@example.com>\r\nSubject: Hi\r\n\r\nBody.\r\n", OutputFormat.Plain);
        Assert.Contains("From: André <andre@example.com>", plain);
    }
}
