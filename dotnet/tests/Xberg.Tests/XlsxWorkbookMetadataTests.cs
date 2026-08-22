using System.IO.Compression;
using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Workbook-level facts a spreadsheet carries outside its cells.
/// </summary>
public class XlsxWorkbookMetadataTests
{
    private static byte[] Package((string Name, string Body)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(body);
            }
        return buffer.ToArray();
    }

    private const string ContentTypes = """
        <?xml version="1.0"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>
        """;

    private const string WorkbookRels = """
        <?xml version="1.0"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Target="worksheets/sheet2.xml"/>
        </Relationships>
        """;

    private static InternalDocument Extract(string workbookXml, string sheet1, string sheet2 = "<worksheet><sheetData/></worksheet>") =>
        new XlsxExtractor().Extract(
            Package(new[]
            {
                ("[Content_Types].xml", ContentTypes),
                ("xl/workbook.xml", workbookXml),
                ("xl/_rels/workbook.xml.rels", WorkbookRels),
                ("xl/worksheets/sheet1.xml", sheet1),
                ("xl/worksheets/sheet2.xml", sheet2),
            }),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            new ExtractionConfig());

    private static string? Additional(InternalDocument doc, string key) =>
        doc.Metadata.Additional.TryGetValue(key, out var v) ? v.GetString() : null;

    [Fact]
    public void AHiddenSheetSaysSoInItsHeading()
    {
        // A sheet's own content carries no visibility flag; only the workbook's sheet list does,
        // and without it hidden content is indistinguishable from visible content.
        var doc = Extract("""
            <?xml version="1.0"?>
            <workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Visible" sheetId="1" r:id="rId1"/>
                <sheet name="Secret" sheetId="2" state="hidden" r:id="rId2"/>
              </sheets>
            </workbook>
            """,
            """<worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c></row></sheetData></worksheet>""",
            """<worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>y</t></is></c></row></sheetData></worksheet>""");

        var headings = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).Select(e => e.Text).ToList();
        Assert.Equal(new[] { "Visible", "Secret (hidden)" }, headings);
    }

    [Fact]
    public void FormulaReferencesAreRelativeToTheFormulaBlock()
    {
        // The entries summarise what a sheet computes rather than mapping where: a block of
        // formulas starting at D2 is reported from A1, which is what the reference produces.
        var doc = Extract("""
            <?xml version="1.0"?>
            <workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Data" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """,
            """
            <worksheet><sheetData>
              <row r="2"><c r="D2"><f>B2+C2</f><v>3</v></c></row>
              <row r="3"><c r="D3"><f>B3+C3</f><v>7</v></c></row>
            </sheetData></worksheet>
            """);

        Assert.Equal("A1=B2+C2; A2=B3+C3", Additional(doc, "formulas_Data"));
    }

    [Fact]
    public void DefinedNamesAreReported()
    {
        var doc = Extract("""
            <?xml version="1.0"?>
            <workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Page 1" sheetId="1" r:id="rId1"/></sheets>
              <definedNames>
                <definedName name="_xlnm.Print_Area">'Page 1'!$A$1:$H$38</definedName>
              </definedNames>
            </workbook>
            """,
            """<worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c></row></sheetData></worksheet>""");

        Assert.Equal("_xlnm.Print_Area='Page 1'!$A$1:$H$38", Additional(doc, "defined_names"));
    }
}
