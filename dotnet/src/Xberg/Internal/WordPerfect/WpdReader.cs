namespace Xberg.Internal.WordPerfect;

/// <summary>
/// A bounds-checked cursor over a WordPerfect document's bytes.
/// </summary>
/// <remarks>
/// Every read is checked and reports end-of-input rather than throwing, because a WordPerfect
/// parse routinely walks off the end of a truncated or malformed group and must treat that as
/// "stop", not as a crash. The corpus includes deliberately-malformed CVE samples for exactly
/// this reason.
/// </remarks>
internal sealed class WpdReader
{
    private readonly byte[] _bytes;

    public WpdReader(byte[] bytes) => _bytes = bytes;

    public int Position { get; set; }

    public int Length => _bytes.Length;

    public bool AtEnd => Position >= _bytes.Length;

    /// <summary>Read one byte, or -1 at end of input.</summary>
    public int ReadByte() => Position < _bytes.Length ? _bytes[Position++] : -1;

    /// <summary>Read one byte, treating end of input as zero.</summary>
    public byte ReadU8() => Position < _bytes.Length ? _bytes[Position++] : (byte)0;

    public ushort ReadU16(bool bigEndian = false)
    {
        byte a = ReadU8(), b = ReadU8();
        return bigEndian ? (ushort)((a << 8) | b) : (ushort)((b << 8) | a);
    }

    public uint ReadU32(bool bigEndian = false)
    {
        uint a = ReadU8(), b = ReadU8(), c = ReadU8(), d = ReadU8();
        return bigEndian
            ? (a << 24) | (b << 16) | (c << 8) | d
            : (d << 24) | (c << 16) | (b << 8) | a;
    }

    /// <summary>Peek at an absolute offset without moving the cursor.</summary>
    public int PeekAt(int offset) => offset >= 0 && offset < _bytes.Length ? _bytes[offset] : -1;

    /// <summary>Move relative to the current position, clamped to the document.</summary>
    public void Skip(int delta) => Position = Math.Clamp(Position + delta, 0, _bytes.Length);

    /// <summary>Move to an absolute position, clamped to the document.</summary>
    public void Seek(int position) => Position = Math.Clamp(position, 0, _bytes.Length);

    /// <summary>A slice of the document, clamped to what exists.</summary>
    public byte[] Slice(int start, int length)
    {
        start = Math.Clamp(start, 0, _bytes.Length);
        length = Math.Clamp(length, 0, _bytes.Length - start);
        var slice = new byte[length];
        Array.Copy(_bytes, start, slice, 0, length);
        return slice;
    }
}
