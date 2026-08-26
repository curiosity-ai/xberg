// Generated from rqrr 0.10.1's `version_db.rs`, itself the QR-code version information database
// of ISO/IEC 18004. Regenerate together after a bump.

namespace Xberg.Internal.Qr;

/// <summary>Reed-Solomon block parameters for one error-correction level.</summary>
/// <param name="Bs">Small block size, in codewords.</param>
/// <param name="Dw">Data words in a small block.</param>
/// <param name="Ns">Number of small blocks.</param>
internal readonly record struct RsParameters(int Bs, int Dw, int Ns);

/// <summary>Everything the decoder needs to know about one QR version.</summary>
internal sealed class QrVersionInfo
{
    public required int DataBytes { get; init; }

    /// <summary>Alignment-pattern centre coordinates, zero-terminated.</summary>
    public required int[] Apat { get; init; }

    /// <summary>Block parameters indexed by error-correction level (0..3).</summary>
    public required RsParameters[] Ecc { get; init; }
}

internal static class QrVersionDb
{
    /// <summary>Indexed by version; entry 0 is a placeholder so the index is the version.</summary>
    public static readonly QrVersionInfo[] Versions =
    {
        new() { DataBytes = 0, Apat = new[] { 0, 0, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0) } },
        new() { DataBytes = 26, Apat = new[] { 0, 0, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(26, 16, 1), new(26, 19, 1), new(26, 9, 1), new(26, 13, 1) } },
        new() { DataBytes = 44, Apat = new[] { 6, 18, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(44, 28, 1), new(44, 34, 1), new(44, 16, 1), new(44, 22, 1) } },
        new() { DataBytes = 70, Apat = new[] { 6, 22, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(70, 44, 1), new(70, 55, 1), new(35, 13, 2), new(35, 17, 2) } },
        new() { DataBytes = 100, Apat = new[] { 6, 26, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(50, 32, 2), new(100, 80, 1), new(25, 9, 4), new(50, 24, 2) } },
        new() { DataBytes = 134, Apat = new[] { 6, 30, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(67, 43, 2), new(134, 108, 1), new(33, 11, 2), new(33, 15, 2) } },
        new() { DataBytes = 172, Apat = new[] { 6, 34, 0, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(43, 27, 4), new(86, 68, 2), new(43, 15, 4), new(43, 19, 4) } },
        new() { DataBytes = 196, Apat = new[] { 6, 22, 38, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(49, 31, 4), new(98, 78, 2), new(39, 13, 4), new(32, 14, 2) } },
        new() { DataBytes = 242, Apat = new[] { 6, 24, 42, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(60, 38, 2), new(121, 97, 2), new(40, 14, 4), new(40, 18, 4) } },
        new() { DataBytes = 292, Apat = new[] { 6, 26, 46, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(58, 36, 3), new(146, 116, 2), new(36, 12, 4), new(36, 16, 4) } },
        new() { DataBytes = 346, Apat = new[] { 6, 28, 50, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(69, 43, 4), new(86, 68, 2), new(43, 15, 6), new(43, 19, 6) } },
        new() { DataBytes = 404, Apat = new[] { 6, 30, 54, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(80, 50, 1), new(101, 81, 4), new(36, 12, 3), new(50, 22, 4) } },
        new() { DataBytes = 466, Apat = new[] { 6, 32, 58, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(58, 36, 6), new(116, 92, 2), new(42, 14, 7), new(46, 20, 4) } },
        new() { DataBytes = 532, Apat = new[] { 6, 34, 62, 0, 0, 0, 0 }, Ecc = new RsParameters[] { new(59, 37, 8), new(133, 107, 4), new(33, 11, 12), new(44, 20, 8) } },
        new() { DataBytes = 581, Apat = new[] { 6, 26, 46, 66, 0, 0, 0 }, Ecc = new RsParameters[] { new(64, 40, 4), new(145, 115, 3), new(36, 12, 11), new(36, 16, 11) } },
        new() { DataBytes = 655, Apat = new[] { 6, 26, 48, 70, 0, 0, 0 }, Ecc = new RsParameters[] { new(65, 41, 5), new(109, 87, 5), new(36, 12, 11), new(54, 24, 5) } },
        new() { DataBytes = 733, Apat = new[] { 6, 26, 50, 74, 0, 0, 0 }, Ecc = new RsParameters[] { new(73, 45, 7), new(122, 98, 5), new(45, 15, 3), new(43, 19, 15) } },
        new() { DataBytes = 815, Apat = new[] { 6, 30, 54, 78, 0, 0, 0 }, Ecc = new RsParameters[] { new(74, 46, 10), new(135, 107, 1), new(42, 14, 2), new(50, 22, 1) } },
        new() { DataBytes = 901, Apat = new[] { 6, 30, 56, 82, 0, 0, 0 }, Ecc = new RsParameters[] { new(69, 43, 9), new(150, 120, 5), new(42, 14, 2), new(50, 22, 17) } },
        new() { DataBytes = 991, Apat = new[] { 6, 30, 58, 86, 0, 0, 0 }, Ecc = new RsParameters[] { new(70, 44, 3), new(141, 113, 3), new(39, 13, 9), new(47, 21, 17) } },
        new() { DataBytes = 1085, Apat = new[] { 6, 34, 62, 90, 0, 0, 0 }, Ecc = new RsParameters[] { new(67, 41, 3), new(135, 107, 3), new(43, 15, 15), new(54, 24, 15) } },
        new() { DataBytes = 1156, Apat = new[] { 6, 28, 50, 72, 92, 0, 0 }, Ecc = new RsParameters[] { new(68, 42, 17), new(144, 116, 4), new(46, 16, 19), new(50, 22, 17) } },
        new() { DataBytes = 1258, Apat = new[] { 6, 26, 50, 74, 98, 0, 0 }, Ecc = new RsParameters[] { new(74, 46, 17), new(139, 111, 2), new(37, 13, 34), new(54, 24, 7) } },
        new() { DataBytes = 1364, Apat = new[] { 6, 30, 54, 78, 102, 0, 0 }, Ecc = new RsParameters[] { new(75, 47, 4), new(151, 121, 4), new(45, 15, 16), new(54, 24, 11) } },
        new() { DataBytes = 1474, Apat = new[] { 6, 28, 54, 80, 106, 0, 0 }, Ecc = new RsParameters[] { new(73, 45, 6), new(147, 117, 6), new(46, 16, 30), new(54, 24, 11) } },
        new() { DataBytes = 1588, Apat = new[] { 6, 32, 58, 84, 110, 0, 0 }, Ecc = new RsParameters[] { new(75, 47, 8), new(132, 106, 8), new(45, 15, 22), new(54, 24, 7) } },
        new() { DataBytes = 1706, Apat = new[] { 6, 30, 58, 86, 114, 0, 0 }, Ecc = new RsParameters[] { new(74, 46, 19), new(142, 114, 10), new(46, 16, 33), new(50, 22, 28) } },
        new() { DataBytes = 1828, Apat = new[] { 6, 34, 62, 90, 118, 0, 0 }, Ecc = new RsParameters[] { new(73, 45, 22), new(152, 122, 8), new(45, 15, 12), new(53, 23, 8) } },
        new() { DataBytes = 1921, Apat = new[] { 6, 26, 50, 74, 98, 122, 0 }, Ecc = new RsParameters[] { new(73, 45, 3), new(147, 117, 3), new(45, 15, 11), new(54, 24, 4) } },
        new() { DataBytes = 2051, Apat = new[] { 6, 30, 54, 78, 102, 126, 0 }, Ecc = new RsParameters[] { new(73, 45, 21), new(146, 116, 7), new(45, 15, 19), new(53, 23, 1) } },
        new() { DataBytes = 2185, Apat = new[] { 6, 26, 52, 78, 104, 130, 0 }, Ecc = new RsParameters[] { new(75, 47, 19), new(145, 115, 5), new(45, 15, 23), new(54, 24, 15) } },
        new() { DataBytes = 2323, Apat = new[] { 6, 30, 56, 82, 108, 134, 0 }, Ecc = new RsParameters[] { new(74, 46, 2), new(145, 115, 13), new(45, 15, 23), new(54, 24, 42) } },
        new() { DataBytes = 2465, Apat = new[] { 6, 34, 60, 86, 112, 138, 0 }, Ecc = new RsParameters[] { new(74, 46, 10), new(145, 115, 17), new(45, 15, 19), new(54, 24, 10) } },
        new() { DataBytes = 2611, Apat = new[] { 6, 30, 58, 86, 114, 142, 0 }, Ecc = new RsParameters[] { new(74, 46, 14), new(145, 115, 17), new(45, 15, 11), new(54, 24, 29) } },
        new() { DataBytes = 2761, Apat = new[] { 6, 34, 62, 90, 118, 146, 0 }, Ecc = new RsParameters[] { new(74, 46, 14), new(145, 115, 13), new(46, 16, 59), new(54, 24, 44) } },
        new() { DataBytes = 2876, Apat = new[] { 6, 30, 54, 78, 102, 126, 150 }, Ecc = new RsParameters[] { new(75, 47, 12), new(151, 121, 12), new(45, 15, 22), new(54, 24, 39) } },
        new() { DataBytes = 3034, Apat = new[] { 6, 24, 50, 76, 102, 128, 154 }, Ecc = new RsParameters[] { new(75, 47, 6), new(151, 121, 6), new(45, 15, 2), new(54, 24, 46) } },
        new() { DataBytes = 3196, Apat = new[] { 6, 28, 54, 80, 106, 132, 158 }, Ecc = new RsParameters[] { new(74, 46, 29), new(152, 122, 17), new(45, 15, 24), new(54, 24, 49) } },
        new() { DataBytes = 3362, Apat = new[] { 6, 32, 58, 84, 110, 136, 162 }, Ecc = new RsParameters[] { new(74, 46, 13), new(152, 122, 4), new(45, 15, 42), new(54, 24, 48) } },
        new() { DataBytes = 3532, Apat = new[] { 6, 26, 54, 82, 110, 138, 166 }, Ecc = new RsParameters[] { new(75, 47, 40), new(147, 117, 20), new(45, 15, 10), new(54, 24, 43) } },
        new() { DataBytes = 3706, Apat = new[] { 6, 30, 58, 86, 114, 142, 170 }, Ecc = new RsParameters[] { new(75, 47, 18), new(148, 118, 19), new(45, 15, 20), new(54, 24, 34) } },
    };
}
