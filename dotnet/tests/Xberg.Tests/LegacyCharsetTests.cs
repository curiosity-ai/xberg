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

    /// <summary>
    /// An RTF `\'hh` escape decodes with the active code page, not always Windows-1252: the
    /// document's `\ansicpg`, and above it the active font's `\fcharsetN`.
    /// </summary>
    [Theory]
    // \ansicpg1251 alone.
    [InlineData(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fnil Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    // The active font's fcharset wins over a contradicting \ansicpg...
    [InlineData(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset204 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    // ...including over a redundant \cpg on the same font entry, per RTF 1.9.1.
    [InlineData(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset204\cpg1252 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    // \deffN alone selects the default font's charset.
    [InlineData(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset204 Arial;}}\'cf\'f0\'e8\'e2\'e5\'f2}", "Привет")]
    // A multi-byte code page spells one character across two escapes.
    [InlineData(@"{\rtf1\ansi\ansicpg932\deff0{\fonttbl{\f0\fnil\fcharset128 MS Mincho;}}\f0 \'93\'fa\'96\'7b}", "日本")]
    public void RtfHexEscapes_DecodeWithTheActiveCodepage(string rtf, string expected)
    {
        var doc = new RtfExtractor().Extract(Encoding.ASCII.GetBytes(rtf), "application/rtf", new ExtractionConfig());
        Assert.Contains(doc.Elements, e => e.Text.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>Each run decodes with its own font's charset when the document switches fonts.</summary>
    [Fact]
    public void RtfHexEscapes_FollowAMidDocumentFontSwitch()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset204 Arial;}" +
                           @"{\f1\fnil\fcharset161 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2 \f1 \'e3\'e5\'e9\'e1}";
        var doc = new RtfExtractor().Extract(Encoding.ASCII.GetBytes(rtf), "application/rtf", new ExtractionConfig());
        string text = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Привет", text);
        Assert.Contains("γεια", text);
    }
}
