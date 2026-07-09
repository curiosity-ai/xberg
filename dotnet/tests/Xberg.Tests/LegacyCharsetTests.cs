using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Email;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Regression tests for legacy-charset decoding: Shift-JIS (code page 932) CSV and
/// no-BOM UTF-16 BE/LE email. These exercise the fixes for the `test_mskanji.csv`
/// and `fake-email-utf-16-*.eml` fixtures, which previously produced garbage/empty output.
/// </summary>
public class LegacyCharsetTests
{
    // Shift-JIS bytes from test_mskanji.csv: "名前,年齢,住所\r\n髙橋淳,35,名古屋\r\n".
    // Row 2 includes 髙 (U+9AD9), encoded as EE E0 — a NEC/IBM-extension character that
    // .NET's CP932 flags as best-fit (ExceptionFallback throws) but WHATWG shift_jis decodes.
    private static readonly byte[] MsKanjiCsv =
    {
        0x96, 0xbc, 0x91, 0x4f, 0x2c, 0x94, 0x4e, 0x97, 0xee, 0x2c, 0x8f, 0x5a, 0x8f, 0x8a, 0x0d, 0x0a,
        0xee, 0xe0, 0x8b, 0xb4, 0x8f, 0x7e, 0x2c, 0x33, 0x35, 0x2c, 0x96, 0xbc, 0x8c, 0xc3, 0x89, 0xae, 0x0d, 0x0a,
    };

    [Fact]
    public void ShiftJisCsv_DecodesJapaneseHeader()
    {
        var doc = new CsvExtractor().Extract(MsKanjiCsv, "text/csv", new ExtractionConfig());

        Assert.Single(doc.Tables);
        var header = doc.Tables[0].Cells[0];
        Assert.Equal(new[] { "名前", "年齢", "住所" }, header);
    }

    [Fact]
    public void ShiftJisCsv_DecodesNecIbmExtensionChar()
    {
        var doc = new CsvExtractor().Extract(MsKanjiCsv, "text/csv", new ExtractionConfig());

        // Row 2 name cell must be the real 髙橋淳, not windows-1252 mojibake.
        Assert.Equal("髙橋淳", doc.Tables[0].Cells[1][0]);
    }

    [Fact]
    public void ShiftJisCsv_ProducesNoReplacementCharacters()
    {
        var doc = new CsvExtractor().Extract(MsKanjiCsv, "text/csv", new ExtractionConfig());
        string plain = Xberg.Rendering.PlainRenderer.Render(doc);
        Assert.DoesNotContain('�', plain);
        Assert.Contains("名古屋", plain);
    }

    private const string EmlBody =
        "MIME-Version: 1.0\n" +
        "Subject: Greetings\n" +
        "From: sender@example.com\n" +
        "To: rcpt@example.com\n" +
        "Content-Type: text/plain; charset=utf-8\n" +
        "\n" +
        "Hello UTF-16 world\n";

    [Fact]
    public void Utf16LeEmail_NoBom_TranscodesAndParses()
    {
        // UTF-16LE, no BOM — matches fake-email-utf-16-le.eml.
        byte[] data = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(EmlBody);
        var result = MimeParser.ParseEmlContent(data);

        Assert.Equal("Greetings", result.Subject);
        Assert.Equal("sender@example.com", result.FromEmail);
        Assert.Contains("Hello UTF-16 world", result.Content);
    }

    [Fact]
    public void Utf16BeEmail_NoBom_TranscodesAndParses()
    {
        // UTF-16BE, no BOM — matches fake-email-utf-16-be.eml.
        byte[] data = new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes(EmlBody);
        var result = MimeParser.ParseEmlContent(data);

        Assert.Equal("Greetings", result.Subject);
        Assert.Equal("sender@example.com", result.FromEmail);
        Assert.Contains("Hello UTF-16 world", result.Content);
    }

    [Fact]
    public void Utf8Email_NotMistakenlyTranscoded()
    {
        // Plain ASCII/UTF-8 must parse unchanged (no false UTF-16 transcode).
        byte[] data = Encoding.UTF8.GetBytes(EmlBody);
        var result = MimeParser.ParseEmlContent(data);

        Assert.Equal("Greetings", result.Subject);
        Assert.Contains("Hello UTF-16 world", result.Content);
    }
}
