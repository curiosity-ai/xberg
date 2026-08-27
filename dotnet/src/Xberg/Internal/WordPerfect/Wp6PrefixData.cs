namespace Xberg.Internal.WordPerfect;

/// <summary>
/// The packets a WordPerfect 6.x file stores outside its body, addressed by number.
/// </summary>
/// <remarks>
/// <para>
/// 6.x does not keep a footnote's text where the footnote is anchored. The body sits in a packet
/// somewhere else in the file, and the anchor carries only its number — so a parser that reads
/// the body alone sees the reference and never the note. The same indirection holds for comments
/// and for the text inside a graphics box.
/// </para>
/// <para>
/// The index is a flat table near the top of the file: one fixed-size entry per packet giving its
/// type, its offset and its length. Entry numbering starts at 1, which is why the entry for
/// packet <c>n</c> is the <c>n-1</c>th read.
/// </para>
/// </remarks>
internal sealed class Wp6PrefixData
{
    /// <summary>A packet holding WordPerfect text: a note body, a box's contents.</summary>
    private const byte TypeGeneralText = 0x08;

    /// <summary>A packet holding a comment's text.</summary>
    private const byte TypeCommentAnnotation = 0x1B;

    /// <summary>Byte offset of the pointer to the index header.</summary>
    private const int IndexHeaderPointerOffset = 14;

    /// <summary>Offset of the packet count within the index header.</summary>
    private const int NumIndicesOffset = 2;

    /// <summary>Offset of the first index entry within the index header.</summary>
    private const int IndicesOffset = 14;

    /// <summary>Size of one index entry: flags, type, two counts, size and offset.</summary>
    private const int IndexEntrySize = 14;

    /// <summary>
    /// The lowest index-header offset the format allows.
    /// </summary>
    /// <remarks>The 6.0 specification says a smaller value means 16, not an error.</remarks>
    private const int MinIndexHeaderOffset = 16;

    private readonly Dictionary<int, byte[]> _textPackets = new();

    /// <summary>The body of the text packet with this ID, or <c>null</c> if there is none.</summary>
    public byte[]? TextPacket(int prefixId) =>
        _textPackets.TryGetValue(prefixId, out var data) ? data : null;

    /// <summary>Read the packet index and the text packets it points at.</summary>
    /// <remarks>
    /// A malformed index costs the packets, not the document: whatever was read before the
    /// damage is kept, and the body still parses. These files are old enough, and the CVE
    /// corpus deliberate enough, that abandoning the whole document over a bad offset would
    /// lose text that is plainly there.
    /// </remarks>
    public static Wp6PrefixData Read(byte[] bytes)
    {
        var data = new Wp6PrefixData();
        var reader = new WpdReader(bytes);

        reader.Seek(IndexHeaderPointerOffset);
        int indexHeaderOffset = Math.Max((int)reader.ReadU16(), MinIndexHeaderOffset);

        reader.Seek(indexHeaderOffset + NumIndicesOffset);
        int count = reader.ReadU16();
        if (count < 2) return data;

        reader.Seek(indexHeaderOffset + IndicesOffset);
        var entries = new List<(int Id, byte Type, uint Offset, uint Size)>(count - 1);
        for (int id = 1; id < count; id++)
        {
            int entryStart = indexHeaderOffset + IndicesOffset + (id - 1) * IndexEntrySize;
            if (entryStart + IndexEntrySize > bytes.Length) break;

            reader.Seek(entryStart);
            reader.ReadU8();                        // flags: child-packet bit, not needed here
            byte type = reader.ReadU8();
            reader.ReadU16();                       // use count
            reader.ReadU16();                       // hide count
            uint size = reader.ReadU32();
            uint offset = reader.ReadU32();
            entries.Add((id, type, offset, size));
        }

        foreach (var entry in entries)
        {
            if (entry.Type is not (TypeGeneralText or TypeCommentAnnotation)) continue;
            if (entry.Size == 0) continue;

            var body = ReadTextPacket(reader, (int)entry.Offset, (int)entry.Size);
            if (body is { Length: > 0 }) data._textPackets[entry.Id] = body;
        }

        return data;
    }

    /// <summary>
    /// Read one text packet: a count of blocks, their lengths, then the blocks themselves.
    /// </summary>
    /// <remarks>
    /// The text is split into blocks because WordPerfect grew them as the note was edited; they
    /// are simply concatenated, with no separator, to recover the note as it reads.
    /// </remarks>
    private static byte[]? ReadTextPacket(WpdReader reader, int offset, int size)
    {
        if (offset < 0 || offset >= reader.Length) return null;

        reader.Seek(offset);
        int blocks = reader.ReadU16();
        reader.Skip(4);
        if (blocks < 1) return null;

        var sizes = new int[blocks];
        long total = 0;
        for (int i = 0; i < blocks; i++)
        {
            if (reader.Position - offset + 4 > size || reader.AtEnd) return null;
            sizes[i] = (int)reader.ReadU32();
            if (sizes[i] < 0) return null;
            total += sizes[i];
        }
        if (total == 0 || total > size) return null;

        var body = new byte[total];
        int written = 0;
        for (int i = 0; i < blocks; i++)
        {
            if (reader.Position - offset + sizes[i] > size || reader.AtEnd) return null;
            var block = reader.Slice(reader.Position, sizes[i]);
            if (block.Length != sizes[i]) return null;
            Array.Copy(block, 0, body, written, block.Length);
            written += block.Length;
            reader.Skip(sizes[i]);
        }

        return body;
    }
}
