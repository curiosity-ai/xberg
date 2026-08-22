using System.Globalization;
using System.Text;

namespace Xberg.Internal.Biff;

/// <summary>Binary Interchange File Format version of a workbook stream, taken from its BOF record.</summary>
internal enum BiffVersion
{
    Biff2,
    Biff3,
    Biff4,
    Biff5,
    Biff8,
}

/// <summary>
/// Workbook-global tables that a formula expression can point into: sheet names in BoundSheet8
/// order, defined names (Lbl records) in file order, and the ExternSheet XTI table that 3d
/// references index through.
/// </summary>
internal sealed class BiffFormulaContext
{
    public List<string> SheetNames { get; } = new();
    public List<string> DefinedNames { get; } = new();

    /// <summary>`itabFirst` of each XTI entry; the rest of the XTI struct is never rendered.</summary>
    public List<short> XtiItabFirst { get; } = new();

    public BiffVersion Biff { get; set; } = BiffVersion.Biff8;

    /// <summary>BIFF5 and earlier pack row/column operands more tightly than BIFF8 does.</summary>
    public bool IsPreBiff8 => Biff != BiffVersion.Biff8;
}

/// <summary>
/// Decoder for the RPN token (Ptg) stream of a FORMULA record — `CellParsedFormula`
/// [MS-XLS 2.5.198.3] — rendered back to infix text. This is what the Excel extractor reports
/// per sheet as `formulas_&lt;sheet&gt;` metadata.
///
/// The rendering follows the reference implementation rather than Excel, and several of its
/// quirks are deliberate because the expected output encodes them:
/// <list type="bullet">
///   <item>Area operands render their column word whole, relative-reference flag bits included,
///     so a relative <c>B2:G2</c> comes out as <c>$USN$2:$USS$2</c>.</item>
///   <item>Column letters come from <see cref="PushColumn"/>, which drops the most significant
///     digit rather than carrying it, so those names are not reversible.</item>
///   <item>PtgAttrSpace inserts its whitespace at the start of the operand that follows it in the
///     token stream, which is not always where Excel showed it.</item>
///   <item>A 3d area names its sheet by indexing the sheet list with the XTI index itself, while a
///     3d reference resolves that index through the XTI table first.</item>
/// </list>
/// Failure is not fatal: an expression that cannot be decoded becomes the same
/// "Unrecognised formula" placeholder the reference implementation substitutes, so one bad cell
/// does not cost the workbook its other formulas.
/// </summary>
internal static class BiffFormula
{
    /// <summary>
    /// Decode the expression bytes of one FORMULA record. <paramref name="start"/> points at the
    /// `cce` expression-length word (offset 20 of the record data), <paramref name="end"/> at the
    /// end of the record. <paramref name="row"/> and <paramref name="col"/> only name the cell in
    /// the placeholder text used when decoding fails.
    /// </summary>
    public static string Parse(byte[] data, int start, int end, BiffFormulaContext ctx, int row, int col)
    {
        try
        {
            return Decode(data, start, end, ctx);
        }
        catch (BiffFormulaException e)
        {
            return $"Unrecognised formula for cell ({row}, {col}): {e.Message}";
        }
    }

    private static string Decode(byte[] data, int start, int end, BiffFormulaContext ctx)
    {
        if (end > data.Length) end = data.Length;
        if (start < 0 || start + 2 > end) throw Eos("formula");
        int cce = data[start] | (data[start + 1] << 8);
        int limit = start + 2 + cce;
        if (limit > end) throw Eos("formula");

        bool isPreBiff8 = ctx.IsPreBiff8;
        int p = start + 2;
        var f = new StringBuilder(cce);
        var stack = new List<int>();

        ushort U16(int at)
        {
            if (at < 0 || at + 2 > limit) throw Eos("formula");
            return (ushort)(data[at] | (data[at + 1] << 8));
        }

        uint U32(int at)
        {
            if (at < 0 || at + 4 > limit) throw Eos("formula");
            return (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));
        }

        byte U8(int at)
        {
            if (at < 0 || at >= limit) throw Eos("formula");
            return data[at];
        }

        // Every token advances past its operands; running off the end is a malformed expression.
        void Advance(int n)
        {
            if (n < 0 || p + n > limit) throw Eos("formula");
            p += n;
        }

        int Peek() => stack.Count == 0 ? throw StackLen() : stack[^1];

        int Pop()
        {
            if (stack.Count == 0) throw StackLen();
            int v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        // Detach everything written from `at` onward, so an operand already emitted can be wrapped
        // in the operator that follows it.
        string SplitOff(int at)
        {
            string tail = f.ToString(at, f.Length - at);
            f.Length = at;
            return tail;
        }

        while (p < limit)
        {
            byte ptg = U8(p);
            p++;
            switch (ptg)
            {
                case 0x3A or 0x5A or 0x7A:
                {
                    // PtgRef3d
                    ushort ixti = U16(p);
                    ushort rowu = U16(p + 2);
                    ushort colu = U16(p + 4);
                    stack.Add(f.Length);
                    f.Append(SheetByXti(ctx, ixti));
                    f.Append('!');
                    uint col = (ushort)(colu << 2);
                    if ((colu & 2) != 0) f.Append('$');
                    PushColumn(col, f);
                    if ((colu & 1) != 0) f.Append('$');
                    AppendNumber(f, (ushort)(rowu + 1));
                    Advance(6);
                    break;
                }
                case 0x3B or 0x5B or 0x7B:
                {
                    // PtgArea3d
                    ushort ixti = U16(p);
                    stack.Add(f.Length);
                    f.Append(SheetByIndex(ctx, ixti));
                    f.Append('!');
                    f.Append('$');
                    PushColumn(U16(p + 6), f);
                    f.Append('$');
                    AppendNumber(f, (uint)U16(p + 2) + 1);
                    f.Append(":$");
                    PushColumn(U16(p + 8), f);
                    f.Append('$');
                    AppendNumber(f, (uint)U16(p + 4) + 1);
                    Advance(10);
                    break;
                }
                case 0x3C or 0x5C or 0x7C:
                {
                    // PtgRefErr3d
                    ushort ixti = U16(p);
                    stack.Add(f.Length);
                    f.Append(SheetByIndex(ctx, ixti));
                    f.Append('!');
                    f.Append("#REF!");
                    Advance(6);
                    break;
                }
                case 0x3D or 0x5D or 0x7D:
                {
                    // PtgAreaErr3d
                    ushort ixti = U16(p);
                    stack.Add(f.Length);
                    f.Append(SheetByIndex(ctx, ixti));
                    f.Append('!');
                    f.Append("#REF!");
                    Advance(10);
                    break;
                }
                case 0x01:
                {
                    // PtgExp: the cell shares an array/shared formula defined elsewhere. It takes a
                    // stack slot but contributes no text, so the cell reports an empty formula.
                    stack.Add(f.Length);
                    Advance(4);
                    break;
                }
                case >= 0x03 and <= 0x11:
                {
                    // Binary operation: the left operand is already the text from the top of stack
                    // onward, so only the right operand needs to be detached and re-appended.
                    int e2 = Pop();
                    string op = ptg switch
                    {
                        0x03 => "+",
                        0x04 => "-",
                        0x05 => "*",
                        0x06 => "/",
                        0x07 => "^",
                        0x08 => "&",
                        0x09 => "<",
                        0x0A => "<=",
                        0x0B => "=",
                        0x0C => ">",
                        0x0D => ">=",
                        0x0E => "<>",
                        0x0F => " ",
                        0x10 => ",",
                        _ => ":",
                    };
                    string tail = SplitOff(e2);
                    f.Append(op).Append(tail);
                    break;
                }
                case 0x12:
                    f.Insert(Peek(), '+');
                    break;
                case 0x13:
                    f.Insert(Peek(), '-');
                    break;
                case 0x14:
                    f.Append('%');
                    break;
                case 0x15:
                    f.Insert(Peek(), '(');
                    f.Append(')');
                    break;
                case 0x16:
                    // PtgMissArg: an omitted argument, empty text on its own stack slot.
                    stack.Add(f.Length);
                    break;
                case 0x17:
                {
                    // PtgStr
                    stack.Add(f.Length);
                    f.Append('"');
                    int cch = U8(p);
                    AppendUnicodeString(data, p + 1, cch, limit, f);
                    f.Append('"');
                    Advance(2 + cch);
                    break;
                }
                case 0x18:
                    Advance(5);
                    break;
                case 0x19:
                {
                    // PtgAttr: control token, identified by the byte that follows.
                    byte etpg = U8(p);
                    p++;
                    switch (etpg)
                    {
                        case 0x01 or 0x02 or 0x08 or 0x20 or 0x21:
                            Advance(2);
                            break;
                        case 0x04:
                        {
                            // PtgAttrChoose: a jump table, skipped whole.
                            int n = U16(p) + 1;
                            Advance(2 + 2 * n);
                            break;
                        }
                        case 0x10:
                        {
                            // PtgAttrSum: the single-argument SUM() shorthand.
                            Advance(2);
                            string tail = SplitOff(Peek());
                            f.Append("SUM(").Append(tail).Append(')');
                            break;
                        }
                        case 0x40 or 0x41:
                        {
                            // PtgAttrSpace: whitespace the author typed, re-inserted before the
                            // operand on top of the stack.
                            int e = Peek();
                            byte kind = U8(p);
                            char space = kind switch
                            {
                                0x00 or 0x02 or 0x04 or 0x06 => ' ',
                                0x01 or 0x03 or 0x05 => '\r',
                                _ => throw Unrecognized("PtgAttrSpaceType", kind),
                            };
                            byte count = U8(p + 1);
                            for (int i = 0; i < count; i++) f.Insert(e, space);
                            Advance(2);
                            break;
                        }
                        default:
                            throw Etpg(etpg);
                    }
                    break;
                }
                case 0x1C:
                {
                    // PtgErr
                    stack.Add(f.Length);
                    byte err = U8(p);
                    p++;
                    f.Append(err switch
                    {
                        0x00 => "#NULL!",
                        0x07 => "#DIV/0!",
                        0x0F => "#VALUE!",
                        0x17 => "#REF!",
                        0x1D => "#NAME?",
                        0x24 => "#NUM!",
                        0x2A => "#N/A",
                        0x2B => "#GETTING_DATA",
                        _ => throw Unrecognized("BErr", err),
                    });
                    break;
                }
                case 0x1D:
                {
                    // PtgBool
                    stack.Add(f.Length);
                    f.Append(U8(p) == 0 ? "FALSE" : "TRUE");
                    Advance(1);
                    break;
                }
                case 0x1E:
                {
                    // PtgInt
                    stack.Add(f.Length);
                    AppendNumber(f, U16(p));
                    Advance(2);
                    break;
                }
                case 0x1F:
                {
                    // PtgNum
                    stack.Add(f.Length);
                    if (p + 8 > limit) throw Eos("formula");
                    f.Append(BiffReader.FormatNumber(BitConverter.Int64BitsToDouble(BitConverter.ToInt64(data, p))));
                    Advance(8);
                    break;
                }
                case 0x20 or 0x40 or 0x60:
                {
                    // PtgArray: the literal lives past the token stream and is not rendered.
                    stack.Add(f.Length);
                    f.Append("{PtgArray}");
                    Advance(7);
                    break;
                }
                case 0x21 or 0x22 or 0x41 or 0x42 or 0x61 or 0x62:
                {
                    // PtgFunc / PtgFuncVar
                    int iftab;
                    int argc;
                    if (ptg is 0x22 or 0x42 or 0x62)
                    {
                        iftab = U16(p + 1);
                        argc = U8(p);
                        Advance(3);
                    }
                    else
                    {
                        iftab = U16(p);
                        if (iftab >= Ftab.Length) throw IfTab(iftab);
                        Advance(2);
                        argc = FtabArgc[iftab];
                    }

                    if (stack.Count < argc) throw StackLen();
                    if (argc > 0)
                    {
                        // The arguments are the last `argc` stack slots; their text is one run
                        // starting at the first of them, sliced back apart at the slot offsets.
                        int argsStart = stack.Count - argc;
                        var args = stack.GetRange(argsStart, argc);
                        stack.RemoveRange(argsStart, argc);
                        int runStart = args[0];
                        for (int i = 0; i < args.Count; i++) args[i] -= runStart;
                        string fargs = SplitOff(runStart);
                        stack.Add(f.Length);
                        args.Add(fargs.Length);
                        f.Append(FtabName(iftab));
                        f.Append('(');
                        for (int i = 0; i + 1 < args.Count; i++)
                        {
                            int from = args[i];
                            int to = args[i + 1];
                            if (from < 0 || to < from || to > fargs.Length) throw StackLen();
                            f.Append(fargs, from, to - from);
                            f.Append(',');
                        }
                        f.Length -= 1;
                        f.Append(')');
                    }
                    else
                    {
                        stack.Add(f.Length);
                        f.Append(FtabName(iftab)).Append("()");
                    }
                    break;
                }
                case 0x23 or 0x43 or 0x63:
                {
                    // PtgName: one-based index into the defined names.
                    long iname = (long)U32(p) - 1;
                    stack.Add(f.Length);
                    f.Append(iname >= 0 && iname < ctx.DefinedNames.Count ? ctx.DefinedNames[(int)iname] : "#REF!");
                    Advance(4);
                    break;
                }
                case 0x24 or 0x44 or 0x64:
                {
                    // PtgRef
                    stack.Add(f.Length);
                    if (isPreBiff8)
                    {
                        // rw(2: 14-bit row + 2 relative flags) + col(1)
                        ushort rwRaw = U16(p);
                        int row = (rwRaw & 0x3FFF) + 1;
                        byte col = U8(p + 2);
                        if ((rwRaw & 0x4000) == 0) f.Append('$');
                        PushColumn(col, f);
                        if ((rwRaw & 0x8000) == 0) f.Append('$');
                        AppendNumber(f, (uint)row);
                        Advance(3);
                    }
                    else
                    {
                        // rw(2) + col(2: 14-bit column + 2 relative flags)
                        int row = (ushort)(U16(p) + 1);
                        byte colHigh = U8(p + 3);
                        uint col = (uint)(U8(p + 2) | ((colHigh & 0x3F) << 8));
                        if ((colHigh & 0x80) != 0x80) f.Append('$');
                        PushColumn(col, f);
                        if ((colHigh & 0x40) != 0x40) f.Append('$');
                        AppendNumber(f, (uint)row);
                        Advance(4);
                    }
                    break;
                }
                case 0x25 or 0x45 or 0x65:
                {
                    // PtgArea
                    stack.Add(f.Length);
                    if (isPreBiff8)
                    {
                        // rwFirst(2) + rwLast(2) + colFirst(1) + colLast(1)
                        uint rowFirst = (uint)(U16(p) & 0x3FFF) + 1;
                        uint rowLast = (uint)(U16(p + 2) & 0x3FFF) + 1;
                        f.Append('$');
                        PushColumn(U8(p + 4), f);
                        f.Append('$');
                        AppendNumber(f, rowFirst);
                        f.Append(":$");
                        PushColumn(U8(p + 5), f);
                        f.Append('$');
                        AppendNumber(f, rowLast);
                        Advance(6);
                    }
                    else
                    {
                        f.Append('$');
                        PushColumn(U16(p + 4), f);
                        f.Append('$');
                        AppendNumber(f, (uint)U16(p) + 1);
                        f.Append(":$");
                        PushColumn(U16(p + 6), f);
                        f.Append('$');
                        AppendNumber(f, (uint)U16(p + 2) + 1);
                        Advance(8);
                    }
                    break;
                }
                case 0x2A or 0x4A or 0x6A:
                {
                    // PtgRefErr
                    stack.Add(f.Length);
                    f.Append("#REF!");
                    Advance(isPreBiff8 ? 3 : 4);
                    break;
                }
                case 0x2B or 0x4B or 0x6B:
                {
                    // PtgAreaErr
                    stack.Add(f.Length);
                    f.Append("#REF!");
                    Advance(isPreBiff8 ? 6 : 8);
                    break;
                }
                case 0x39 or 0x59:
                {
                    // PtgNameX: an external workbook's defined name, not resolvable from this file.
                    stack.Add(f.Length);
                    f.Append("[PtgNameX]");
                    Advance(6);
                    break;
                }
                default:
                    throw Unrecognized("ptg", ptg);
            }
        }

        if (stack.Count != 1) throw InvalidFormula(stack.Count);
        return f.ToString();
    }

    /// <summary>Entries a single sheet contributes to workbook metadata before it is truncated.</summary>
    private const int MaxMetadataEntriesPerSheet = 200;

    /// <summary>
    /// A sheet's formulas as <c>cell=formula</c> entries joined by <c>"; "</c>, or null when the
    /// sheet has none.
    /// </summary>
    /// <remarks>
    /// The cell reference is relative to the top-left of the block of cells that carry formulas,
    /// not to the sheet: a lone formula anywhere on a sheet is reported as <c>A1</c>. That is what
    /// the formula range gives, and the entries are a summary of what the sheet computes rather
    /// than a map of where. Cells whose formula decoded to nothing — shared and array formulas
    /// defined in another cell — are skipped, but they still count towards the grid origin.
    /// </remarks>
    public static string? CollectSheetFormulas(List<List<string>>? grid)
    {
        if (grid is null) return null;
        var entries = new List<string>();
        for (int r = 0; r < grid.Count && entries.Count < MaxMetadataEntriesPerSheet; r++)
        {
            var row = grid[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (row[c].Length == 0) continue;
                entries.Add($"{ColumnLetters(c)}{(r + 1).ToString(CultureInfo.InvariantCulture)}={row[c]}");
                if (entries.Count >= MaxMetadataEntriesPerSheet) break;
            }
        }
        return entries.Count == 0 ? null : string.Join("; ", entries);
    }

    /// <summary>A zero-based column index as spreadsheet column letters, "A", "Z", "AA".</summary>
    private static string ColumnLetters(int col)
    {
        var letters = new StringBuilder();
        int n = col;
        while (true)
        {
            letters.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
            if (n < 0) break;
        }
        return letters.ToString();
    }

    /// <summary>A 3d reference names its sheet through the XTI table.</summary>
    private static string SheetByXti(BiffFormulaContext ctx, int ixti)
    {
        if (ixti >= 0 && ixti < ctx.XtiItabFirst.Count)
        {
            int itab = ctx.XtiItabFirst[ixti];
            if (itab >= 0 && itab < ctx.SheetNames.Count) return ctx.SheetNames[itab];
        }
        return "#REF";
    }

    /// <summary>A 3d area names its sheet by using the XTI index as a sheet index directly.</summary>
    private static string SheetByIndex(BiffFormulaContext ctx, int index) =>
        index >= 0 && index < ctx.SheetNames.Count ? ctx.SheetNames[index] : "#REF";

    /// <summary>
    /// Append a column word as letters. Columns of 26 or more lose their most significant digit
    /// (26 renders as "A", not "AA"), which is why references carrying relative-reference flag
    /// bits come out as names like "USN".
    /// </summary>
    internal static void PushColumn(uint col, StringBuilder buf)
    {
        if (col < 26)
        {
            buf.Append((char)('A' + (int)col));
            return;
        }

        var rev = new StringBuilder();
        while (col >= 26)
        {
            uint c = col % 26;
            rev.Append((char)('A' + (int)c));
            col -= c;
            col /= 26;
        }
        for (int i = rev.Length - 1; i >= 0; i--) buf.Append(rev[i]);
    }

    /// <summary>
    /// Append a string operand: a flags byte whose low bit marks UTF-16 characters, then
    /// <paramref name="cch"/> *bytes* of text. A UTF-16 operand therefore yields half as many
    /// characters as its declared length, which is what the reference decoder produces.
    /// </summary>
    private static void AppendUnicodeString(byte[] data, int at, int cch, int limit, StringBuilder f)
    {
        if (at >= limit) return;
        bool highByte = (data[at] & 0x01) != 0;
        int available = Math.Max(0, Math.Min(cch, limit - (at + 1)));
        int p = at + 1;
        if (highByte)
        {
            for (int i = 0; i + 1 < available; i += 2) f.Append((char)(ushort)(data[p + i] | (data[p + i + 1] << 8)));
        }
        else
        {
            for (int i = 0; i < available; i++) f.Append((char)data[p + i]);
        }
    }

    private static void AppendNumber(StringBuilder f, uint value) =>
        f.Append(value.ToString(CultureInfo.InvariantCulture));

    private static string FtabName(int iftab) => iftab >= 0 && iftab < Ftab.Length ? Ftab[iftab] : throw IfTab(iftab);

    // ── errors ───────────────────────────────────────────────────────────────────
    // The messages are the ones that end up inside the "Unrecognised formula" placeholder.
    private sealed class BiffFormulaException : Exception
    {
        public BiffFormulaException(string message) : base(message) { }
    }

    private static BiffFormulaException StackLen() => new("StackLen");

    private static BiffFormulaException Eos(string what) => new($"EoStream(\"{what}\")");

    private static BiffFormulaException Etpg(byte value) => new($"Etpg({value})");

    private static BiffFormulaException IfTab(int iftab) => new($"IfTab({iftab})");

    private static BiffFormulaException Unrecognized(string typ, byte value) =>
        new($"Unrecognized {{ typ: \"{typ}\", val: {value} }}");

    private static BiffFormulaException InvalidFormula(int stackSize) =>
        new($"InvalidFormula {{ stack_size: {stackSize} }}");

    // ── built-in function table [MS-XLS 2.5.198.17] ──────────────────────────────
    private static readonly string[] Ftab =
    {
        "COUNT", "IF", "ISNA", "ISERROR", "SUM", "AVERAGE", "MIN", "MAX", "ROW", "COLUMN", "NA", "NPV",
        "STDEV", "DOLLAR", "FIXED", "SIN", "COS", "TAN", "ATAN", "PI", "SQRT", "EXP", "LN", "LOG10", "ABS",
        "INT", "SIGN", "ROUND", "LOOKUP", "INDEX", "REPT", "MID", "LEN", "VALUE", "TRUE", "FALSE", "AND", "OR",
        "NOT", "MOD", "DCOUNT", "DSUM", "DAVERAGE", "DMIN", "DMAX", "DSTDEV", "VAR", "DVAR", "TEXT", "LINEST",
        "TREND", "LOGEST", "GROWTH", "GOTO", "HALT", "RETURN", "PV", "FV", "NPER", "PMT", "RATE", "MIRR",
        "IRR", "RAND", "MATCH", "DATE", "TIME", "DAY", "MONTH", "YEAR", "WEEKDAY", "HOUR", "MINUTE", "SECOND",
        "NOW", "AREAS", "ROWS", "COLUMNS", "OFFSET", "ABSREF", "RELREF", "ARGUMENT", "SEARCH", "TRANSPOSE",
        "ERROR", "STEP", "TYPE", "ECHO", "SET.NAME", "CALLER", "DEREF", "WINDOWS", "SERIES", "DOCUMENTS",
        "ACTIVE.CELL", "SELECTION", "RESULT", "ATAN2", "ASIN", "ACOS", "CHOOSE", "HLOOKUP", "VLOOKUP", "LINKS",
        "INPUT", "ISREF", "GET.FORMULA", "GET.NAME", "SET.VALUE", "LOG", "EXEC", "CHAR", "LOWER", "UPPER",
        "PROPER", "LEFT", "RIGHT", "EXACT", "TRIM", "REPLACE", "SUBSTITUTE", "CODE", "NAMES", "DIRECTORY",
        "FIND", "CELL", "ISERR", "ISTEXT", "ISNUMBER", "ISBLANK", "T", "N", "FOPEN", "FCLOSE", "FSIZE",
        "FREADLN", "FREAD", "FWRITELN", "FWRITE", "FPOS", "DATEVALUE", "TIMEVALUE", "SLN", "SYD", "DDB",
        "GET.DEF", "REFTEXT", "TEXTREF", "INDIRECT", "REGISTER", "CALL", "ADD.BAR", "ADD.MENU", "ADD.COMMAND",
        "ENABLE.COMMAND", "CHECK.COMMAND", "RENAME.COMMAND", "SHOW.BAR", "DELETE.MENU", "DELETE.COMMAND",
        "GET.CHART.ITEM", "DIALOG.BOX", "CLEAN", "MDETERM", "MINVERSE", "MMULT", "FILES", "IPMT", "PPMT",
        "COUNTA", "CANCEL.KEY", "FOR", "WHILE", "BREAK", "NEXT", "INITIATE", "REQUEST", "POKE", "EXECUTE",
        "TERMINATE", "RESTART", "HELP", "GET.BAR", "PRODUCT", "FACT", "GET.CELL", "GET.WORKSPACE",
        "GET.WINDOW", "GET.DOCUMENT", "DPRODUCT", "ISNONTEXT", "GET.NOTE", "NOTE", "STDEVP", "VARP", "DSTDEVP",
        "DVARP", "TRUNC", "ISLOGICAL", "DCOUNTA", "DELETE.BAR", "UNREGISTER", "", "", "USDOLLAR", "FINDB",
        "SEARCHB", "REPLACEB", "LEFTB", "RIGHTB", "MIDB", "LENB", "ROUNDUP", "ROUNDDOWN", "ASC", "DBCS",
        "RANK", "", "", "ADDRESS", "DAYS360", "TODAY", "VDB", "ELSE", "ELSE.IF", "END.IF", "FOR.CELL",
        "MEDIAN", "SUMPRODUCT", "SINH", "COSH", "TANH", "ASINH", "ACOSH", "ATANH", "DGET", "CREATE.OBJECT",
        "VOLATILE", "LAST.ERROR", "CUSTOM.UNDO", "CUSTOM.REPEAT", "FORMULA.CONVERT", "GET.LINK.INFO",
        "TEXT.BOX", "INFO", "GROUP", "GET.OBJECT", "DB", "PAUSE", "", "", "RESUME", "FREQUENCY", "ADD.TOOLBAR",
        "DELETE.TOOLBAR", "User", "RESET.TOOLBAR", "EVALUATE", "GET.TOOLBAR", "GET.TOOL", "SPELLING.CHECK",
        "ERROR.TYPE", "APP.TITLE", "WINDOW.TITLE", "SAVE.TOOLBAR", "ENABLE.TOOL", "PRESS.TOOL", "REGISTER.ID",
        "GET.WORKBOOK", "AVEDEV", "BETADIST", "GAMMALN", "BETAINV", "BINOMDIST", "CHIDIST", "CHIINV", "COMBIN",
        "CONFIDENCE", "CRITBINOM", "EVEN", "EXPONDIST", "FDIST", "FINV", "FISHER", "FISHERINV", "FLOOR",
        "GAMMADIST", "GAMMAINV", "CEILING", "HYPGEOMDIST", "LOGNORMDIST", "LOGINV", "NEGBINOMDIST", "NORMDIST",
        "NORMSDIST", "NORMINV", "NORMSINV", "STANDARDIZE", "ODD", "PERMUT", "POISSON", "TDIST", "WEIBULL",
        "SUMXMY2", "SUMX2MY2", "SUMX2PY2", "CHITEST", "CORREL", "COVAR", "FORECAST", "FTEST", "INTERCEPT",
        "PEARSON", "RSQ", "STEYX", "SLOPE", "TTEST", "PROB", "DEVSQ", "GEOMEAN", "HARMEAN", "SUMSQ", "KURT",
        "SKEW", "ZTEST", "LARGE", "SMALL", "QUARTILE", "PERCENTILE", "PERCENTRANK", "MODE", "TRIMMEAN", "TINV",
        "", "MOVIE.COMMAND", "GET.MOVIE", "CONCATENATE", "POWER", "PIVOT.ADD.DATA", "GET.PIVOT.TABLE",
        "GET.PIVOT.FIELD", "GET.PIVOT.ITEM", "RADIANS", "DEGREES", "SUBTOTAL", "SUMIF", "COUNTIF",
        "COUNTBLANK", "SCENARIO.GET", "OPTIONS.LISTS.GET", "ISPMT", "DATEDIF", "DATESTRING", "NUMBERSTRING",
        "ROMAN", "OPEN.DIALOG", "SAVE.DIALOG", "VIEW.GET", "GETPIVOTDATA", "HYPERLINK", "PHONETIC", "AVERAGEA",
        "MAXA", "MINA", "STDEVPA", "VARPA", "STDEVA", "VARA", "BAHTTEXT", "THAIDAYOFWEEK", "THAIDIGIT",
        "THAIMONTHOFYEAR", "THAINUMSOUND", "THAINUMSTRING", "THAISTRINGLENGTH", "ISTHAIDIGIT", "ROUNDBAHTDOWN",
        "ROUNDBAHTUP", "THAIYEAR", "RTD", "CUBEVALUE", "CUBEMEMBER", "CUBEMEMBERPROPERTY", "CUBERANKEDMEMBER",
        "HEX2BIN", "HEX2DEC", "HEX2OCT", "DEC2BIN", "DEC2HEX", "DEC2OCT", "OCT2BIN", "OCT2HEX", "OCT2DEC",
        "BIN2DEC", "BIN2OCT", "BIN2HEX", "IMSUB", "IMDIV", "IMPOWER", "IMABS", "IMSQRT", "IMLN", "IMLOG2",
        "IMLOG10", "IMSIN", "IMCOS", "IMEXP", "IMARGUMENT", "IMCONJUGATE", "IMAGINARY", "IMREAL", "COMPLEX",
        "IMSUM", "IMPRODUCT", "SERIESSUM", "FACTDOUBLE", "SQRTPI", "QUOTIENT", "DELTA", "GESTEP", "ISEVEN",
        "ISODD", "MROUND", "ERF", "ERFC", "BESSELJ", "BESSELK", "BESSELY", "BESSELI", "XIRR", "XNPV",
        "PRICEMAT", "YIELDMAT", "INTRATE", "RECEIVED", "DISC", "PRICEDISC", "YIELDDISC", "TBILLEQ",
        "TBILLPRICE", "TBILLYIELD", "PRICE", "YIELD", "DOLLARDE", "DOLLARFR", "NOMINAL", "EFFECT", "CUMPRINC",
        "CUMIPMT", "EDATE", "EOMONTH", "YEARFRAC", "COUPDAYBS", "COUPDAYS", "COUPDAYSNC", "COUPNCD", "COUPNUM",
        "COUPPCD", "DURATION", "MDURATION", "ODDLPRICE", "ODDLYIELD", "ODDFPRICE", "ODDFYIELD", "RANDBETWEEN",
        "WEEKNUM", "AMORDEGRC", "AMORLINC", "CONVERT", "ACCRINT", "ACCRINTM", "WORKDAY", "NETWORKDAYS", "GCD",
        "MULTINOMIAL", "LCM", "FVSCHEDULE", "CUBEKPIMEMBER", "CUBESET", "CUBESETCOUNT", "IFERROR", "COUNTIFS",
        "SUMIFS", "AVERAGEIF", "AVERAGEIFS",
    };

    /// <summary>Argument count per built-in function; 255 marks a variable-argument function.</summary>
    private static readonly byte[] FtabArgc =
    {
        255, 3, 1, 1, 255, 255, 255, 255, 1, 1, 0, 254, 255, 2, 3, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4,
        2, 3, 1, 1, 0, 0, 255, 255, 1, 2, 3, 3, 3, 3, 3, 3, 255, 3, 2, 4, 4, 4, 4, 1, 1, 1, 5, 5, 5, 5, 6, 3,
        2, 0, 3, 3, 3, 1, 1, 1, 2, 1, 1, 1, 0, 1, 1, 1, 5, 2, 2, 3, 3, 1, 2, 0, 1, 1, 2, 0, 1, 2, 2, 2, 0, 0,
        1, 2, 1, 1, 255, 4, 4, 2, 7, 1, 1, 2, 2, 2, 4, 1, 1, 1, 1, 2, 2, 2, 1, 4, 4, 1, 3, 1, 3, 2, 1, 1, 1, 1,
        1, 1, 2, 1, 1, 1, 2, 2, 2, 2, 1, 1, 3, 4, 5, 3, 2, 2, 2, 255, 255, 1, 4, 5, 5, 5, 5, 1, 3, 4, 3, 1, 1,
        1, 1, 1, 2, 6, 6, 255, 2, 4, 1, 0, 0, 2, 2, 3, 2, 1, 1, 1, 4, 255, 1, 2, 1, 2, 2, 3, 1, 3, 4, 255, 255,
        3, 3, 2, 1, 3, 1, 1, 0, 0, 2, 3, 3, 4, 2, 2, 3, 3, 2, 2, 1, 1, 3, 0, 0, 5, 3, 0, 7, 0, 1, 0, 3, 255,
        255, 1, 1, 1, 1, 1, 1, 3, 11, 1, 0, 2, 3, 5, 4, 4, 1, 0, 5, 5, 1, 0, 0, 1, 2, 2, 1, 255, 1, 1, 2, 3, 3,
        1, 1, 1, 2, 3, 3, 3, 2, 255, 5, 1, 5, 4, 2, 2, 2, 3, 3, 1, 3, 3, 3, 1, 1, 2, 4, 3, 2, 4, 3, 3, 3, 4, 1,
        3, 1, 3, 1, 2, 3, 3, 4, 2, 2, 2, 2, 2, 2, 3, 2, 2, 2, 2, 2, 2, 4, 4, 255, 255, 255, 255, 255, 255, 3,
        2, 2, 2, 2, 3, 255, 2, 2, 4, 4, 3, 255, 2, 9, 2, 3, 4, 1, 1, 255, 3, 2, 1, 2, 1, 4, 3, 1, 2, 2, 4, 5,
        2, 128, 2, 1, 255, 255, 255, 255, 255, 255, 255, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 255, 255, 3, 3, 4, 2,
        1, 2, 2, 2, 2, 2, 2, 1, 1, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 255, 255, 4, 1, 1, 2,
        2, 2, 1, 1, 2, 2, 1, 2, 2, 2, 2, 3, 3, 6, 6, 5, 5, 5, 5, 5, 3, 3, 3, 7, 7, 2, 2, 2, 2, 6, 6, 2, 2, 3,
        4, 4, 4, 4, 4, 4, 6, 6, 8, 8, 8, 8, 2, 2, 7, 7, 8, 8, 5, 3, 3, 255, 255, 255, 2, 4, 5, 1, 2, 128, 129,
        3, 129,
    };
}
