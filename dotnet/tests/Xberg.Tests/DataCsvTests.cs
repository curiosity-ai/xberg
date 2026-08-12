using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>Tests for <see cref="CsvExtractor"/>. Ports the Rust `extractors/csv.rs` tests.</summary>
public class DataCsvTests
{
    private static InternalDocument Extract(string text, string mime = "text/csv") =>
        new CsvExtractor().Extract(Encoding.UTF8.GetBytes(text), mime, new ExtractionConfig());

    [Fact]
    public void SimpleCsv_ProducesTableAndCsvMetadata()
    {
        var doc = Extract("Name,Age,City\nAlice,30,NYC\nBob,25,LA\n");
        Assert.Single(doc.Tables);
        Assert.Equal(3, doc.Tables[0].Cells.Count);
        var meta = Assert.IsType<CsvMetadata>(doc.Metadata.Format!.Payload);
        Assert.True(meta.HasHeader);
        Assert.Equal(3u, meta.ColumnCount);
        Assert.Equal(3u, meta.RowCount);
        Assert.NotNull(meta.ColumnTypes);
        Assert.Equal(new[] { "text", "numeric", "text" }, meta.ColumnTypes);
    }

    [Fact]
    public void QuotedFieldsWithCommas_ParsedCorrectly()
    {
        var doc = Extract("Name,Description\n\"Smith, John\",\"Has a comma, inside\"\n");
        Assert.Equal("Smith, John", doc.Tables[0].Cells[1][0]);
        Assert.Equal("Has a comma, inside", doc.Tables[0].Cells[1][1]);
    }

    [Fact]
    public void HeaderCsv_RendersEmbeddingText()
    {
        string plain = Xberg.Rendering.PlainRenderer.Render(Extract("Name,Age\nAlice,30\n"));
        Assert.Equal("Row 1:\nName: Alice\nAge: 30", plain);
    }

    [Fact]
    public void Tsv_UsesTabDelimiter()
    {
        var doc = Extract("a\tb\tc\n1\t2\t3\n", "text/tab-separated-values");
        Assert.Equal(new[] { "a", "b", "c" }, doc.Tables[0].Cells[0]);
    }

    [Fact]
    public void DelimiterSniffing_DetectsSemicolon()
    {
        var doc = Extract("a;b;c\n1;2;3\n4;5;6\n");
        var meta = Assert.IsType<CsvMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(";", meta.Delimiter);
    }

    [Fact]
    public void DateColumn_InferredAsDate()
    {
        var doc = Extract("Name,Date\nAlice,2024-01-15\nBob,2024-02-20\n");
        var meta = Assert.IsType<CsvMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(new[] { "text", "date" }, meta.ColumnTypes);
    }

    [Fact]
    public void NumericFirstRow_NotTreatedAsHeader()
    {
        var doc = Extract("1,2,3\n4,5,6\n");
        var meta = Assert.IsType<CsvMetadata>(doc.Metadata.Format!.Payload);
        Assert.False(meta.HasHeader);
    }
}
