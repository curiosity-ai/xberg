using System.Text;

namespace Xberg.Internal.Qr;

/// <summary>A square of black/white modules, read by <see cref="QrDecoder"/>.</summary>
internal interface IBitGrid
{
    int Size { get; }

    /// <summary>Whether the module at (<paramref name="y"/>, <paramref name="x"/>) is dark.</summary>
    bool Bit(int y, int x);
}

/// <summary>What the format bits said about a grid.</summary>
internal readonly record struct QrMetaData(int Version, int EccLevel, int Mask);

/// <summary>
/// Decode a QR grid into its payload, ported from rqrr's <c>decode.rs</c>: format bits and their
/// BCH correction, the zig-zag codeword read, de-interleaving, Reed-Solomon correction, and the
/// mode segments.
/// </summary>
internal static class QrDecoder
{
    private const int MaxPayloadSize = 8896;

    /// <summary>The grid read with its axes swapped, for a code photographed through a mirror.</summary>
    private sealed class MirroredGrid : IBitGrid
    {
        private readonly IBitGrid _inner;
        public MirroredGrid(IBitGrid inner) => _inner = inner;
        public int Size => _inner.Size;
        public bool Bit(int y, int x) => _inner.Bit(x, y);
    }

    /// <summary>
    /// Decode <paramref name="code"/>, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// A failed read is retried against the mirrored grid before giving up, which is how a code
    /// photographed in a mirror — or read from the back of a transparency — still decodes. The
    /// first failure is the one reported if both fail.
    /// </remarks>
    public static byte[]? Decode(IBitGrid code)
    {
        var result = TryDecode(code);
        if (result is not null) return result;
        return TryDecode(new MirroredGrid(code));
    }

    private static byte[]? TryDecode(IBitGrid code)
    {
        if (ReadFormat(code) is not { } meta) return null;
        var raw = ReadData(code, meta);
        if (CodestreamEcc(meta, raw) is not { } stream) return null;
        return DecodePayload(meta, stream);
    }

    // ── format ───────────────────────────────────────────────────────────────

    private static QrMetaData? ReadFormat(IBitGrid code)
    {
        // The format word is written twice; the second copy is tried only if the first fails
        // its own error correction.
        int[] xs = { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
        int[] ys = { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };

        int format = 0;
        for (int i = 14; i >= 0; i--)
            format = (format << 1) | (code.Bit(ys[i], xs[i]) ? 1 : 0);
        format ^= 0x5412;

        int? verified = CorrectFormat(format);
        if (verified is null)
        {
            format = 0;
            for (int i = 0; i < 7; i++)
                format = (format << 1) | (code.Bit(code.Size - 1 - i, 8) ? 1 : 0);
            for (int i = 0; i < 8; i++)
                format = (format << 1) | (code.Bit(8, code.Size - 8 + i) ? 1 : 0);
            format ^= 0x5412;
            verified = CorrectFormat(format);
        }
        if (verified is not { } word) return null;

        int fdata = word >> 10;
        int version = (code.Size - 17) / 4;
        if (version <= 0 || version > 40) return null;

        return new QrMetaData(version, fdata >> 3, fdata & 7);
    }

    /// <summary>Correct the 15-bit BCH-coded format word, or null when it is beyond repair.</summary>
    private static int? CorrectFormat(int word)
    {
        var syndromes = FormatSyndromes(word);
        if (syndromes is null) return word;

        var sigma = BerlekampMassey(GaloisField.Gf16, syndromes, 6);
        for (int i = 0; i < 15; i++)
            if (PolyEval(GaloisField.Gf16, sigma, GaloisField.Gf16.GeneratorPow(15 - i)) == 0)
                word ^= 1 << i;

        return FormatSyndromes(word) is null ? word : null;
    }

    /// <summary>Syndromes of the format word, or null when they are all zero.</summary>
    private static byte[]? FormatSyndromes(int u)
    {
        var result = new byte[64];
        bool nonzero = false;
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 15; j++)
                if ((u & (1 << j)) != 0)
                    result[i] = GaloisField.Add(result[i], GaloisField.Gf16.GeneratorPow((i + 1) * j));
            if (result[i] != 0) nonzero = true;
        }
        return nonzero ? result : null;
    }

    // ── raw bits ─────────────────────────────────────────────────────────────

    private sealed class RawData
    {
        public readonly byte[] Data = new byte[MaxPayloadSize];
        public int Len;

        public void Push(bool bit)
        {
            int bitpos = Len & 7;
            int bytepos = Len >> 3;
            if (bit) Data[bytepos] |= (byte)(0x80 >> bitpos);
            Len++;
        }
    }

    /// <summary>Read the codewords in the zig-zag order, unmasking as it goes.</summary>
    private static RawData ReadData(IBitGrid code, QrMetaData meta)
    {
        var ds = new RawData();

        int y = code.Size - 1;
        int x = code.Size - 1;
        bool negDir = true;

        while (x > 0)
        {
            if (x == 6) x -= 1;
            if (!ReservedCell(meta.Version, y, x)) ds.Push(ReadBit(code, meta, y, x));
            if (!ReservedCell(meta.Version, y, x - 1)) ds.Push(ReadBit(code, meta, y, x - 1));

            if (y == 0 && negDir) { x = Math.Max(0, x - 2); y = 0; negDir = false; }
            else if (!negDir && y == code.Size - 1) { x = Math.Max(0, x - 2); y = code.Size - 1; negDir = true; }
            else if (negDir) y -= 1;
            else y += 1;
        }

        return ds;
    }

    private static bool ReadBit(IBitGrid code, QrMetaData meta, int y, int x)
    {
        bool v = code.Bit(y, x);
        if (MaskBit(meta.Mask, y, x)) v = !v;
        return v;
    }

    private static bool MaskBit(int mask, int y, int x) => mask switch
    {
        0 => (y + x) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (y + x) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => (y * x) % 2 + (y * x) % 3 == 0,
        6 => ((y * x) % 2 + (y * x) % 3) % 2 == 0,
        7 => ((y * x) % 3 + (y + x) % 2) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    /// <summary>Whether a module carries function patterns rather than data.</summary>
    private static bool ReservedCell(int version, int i, int j)
    {
        var ver = QrVersionDb.Versions[version];
        int size = version * 4 + 17;

        if (i < 9 && j < 9) return true;                  // finder + format, top left
        if (i + 8 >= size && j < 9) return true;          // bottom left
        if (i < 9 && j + 8 >= size) return true;          // top right
        if (i == 6 || j == 6) return true;                // timing patterns

        // Version info sits beside the top-right and bottom-left finders, three rows deep,
        // bounded by the timing pattern.
        if (version >= 7)
        {
            if (i < 6 && j + 11 >= size) return true;
            if (i + 11 >= size && j < 6) return true;
        }

        int? ai = null, aj = null;
        int len = 0;
        for (int a = 0; a < ver.Apat.Length && ver.Apat[a] != 0; a++)
        {
            len = a;
            if (Math.Abs(ver.Apat[a] - i) < 3) ai = a;
            if (Math.Abs(ver.Apat[a] - j) < 3) aj = a;
        }

        if (ai is not { } x2 || aj is not { } y2) return false;
        if (x2 == len && y2 == len) return true;
        if (0 < x2 && x2 < len) return true;
        if (0 < y2 && y2 < len) return true;
        return false;
    }

    // ── error correction ─────────────────────────────────────────────────────

    private sealed class CorrectedStream
    {
        public readonly byte[] Data = new byte[MaxPayloadSize];
        public int Ptr;
        public int BitLen;

        public int BitsRemaining => BitLen - Ptr;

        public int TakeBits(int nbits)
        {
            int ret = 0;
            int maxLen = Math.Min(BitsRemaining, nbits);
            for (int i = 0; i < maxLen; i++)
            {
                byte b = Data[Ptr >> 3];
                int bitpos = Ptr & 7;
                ret <<= 1;
                if (((b << bitpos) & 0x80) != 0) ret |= 1;
                Ptr++;
            }
            return ret;
        }
    }

    /// <summary>De-interleave the blocks and correct each one, or null if any is beyond repair.</summary>
    private static CorrectedStream? CodestreamEcc(QrMetaData meta, RawData ds)
    {
        var output = new CorrectedStream();

        var ver = QrVersionDb.Versions[meta.Version];
        var sbEcc = ver.Ecc[meta.EccLevel];
        var lbEcc = new RsParameters(sbEcc.Bs + 1, sbEcc.Dw + 1, sbEcc.Ns);

        int lbCount = (ver.DataBytes - sbEcc.Bs * sbEcc.Ns) / (sbEcc.Bs + 1);
        int bc = lbCount + sbEcc.Ns;
        int eccOffset = sbEcc.Dw * bc + lbCount;

        int dstOffset = 0;
        for (int i = 0; i < bc; i++)
        {
            var ecc = i < sbEcc.Ns ? sbEcc : lbEcc;
            var block = new byte[ecc.Bs];
            int numEc = ecc.Bs - ecc.Dw;

            for (int j = 0; j < ecc.Dw; j++) block[j] = ds.Data[j * bc + i];
            for (int j = 0; j < numEc; j++) block[ecc.Dw + j] = ds.Data[eccOffset + j * bc + i];

            if (!CorrectBlock(block, ecc)) return null;

            Array.Copy(block, 0, output.Data, dstOffset, ecc.Dw);
            dstOffset += ecc.Dw;
        }

        output.BitLen = dstOffset * 8;
        return output;
    }

    private static bool CorrectBlock(byte[] block, RsParameters ecc)
    {
        var gf = GaloisField.Gf256;
        int npar = ecc.Bs - ecc.Dw;

        var s = BlockSyndromes(block, ecc.Bs, npar);
        if (s is null) return true;   // already clean

        var sigma = BerlekampMassey(gf, s, npar);

        // The derivative of sigma: in characteristic 2 only the odd-degree terms survive.
        var sigmaDeriv = new byte[64];
        for (int i = 1; i < 64; i += 2) sigmaDeriv[i - 1] = sigma[i];

        var omega = ElocPoly(gf, s, sigma, npar - 1);

        for (int i = 0; i < ecc.Bs; i++)
        {
            byte xinv = gf.GeneratorPow(255 - i);
            if (PolyEval(gf, sigma, xinv) != 0) continue;

            byte sdX = PolyEval(gf, sigmaDeriv, xinv);
            byte omegaX = PolyEval(gf, omega, xinv);
            if (sdX == 0) return false;

            byte error = gf.Div(omegaX, sdX);
            block[ecc.Bs - i - 1] = GaloisField.Add(block[ecc.Bs - i - 1], error);
        }

        return BlockSyndromes(block, ecc.Bs, npar) is null;
    }

    /// <summary>Syndromes of a block, or null when they are all zero.</summary>
    private static byte[]? BlockSyndromes(byte[] block, int len, int npar)
    {
        var gf = GaloisField.Gf256;
        var s = new byte[64];
        bool nonzero = false;

        for (int i = 0; i < npar; i++)
        {
            for (int j = 0; j < len; j++)
            {
                byte c = block[len - 1 - j];
                s[i] = GaloisField.Add(s[i], gf.Mul(c, gf.GeneratorPow(i * j)));
            }
            if (s[i] != 0) nonzero = true;
        }

        return nonzero ? s : null;
    }

    private static byte PolyEval(GaloisField gf, byte[] s, byte x)
    {
        byte sum = 0;
        byte xPow = 1;
        for (int i = 0; i < 64; i++)
        {
            sum = GaloisField.Add(sum, gf.Mul(s[i], xPow));
            xPow = gf.Mul(xPow, x);
        }
        return sum;
    }

    /// <summary>The error-evaluator polynomial.</summary>
    private static byte[] ElocPoly(GaloisField gf, byte[] s, byte[] sigma, int npar)
    {
        var omega = new byte[64];
        for (int i = 0; i < npar; i++)
        {
            byte a = sigma[i];
            for (int j = 0; j < npar - i; j++)
                omega[i + j] = GaloisField.Add(omega[i + j], gf.Mul(a, s[j + 1]));
        }
        return omega;
    }

    /// <summary>Berlekamp-Massey, for the error-locator polynomial.</summary>
    private static byte[] BerlekampMassey(GaloisField gf, byte[] s, int n)
    {
        var ts = new byte[64];
        var cs = new byte[64];
        var bs = new byte[64];
        int l = 0;
        int m = 1;
        byte b = 1;
        bs[0] = 1;
        cs[0] = 1;

        for (int k = 0; k < n; k++)
        {
            byte d = s[k];
            for (int i = 1; i <= l; i++) d = GaloisField.Add(d, gf.Mul(cs[i], s[k - i]));

            byte mult = gf.Div(d, b);

            if (d == 0) m += 1;
            else if (l * 2 <= k)
            {
                Array.Copy(cs, ts, 64);
                PolyAdd(gf, cs, bs, mult, m);
                Array.Copy(ts, bs, 64);
                l = k + 1 - l;
                b = d;
                m = 1;
            }
            else
            {
                PolyAdd(gf, cs, bs, mult, m);
                m += 1;
            }
        }

        return cs;
    }

    private static void PolyAdd(GaloisField gf, byte[] dst, byte[] src, byte c, int shift)
    {
        if (c == 0) return;
        for (int i = 0; i < 64; i++)
        {
            int p = i + shift;
            if (p >= 64) break;
            dst[p] = GaloisField.Add(dst[p], gf.Mul(src[i], c));
        }
    }

    // ── payload ──────────────────────────────────────────────────────────────

    private static byte[]? DecodePayload(QrMetaData meta, CorrectedStream ds)
    {
        var output = new List<byte>();

        while (ds.BitsRemaining >= 4)
        {
            int ty = ds.TakeBits(4);
            bool ok = ty switch
            {
                0 => false,                                    // terminator
                1 => DecodeNumeric(meta, ds, output),
                2 => DecodeAlpha(meta, ds, output),
                4 => DecodeByte(meta, ds, output),
                7 => DecodeEci(ds),
                8 => DecodeKanji(meta, ds, output),
                _ => throw new FormatException("unknown QR data type"),
            };
            if (ty == 0) break;
            if (!ok) return null;
        }

        return output.ToArray();
    }

    /// <summary>Consume an ECI designator. The value is read and discarded, as upstream does.</summary>
    private static bool DecodeEci(CorrectedStream ds)
    {
        if (ds.BitsRemaining < 8) return false;
        int eci = ds.TakeBits(8);
        if ((eci & 0xc0) == 0x80)
        {
            if (ds.BitsRemaining < 8) return false;
            ds.TakeBits(8);
        }
        else if ((eci & 0xe0) == 0xc0)
        {
            if (ds.BitsRemaining < 16) return false;
            ds.TakeBits(16);
        }
        return true;
    }

    private static bool DecodeKanji(QrMetaData meta, CorrectedStream ds, List<byte> output)
    {
        int nbits = meta.Version <= 9 ? 8 : meta.Version <= 26 ? 10 : 12;
        int count = ds.TakeBits(nbits);
        if (ds.BitsRemaining < count * 13) return false;

        for (int i = 0; i < count; i++)
        {
            int d = ds.TakeBits(13);
            int msB = d / 0xc0;
            int lsB = d % 0xc0;
            int intermediate = (msB << 8) | lsB;
            // Shift-JIS is split into two ranges; which one a value lands in decides the offset.
            int sjw = intermediate + 0x8140 <= 0x9ffc ? intermediate + 0x8140 : intermediate + 0xc140;
            output.Add((byte)(sjw >> 8));
            output.Add((byte)(sjw & 0xff));
        }
        return true;
    }

    private static bool DecodeByte(QrMetaData meta, CorrectedStream ds, List<byte> output)
    {
        int nbits = meta.Version <= 9 ? 8 : 16;
        int count = ds.TakeBits(nbits);
        if (ds.BitsRemaining < count * 8) return false;

        for (int i = 0; i < count; i++) output.Add((byte)ds.TakeBits(8));
        return true;
    }

    private const string AlphaMap = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:\0";

    private static bool DecodeAlpha(QrMetaData meta, CorrectedStream ds, List<byte> output)
    {
        int nbits = meta.Version <= 9 ? 9 : meta.Version <= 26 ? 11 : 13;
        int count = ds.TakeBits(nbits);
        var buf = new byte[2];

        while (count >= 2)
        {
            if (!AlphaTuple(buf, ds, 11, 2)) return false;
            output.Add(buf[0]);
            output.Add(buf[1]);
            count -= 2;
        }
        if (count == 1)
        {
            if (!AlphaTuple(buf, ds, 6, 1)) return false;
            output.Add(buf[0]);
        }
        return true;
    }

    private static bool AlphaTuple(byte[] buf, CorrectedStream ds, int nbits, int digits)
    {
        if (ds.BitsRemaining < nbits) return false;
        int tuple = ds.TakeBits(nbits);
        for (int i = digits - 1; i >= 0; i--)
        {
            buf[i] = (byte)AlphaMap[tuple % 45];
            tuple /= 45;
        }
        return true;
    }

    private static bool DecodeNumeric(QrMetaData meta, CorrectedStream ds, List<byte> output)
    {
        int nbits = meta.Version <= 9 ? 10 : meta.Version <= 26 ? 12 : 14;
        int count = ds.TakeBits(nbits);
        var buf = new byte[3];

        while (count >= 3)
        {
            if (!NumericTuple(buf, ds, 10, 3)) return false;
            output.AddRange(buf);
            count -= 3;
        }
        if (count == 2)
        {
            if (!NumericTuple(buf, ds, 7, 2)) return false;
            output.Add(buf[0]);
            output.Add(buf[1]);
            count -= 2;
        }
        if (count == 1)
        {
            if (!NumericTuple(buf, ds, 4, 1)) return false;
            output.Add(buf[0]);
        }
        return true;
    }

    private static bool NumericTuple(byte[] buf, CorrectedStream ds, int nbits, int digits)
    {
        if (ds.BitsRemaining < nbits) return false;
        int tuple = ds.TakeBits(nbits);
        for (int i = digits - 1; i >= 0; i--)
        {
            buf[i] = (byte)('0' + tuple % 10);
            tuple /= 10;
        }
        return true;
    }
}
