using System.IO.Compression;
using Xberg.Internal.Archive;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers how an archive member that claims to be text but is not gets decoded.</summary>
public class ArchiveTextDecodeTests
{
    /// <summary>
    /// A member reaches the decoder only once its extension has judged it textual, so failing
    /// to decode is not grounds for dropping it — the archive listing announces the member
    /// either way, and skipping it leaves the listing and the body disagreeing.
    /// </summary>
    [Fact]
    public void AMemberThatIsNotValidUtf8StillYieldsText()
    {
        // An AppleDouble sidecar's leading bytes: a magic number, not UTF-8.
        byte[] appleDouble = { 0x00, 0x05, 0x16, 0x07, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xFE, 0xFD };

        string decoded = ZipReader.DecodeArchiveText(appleDouble);

        Assert.NotNull(decoded);
        Assert.NotEqual(0, decoded.Length);
    }

    /// <summary>A leading byte-order mark is part of the encoding, not of the document.</summary>
    [Fact]
    public void AByteOrderMarkIsStripped()
    {
        byte[] withBom = { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };

        Assert.Equal("hi", ZipReader.DecodeArchiveText(withBom));
    }

    /// <summary>Valid UTF-8 is returned exactly, multi-byte characters included.</summary>
    [Fact]
    public void ValidUtf8SurvivesUnchanged()
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("héllo wörld");

        Assert.Equal("héllo wörld", ZipReader.DecodeArchiveText(utf8));
    }
}
