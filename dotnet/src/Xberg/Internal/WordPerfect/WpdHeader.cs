namespace Xberg.Internal.WordPerfect;

/// <summary>Which WordPerfect family a document belongs to.</summary>
internal enum WpdFormat
{
    Unknown,

    /// <summary>WordPerfect 4.2 for DOS. No header at all — identified by heuristic.</summary>
    Wp42,

    /// <summary>WordPerfect 5.0 and 5.1 for DOS.</summary>
    Wp5,

    /// <summary>WordPerfect 6.x and later for DOS/Windows.</summary>
    Wp6,

    /// <summary>WordPerfect 1.x for Macintosh. No header — identified by heuristic.</summary>
    Wp1,

    /// <summary>WordPerfect 2.x and 3.x for Macintosh.</summary>
    Wp3,
}

/// <summary>
/// The generic <c>\xFFWPC</c> header that WordPerfect 5.0 and later write.
/// </summary>
/// <remarks>
/// Formats before 5.0 have no header, which is why detection falls back to heuristics rather than
/// simply reporting "not WordPerfect".
/// </remarks>
internal sealed record WpdHeader(
    uint DocumentOffset,
    byte ProductType,
    byte FileType,
    byte MajorVersion,
    byte MinorVersion,
    ushort DocumentEncryption)
{
    private const int MagicOffset = 1;
    private const int DocumentPointerOffset = 4;
    private const int ProductTypeOffset = 8;
    private const int EncryptionOffset = 15;

    /// <summary>Read the header, or <c>null</c> when the magic is absent.</summary>
    public static WpdHeader? TryRead(WpdReader reader)
    {
        if (reader.PeekAt(MagicOffset) != 'W'
            || reader.PeekAt(MagicOffset + 1) != 'P'
            || reader.PeekAt(MagicOffset + 2) != 'C')
            return null;

        reader.Seek(DocumentPointerOffset);
        uint documentOffset = reader.ReadU32();

        reader.Seek(ProductTypeOffset);
        byte productType = reader.ReadU8();
        byte fileType = reader.ReadU8();
        byte majorVersion = reader.ReadU8();
        byte minorVersion = reader.ReadU8();

        reader.Seek(EncryptionOffset);
        ushort encryption = reader.ReadU16();

        // WP5 stores the encryption word the other way round from WP6.
        if (fileType == 0x0a && majorVersion == 0x00)
            encryption = (ushort)(((encryption & 0xff00) >> 8) | ((encryption & 0x00ff) << 8));

        return new WpdHeader(documentOffset, productType, fileType, majorVersion, minorVersion, encryption);
    }

    /// <summary>The parser family this header selects, if any.</summary>
    public WpdFormat Format => (FileType, MajorVersion) switch
    {
        (0x0a, 0x00) => WpdFormat.Wp5,
        (0x0a, 0x02) => WpdFormat.Wp6,
        (0x2c, 0x02) or (0x2c, 0x03) or (0x2c, 0x04) => WpdFormat.Wp3,
        _ => WpdFormat.Unknown,
    };
}
