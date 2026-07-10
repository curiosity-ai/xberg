using System.Globalization;
using System.Text;
using Xberg.Internal.Cfb;

namespace Xberg.Internal.Biff;

/// <summary>
/// Minimal BIFF8/BIFF5 (.xls) workbook reader — the OLE branch that calamine handles in the Rust
/// <c>extraction/excel.rs</c>. Reads the "Workbook"/"Book" stream from the CFB container, parses the
/// shared string table and per-sheet cell records (LabelSst / Label / RK / MulRk / Number / BoolErr
/// / Formula+String), and produces a rectangular string grid per sheet bounded by the used-cell
/// range — matching calamine's <c>worksheet_range</c> semantics that the extractor consumes.
/// </summary>
internal static class BiffReader
{
    internal sealed class Sheet
    {
        public string Name = "";
        public long StreamPos;
        public List<List<string>>? Cells;
    }

    private const int BOF = 0x0809;
    private const int EOF = 0x000A;
    private const int BOUNDSHEET8 = 0x0085;
    private const int SST = 0x00FC;
    private const int CONTINUE = 0x003C;
    private const int DIMENSIONS = 0x0200;
    private const int LABELSST = 0x00FD;
    private const int LABEL = 0x0204;
    private const int RSTRING = 0x00D6;
    private const int RK = 0x027E;
    private const int MULRK = 0x00BD;
    private const int NUMBER = 0x0203;
    private const int BLANK = 0x0201;
    private const int MULBLANK = 0x00BE;
    private const int BOOLERR = 0x0205;
    private const int FORMULA = 0x0006;
    private const int STRING_REC = 0x0207;

    public static List<Sheet> ReadSheets(CompoundFile comp)
    {
        byte[] wb = comp.TryReadStream("/Workbook") ?? comp.TryReadStream("/Book")
            ?? throw new InvalidDataException("No Workbook/Book stream in XLS");

        var (sst, sheets) = ParseGlobals(wb);
        foreach (var sheet in sheets)
            sheet.Cells = ParseSheet(wb, (int)sheet.StreamPos, sst);
        return sheets;
    }

    // ── globals substream: BoundSheet + SST ─────────────────────────────────────
    private static (List<string> Sst, List<Sheet> Sheets) ParseGlobals(byte[] wb)
    {
        var sheets = new List<Sheet>();
        var sst = new List<string>();
        int pos = 0;
        // Skip the initial globals BOF.
        while (pos + 4 <= wb.Length)
        {
            int type = U16(wb, pos);
            int len = U16(wb, pos + 2);
            int dataStart = pos + 4;
            if (dataStart + len > wb.Length) break;

            if (type == BOUNDSHEET8)
            {
                long lbPlyPos = U32(wb, dataStart);
                var s = new Sheet { StreamPos = lbPlyPos, Name = ReadShortXlString(wb, dataStart + 6) };
                sheets.Add(s);
                pos = dataStart + len;
            }
            else if (type == SST)
            {
                // Collect SST data + following CONTINUE records into one logical buffer.
                var segments = new List<(int Start, int Len)> { (dataStart, len) };
                int scan = dataStart + len;
                while (scan + 4 <= wb.Length && U16(wb, scan) == CONTINUE)
                {
                    int clen = U16(wb, scan + 2);
                    if (scan + 4 + clen > wb.Length) break;
                    segments.Add((scan + 4, clen));
                    scan += 4 + clen;
                }
                ParseSst(wb, segments, sst);
                pos = scan;
            }
            else if (type == EOF)
            {
                pos = dataStart + len;
                break;
            }
            else
            {
                pos = dataStart + len;
            }
        }
        return (sst, sheets);
    }

    private static void ParseSst(byte[] wb, List<(int Start, int Len)> segments, List<string> sst)
    {
        // Flatten segments into one buffer, tracking boundary offsets for the per-CONTINUE grbit.
        var buf = new List<byte>();
        var boundaries = new HashSet<int>();
        foreach (var (start, len) in segments)
        {
            if (buf.Count > 0) boundaries.Add(buf.Count);
            for (int i = 0; i < len; i++) buf.Add(wb[start + i]);
        }
        byte[] d = buf.ToArray();
        int pos = 0;
        if (d.Length < 8) return;
        pos += 4; // cstTotal
        long cstUnique = (uint)(d[4] | (d[5] << 8) | (d[6] << 16) | (d[7] << 24));
        pos += 4;

        for (long si = 0; si < cstUnique && pos + 3 <= d.Length; si++)
        {
            int cch = d[pos] | (d[pos + 1] << 8); pos += 2;
            byte flags = d[pos]; pos += 1;
            bool highByte = (flags & 0x01) != 0;
            int cRun = 0, cbExt = 0;
            if ((flags & 0x08) != 0) { cRun = d[pos] | (d[pos + 1] << 8); pos += 2; }
            if ((flags & 0x04) != 0) { cbExt = (int)((uint)(d[pos] | (d[pos + 1] << 8) | (d[pos + 2] << 16) | (d[pos + 3] << 24))); pos += 4; }

            var sb = new StringBuilder(cch);
            int read = 0;
            while (read < cch && pos < d.Length)
            {
                if (boundaries.Contains(pos))
                {
                    // A continue boundary in the middle of char data carries a fresh grbit byte.
                    flags = d[pos]; pos += 1;
                    highByte = (flags & 0x01) != 0;
                }
                if (highByte)
                {
                    if (pos + 1 >= d.Length) break;
                    sb.Append((char)(ushort)(d[pos] | (d[pos + 1] << 8)));
                    pos += 2;
                }
                else
                {
                    sb.Append((char)d[pos]);
                    pos += 1;
                }
                read++;
            }
            // Skip rich-text runs (4 bytes each) and extended (phonetic) data.
            pos += cRun * 4;
            pos += cbExt;
            sst.Add(sb.ToString());
        }
    }

    // ── worksheet substream: cells ──────────────────────────────────────────────
    private static List<List<string>>? ParseSheet(byte[] wb, int start, List<string> sst)
    {
        var cells = new List<(int Row, int Col, string Val)>();
        int pos = start;
        // Expect a BOF at the sheet start.
        bool started = false;
        int depth = 0;
        while (pos + 4 <= wb.Length)
        {
            int type = U16(wb, pos);
            int len = U16(wb, pos + 2);
            int d = pos + 4;
            if (d + len > wb.Length) break;

            if (type == BOF)
            {
                depth++;
                started = true;
                pos = d + len;
                continue;
            }
            if (type == EOF)
            {
                depth--;
                pos = d + len;
                if (started && depth <= 0) break;
                continue;
            }

            switch (type)
            {
                case LABELSST:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    int isst = (int)U32(wb, d + 6);
                    string v = isst >= 0 && isst < sst.Count ? sst[isst] : "";
                    AddCell(cells, row, col, v);
                    break;
                }
                case LABEL:
                case RSTRING:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    string v = ReadXlString(wb, d + 6);
                    AddCell(cells, row, col, v);
                    break;
                }
                case RK:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    double v = RkToDouble(U32(wb, d + 6));
                    AddCell(cells, row, col, FormatNumber(v));
                    break;
                }
                case MULRK:
                {
                    int row = U16(wb, d), colFirst = U16(wb, d + 2);
                    int n = (len - 6) / 6;
                    for (int i = 0; i < n; i++)
                    {
                        // Each RkRec is { ixfe:u16, rk:u32 }; the RK value sits after the ixfe.
                        int off = d + 4 + i * 6 + 2;
                        double v = RkToDouble(U32(wb, off));
                        AddCell(cells, row, colFirst + i, FormatNumber(v));
                    }
                    break;
                }
                case NUMBER:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    double v = BitConverter.Int64BitsToDouble((long)U64(wb, d + 6));
                    AddCell(cells, row, col, FormatNumber(v));
                    break;
                }
                case BOOLERR:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    byte val = wb[d + 6];
                    byte isErr = wb[d + 7];
                    string v = isErr != 0 ? $"#ERR: {val}" : (val != 0 ? "true" : "false");
                    AddCell(cells, row, col, v);
                    break;
                }
                case FORMULA:
                {
                    int row = U16(wb, d), col = U16(wb, d + 2);
                    // Cached result: 8 bytes at d+6.
                    if (wb[d + 12] == 0xFF && wb[d + 13] == 0xFF)
                    {
                        byte kind = wb[d + 6];
                        if (kind == 0)
                        {
                            // String result follows in a STRING record.
                            int np = d + len;
                            if (np + 4 <= wb.Length && U16(wb, np) == STRING_REC)
                            {
                                string v = ReadXlString(wb, np + 4);
                                AddCell(cells, row, col, v);
                            }
                        }
                        else if (kind == 1)
                            AddCell(cells, row, col, wb[d + 8] != 0 ? "true" : "false");
                        else if (kind == 2)
                            AddCell(cells, row, col, $"#ERR: {wb[d + 8]}");
                        // kind == 3 → empty string, skip.
                    }
                    else
                    {
                        double v = BitConverter.Int64BitsToDouble((long)U64(wb, d + 6));
                        AddCell(cells, row, col, FormatNumber(v));
                    }
                    break;
                }
            }
            pos = d + len;
        }

        return BuildGrid(cells);
    }

    private static void AddCell(List<(int, int, string)> cells, int row, int col, string val)
    {
        if (val.Length == 0) return; // Empty cells don't extend the used range.
        cells.Add((row, col, val));
    }

    private static List<List<string>>? BuildGrid(List<(int Row, int Col, string Val)> cells)
    {
        if (cells.Count == 0) return null;
        int minRow = int.MaxValue, maxRow = int.MinValue, minCol = int.MaxValue, maxCol = int.MinValue;
        foreach (var (r, c, _) in cells)
        {
            if (r < minRow) minRow = r;
            if (r > maxRow) maxRow = r;
            if (c < minCol) minCol = c;
            if (c > maxCol) maxCol = c;
        }
        int rows = maxRow - minRow + 1;
        int cols = maxCol - minCol + 1;
        var grid = new List<List<string>>(rows);
        for (int r = 0; r < rows; r++)
        {
            var row = new List<string>(cols);
            for (int c = 0; c < cols; c++) row.Add("");
            grid.Add(row);
        }
        foreach (var (r, c, v) in cells)
            grid[r - minRow][c - minCol] = v;
        return grid;
    }

    // ── value helpers ────────────────────────────────────────────────────────────
    internal static double RkToDouble(uint rk)
    {
        bool fx100 = (rk & 0x01) != 0;
        bool fInt = (rk & 0x02) != 0;
        double value;
        if (fInt)
        {
            int signed = (int)rk >> 2; // arithmetic shift keeps sign
            value = signed;
        }
        else
        {
            ulong bits = (ulong)(rk & 0xFFFF_FFFC) << 32;
            value = BitConverter.Int64BitsToDouble((long)bits);
        }
        return fx100 ? value / 100.0 : value;
    }

    /// <summary>Format a numeric cell the way Rust's <c>format!("{}", f64)</c> does: shortest
    /// round-trippable representation, whole numbers without a trailing decimal.</summary>
    internal static string FormatNumber(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "inf";
        if (double.IsNegativeInfinity(v)) return "-inf";
        // .NET "R"/default (Core 3+) gives shortest round-trip; whole values render without ".0".
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Read an XLUnicodeString (u16 char-count + flags byte + chars).</summary>
    private static string ReadXlString(byte[] b, int off)
    {
        if (off + 3 > b.Length) return "";
        int cch = U16(b, off);
        byte flags = b[off + 2];
        bool highByte = (flags & 0x01) != 0;
        int p = off + 3;
        var sb = new StringBuilder(cch);
        for (int i = 0; i < cch; i++)
        {
            if (highByte) { if (p + 1 >= b.Length) break; sb.Append((char)(ushort)(b[p] | (b[p + 1] << 8))); p += 2; }
            else { if (p >= b.Length) break; sb.Append((char)b[p]); p += 1; }
        }
        return sb.ToString();
    }

    /// <summary>Read a ShortXLUnicodeString (u8 char-count + flags byte + chars), used by BoundSheet8.</summary>
    private static string ReadShortXlString(byte[] b, int off)
    {
        if (off + 2 > b.Length) return "";
        int cch = b[off];
        byte flags = b[off + 1];
        bool highByte = (flags & 0x01) != 0;
        int p = off + 2;
        var sb = new StringBuilder(cch);
        for (int i = 0; i < cch; i++)
        {
            if (highByte) { if (p + 1 >= b.Length) break; sb.Append((char)(ushort)(b[p] | (b[p + 1] << 8))); p += 2; }
            else { if (p >= b.Length) break; sb.Append((char)b[p]); p += 1; }
        }
        return sb.ToString();
    }

    private static int U16(byte[] b, int o) => o + 1 < b.Length ? b[o] | (b[o + 1] << 8) : 0;
    private static uint U32(byte[] b, int o) =>
        o + 3 < b.Length ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) : 0u;
    private static ulong U64(byte[] b, int o) => U32(b, o) | ((ulong)U32(b, o + 4) << 32);
}
