namespace Xberg.Internal.Qr;

/// <summary>
/// Arithmetic in GF(2^k), as the <c>g2p</c> crate generates it for rqrr: GF(16) with modulus
/// <c>0b1_0011</c> for the format bits, GF(256) with <c>0b1_0001_1101</c> for the data.
/// </summary>
/// <remarks>
/// Log and antilog tables are built once per field. The generator is 2 in both fields, which is
/// what <c>g2p</c> uses and what the QR spec assumes.
/// </remarks>
internal sealed class GaloisField
{
    /// <summary>GF(16), for the 15-bit BCH-coded format word.</summary>
    public static readonly GaloisField Gf16 = new(4, 0b1_0011);

    /// <summary>GF(256), for Reed-Solomon over the data codewords.</summary>
    public static readonly GaloisField Gf256 = new(8, 0b1_0001_1101);

    /// <summary>The field's generator element — 2 in both fields used here.</summary>
    public const byte Generator = 2;

    private readonly int _order;
    private readonly byte[] _exp;
    private readonly byte[] _log;

    private GaloisField(int bits, int modulus)
    {
        _order = 1 << bits;
        _exp = new byte[_order - 1];
        _log = new byte[_order];

        int x = 1;
        for (int i = 0; i < _order - 1; i++)
        {
            _exp[i] = (byte)x;
            _log[x] = (byte)i;
            x <<= 1;
            if ((x & _order) != 0) x ^= modulus;
        }
    }

    public static byte Add(byte a, byte b) => (byte)(a ^ b);

    public byte Mul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return _exp[(_log[a] + _log[b]) % (_order - 1)];
    }

    public byte Div(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException("division by the zero element of a Galois field");
        if (a == 0) return 0;
        int d = _log[a] - _log[b];
        if (d < 0) d += _order - 1;
        return _exp[d];
    }

    /// <summary>The generator raised to <paramref name="power"/>.</summary>
    /// <remarks>
    /// The exponent wraps at the multiplicative order, which rqrr relies on: it passes
    /// <c>255 - i</c> and <c>(i + 1) * j</c> without reducing them first.
    /// </remarks>
    public byte GeneratorPow(int power)
    {
        int e = power % (_order - 1);
        if (e < 0) e += _order - 1;
        return _exp[e];
    }
}
