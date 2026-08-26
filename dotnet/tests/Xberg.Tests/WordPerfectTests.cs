using Xberg.Internal.WordPerfect;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the managed WordPerfect reader.
/// </summary>
/// <remarks>
/// Built on synthetic byte streams rather than corpus fixtures so they state the format rule each
/// one covers. The end-to-end check that the reader agrees with libwpd runs against real documents
/// through the parity harness; these pin the rules that check would only fail vaguely on.
/// </remarks>
public class WordPerfectTests
{
    // ------------------------------------------------------------------ Detection

    /// <summary>A WP5 header: the magic, a document offset, and the product/type/version bytes.</summary>
    private static byte[] Wp5Header(byte majorVersion, byte fileType, params byte[] body)
    {
        var bytes = new byte[16 + body.Length];
        bytes[0] = 0xFF; bytes[1] = (byte)'W'; bytes[2] = (byte)'P'; bytes[3] = (byte)'C';
        bytes[4] = 16;                       // document offset, little-endian
        bytes[8] = 1;                        // product type: WordPerfect
        bytes[9] = fileType;
        bytes[10] = majorVersion;
        Array.Copy(body, 0, bytes, 16, body.Length);
        return bytes;
    }

    [Fact]
    public void DetectsWp5FromTheHeader() =>
        Assert.Equal(WpdFormat.Wp5, WordPerfectReader.Detect(Wp5Header(0x00, 0x0a)));

    [Fact]
    public void DetectsWp6FromTheHeader() =>
        Assert.Equal(WpdFormat.Wp6, WordPerfectReader.Detect(Wp5Header(0x02, 0x0a)));

    /// <summary>File type 0x2c is Macintosh WordPerfect, a different parser entirely.</summary>
    [Fact]
    public void DetectsMacFromTheHeader() =>
        Assert.Equal(WpdFormat.Wp3, WordPerfectReader.Detect(Wp5Header(0x02, 0x2c)));

    [Fact]
    public void RejectsAnUnknownFileType() =>
        Assert.Equal(WpdFormat.Unknown, WordPerfectReader.Detect(Wp5Header(0x00, 0x77)));

    /// <summary>
    /// A plain text file must not be claimed as WordPerfect 4.2.
    /// </summary>
    /// <remarks>
    /// 4.2 has no header, so detection is structural — and every byte of plain ASCII passes the
    /// range checks. Requiring at least one function group is what stops the heuristic claiming
    /// every text file it is shown.
    /// </remarks>
    [Fact]
    public void PlainTextIsNotWp42() =>
        Assert.Equal(WpdFormat.Unknown,
            WordPerfectReader.Detect("Just some ordinary text.\n"u8.ToArray()));

    [Fact]
    public void DetectsWp42FromItsFunctionGroups()
    {
        // 0xCB is a fixed-length group of 6 bytes, closing with its own opening byte.
        byte[] document = [(byte)'H', (byte)'i', 0xCB, 0x01, 0x02, 0x03, 0x04, 0xCB];
        Assert.Equal(WpdFormat.Wp42, WordPerfectReader.Detect(document));
    }

    /// <summary>A group that does not close where its size says is not this format.</summary>
    [Fact]
    public void Wp42RejectsAGroupThatDoesNotClose()
    {
        byte[] document = [(byte)'H', 0xCB, 0x01, 0x02, 0x03, 0x04, 0x00];
        Assert.Equal(WpdFormat.Unknown, WordPerfectReader.Detect(document));
    }

    // ------------------------------------------------------------------ WP4.2 body

    private static string RenderWp42(params byte[] body) =>
        WordPerfectReader.RenderPlain(Wp42Parser.Parse(body));

    [Fact]
    public void Wp42ReadsTextAndParagraphs()
    {
        // "Hi", hard return, "There", then a group so the format is recognisable.
        byte[] document = [
            (byte)'H', (byte)'i', 0x0A, (byte)'T', (byte)'h', (byte)'e', (byte)'r', (byte)'e',
            0xCB, 0x01, 0x02, 0x03, 0x04, 0xCB,
        ];
        Assert.Equal("Hi\n\nThere", RenderWp42(document));
    }

    /// <summary>A soft return is where the text merely wrapped, so it reads as a space.</summary>
    [Fact]
    public void Wp42SoftReturnIsASpace() =>
        Assert.Equal("a b", RenderWp42((byte)'a', 0x0D, (byte)'b'));

    [Fact]
    public void Wp42TabBecomesATab() =>
        Assert.Equal("a\tb", RenderWp42((byte)'a', 0x09, (byte)'b'));

    /// <summary>
    /// The extended character group is 0xE1, and its payload byte indexes the 4.2 character set.
    /// </summary>
    [Fact]
    public void Wp42ExtendedCharacterMapsThroughTheCharacterSet()
    {
        // 0xE1 is a variable-length group, so it runs to its own closing byte. Entry 130 of the
        // 4.2 set is a lower-case e with a diaeresis.
        Assert.Equal("ë", RenderWp42(0xE1, 130, 0xE1));
    }

    /// <summary>
    /// A document ends with a hard return, and an empty paragraph is dropped rather than emitted.
    /// </summary>
    [Fact]
    public void Wp42DropsEmptyParagraphs() =>
        Assert.Equal("a\n\nb", RenderWp42((byte)'a', 0x0A, 0x0A, 0x0A, (byte)'b', 0x0A));

    // ------------------------------------------------------------------ Event sink

    [Fact]
    public void SinkBuffersRunsIntoOneTextEvent()
    {
        var events = new List<WpdEvent>();
        var sink = new WpdEventSink(events);
        sink.Character('a');
        sink.Character('b');
        sink.Character('c');
        sink.Finish();

        var only = Assert.Single(events);
        Assert.Equal(WpdEventKind.Text, only.Kind);
        Assert.Equal("abc", only.Text);
    }

    /// <summary>
    /// WordPerfect keeps deleted text in the file so an undo can restore it. Reading it would put
    /// text the author removed into the extraction.
    /// </summary>
    [Fact]
    public void SinkDiscardsWhileDeleting()
    {
        var events = new List<WpdEvent>();
        var sink = new WpdEventSink(events);
        sink.Text("kept ");
        sink.Discarding = true;
        sink.Text("deleted");
        sink.ParagraphEnd();
        sink.Discarding = false;
        sink.Text("also kept");
        sink.Finish();

        Assert.Equal(new[] { "kept also kept" }, events.Select(e => e.Text));
    }

    /// <summary>The terminating hard return every document carries is not an empty paragraph.</summary>
    [Fact]
    public void SinkDropsTheTrailingParagraphEnd()
    {
        var events = new List<WpdEvent>();
        var sink = new WpdEventSink(events);
        sink.Text("body");
        sink.ParagraphEnd();
        sink.ParagraphEnd();
        sink.Finish();

        Assert.Equal(new[] { WpdEventKind.Text }, events.Select(e => e.Kind));
    }

    // ------------------------------------------------------------------ Character map

    /// <summary>Character set 0 is plain ASCII, which is not how the document body reads the byte.</summary>
    [Fact]
    public void CharacterSetZeroIsAscii()
    {
        Assert.Equal("A", WpCharacterMap.Wp5Extended((byte)'A', 0));
        Assert.Equal("A", WpCharacterMap.Wp6Extended((byte)'A', 0));
        Assert.Equal(" ", WpCharacterMap.Wp5Extended(0x01, 0));
    }

    [Fact]
    public void MultinationalSetMapsAccentedLetters()
    {
        // Set 1 is shared between 5.x and 6.x: entry 26 is a capital A with an acute accent,
        // entry 27 the lower-case form.
        Assert.Equal("Á", WpCharacterMap.Wp6Extended(26, 1));
        Assert.Equal("Á", WpCharacterMap.Wp5Extended(26, 1));
        Assert.Equal("á", WpCharacterMap.Wp6Extended(27, 1));
    }

    /// <summary>
    /// A few entries map to a sequence — a combining accent and its base letter — which is why
    /// the lookup returns text rather than a scalar.
    /// </summary>
    [Fact]
    public void ComplexEntriesMapToASequence()
    {
        string mapped = WpCharacterMap.Wp6Extended(156, 1);
        Assert.Equal(2, mapped.Length);
        Assert.Equal('ʼ', mapped[0]);
        Assert.Equal('N', mapped[1]);
    }

    /// <summary>An unmapped character falls back to a space rather than vanishing or throwing.</summary>
    [Fact]
    public void UnmappedCharactersFallBackToASpace()
    {
        Assert.Equal(" ", WpCharacterMap.Wp6Extended(255, 99));
        Assert.Equal(" ", WpCharacterMap.Wp5Extended(255, 99));
    }

    // ------------------------------------------------------------------ Reader bounds

    /// <summary>
    /// Every read is bounds-checked: a parse walks off the end of a truncated group routinely, and
    /// that has to mean "stop" rather than "crash".
    /// </summary>
    [Fact]
    public void ReaderTreatsEndOfInputAsZero()
    {
        var reader = new WpdReader([0x41]);
        Assert.Equal(0x41, reader.ReadU8());
        Assert.True(reader.AtEnd);
        Assert.Equal(0, reader.ReadU8());
        Assert.Equal(-1, reader.ReadByte());
    }

    [Fact]
    public void ReaderClampsSeeksAndSlices()
    {
        var reader = new WpdReader([1, 2, 3]);
        reader.Seek(100);
        Assert.Equal(3, reader.Position);
        reader.Skip(-100);
        Assert.Equal(0, reader.Position);
        Assert.Equal(new byte[] { 2, 3 }, reader.Slice(1, 99));
        Assert.Empty(reader.Slice(99, 5));
    }

    // ------------------------------------------------------------------ Unsupported formats

    /// <summary>An unreadable document says which format it is rather than failing vaguely.</summary>
    [Fact]
    public void MacFormatsReportThemselvesAsUnsupported()
    {
        var error = Assert.Throws<WpdParseException>(
            () => WordPerfectReader.Parse(Wp5Header(0x02, 0x2c)));
        Assert.Contains("Wp3", error.Message);
    }

    [Fact]
    public void AnUnrecognisableDocumentIsRefused()
    {
        var error = Assert.Throws<WpdParseException>(
            () => WordPerfectReader.Parse("not a WordPerfect document"u8.ToArray()));
        Assert.Contains("unrecognised", error.Message);
    }
}
