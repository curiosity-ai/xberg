// Ported from the `outlook-pst` crate's src/crc.rs.
//
// [MS-PST] 5.3's CRC is the ordinary reflected CRC-32 polynomial with a zero seed and no final
// complement. Upstream unrolls it across eight shifted tables for speed; the single-table form
// below computes the same value, which is all the header and block trailers are checked against.

namespace Xberg.Internal.Pst;

internal static class PstCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(uint seed, ReadOnlySpan<byte> data)
    {
        uint crc = seed;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
