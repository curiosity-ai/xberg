using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Xberg.Internal.Ooxml;

/// <summary>One worksheet: name, dense cell grid (null when empty), and pre-rendered markdown.</summary>
public sealed class ExcelSheet
{
    public string Name = "";
    public string Markdown = "";
    public List<List<string>>? TableCells;
}

/// <summary>A parsed workbook: ordered sheets + a flat string metadata map.</summary>
public sealed class ExcelWorkbook
{
    public List<ExcelSheet> Sheets { get; } = new();
    public Dictionary<string, string> Metadata { get; } = new();
}

/// <summary>
/// Pure-managed XLSX/XLSM/XLTM/XLAM reader — ports the calamine-backed behaviour in
/// <c>extraction/excel.rs</c> (dense used-range grid, cell-to-string formatting,
/// markdown table generation) over an OOXML ZIP package. Binary formats (.xls/.xlsb) and
/// ODS are out of scope.
/// </summary>
public static class XlsxReader
{
    public static ExcelWorkbook Read(ReadOnlySpan<byte> content, bool officeMetadata)
    {
        using var pkg = new OoxmlPackage(content);
        var wb = new ExcelWorkbook();

        var sheetNames = ReadSheetOrder(pkg);
        var shared = ReadSharedStrings(pkg);

        foreach (var (name, target) in sheetNames)
        {
            var sheet = ProcessSheet(pkg, name, target, shared);
            wb.Sheets.Add(sheet);
        }

        ExtractMetadata(pkg, wb, sheetNames.Select(s => s.Name).ToList(), officeMetadata);
        return wb;
    }

    // ── workbook.xml: ordered (sheet name → worksheet part path) ───────────────
    private static List<(string Name, string Target)> ReadSheetOrder(OoxmlPackage pkg)
    {
        var result = new List<(string, string)>();
        var wbXml = pkg.ReadXml("xl/workbook.xml");
        if (wbXml?.Root is null) return result;

        // rId → target (relative to xl/)
        var rels = new Dictionary<string, string>(StringComparer.Ordinal);
        var relsXml = pkg.ReadXml("xl/_rels/workbook.xml.rels");
        if (relsXml?.Root is not null)
            foreach (var r in relsXml.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                var id = r.Attribute("Id")?.Value;
                var tgt = r.Attribute("Target")?.Value;
                if (id is not null && tgt is not null) rels[id] = tgt;
            }

        var sheetsEl = wbXml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheets");
        if (sheetsEl is null) return result;
        foreach (var s in sheetsEl.Elements().Where(e => e.Name.LocalName == "sheet"))
        {
            var name = s.Attributes().FirstOrDefault(a => a.Name.LocalName == "name")?.Value ?? "";
            var rid = s.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value; // r:id
            string target = "";
            if (rid is not null && rels.TryGetValue(rid, out var t))
                target = ResolveXlPath(t);
            result.Add((name, target));
        }
        return result;
    }

    private static string ResolveXlPath(string target)
    {
        if (target.StartsWith('/')) return target[1..];
        if (target.StartsWith("xl/", StringComparison.Ordinal)) return target;
        return "xl/" + target;
    }

    // ── sharedStrings.xml ──────────────────────────────────────────────────────
    private static List<string> ReadSharedStrings(OoxmlPackage pkg)
    {
        var list = new List<string>();
        var xml = pkg.ReadXml("xl/sharedStrings.xml");
        if (xml?.Root is null) return list;
        foreach (var si in xml.Root.Elements().Where(e => e.Name.LocalName == "si"))
            list.Add(ConcatText(si));
        return list;
    }

    /// <summary>Concatenate all descendant &lt;t&gt; text in document order.</summary>
    private static string ConcatText(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var t in element.Descendants().Where(e => e.Name.LocalName == "t"))
            sb.Append(t.Value);
        return sb.ToString();
    }

    // ── worksheet → ExcelSheet ─────────────────────────────────────────────────
    private static ExcelSheet ProcessSheet(OoxmlPackage pkg, string name, string target, List<string> shared)
    {
        var cellsByPos = new Dictionary<(int Row, int Col), string>();
        int rowMin = int.MaxValue, rowMax = -1, colMin = int.MaxValue, colMax = -1;

        var xml = target.Length > 0 ? pkg.ReadXml(target) : null;
        var sheetData = xml?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is not null)
        {
            int autoRow = 0;
            foreach (var row in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
            {
                int rowIdx = ParseRowIndex(row) ?? autoRow;
                autoRow = rowIdx + 1;
                int autoCol = 0;
                foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    int colIdx = ParseColIndex(c) ?? autoCol;
                    autoCol = colIdx + 1;
                    if (TryCellValue(c, shared, out var value))
                    {
                        cellsByPos[(rowIdx, colIdx)] = value;
                        if (rowIdx < rowMin) rowMin = rowIdx;
                        if (rowIdx > rowMax) rowMax = rowIdx;
                        if (colIdx < colMin) colMin = colIdx;
                        if (colIdx > colMax) colMax = colIdx;
                    }
                }
            }
        }

        if (rowMax < 0)
        {
            // Empty sheet.
            return new ExcelSheet { Name = name, Markdown = $"## {name}\n\n*Empty sheet*", TableCells = null };
        }

        // Dense grid over the used bounding box.
        int width = colMax - colMin + 1;
        var grid = new List<List<string>>(rowMax - rowMin + 1);
        for (int r = rowMin; r <= rowMax; r++)
        {
            var rowCells = new List<string>(width);
            for (int col = colMin; col <= colMax; col++)
                rowCells.Add(cellsByPos.TryGetValue((r, col), out var v) ? v : "");
            grid.Add(rowCells);
        }

        var markdown = GenerateMarkdown(name, grid);
        return new ExcelSheet { Name = name, Markdown = markdown, TableCells = grid };
    }

    private static int? ParseRowIndex(XElement row)
    {
        var r = row.Attribute("r")?.Value;
        if (r is not null && int.TryParse(r, out var v)) return v - 1;
        return null;
    }

    private static int? ParseColIndex(XElement cell)
    {
        var r = cell.Attribute("r")?.Value;
        if (r is null) return null;
        int col = 0; bool any = false;
        foreach (var ch in r)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); any = true; }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); any = true; }
            else break;
        }
        return any ? col - 1 : null;
    }

    /// <summary>Format a cell's value like calamine's <c>format_cell_to_string</c>. Returns false for empty cells.</summary>
    private static bool TryCellValue(XElement c, List<string> shared, out string value)
    {
        value = "";
        string? type = c.Attribute("t")?.Value;

        if (type == "inlineStr")
        {
            var isEl = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
            if (isEl is null) return false;
            value = ConcatText(isEl);
            return true;
        }

        var vEl = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
        if (vEl is null) return false;
        string raw = vEl.Value;

        switch (type)
        {
            case "s":
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                    && idx >= 0 && idx < shared.Count)
                    value = shared[idx];
                else value = "";
                return true;
            case "str":
                value = raw;
                return true;
            case "b":
                value = raw.Trim() == "1" ? "true" : "false";
                return true;
            case "e":
                value = "#ERR: " + raw;
                return true;
            case "d":
                value = raw;
                return true;
            default:
                // Numeric (t absent or "n"): shortest round-trippable, integers without ".0".
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    value = d.ToString("R", CultureInfo.InvariantCulture);
                else
                    value = raw;
                return true;
        }
    }

    // ── markdown table (faithful to generate_markdown_and_cells) ───────────────
    internal static string GenerateMarkdown(string name, List<List<string>> cells)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(name).Append("\n\n");
        if (cells.Count == 0) return sb.ToString();

        int headerLen = cells[0].Count;
        // Header row
        sb.Append("| ");
        for (int i = 0; i < headerLen; i++)
        {
            if (i > 0) sb.Append(" | ");
            AppendCell(sb, cells[0][i]);
        }
        sb.Append(" |\n");
        // Separator
        sb.Append("| ");
        for (int i = 0; i < headerLen; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append("---");
        }
        sb.Append(" |\n");
        // Body
        for (int r = 1; r < cells.Count; r++)
        {
            sb.Append("| ");
            for (int i = 0; i < headerLen; i++)
            {
                if (i > 0) sb.Append(" | ");
                AppendCell(sb, i < cells[r].Count ? cells[r][i] : "");
            }
            sb.Append(" |\n");
        }
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, string cell)
    {
        if (cell.Contains('|') || cell.Contains('\\'))
        {
            foreach (var ch in cell)
            {
                if (ch == '|') sb.Append("\\|");
                else if (ch == '\\') sb.Append("\\\\");
                else sb.Append(ch);
            }
        }
        else sb.Append(cell);
    }

    // ── metadata (extract_metadata + office metadata merge) ────────────────────
    private static void ExtractMetadata(OoxmlPackage pkg, ExcelWorkbook wb, List<string> sheetNames, bool officeMetadata)
    {
        int sheetCount = sheetNames.Count;
        wb.Metadata["sheet_count"] = sheetCount.ToString(CultureInfo.InvariantCulture);

        string namesStr;
        if (sheetCount <= 5)
            namesStr = string.Join(", ", sheetNames);
        else
            namesStr = string.Join(", ", sheetNames.Take(5)) + $", ... ({sheetCount} total)";
        wb.Metadata["sheet_names"] = namesStr;

        if (!officeMetadata) return;

        var core = OfficeMetadata.ExtractCore(pkg);
        if (core.Title is not null) wb.Metadata["title"] = core.Title;
        if (core.Creator is not null) { wb.Metadata["creator"] = core.Creator; wb.Metadata["created_by"] = core.Creator; }
        if (core.Subject is not null) wb.Metadata["subject"] = core.Subject;
        if (core.Keywords is not null) wb.Metadata["keywords"] = core.Keywords;
        if (core.Description is not null) wb.Metadata["description"] = core.Description;
        if (core.LastModifiedBy is not null) wb.Metadata["modified_by"] = core.LastModifiedBy;
        if (core.Created is not null) wb.Metadata["created_at"] = core.Created;
        if (core.Modified is not null) wb.Metadata["modified_at"] = core.Modified;
        if (core.Revision is not null) wb.Metadata["revision"] = core.Revision;
        if (core.Category is not null) wb.Metadata["category"] = core.Category;
        if (core.ContentStatus is not null) wb.Metadata["content_status"] = core.ContentStatus;
        if (core.Language is not null) wb.Metadata["language"] = core.Language;

        var app = OfficeMetadata.ExtractApp(pkg);
        if (app.TitlesOfParts.Count > 0) wb.Metadata["worksheet_names"] = string.Join(", ", app.TitlesOfParts);
        if (app.Company is not null) wb.Metadata["organization"] = app.Company;
        if (app.Application is not null) wb.Metadata["application"] = app.Application;
        if (app.AppVersion is not null) wb.Metadata["application_version"] = app.AppVersion;
        // #230: surface the raw DocSecurity integer plus its decoded ECMA-376 flags —
        // XlsxAppProperties never reaches the format metadata, so without this the workbook's
        // protection state is discarded entirely.
        if (app.DocSecurity is { } docSecurity)
        {
            wb.Metadata[OfficeMetadata.DocSecurityKey] = docSecurity.ToString(CultureInfo.InvariantCulture);
            foreach (var (key, value) in OfficeMetadata.DecodeDocSecurityFlags(docSecurity))
                wb.Metadata[key] = value ? "true" : "false";
        }

        foreach (var (k, v) in OfficeMetadata.ExtractCustom(pkg))
            wb.Metadata["custom_" + k] = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    }
}
