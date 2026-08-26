using Xberg.Internal.Heif;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the HEIF-family container reader.
/// </summary>
/// <remarks>
/// Built on synthetic containers so each one states the rule it covers. The check that the
/// reader agrees with libheif runs against the real fixtures: for all four in the corpus its
/// dimensions match what libheif reports after decoding, and the EXIF block it isolates from
/// <c>test.heic</c> is byte-identical to the one libheif hands out.
/// </remarks>
public class HeifContainerTests
{
    // ------------------------------------------------------------------ Building blocks

    private static byte[] U32(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] U16(uint value) => [(byte)(value >> 8), (byte)value];

    /// <summary>A box: its size, its four-character type, then its payload.</summary>
    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        return [.. U32((uint)(body.Length + 8)), .. type.Select(c => (byte)c), .. body];
    }

    /// <summary>A full box, which carries a version and three flag bytes before its payload.</summary>
    private static byte[] FullBox(string type, byte version, uint flags, params byte[][] payload) =>
        Box(type, [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags],
            payload.SelectMany(p => p).ToArray());

    private static byte[] Ftyp(string majorBrand, params string[] compatible) =>
        Box("ftyp",
            majorBrand.Select(c => (byte)c).ToArray(),
            U32(0),
            compatible.SelectMany(b => b.Select(c => (byte)c)).ToArray());

    private static byte[] Ispe(uint width, uint height) =>
        FullBox("ispe", 0, 0, U32(width), U32(height));

    /// <summary>An item information entry naming one item's type.</summary>
    private static byte[] Infe(uint id, string type) =>
        FullBox("infe", 2, 0, U16(id), U16(0), type.Select(c => (byte)c).ToArray(), [0]);

    /// <summary>An item location entry: one extent, offsets and lengths four bytes wide.</summary>
    private static byte[] Iloc(params (uint Id, uint Offset, uint Length)[] items)
    {
        var body = new List<byte>
        {
            0x44,                                       // offset size 4, length size 4
            0x00,                                       // base offset size 0, index size 0
        };
        body.AddRange(U16((uint)items.Length));
        foreach (var (id, offset, length) in items)
        {
            body.AddRange(U16(id));
            body.AddRange(U16(0));                      // data reference index
            body.AddRange(U16(1));                      // one extent
            body.AddRange(U32(offset));
            body.AddRange(U32(length));
        }
        return FullBox("iloc", 0, 0, body.ToArray());
    }

    /// <summary>An association of one item with property indices, which are one-based.</summary>
    private static byte[] Ipma(uint itemId, params int[] indices)
    {
        var body = new List<byte>();
        body.AddRange(U32(1));                          // one entry
        body.AddRange(U16(itemId));
        body.Add((byte)indices.Length);
        foreach (int index in indices) body.Add((byte)index);
        return FullBox("ipma", 0, 0, body.ToArray());
    }

    private static byte[] Meta(params byte[][] children) => FullBox("meta", 0, 0, children);

    /// <summary>A minimal container: one coded item, its extent, and the properties given.</summary>
    private static byte[] Container(string brand, string itemType, byte[][] properties, int[] indices) =>
    [
        .. Ftyp(brand),
        .. Meta(
            FullBox("pitm", 0, 0, U16(1)),
            FullBox("iinf", 0, 0, U16(1), Infe(1, itemType)),
            Iloc((1, 0, 0)),
            Box("iprp", Box("ipco", properties.SelectMany(p => p).ToArray()), Ipma(1, indices))),
    ];

    /// <summary>
    /// A container whose single item's bytes are appended after the description.
    /// </summary>
    /// <remarks>
    /// The location box has to name where those bytes landed, which is only known once the
    /// description is built — so the description is built twice, the second time with the
    /// offset the first one measured.
    /// </remarks>
    private static byte[] ContainerWithItemData(
        byte[][] describedItems, uint dataItemId, byte[] data, byte[] properties)
    {
        byte[] Build(uint offset) =>
        [
            .. Ftyp("heic"),
            .. Meta(
                FullBox("pitm", 0, 0, U16(1)),
                FullBox("iinf", 0, 0, U16((uint)describedItems.Length),
                    describedItems.SelectMany(i => i).ToArray()),
                Iloc((1, 0, 0), (dataItemId, offset, (uint)data.Length)),
                Box("iprp", Box("ipco", properties), Ipma(1, 1))),
        ];

        uint dataOffset = (uint)Build(0).Length;
        return [.. Build(dataOffset), .. data];
    }

    // ------------------------------------------------------------------ Sniffing

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    [InlineData("avif")]
    [InlineData("avis")]
    [InlineData("avcs")]
    public void KnownBrandsAreHeif(string brand) =>
        Assert.True(HeifContainer.IsHeifContainer(Ftyp(brand)));

    /// <summary>
    /// A brand the file only claims compatibility with still identifies it.
    /// </summary>
    /// <remarks>
    /// Writers routinely put a generic major brand on a HEIF file and name the specific one
    /// among the compatible brands; reading only the major brand misses those files entirely.
    /// </remarks>
    [Fact]
    public void ACompatibleBrandIsEnough() =>
        Assert.True(HeifContainer.IsHeifContainer(Ftyp("qt  ", "isom", "heic")));

    [Fact]
    public void OtherFilesAreNotHeif()
    {
        Assert.False(HeifContainer.IsHeifContainer([]));
        Assert.False(HeifContainer.IsHeifContainer("hello world, at some length"u8));
        Assert.False(HeifContainer.IsHeifContainer(Ftyp("qt  ", "isom")));
    }

    // ------------------------------------------------------------------ Dimensions

    [Fact]
    public void DimensionsComeFromTheSpatialExtent()
    {
        var info = HeifContainer.TryRead(Container("heic", "hvc1", [Ispe(640, 480)], [1]));
        Assert.NotNull(info);
        Assert.Equal(640u, info.Width);
        Assert.Equal(480u, info.Height);
    }

    /// <summary>
    /// A clean aperture crops the coded picture, and the crop is what a viewer shows.
    /// </summary>
    /// <remarks>
    /// Coded pictures are padded out to the codec's block size, so a camera that shoots an odd
    /// number of rows stores an even number and crops one away. Reporting the coded extent puts
    /// the dimensions one pixel out on exactly the files most likely to be real photographs —
    /// which is what <c>test.heic</c> in the corpus is.
    /// </remarks>
    [Fact]
    public void ACleanApertureCropsTheReportedSize()
    {
        // A clean aperture is a plain box: its eight fields start where the payload does.
        var clap = Box("clap",
            U32(1652), U32(1), U32(1791), U32(1), U32(0), U32(1), U32(0), U32(1));
        var info = HeifContainer.TryRead(
            Container("heic", "hvc1", [Ispe(1652, 1792), clap], [1, 2]));

        Assert.NotNull(info);
        Assert.Equal(1652u, info.Width);
        Assert.Equal(1791u, info.Height);
    }

    /// <summary>A quarter turn swaps the dimensions; a half turn leaves them alone.</summary>
    [Theory]
    [InlineData(0, 640u, 480u)]
    [InlineData(1, 480u, 640u)]
    [InlineData(2, 640u, 480u)]
    [InlineData(3, 480u, 640u)]
    public void ARotationTurnsTheReportedSize(byte angle, uint width, uint height)
    {
        var irot = Box("irot", [angle]);
        var info = HeifContainer.TryRead(Container("heic", "hvc1", [Ispe(640, 480), irot], [1, 2]));

        Assert.NotNull(info);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
    }

    /// <summary>
    /// A grid image reports the size of the whole, not of one tile.
    /// </summary>
    /// <remarks>
    /// Phones store a large photograph as a grid of small coded tiles, and the grid item's own
    /// spatial extent describes a tile. Reading that would report a fraction of the picture.
    /// </remarks>
    [Fact]
    public void AGridReportsTheAssembledSize()
    {
        // The grid descriptor: version, flags, rows-1, columns-1, then the assembled size.
        byte[] descriptor = [0x00, 0x00, 0x01, 0x01, .. U16(4032), .. U16(3024)];
        var bytes = ContainerWithItemData(
            [Infe(1, "grid")], dataItemId: 1, descriptor, Ispe(2016, 1512));

        var info = HeifContainer.TryRead(bytes);
        Assert.NotNull(info);
        Assert.Equal(4032u, info.Width);
        Assert.Equal(3024u, info.Height);
    }

    /// <summary>Only the primary item's properties are read, not the thumbnail's.</summary>
    [Fact]
    public void PropertiesOfOtherItemsAreNotUsed()
    {
        byte[] bytes =
        [
            .. Ftyp("heic"),
            .. Meta(
                FullBox("pitm", 0, 0, U16(1)),
                FullBox("iinf", 0, 0, U16(2), Infe(1, "hvc1"), Infe(2, "hvc1")),
                Iloc((1, 0, 0), (2, 0, 0)),
                Box("iprp",
                    Box("ipco", Ispe(1600, 1200), Ispe(160, 120)),
                    FullBox("ipma", 0, 0,
                        U32(2),
                        U16(1), [1], [1],
                        U16(2), [1], [2]))),
        ];

        var info = HeifContainer.TryRead(bytes);
        Assert.NotNull(info);
        Assert.Equal(1600u, info.Width);
        Assert.Equal(1200u, info.Height);
    }

    // ------------------------------------------------------------------ EXIF

    /// <summary>
    /// The EXIF item's payload begins with an offset to the TIFF header, not the header itself.
    /// </summary>
    /// <remarks>
    /// Handing the payload straight to an EXIF reader has it parsing that offset as a byte-order
    /// mark, and every field comes out wrong or missing.
    /// </remarks>
    [Fact]
    public void TheExifItemSkipsItsHeaderOffset()
    {
        byte[] tiff = [(byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08];
        byte[] payload = [.. U32(0), .. tiff];

        var bytes = ContainerWithItemData(
            [Infe(1, "hvc1"), Infe(2, "Exif")], dataItemId: 2, payload, Ispe(64, 64));

        var info = HeifContainer.TryRead(bytes);
        Assert.NotNull(info);
        Assert.Equal(tiff, info.Exif);
    }

    [Fact]
    public void AFileWithoutExifReportsNone()
    {
        var info = HeifContainer.TryRead(Container("heic", "hvc1", [Ispe(64, 64)], [1]));
        Assert.NotNull(info);
        Assert.Null(info.Exif);
    }

    // ------------------------------------------------------------------ Malformed input

    /// <summary>A truncated box costs what follows it, not an exception.</summary>
    [Fact]
    public void ATruncatedContainerIsRefusedNotThrown()
    {
        var full = Container("heic", "hvc1", [Ispe(64, 64)], [1]);
        for (int cut = 1; cut < full.Length; cut++)
            HeifContainer.TryRead(full[..cut]);          // must not throw

        Assert.Null(HeifContainer.TryRead(Ftyp("heic")));
    }

    /// <summary>A box claiming to be larger than its parent ends the walk.</summary>
    [Fact]
    public void AnOversizedBoxIsRefused()
    {
        var bytes = Container("heic", "hvc1", [Ispe(64, 64)], [1]);
        int meta = Array.IndexOf(bytes, (byte)'m');
        bytes[meta - 4] = 0x7F;                          // a size far past the end of the file
        Assert.Null(HeifContainer.TryRead(bytes));
    }
}
