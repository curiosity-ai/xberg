using System.Text;
using Xberg.Internal.Biff;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for the BIFF formula (Ptg) decoder that backs the <c>formulas_&lt;sheet&gt;</c> metadata
/// of legacy .xls workbooks. Several expectations encode rendering quirks of the reference
/// implementation rather than what Excel itself would display; those are called out where they
/// appear.
/// </summary>
public sealed class BiffFormulaTests
{
    private static BiffFormulaContext Context(params string[] sheetNames)
    {
        var ctx = new BiffFormulaContext();
        ctx.SheetNames.AddRange(sheetNames);
        return ctx;
    }

    /// <summary>Wrap raw tokens in the `cce` length word a FORMULA record's expression starts with.</summary>
    private static string Parse(BiffFormulaContext ctx, params byte[] tokens)
    {
        var data = new byte[tokens.Length + 2];
        data[0] = (byte)(tokens.Length & 0xFF);
        data[1] = (byte)((tokens.Length >> 8) & 0xFF);
        tokens.CopyTo(data, 2);
        return BiffFormula.Parse(data, 0, data.Length, ctx, 0, 0);
    }

    private static string Parse(params byte[] tokens) => Parse(Context(), tokens);

    /// <summary>PtgRef (BIFF8): row word, then a column word whose top two bits mark relativeness.</summary>
    private static byte[] Ref(int row, int col, bool colRelative = true, bool rowRelative = true)
    {
        int colWord = col | (colRelative ? 0x8000 : 0) | (rowRelative ? 0x4000 : 0);
        return new byte[]
        {
            0x24,
            (byte)(row & 0xFF), (byte)((row >> 8) & 0xFF),
            (byte)(colWord & 0xFF), (byte)((colWord >> 8) & 0xFF),
        };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }

    // ── operands ────────────────────────────────────────────────────────────────

    [Fact]
    public void RelativeReferenceHasNoDollarSigns()
    {
        Assert.Equal("A1", Parse(Ref(0, 0)));
        Assert.Equal("N2", Parse(Ref(1, 13)));
    }

    [Fact]
    public void AbsoluteReferenceKeepsDollarSigns()
    {
        Assert.Equal("$B$1", Parse(Ref(0, 1, colRelative: false, rowRelative: false)));
    }

    [Fact]
    public void PreBiff8ReferencePacksTheColumnIntoOneByte()
    {
        var ctx = Context();
        ctx.Biff = BiffVersion.Biff5;
        // rw(2) with both relative bits set + col(1).
        Assert.Equal("C4", Parse(ctx, 0x24, 0x03, 0xC0, 0x02));
    }

    [Fact]
    public void AreaReferenceRendersTheColumnWordWithItsFlagBits()
    {
        // B2:G2 with relative rows and columns. The decoder renders the whole column word,
        // relative-reference flags included, which is where names like "USN" come from.
        var area = new byte[]
        {
            0x25,
            0x01, 0x00, // rowFirst = 1
            0x01, 0x00, // rowLast  = 1
            0x01, 0xC0, // colFirst = 1, both relative
            0x06, 0xC0, // colLast  = 6, both relative
        };
        Assert.Equal("$USN$2:$USS$2", Parse(area));
    }

    [Fact]
    public void ThreeDimensionalReferenceNamesItsSheetThroughTheXtiTable()
    {
        var ctx = Context("Summary", "Data");
        ctx.XtiItabFirst.Add(1);
        // PtgRef3d: ixti(2) + row(2) + col(2). The column word is shifted, not masked, so a
        // reference to a column beyond A does not round-trip; column 0 does.
        Assert.Equal("Data!A3", Parse(ctx, 0x3A, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00));
    }

    [Fact]
    public void ThreeDimensionalReferenceWithoutAnXtiEntryIsRef()
    {
        Assert.Equal("#REF!A1", Parse(Context("Sheet1"), 0x3A, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00));
    }

    [Fact]
    public void ThreeDimensionalAreaIndexesTheSheetListDirectly()
    {
        // Unlike a 3d reference, a 3d area uses the XTI index as a sheet index.
        var ctx = Context("Summary", "Data");
        var area = new byte[]
        {
            0x3B,
            0x01, 0x00, // ixti = 1, used as a sheet index
            0x00, 0x00, // rowFirst
            0x02, 0x00, // rowLast
            0x00, 0x00, // colFirst
            0x01, 0x00, // colLast
        };
        Assert.Equal("Data!$A$1:$B$3", Parse(ctx, area));
    }

    [Fact]
    public void LiteralOperands()
    {
        Assert.Equal("12", Parse(0x1E, 0x0C, 0x00));
        Assert.Equal("TRUE", Parse(0x1D, 0x01));
        Assert.Equal("FALSE", Parse(0x1D, 0x00));
        Assert.Equal("#DIV/0!", Parse(0x1C, 0x07));
        Assert.Equal("1.5", Parse(0x1F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x3F));
        Assert.Equal("\"hi\"", Parse(0x17, 0x02, 0x00, (byte)'h', (byte)'i'));
    }

    [Fact]
    public void DefinedNameIsResolvedOneBased()
    {
        var ctx = Context();
        ctx.DefinedNames.Add("Rate");
        Assert.Equal("Rate", Parse(ctx, 0x23, 0x01, 0x00, 0x00, 0x00));
        Assert.Equal("#REF!", Parse(ctx, 0x23, 0x09, 0x00, 0x00, 0x00));
    }

    // ── operators ───────────────────────────────────────────────────────────────

    [Fact]
    public void BinaryOperatorsJoinTheirOperands()
    {
        Assert.Equal("A1+B1", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x03 })));
        Assert.Equal("A1/B1", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x06 })));
        Assert.Equal("A1<>B1", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x0E })));
    }

    [Fact]
    public void UnaryOperatorsAndParenthesesWrapTheOperandOnTopOfTheStack()
    {
        Assert.Equal("-A1", Parse(Concat(Ref(0, 0), new byte[] { 0x13 })));
        Assert.Equal("A1%", Parse(Concat(Ref(0, 0), new byte[] { 0x14 })));
        Assert.Equal("(A1+B1)", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x03, 0x15 })));
    }

    [Fact]
    public void AttrSumWrapsItsOperandInSum()
    {
        Assert.Equal("SUM(A1)", Parse(Concat(Ref(0, 0), new byte[] { 0x19, 0x10, 0x00, 0x00 })));
    }

    [Fact]
    public void AttrSpaceInsertsWhitespaceAtTheStartOfTheOperandOnTopOfTheStack()
    {
        // The attribute carries whitespace the author typed. It is inserted at the start of the
        // expression currently on top of the stack — the operand that precedes it in the token
        // stream — so with a parenthesis token following, the space ends up just inside the
        // opening parenthesis.
        var space = new byte[] { 0x19, 0x40, 0x00, 0x01 };
        Assert.Equal(" A1*B1", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x05 }, space)));
        Assert.Equal("( A1*B1)", Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x05 }, space, new byte[] { 0x15 })));
    }

    [Fact]
    public void FunctionsRenderTheirArgumentsCommaSeparated()
    {
        // PtgFuncVar: argc(1) + iftab(2). iftab 4 is SUM.
        string sum = Parse(Concat(Ref(0, 0), Ref(0, 1), new byte[] { 0x22, 0x02, 0x04, 0x00 }));
        Assert.Equal("SUM(A1,B1)", sum);

        // PtgFunc: iftab(2) only, argument count comes from the built-in table. iftab 2 is ISNA.
        string isna = Parse(Concat(Ref(0, 0), new byte[] { 0x21, 0x02, 0x00 }));
        Assert.Equal("ISNA(A1)", isna);
    }

    [Fact]
    public void ZeroArgumentFunctionsRenderEmptyParentheses()
    {
        // iftab 10 is NA.
        Assert.Equal("NA()", Parse(0x22, 0x00, 0x0A, 0x00));
    }

    // ── failure handling ────────────────────────────────────────────────────────

    [Fact]
    public void UnknownTokenBecomesAPlaceholderNamingTheCell()
    {
        var data = new byte[] { 0x01, 0x00, 0xFF };
        Assert.Equal(
            "Unrecognised formula for cell (3, 4): Unrecognized { typ: \"ptg\", val: 255 }",
            BiffFormula.Parse(data, 0, data.Length, Context(), 3, 4));
    }

    [Fact]
    public void LeftoverOperandsBecomeAPlaceholder()
    {
        Assert.Equal(
            "Unrecognised formula for cell (0, 0): InvalidFormula { stack_size: 2 }",
            Parse(Concat(Ref(0, 0), Ref(0, 1))));
    }

    [Fact]
    public void TruncatedExpressionBecomesAPlaceholderInsteadOfThrowing()
    {
        // `cce` claims more bytes than the record holds.
        var data = new byte[] { 0x20, 0x00, 0x24, 0x00 };
        Assert.StartsWith("Unrecognised formula for cell (1, 1): EoStream", BiffFormula.Parse(data, 0, data.Length, Context(), 1, 1));
    }

    [Fact]
    public void SharedFormulaTokenDecodesToEmptyText()
    {
        // PtgExp points at the cell that owns the shared formula; nothing is rendered here.
        Assert.Equal("", Parse(0x01, 0x00, 0x00, 0x00, 0x00));
    }

    // ── column letters and metadata collection ──────────────────────────────────

    [Fact]
    public void PushColumnDropsTheMostSignificantDigit()
    {
        // 26 renders as "A" rather than "AA": the loop divides without carrying. Reference
        // behaviour, and the reason flagged column words render as they do.
        Assert.Equal("A", Column(0));
        Assert.Equal("Z", Column(25));
        Assert.Equal("A", Column(26));
        Assert.Equal("USN", Column(0xC001));

        static string Column(uint col)
        {
            var sb = new StringBuilder();
            BiffFormula.PushColumn(col, sb);
            return sb.ToString();
        }
    }

    [Fact]
    public void CollectedFormulasAreRelativeToTheFormulaGrid()
    {
        var grid = new List<List<string>>
        {
            new() { "A1+1", "", "B1+1" },
            new() { "", "", "" },
            new() { "C1+1", "", "" },
        };
        Assert.Equal("A1=A1+1; C1=B1+1; A3=C1+1", BiffFormula.CollectSheetFormulas(grid));
    }

    [Fact]
    public void CollectedFormulasAreCappedPerSheet()
    {
        var row = new List<string>();
        for (int i = 0; i < 250; i++) row.Add("A1");
        var result = BiffFormula.CollectSheetFormulas(new List<List<string>> { row });
        Assert.NotNull(result);
        Assert.Equal(200, result!.Split("; ").Length);
    }

    [Fact]
    public void SheetsWithoutFormulasReportNothing()
    {
        Assert.Null(BiffFormula.CollectSheetFormulas(null));
        Assert.Null(BiffFormula.CollectSheetFormulas(new List<List<string>> { new() { "", "" } }));
    }

    // ── end to end over a real workbook ─────────────────────────────────────────

    [Fact]
    public void XlsWorkbookReportsItsSheetFormulas()
    {
        var path = FindFixture("xls/test_excel.xls");
        if (path is null) return;

        var comp = Xberg.Internal.Cfb.CompoundFile.Open(File.ReadAllBytes(path));
        var sheets = BiffReader.ReadSheets(comp);
        var sheet = Assert.Single(sheets);
        var formulas = BiffFormula.CollectSheetFormulas(sheet.Formulas);
        Assert.NotNull(formulas);
        Assert.StartsWith("A1=K2*L2*12; B1=B2; C1=C2; D1=D2; E1=E2; F1=F2; G1=SUM($USN$2:$USS$2); M1=((K2*L2)-( L2*M2))*12;", formulas);
        Assert.EndsWith("I6=I7-H7; I7=J7/I7", formulas);
    }

    private static string? FindFixture(string relative)
    {
        foreach (var candidate in new[]
        {
            "/workspace/test_documents",
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents"),
        })
        {
            if (!Directory.Exists(candidate)) continue;
            var path = Path.Combine(candidate, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
