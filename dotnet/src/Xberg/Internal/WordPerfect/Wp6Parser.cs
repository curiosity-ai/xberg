namespace Xberg.Internal.WordPerfect;

/// <summary>
/// WordPerfect 6.x parser, reimplemented from libwpd's <c>WP6Parser</c>.
/// </summary>
/// <remarks>
/// <para>
/// The 6.x body classifies bytes differently from 5.x: 0x01 to 0x20 are an international
/// character shorthand rather than control codes, ASCII runs to 0x7F, single-byte functions reach
/// all the way to 0xCF, variable-length groups occupy 0xD0 to 0xEF, and fixed-length groups sit
/// at the top from 0xF0.
/// </para>
/// <para>
/// The single-byte function block is where 6.x puts most of its structure: table rows, cells and
/// table-off are single bytes, with a multi-byte variant used only when a row needs non-default
/// values. That is why so much of the table handling here is a switch over byte values rather
/// than group parsing.
/// </para>
/// </remarks>
internal static class Wp6Parser
{
    /// <summary>Size of each fixed-length group from 0xF0 to 0xFE, counting both delimiters.</summary>
    /// <remarks>0xFF has no size: it is reserved and never opens a group.</remarks>
    private static readonly int[] FixedLengthGroupSize =
    {
        4, 5, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 8, 8,
    };

    /// <summary>
    /// The shorthand for accented characters that 6.x encodes in the 0x01 to 0x20 range.
    /// </summary>
    /// <remarks>
    /// These are Latin-1 code points, so the byte's own value has nothing to do with the
    /// character it names.
    /// </remarks>
    private static readonly ushort[] ExtendedInternational =
    {
        229, 197, 230, 198, 228, 196, 225, 224, 226, 227, 195, 231, 199, 235, 233, 201,
        232, 234, 237, 241, 209, 248, 216, 245, 213, 246, 214, 252, 220, 250, 249, 223,
    };

    private const byte TopExtendedCharacter = 0xF0;
    private const byte TopUndoGroup = 0xF1;
    private const byte TopAttributeOn = 0xF2;
    private const byte TopAttributeOff = 0xF3;

    private const byte TopEolGroup = 0xD0;
    private const byte TopParagraphGroup = 0xD3;
    private const byte TopCharacterGroup = 0xD4;
    private const byte TopTabGroup = 0xE0;

    private const byte AttributeSuperscript = 5;
    private const byte AttributeSubscript = 6;
    private const byte AttributeItalics = 8;
    private const byte AttributeBold = 12;
    private const byte AttributeStrikeOut = 13;
    private const byte AttributeUnderline = 14;

    /// <summary>Parse a WordPerfect 6.x document into an event stream.</summary>
    public static WpdDocument Parse(byte[] bytes, WpdHeader header)
    {
        var document = new WpdDocument();
        var sink = new WpdEventSink(document.Events);
        var reader = new WpdReader(bytes);
        reader.Seek((int)header.DocumentOffset);
        ParseBody(reader, sink);
        sink.Finish();
        return document;
    }

    private static void ParseBody(WpdReader reader, WpdEventSink sink)
    {
        while (!reader.AtEnd)
        {
            byte value = reader.ReadU8();

            if (value == 0)
            {
                // Meaningless, and most often just corruption.
            }
            else if (value <= 0x20)
            {
                sink.Character((char)ExtendedInternational[value - 1]);
            }
            else if (value <= 0x7F)
            {
                sink.Character((char)value);
            }
            else if (value <= 0xCF)
            {
                ParseSingleByteFunction(sink, value);
            }
            else if (value <= 0xEF)
            {
                ParseVariableLengthGroup(reader, sink, value);
            }
            else if (value < 0xFF)
            {
                ParseFixedLengthGroup(reader, sink, value);
            }
        }
    }

    private static void ParseSingleByteFunction(WpdEventSink sink, byte function)
    {
        switch (function)
        {
            case 0x80:                                  // soft space
            case 0xCD: case 0xCE: case 0xCF:            // soft end of line
                sink.Character(' ');
                break;

            case 0x81: sink.Character(' '); break; // hard space
            case 0x82: case 0x83: sink.Character('­'); break; // soft hyphen
            case 0x84: sink.Character('-'); break;      // hard hyphen

            case 0x87:                                  // dormant hard return
            case 0xB7: case 0xB8: case 0xB9:            // deletable hard end of line
            case 0xCA: case 0xCB: case 0xCC:            // hard end of line
                sink.ParagraphEnd();
                break;

            // Column and page breaks (0xC7-0xC9 and their deletable forms 0xB4-0xB6) end no
            // paragraph and produce no text, and the deletable soft end-of-line codes
            // (0xBA-0xBC) are removed content that libwpd does not dispatch at all.

            case 0xBD: case 0xBE: case 0xBF:            // table off
                sink.Emit(WpdEvent.Simple(WpdEventKind.TableEnd));
                break;

            case 0xC0: case 0xC1: case 0xC2:
            case 0xC3: case 0xC4: case 0xC5:            // table row
                sink.Emit(WpdEvent.Simple(WpdEventKind.RowStart));
                break;

            case 0xC6:                                  // table cell
                sink.Emit(WpdEvent.Simple(WpdEventKind.CellStart));
                break;
        }
    }

    /// <summary>Whether a fixed-length group repeats its own opening byte where it should.</summary>
    private static bool FixedGroupIsConsistent(WpdReader reader, byte group)
    {
        int closing = reader.Position + FixedLengthGroupSize[group - 0xF0] - 2;
        return closing < reader.Length && reader.PeekAt(closing) == group;
    }

    private static void ParseFixedLengthGroup(WpdReader reader, WpdEventSink sink, byte group)
    {
        if (!FixedGroupIsConsistent(reader, group)) return;

        int start = reader.Position;
        switch (group)
        {
            case TopExtendedCharacter:
            {
                byte character = reader.ReadU8();
                byte characterSet = reader.ReadU8();
                sink.Text(WpCharacterMap.Wp6Extended(character, characterSet));
                break;
            }

            case TopUndoGroup:
            {
                // WordPerfect keeps deleted text in the file so an undo can restore it, bracketed
                // by this group. Reading it would put text the author removed into the output.
                byte undoType = reader.ReadU8();
                if (undoType == 0) sink.Discarding = true;        // invalid text starts
                else if (undoType == 1) sink.Discarding = false;  // invalid text ends
                break;
            }

            case TopAttributeOn: Attribute(sink, reader.ReadU8(), on: true); break;
            case TopAttributeOff: Attribute(sink, reader.ReadU8(), on: false); break;
        }

        reader.Seek(start + FixedLengthGroupSize[group - 0xF0] - 1);
    }

    /// <summary>
    /// Dispatch an end-of-line group by its subgroup.
    /// </summary>
    /// <remarks>
    /// This one group covers everything from a soft line wrap to a table row: 6.x folds the whole
    /// family into subgroups of 0xD0, so treating the group as a single paragraph break turns
    /// every wrapped line into a paragraph and every table row into two.
    /// </remarks>
    private static void ParseEolGroup(WpdEventSink sink, byte subGroup)
    {
        switch (subGroup)
        {
            // A soft break is where the text merely wrapped, so it reads as a space.
            case 0x01: case 0x02: case 0x03:
                sink.Character(' ');
                break;

            // Hard returns, and the deletable forms of them, end a paragraph.
            case 0x04: case 0x05: case 0x06:
            case 0x17: case 0x18: case 0x19: case 0x1C:
                sink.ParagraphEnd();
                break;

            case 0x0A:
                sink.Emit(WpdEvent.Simple(WpdEventKind.CellStart));
                break;

            case 0x0B: case 0x0C: case 0x0D: case 0x0E: case 0x0F: case 0x10:
                sink.Emit(WpdEvent.Simple(WpdEventKind.RowStart));
                sink.Emit(WpdEvent.Simple(WpdEventKind.CellStart));
                break;

            case 0x11: case 0x12: case 0x13:
                sink.Emit(WpdEvent.Simple(WpdEventKind.TableEnd));
                break;

            // Column and page breaks (0x07-0x09, 0x1A, 0x1B) produce no text, and the deletable
            // soft forms (0x14-0x16) are removed content.
        }
    }

    private static void Attribute(WpdEventSink sink, byte attribute, bool on)
    {
        switch (attribute)
        {
            case AttributeBold:
                sink.Emit(WpdEvent.Simple(on ? WpdEventKind.BoldStart : WpdEventKind.BoldEnd));
                break;
            case AttributeItalics:
                sink.Emit(WpdEvent.Simple(on ? WpdEventKind.ItalicStart : WpdEventKind.ItalicEnd));
                break;
            case AttributeUnderline:
                sink.Emit(WpdEvent.Simple(on ? WpdEventKind.UnderlineStart : WpdEventKind.UnderlineEnd));
                break;
            case AttributeStrikeOut:
                sink.Emit(WpdEvent.Simple(
                    on ? WpdEventKind.StrikethroughStart : WpdEventKind.StrikethroughEnd));
                break;
            case AttributeSuperscript:
                sink.Emit(WpdEvent.Simple(
                    on ? WpdEventKind.SuperscriptStart : WpdEventKind.SuperscriptEnd));
                break;
            case AttributeSubscript:
                sink.Emit(WpdEvent.Simple(on ? WpdEventKind.SubscriptStart : WpdEventKind.SubscriptEnd));
                break;
        }
    }

    /// <summary>
    /// Read a variable-length group, whose header is subgroup, size, flags, optional prefix IDs
    /// and a non-deletable size, and whose trailer repeats the size and the opening byte.
    /// </summary>
    /// <remarks>
    /// The prefix IDs are how 6.x addresses content held elsewhere in the file — a header's text,
    /// a note's body — rather than inline as 5.x does. Those packets are not read here, which is
    /// the one deliberate gap in this parser.
    /// </remarks>
    private static void ParseVariableLengthGroup(WpdReader reader, WpdEventSink sink, byte group)
    {
        int start = reader.Position;

        // Consistency first: the trailer has to agree before the header is trusted.
        reader.Skip(1);
        int size = reader.ReadU16();
        int trailer = start + size - 4;
        if (size == 0 || trailer + 2 >= reader.Length
            || reader.PeekAt(trailer) + (reader.PeekAt(trailer + 1) << 8) != size
            || reader.PeekAt(trailer + 2) != group)
        {
            reader.Seek(start);
            return;
        }

        byte subGroup = (byte)reader.PeekAt(start);
        switch (group)
        {
            case TopTabGroup:
                sink.Tab();
                break;

            case TopEolGroup:
                ParseEolGroup(sink, subGroup);
                break;

            // The paragraph and character groups carry justification, spacing, fonts and colour:
            // formatting only, with no text and no structure the event stream models.
            case TopParagraphGroup:
            case TopCharacterGroup:
                break;
        }

        reader.Seek(start + size - 1);
    }
}
