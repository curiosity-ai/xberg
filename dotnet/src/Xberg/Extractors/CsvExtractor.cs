using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xberg.Core;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// CSV / TSV extractor with delimiter sniffing, header detection, column-type inference,
/// and table extraction. Ported from Rust `extractors/csv.rs`.
/// </summary>
public sealed partial class CsvExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "text/csv", "text/tab-separated-values" };

    public int Priority => 60;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")] private static partial Regex DateIso();
    [GeneratedRegex(@"^\d{1,2}/\d{1,2}/\d{2,4}")] private static partial Regex DateUs();
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{2,4}")] private static partial Regex DateEu();

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string text = DecodeCsvBytes(content);
        char delimiter = mimeType == "text/tab-separated-values" ? '\t' : DetectDelimiter(text);

        var rows = ParseCsv(text, delimiter);

        int rowCount = rows.Count;
        int colCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        bool hasHeader = DetectHeader(rows);
        var columnTypes = InferColumnTypes(rows, hasHeader);

        string markdown = BuildMarkdownTable(rows);

        var csvMeta = new CsvMetadata
        {
            RowCount = (uint)rowCount,
            ColumnCount = (uint)colCount,
            Delimiter = delimiter != ',' ? delimiter.ToString() : null,
            HasHeader = hasHeader,
            ColumnTypes = columnTypes.Count == 0 ? null : columnTypes,
        };

        var builder = new InternalDocumentBuilder("csv");
        string contentText = hasHeader ? RenderCsvEmbeddingText(rows) : RenderTablePlainCsv(rows);
        builder.PushParagraph(contentText, new(), null, null);

        var doc = builder.Build();
        doc.Tables.Add(new Table
        {
            Cells = rows.Select(r => new List<string>(r)).ToList(),
            Markdown = markdown,
            PageNumber = 1,
            BoundingBox = null,
        });
        doc.MimeType = mimeType;
        doc.Metadata = new Metadata { Format = FormatMetadata.Csv(csvMeta) };
        return doc;
    }

    private static char DetectDelimiter(string text)
    {
        char[] candidates = { ',', '\t', '|', ';' };
        char best = ',';
        int bestScore = 0;
        string sample = string.Join("\n", text.Split('\n').Take(10));

        foreach (var candidate in candidates)
        {
            var rows = ParseCsv(sample, candidate);
            if (rows.Count < 2) continue;
            int firstCount = rows[0].Count;
            if (firstCount <= 1) continue;
            int consistent = rows.Count(r => r.Count == firstCount);
            int score = consistent * firstCount;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    private static List<List<string>> ParseCsv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { currentRow.Add(field.ToString()); field.Clear(); }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                currentRow.Add(field.ToString()); field.Clear();
                if (!currentRow.All(f => f.Length == 0)) rows.Add(currentRow);
                currentRow = new List<string>();
            }
            else if (c == '\n')
            {
                currentRow.Add(field.ToString()); field.Clear();
                if (!currentRow.All(f => f.Length == 0)) rows.Add(currentRow);
                currentRow = new List<string>();
            }
            else field.Append(c);
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            if (!currentRow.All(f => f.Length == 0)) rows.Add(currentRow);
        }
        return rows;
    }

    // Ordered fallback labels, mirroring Rust `decode_csv_bytes_fallback` (encoding_rs).
    private static readonly string[] CsvEncodingLabels =
        { "shift_jis", "windows-31j", "windows-1252", "iso-8859-1", "gb18030", "big5" };

    /// <summary>
    /// Decode raw CSV bytes with encoding detection, porting Rust `decode_csv_bytes`.
    /// UTF-8 fast path first; otherwise try the fallback labels in order and pick the first
    /// that decodes without producing replacement characters (matching encoding_rs's
    /// <c>had_errors == false</c> check). Legacy code pages are enabled by the module
    /// initializer in <see cref="Core.Encodings"/>.
    /// </summary>
    private static string DecodeCsvBytes(ReadOnlySpan<byte> content)
    {
        // Fast path: valid UTF-8.
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            // continue to legacy-charset detection
        }

        byte[] bytes = content.ToArray();

        // First label that decodes cleanly (no U+FFFD replacement) wins.
        foreach (var label in CsvEncodingLabels)
        {
            if (TryDecodeWithoutErrors(label, bytes, out string decoded))
                return decoded;
        }

        // All candidates had errors: decode as Shift-JIS anyway (lossy), matching Rust,
        // then fall back to lossy UTF-8.
        try { return Encoding.GetEncoding("shift_jis").GetString(bytes); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes); }
    }

    /// <summary>
    /// Decode <paramref name="bytes"/> with <paramref name="label"/> using the encoding's
    /// default replacement fallback, returning false when the label is unknown or the decoded
    /// text contains a U+FFFD replacement character (i.e. the encoding had decode errors).
    /// Note: we deliberately do NOT use <see cref="DecoderFallback.ExceptionFallback"/> — .NET's
    /// CP932 flags valid NEC/IBM-extension characters (e.g. 髙, bytes EE E0) as best-fit and
    /// would throw on them, unlike WHATWG shift_jis / encoding_rs which decode them correctly.
    /// </summary>
    private static bool TryDecodeWithoutErrors(string label, byte[] bytes, out string decoded)
    {
        decoded = "";
        Encoding enc;
        try { enc = Encoding.GetEncoding(label); }
        catch (ArgumentException) { return false; }
        string s = enc.GetString(bytes);
        if (s.Contains('�')) return false;
        decoded = s;
        return true;
    }

    private static bool DetectHeader(List<List<string>> rows)
    {
        if (rows.Count < 2) return false;
        var first = rows[0];
        if (first.Count < 2) return false;

        bool firstHasNumber = first.Any(cell =>
        {
            string t = cell.Trim();
            return t.Length > 0 && double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        });
        if (firstHasNumber) return false;

        int end = Math.Min(rows.Count, 6);
        for (int r = 1; r < end; r++)
        {
            if (rows[r].Any(cell =>
            {
                string t = cell.Trim();
                return t.Length > 0 && double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
            }))
                return true;
        }
        return false;
    }

    private static List<string> InferColumnTypes(List<List<string>> rows, bool hasHeader)
    {
        if (rows.Count == 0) return new();
        int colCount = rows.Max(r => r.Count);
        if (colCount == 0) return new();

        int dataStart = hasHeader ? 1 : 0;
        int scanEnd = Math.Min(rows.Count, dataStart + 20);
        if (dataStart >= scanEnd)
            return Enumerable.Repeat("text", colCount).ToList();

        var datePatterns = new[] { DateIso(), DateUs(), DateEu() };
        var result = new List<string>(colCount);

        for (int col = 0; col < colCount; col++)
        {
            int numeric = 0, date = 0, nonEmpty = 0;
            for (int r = dataStart; r < scanEnd; r++)
            {
                string cell = col < rows[r].Count ? rows[r][col].Trim() : "";
                if (cell.Length == 0) continue;
                nonEmpty++;
                if (double.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) numeric++;
                else if (datePatterns.Any(re => re.IsMatch(cell))) date++;
            }

            if (nonEmpty == 0) result.Add("text");
            else if (numeric * 2 >= nonEmpty) result.Add("numeric");
            else if (date * 2 >= nonEmpty) result.Add("date");
            else result.Add("text");
        }
        return result;
    }

    private static string RenderCsvEmbeddingText(List<List<string>> cells)
    {
        if (cells.Count < 2)
        {
            if (cells.Count == 1)
                return string.Join(" ", cells[0].Where(h => h.Trim().Length > 0));
            return "";
        }

        var headers = cells[0];
        var sb = new StringBuilder();
        int rowNumber = 0;

        for (int r = 1; r < cells.Count; r++)
        {
            var row = cells[r];
            if (row.All(c => c.Trim().Length == 0)) continue;
            rowNumber++;
            if (rowNumber > 1) sb.Append("\n\n");
            sb.Append("Row ").Append(rowNumber).Append(':');

            for (int col = 0; col < headers.Count; col++)
            {
                string header = headers[col].Trim();
                if (header.Length == 0) continue;
                string value = col < row.Count ? row[col].Trim() : "";
                if (value.Length == 0) continue;
                sb.Append('\n').Append(header).Append(": ").Append(value);
            }
        }
        return sb.ToString();
    }

    private static string RenderTablePlainCsv(List<List<string>> cells) =>
        string.Join("\n", cells
            .Select(row => string.Join(" ", row.Select(c => c.Trim()).Where(c => c.Length > 0)))
            .Where(line => line.Length > 0));

    private static string BuildMarkdownTable(List<List<string>> rows)
    {
        if (rows.Count == 0) return "";
        int colCount = rows.Max(r => r.Count);
        if (colCount == 0) return "";

        var md = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            md.Append('|');
            for (int j = 0; j < colCount; j++)
            {
                string cell = j < rows[i].Count ? rows[i][j].Trim() : "";
                md.Append(' ').Append(cell).Append(" |");
            }
            md.Append('\n');
            if (i == 0)
            {
                md.Append('|');
                for (int j = 0; j < colCount; j++) md.Append(" --- |");
                md.Append('\n');
            }
        }
        return md.ToString();
    }
}
