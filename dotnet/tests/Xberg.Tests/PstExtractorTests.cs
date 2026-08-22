using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.Pst;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the Outlook personal-folders (.pst) extractor and the NDB/LTP reader beneath it.
/// Ports the intent of the Rust `extractors/pst.rs` and `extraction/pst.rs` tests, plus coverage
/// of the byte-level primitives (CRC, block obfuscation, node/heap ids) they sit on.
/// </summary>
public class PstExtractorTests
{
    // ── the extractor's contract ────────────────────────────────────────────

    [Fact]
    public void SupportedMimeTypesIsExactlyPst()
    {
        var mimeTypes = new PstExtractor().SupportedMimeTypes.ToList();
        Assert.Single(mimeTypes);
        Assert.Contains("application/vnd.ms-outlook-pst", mimeTypes);
    }

    [Fact]
    public void RegistryRoutesPstMimeToThePstExtractor()
    {
        var extractor = Registry.RegisterDefaults().ForMime("application/vnd.ms-outlook-pst");
        Assert.IsType<PstExtractor>(extractor);
    }

    [Fact]
    public void PstMagicIsDetectedAsPst()
    {
        var bytes = new byte[] { 0x21, 0x42, 0x44, 0x4E, 0, 0, 0, 0 };
        Assert.Equal("application/vnd.ms-outlook-pst", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void InvalidDataFailsToExtract()
    {
        var extractor = new PstExtractor();
        Assert.ThrowsAny<Exception>(() =>
            extractor.Extract(Encoding.ASCII.GetBytes("not a pst file"), "application/vnd.ms-outlook-pst", new ExtractionConfig()));
    }

    [Fact]
    public void TruncatedHeaderMagicIsRejectedEvenWithTheRightPrefix()
    {
        // Right magic, nothing behind it: the reader must not read past what it was given.
        var bytes = new byte[64];
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E;
        Assert.Throws<InvalidDataException>(() => PstStore.Open(bytes));
    }

    [Fact]
    public void HeaderCrcMismatchIsRejected()
    {
        byte[]? pst = ReadCorpusPst();
        if (pst is null) return; // fixture-gated

        pst[100] ^= 0xFF; // inside the CRC-covered header range
        Assert.Throws<InvalidDataException>(() => PstStore.Open(pst));
    }

    // ── the corpus fixture: an empty store ──────────────────────────────────

    [Fact]
    public void EmptyStoreExtractsZeroMessages()
    {
        byte[]? pst = ReadCorpusPst();
        if (pst is null) return; // fixture-gated

        var doc = new PstExtractor().Extract(pst, "application/vnd.ms-outlook-pst", new ExtractionConfig());

        Assert.Equal("pst", doc.SourceFormat);
        Assert.Equal("application/vnd.ms-outlook-pst", doc.MimeType);
        Assert.Empty(doc.Elements);
        Assert.Empty(doc.ProcessingWarnings);

        Assert.Equal("pst", doc.Metadata.Format!.FormatType);
        Assert.Equal(0, Assert.IsType<PstMetadata>(doc.Metadata.Format.Payload).MessageCount);
        Assert.Equal(0, doc.Metadata.Additional["message_count"].GetInt32());
        Assert.Null(doc.Metadata.Subject);
        Assert.Null(doc.Metadata.CreatedAt);
    }

    [Fact]
    public void EmptyStoreStillExposesItsFolderTree()
    {
        byte[]? pst = ReadCorpusPst();
        if (pst is null) return; // fixture-gated

        // The store is empty of mail, not of structure: the IPM sub-tree opens, and the root
        // folder's hierarchy table lists the top-level folders the walk seeds itself from.
        var store = PstStore.Open(pst);
        var ipmRoot = store.OpenFolder(store.IpmSubTreeEntryId());
        Assert.False(string.IsNullOrEmpty(ipmRoot.DisplayName));

        var rootFolder = store.OpenFolder(store.MakeEntryId(PstNodeType.RootFolderNid));
        var hierarchy = rootFolder.HierarchyTable();
        Assert.NotNull(hierarchy);
        Assert.NotEmpty(hierarchy!.Rows);

        // Every top-level folder opens, and none of them holds a message.
        foreach (var row in hierarchy.Rows)
        {
            var folder = store.OpenFolder(store.MakeEntryId(PstTableContext.RowId(row)));
            Assert.Empty(folder.ContentsTable()?.Rows ?? new List<byte[]>());
        }
    }

    // ── body resolution ─────────────────────────────────────────────────────

    [Fact]
    public void BodyResolutionPrefersPlainText() =>
        Assert.Equal("plain body", PstExtraction.ResolveBody("plain body", "<p>html body</p>", "rtf body"));

    [Fact]
    public void BodyResolutionCleansHtmlWhenPlainIsAbsent() =>
        Assert.Equal("Hello World", PstExtraction.ResolveBody(null, "<p>Hello <b>World</b></p>", "rtf body"));

    [Fact]
    public void BodyResolutionFallsBackToRtf() =>
        Assert.Equal("rtf-derived plain text", PstExtraction.ResolveBody(null, null, "rtf-derived plain text"));

    [Fact]
    public void BodyResolutionYieldsNothingWhenNoSourceHasContent() =>
        Assert.Equal("", PstExtraction.ResolveBody(null, "", null));

    // ── timestamps ──────────────────────────────────────────────────────────

    [Fact]
    public void FileTimeConvertsToRfc3339WithSecondPrecision()
    {
        // 2001-01-01T00:00:00Z as 100ns intervals since 1601.
        const long filetime = 116_444_736_000_000_000L + 978_307_200L * 10_000_000L;
        Assert.Equal("2001-01-01T00:00:00Z", PstExtraction.WindowsFileTimeToString(filetime));
    }

    [Fact]
    public void FileTimeBeforeTheUnixEpochIsReportedAsInvalid() =>
        Assert.Equal("(invalid timestamp: 0)", PstExtraction.WindowsFileTimeToString(0));

    // ── byte-level primitives ───────────────────────────────────────────────

    [Fact]
    public void CrcUsesAZeroSeedAndNoFinalComplement()
    {
        // The same reflected polynomial as CRC-32, but seeded with zero and left uncomplemented,
        // so the familiar 0xCBF43926 check value does not apply: over "123456789" it lands here.
        Assert.Equal(0x2DFD_2D88u, PstCrc.Compute(0, Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void CrcOfNothingIsTheSeed() => Assert.Equal(0u, PstCrc.Compute(0, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void PermuteDecodeIsAPermutationOfEveryByte()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;

        PstEncoding.PermuteDecode(data);
        Assert.Equal(256, data.Distinct().Count());
    }

    [Fact]
    public void CyclicDecodeDependsOnTheKeyAndThePosition()
    {
        var a = Encoding.ASCII.GetBytes("Hello, World!");
        var b = (byte[])a.Clone();

        PstEncoding.CyclicDecode(a, 0x1234_5678);
        PstEncoding.CyclicDecode(b, 0x8765_4321);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, Encoding.ASCII.GetBytes("Hello, World!"));
    }

    [Fact]
    public void NodeIdsSplitIntoTypeAndIndex()
    {
        // NID_ROOT_FOLDER is a normal folder (type 0x02) at index 9.
        Assert.Equal(PstNodeType.NormalFolder, PstNodeType.TypeOf(PstNodeType.RootFolderNid));
        Assert.Equal(9u, PstNodeType.IndexOf(PstNodeType.RootFolderNid));

        // A folder's tables are the same index under a different type.
        uint hierarchy = PstNodeType.Make(PstNodeType.HierarchyTable, PstNodeType.IndexOf(PstNodeType.RootFolderNid));
        Assert.Equal(PstNodeType.HierarchyTable, PstNodeType.TypeOf(hierarchy));
        Assert.Equal(9u, PstNodeType.IndexOf(hierarchy));
    }

    [Fact]
    public void UnsupportedPropertyTypesAreRejected()
    {
        Assert.Equal(PstPropertyType.Unicode, PstPropertyTypes.Parse(0x001F));
        Assert.Throws<InvalidDataException>(() => PstPropertyTypes.Parse(0x0000));
    }

    [Fact]
    public void StringValuesDecodePerTheirPropertyType()
    {
        var unicode = PstValue.Read(Encoding.Unicode.GetBytes("héllo\0trailing"), PstPropertyType.Unicode);
        Assert.Equal("héllo", unicode.AsString());

        var string8 = PstValue.Read(new byte[] { 0x41, 0x42, 0x00, 0x43 }, PstPropertyType.String8);
        Assert.Equal("AB", string8.AsString());

        Assert.Null(PstValue.Read(new byte[8], PstPropertyType.Time).AsString());
    }

    // ── fixture lookup ──────────────────────────────────────────────────────

    private static byte[]? ReadCorpusPst()
    {
        string? path = FindCorpusFile(Path.Combine("email", "empty.pst"));
        return path is null ? null : File.ReadAllBytes(path);
    }

    /// <summary>Locate a corpus fixture, whether the checkout sits at /workspace or elsewhere.</summary>
    private static string? FindCorpusFile(string relative)
    {
        string absolute = Path.Combine("/workspace/test_documents", relative);
        if (File.Exists(absolute)) return absolute;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "test_documents", relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
