using Xberg.Internal.Blake3;
using Xunit;

namespace Xberg.Tests;

public class Blake3Tests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // Official BLAKE3 test vectors use input byte i => (i % 251).
    private static byte[] Pattern(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(i % 251);
        return b;
    }

    [Fact]
    public void EmptyInput_MatchesOfficialVector()
    {
        Assert.Equal(
            "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
            Hex(Blake3Hasher.Hash(ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void SingleZeroByte_MatchesOfficialVector()
    {
        Assert.Equal(
            "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213",
            Hex(Blake3Hasher.Hash(new byte[] { 0x00 })));
    }

    [Fact]
    public void Length1024_MatchesOfficialVector()
    {
        Assert.Equal(
            "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7",
            Hex(Blake3Hasher.Hash(Pattern(1024))));
    }

    [Fact]
    public void Length1023_MatchesOfficialVector()
    {
        Assert.Equal(
            "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35bba72b28f70bd11",
            Hex(Blake3Hasher.Hash(Pattern(1023))));
    }

    [Fact]
    public void IncrementalUpdatesMatchSinglePass()
    {
        var data = Pattern(3000);
        var single = Blake3Hasher.Hash(data);

        var hasher = new Blake3Hasher();
        hasher.Update(data.AsSpan(0, 500));
        hasher.Update(data.AsSpan(500, 1000));
        hasher.Update(data.AsSpan(1500));
        var outBytes = new byte[32];
        hasher.Finalize(outBytes);

        Assert.Equal(Hex(single), Hex(outBytes));
    }
}
