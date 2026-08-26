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

    /// <summary>
    /// 4.2 is told from Macintosh 1.x by its variable-length groups.
    /// </summary>
    /// <remarks>
    /// The two formats overlap heavily — both classify bytes the same way and both close a group
    /// with its opening byte — so a fixed-length group alone is ambiguous between them. 0xD1 is
    /// variable-length in 4.2 and a six-byte fixed group in 1.x, so a short one is 4.2 and only
    /// 4.2. Macintosh is tested first, matching libwpd, because its length-framed groups are the
    /// more specific evidence.
    /// </remarks>
    [Fact]
    public void DetectsWp42FromItsVariableLengthGroups()
    {
        byte[] document = [(byte)'H', (byte)'i', 0xD1, 0x01, 0x02, 0xD1];
        Assert.Equal(WpdFormat.Wp42, WordPerfectReader.Detect(document));
    }

    /// <summary>A group that does not close where its size says is not this format.</summary>
    [Fact]
    public void AGroupThatDoesNotCloseIsNotWordPerfect()
    {
        byte[] document = [(byte)'H', 0xCB, 0x01, 0x02, 0x03, 0x04, 0x00];
        Assert.Equal(WpdFormat.Unknown, WordPerfectReader.Detect(document));
    }

    /// <summary>
    /// A Macintosh 1.x variable-length group frames its payload with a repeated big-endian length,
    /// which is the evidence that separates it from 4.2.
    /// </summary>
    [Fact]
    public void DetectsMac1FromItsLengthFramedGroups()
    {
        // 0xC9 is variable-length in both tables; only 1.x expects the length pair.
        byte[] document =
        [
            (byte)'H', (byte)'i',
            0xC9, 0x00, 0x00, 0x00, 0x02, (byte)'x', (byte)'y', 0x00, 0x00, 0x00, 0x02, 0xC9,
        ];
        Assert.Equal(WpdFormat.Wp1, WordPerfectReader.Detect(document));
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

    [Fact]
    public void AnUnrecognisableDocumentIsRefused()
    {
        var error = Assert.Throws<WpdParseException>(
            () => WordPerfectReader.Parse("not a WordPerfect document"u8.ToArray()));
        Assert.Contains("unrecognised", error.Message);
    }

    // ------------------------------------------------------------------ Tables

    /// <summary>A WordPerfect 6.x variable-length group around the non-deletable bytes given.</summary>
    /// <remarks>
    /// The group's size counts every byte from the opening group byte to the closing one, and
    /// appears twice — once in the header and once in the trailer, which is how a parser tells a
    /// real group from a coincidence.
    /// </remarks>
    private static byte[] Wp6Group(byte group, byte subGroup, params byte[] nonDeletable)
    {
        int size = 10 + nonDeletable.Length;
        var bytes = new List<byte> { group, subGroup, (byte)(size & 0xFF), (byte)(size >> 8), 0x00 };
        bytes.Add((byte)(nonDeletable.Length & 0xFF));
        bytes.Add((byte)(nonDeletable.Length >> 8));
        bytes.AddRange(nonDeletable);
        bytes.Add((byte)(size & 0xFF));
        bytes.Add((byte)(size >> 8));
        bytes.Add(group);
        return bytes.ToArray();
    }

    private const byte Wp6CharacterGroup = 0xD4;
    private const byte Wp6EolGroup = 0xD0;

    /// <summary>An end-of-line group carrying no sub-function data.</summary>
    private static byte[] Wp6Eol(byte subGroup) => Wp6Group(Wp6EolGroup, subGroup, 0x00, 0x00);

    private static List<WpdEvent> ParseWp6(params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return WordPerfectReader.Parse(Wp5Header(0x02, 0x0a, body)).Events;
    }

    /// <summary>
    /// A table's rows and cells are bracketed, not left as bare markers.
    /// </summary>
    /// <remarks>
    /// WordPerfect writes a row code and a cell code and nothing else — no close for either. The
    /// reader is what turns that into a properly nested structure, and getting it wrong collapses
    /// every table into one run-on line.
    /// </remarks>
    [Fact]
    public void Wp6TableRowsAndCellsAreBracketed()
    {
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),           // table definition on
            Wp6Group(Wp6CharacterGroup, 0x2C),           // column
            Wp6Group(Wp6CharacterGroup, 0x2C),           // column
            Wp6Group(Wp6CharacterGroup, 0x2B),           // table definition off: the table opens
            Wp6Eol(0x0B), [(byte)'A'],                   // row, and its first cell
            Wp6Eol(0x0A), [(byte)'B'],                   // next cell
            Wp6Eol(0x11));                               // table off

        Assert.Equal(
            new[]
            {
                WpdEventKind.TableStart,
                WpdEventKind.RowStart,
                WpdEventKind.CellStart, WpdEventKind.Text, WpdEventKind.ParagraphEnd,
                WpdEventKind.CellEnd,
                WpdEventKind.CellStart, WpdEventKind.Text, WpdEventKind.ParagraphEnd,
                WpdEventKind.CellEnd,
                WpdEventKind.RowEnd,
                WpdEventKind.TableEnd,
            },
            events.Select(e => e.Kind));
        Assert.Equal(new[] { "A", "B" },
            events.Where(e => e.Kind == WpdEventKind.Text).Select(e => e.Text));
    }

    /// <summary>
    /// A row shorter than the table's column count is padded with empty cells.
    /// </summary>
    /// <remarks>
    /// The column count comes from the definition, not from the row: a row that stops early still
    /// occupies the full grid, and a table whose rows differ in length is not a table any renderer
    /// can lay out.
    /// </remarks>
    [Fact]
    public void Wp6ShortRowsArePaddedToTheColumnCount()
    {
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2B),
            Wp6Eol(0x0B), [(byte)'A'],
            Wp6Eol(0x11));

        Assert.Equal(3, events.Count(e => e.Kind == WpdEventKind.CellStart));
    }

    /// <summary>
    /// A row flagged as a header row says so, and only the first such row does.
    /// </summary>
    [Fact]
    public void Wp6HeaderRowIsReportedOnce()
    {
        // Sub-function 128 is the row information block: five bytes, whose first is the flags,
        // and 0x04 is the header-row bit.
        byte[] headerRow = [0x00, 0x00, 128, 0x04, 0x00, 0x00, 0x00];
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2B),
            Wp6Group(Wp6EolGroup, 0x0B, headerRow), [(byte)'A'],
            Wp6Group(Wp6EolGroup, 0x0B, headerRow), [(byte)'B'],
            Wp6Eol(0x11));

        Assert.Equal(new[] { true, false },
            events.Where(e => e.Kind == WpdEventKind.RowStart).Select(e => e.Header));
    }

    /// <summary>A cell spanning columns says so, so the grid can be padded out.</summary>
    [Fact]
    public void Wp6CellSpansAreRead()
    {
        // Sub-function 133 is the cell spanning block: four bytes, columns then rows.
        byte[] spanning = [0x00, 0x00, 133, 0x02, 0x01, 0x00];
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2B),
            Wp6Group(Wp6EolGroup, 0x0B, spanning), [(byte)'A'],
            Wp6Eol(0x11));

        var cell = Assert.Single(events.Where(e => e.Kind == WpdEventKind.CellStart));
        Assert.Equal(2u, cell.ColSpan);
        Assert.Equal(0, cell.Column);
    }

    /// <summary>
    /// A cell whose slot the row above already covers reports the coverage rather than a cell.
    /// </summary>
    /// <remarks>
    /// A column span of 128 or more is the format's marker for a slot bound from above; read as a
    /// real span it would claim a cell that is not there.
    /// </remarks>
    [Fact]
    public void Wp6BoundFromAboveEmitsNoCell()
    {
        byte[] boundFromAbove = [0x00, 0x00, 133, 0x80, 0x01, 0x00];
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2B),
            Wp6Group(Wp6EolGroup, 0x0B, boundFromAbove),
            Wp6Eol(0x11));

        Assert.Contains(events, e => e.Kind == WpdEventKind.RowStart);
        // The row is padded to the column count, so the cell that appears is the filler rather
        // than one the document wrote.
        Assert.All(events.Where(e => e.Kind == WpdEventKind.CellStart),
            e => Assert.Equal(1u, e.ColSpan));
    }

    /// <summary>An unterminated table is still closed, so the events stay nested.</summary>
    [Fact]
    public void Wp6UnterminatedTableIsClosed()
    {
        var events = ParseWp6(
            Wp6Group(Wp6CharacterGroup, 0x2A),
            Wp6Group(Wp6CharacterGroup, 0x2C),
            Wp6Group(Wp6CharacterGroup, 0x2B),
            Wp6Eol(0x0B), [(byte)'A']);

        Assert.Equal(WpdEventKind.TableEnd, events[^1].Kind);
    }

    // ------------------------------------------------------------------ Tabs

    /// <summary>
    /// A tab before any text on the line is an indent, not a tab stop.
    /// </summary>
    /// <remarks>
    /// WordPerfect expresses a paragraph's first-line indent with the same codes it uses for a
    /// tab the author typed; emitting both puts a stray tab at the head of every indented
    /// paragraph.
    /// </remarks>
    [Fact]
    public void Wp6LeadingLeftTabIsAnIndent()
    {
        // Subgroup 0x10 is a left tab: (0x10 & 0xF8) >> 3 == 2.
        var leading = ParseWp6(Wp6Group(0xE0, 0x10), [(byte)'A']);
        Assert.DoesNotContain(leading, e => e.Kind == WpdEventKind.Tab);

        var midLine = ParseWp6([(byte)'A'], Wp6Group(0xE0, 0x10), [(byte)'B']);
        Assert.Contains(midLine, e => e.Kind == WpdEventKind.Tab);
    }

    /// <summary>A back tab is a hanging indent and never reaches the text.</summary>
    [Fact]
    public void Wp6BackTabIsNeverATab()
    {
        var events = ParseWp6([(byte)'A'], Wp6Group(0xE0, 0x00), [(byte)'B']);
        Assert.DoesNotContain(events, e => e.Kind == WpdEventKind.Tab);
    }

    // ------------------------------------------------------------------ Notes

    /// <summary>
    /// A note's anchor number belongs to the note, not to the sentence it interrupts.
    /// </summary>
    /// <remarks>
    /// The number is written into the body as literal characters between the note-on and note-off
    /// codes. Read as text it becomes a bare digit glued to the preceding word.
    /// </remarks>
    [Fact]
    public void Wp6NoteNumberIsNotText()
    {
        var events = ParseWp6(
            [(byte)'a'],
            Wp6Group(0xD7, 0x00),            // footnote on
            [(byte)'1'],                     // the anchor number
            Wp6Group(0xD7, 0x01),            // footnote off
            [(byte)'b']);

        Assert.Equal(new[] { "a", "b" },
            events.Where(e => e.Kind == WpdEventKind.Text).Select(e => e.Text));
        Assert.Contains(events, e => e.Kind == WpdEventKind.NoteStart);
        Assert.Contains(events, e => e.Kind == WpdEventKind.NoteEnd);
    }

    /// <summary>An endnote is reported as one, so it is not filed as a footnote.</summary>
    [Fact]
    public void Wp6EndnoteIsDistinguished()
    {
        var events = ParseWp6(Wp6Group(0xD7, 0x02), Wp6Group(0xD7, 0x03));
        var note = Assert.Single(events.Where(e => e.Kind == WpdEventKind.NoteStart));
        Assert.True(note.Endnote);
    }

    /// <summary>
    /// The literal digits of an automatic number never reach the text.
    /// </summary>
    /// <remarks>
    /// The display-number-reference group brackets a number the document wrote out — a note's
    /// number, a page number — with an even subgroup to open and the odd one after it to close.
    /// </remarks>
    [Fact]
    public void Wp6DisplayedNumbersAreDropped()
    {
        var events = ParseWp6(
            [(byte)'a'],
            Wp6Group(0xDA, 0x0E),            // footnote number display on
            [(byte)'7'],
            Wp6Group(0xDA, 0x0F),            // footnote number display off
            [(byte)'b']);

        Assert.Equal(new[] { "a", "b" },
            events.Where(e => e.Kind == WpdEventKind.Text).Select(e => e.Text));
    }

    // ------------------------------------------------------------------ Prefix packets

    /// <summary>
    /// A 6.x file's out-of-band packets are found through the index and read whole.
    /// </summary>
    /// <remarks>
    /// A footnote's body is not where the footnote is anchored: it sits in a packet elsewhere in
    /// the file, split into blocks, and the anchor carries only its number. A parser that reads
    /// the body alone sees every reference and none of the notes.
    /// </remarks>
    [Fact]
    public void PrefixIndexFindsATextPacket()
    {
        const int indexHeader = 32;
        const int packetOffset = 64;
        var bytes = new byte[128];

        bytes[14] = indexHeader;                        // pointer to the index header
        bytes[indexHeader + 2] = 2;                     // two indices, so one real packet

        int entry = indexHeader + 14;
        bytes[entry] = 0x00;                            // flags
        bytes[entry + 1] = 0x08;                        // type: general WordPerfect text
        bytes[entry + 6] = 32;                          // data size
        bytes[entry + 10] = packetOffset;               // data offset

        bytes[packetOffset] = 2;                        // two text blocks
        bytes[packetOffset + 6] = 3;                     // first block: three bytes
        bytes[packetOffset + 10] = 2;                    // second block: two bytes
        "abcde"u8.ToArray().CopyTo(bytes, packetOffset + 14);

        var packet = Wp6PrefixData.Read(bytes).TextPacket(1);
        Assert.Equal("abcde"u8.ToArray(), packet);
    }

    /// <summary>A packet the index does not name is not invented.</summary>
    [Fact]
    public void AnAbsentPacketIsNull()
    {
        var bytes = new byte[64];
        bytes[14] = 32;
        Assert.Null(Wp6PrefixData.Read(bytes).TextPacket(1));
    }

    /// <summary>A packet pointing outside the file costs the packet, not the document.</summary>
    [Fact]
    public void AnOutOfRangePacketIsRefused()
    {
        var bytes = new byte[128];
        bytes[14] = 32;
        bytes[32 + 2] = 2;
        int entry = 32 + 14;
        bytes[entry + 1] = 0x08;
        bytes[entry + 6] = 32;
        bytes[entry + 10] = 0xFF;                       // an offset past the end
        bytes[entry + 11] = 0xFF;

        Assert.Null(Wp6PrefixData.Read(bytes).TextPacket(1));
    }

    // ------------------------------------------------------------------ Macintosh attributes

    /// <summary>
    /// A Macintosh attribute group names the attribute first and its state second.
    /// </summary>
    /// <remarks>
    /// Read the other way round the two swap: bold turns into italic, and italic into nothing at
    /// all, because the state byte is then read as the attribute.
    /// </remarks>
    [Fact]
    public void Wp3AttributeGroupReadsAttributeThenState()
    {
        // The Mac header, then the attribute group 0xC3: attribute, state, and its closing byte.
        byte[] body = [0xC3, 0x00, 0x01, 0xC3, (byte)'x', 0xC3, 0x00, 0x00, 0xC3];
        var events = WordPerfectReader.Parse(Wp5Header(0x02, 0x2c, body)).Events;

        Assert.Equal(
            new[] { WpdEventKind.BoldStart, WpdEventKind.Text, WpdEventKind.BoldEnd },
            events.Select(e => e.Kind));
    }
}
