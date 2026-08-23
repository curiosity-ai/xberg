// Tests for the structure-tree reading-order tier: pdf_oxide
// `pipeline/reading_order/structure_tree.rs` (`StructureTreeStrategy::apply`,
// `mcid_order_zigzags_columns`), reached through `pipeline::page_reading_order`.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xberg.Core;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Layout;
using Xunit;

namespace Xberg.Tests;

public class OxStructureOrderTests
{
    private static OxTextSpan Span(string text, float x, float y, int? mcid = null, float width = 40.0f) =>
        new()
        {
            Text = text,
            Bbox = new OxRect(x, y, width, 12.0f),
            FontSize = 12.0f,
            Mcid = mcid,
        };

    [Fact]
    public void McidOrderDrivesTheOrderAgainstGeometry()
    {
        // Three spans whose logical order (2, 0, 1) is the reverse of nothing geometric:
        // the title is drawn last and sits at the bottom of the page.
        var spans = new List<OxTextSpan>
        {
            Span("body-a", 50.0f, 700.0f, mcid: 1),
            Span("body-b", 50.0f, 680.0f, mcid: 2),
            Span("title", 50.0f, 100.0f, mcid: 0),
        };

        var ordered = OxStructureOrder.Apply(spans, new[] { 0, 1, 2 });

        Assert.Equal(new[] { "title", "body-a", "body-b" }, ordered.Select(s => s.Text));
    }

    [Fact]
    public void SpansOutsideTheTreeAreAppendedInGeometricOrder()
    {
        var spans = new List<OxTextSpan>
        {
            Span("loose-low", 50.0f, 100.0f),
            Span("tagged", 50.0f, 700.0f, mcid: 0),
            // An MCID the tree never references reads as untagged.
            Span("loose-high", 50.0f, 200.0f, mcid: 99),
        };

        var ordered = OxStructureOrder.Apply(spans, new[] { 0 });

        // Tagged spans first in structure order, then the rest top-to-bottom.
        Assert.Equal(new[] { "tagged", "loose-high", "loose-low" }, ordered.Select(s => s.Text));
    }

    [Fact]
    public void SpansSharingOneMcidKeepTheirIncomingOrder()
    {
        var spans = new List<OxTextSpan>
        {
            Span("first", 300.0f, 700.0f, mcid: 5),
            Span("second", 50.0f, 700.0f, mcid: 5),
        };

        var ordered = OxStructureOrder.Apply(spans, new[] { 5 });

        Assert.Equal(new[] { "first", "second" }, ordered.Select(s => s.Text));
    }

    /// Two 220pt columns with a 30pt gutter, ten lines each.
    private static List<OxTextSpan> TwoColumnPage()
    {
        var spans = new List<OxTextSpan>();
        for (int line = 0; line < 10; line++)
        {
            float y = 700.0f - line * 14.0f;
            spans.Add(Span($"L{line}", 50.0f, y, mcid: line * 2, width: 220.0f));
            spans.Add(Span($"R{line}", 300.0f, y, mcid: line * 2 + 1, width: 220.0f));
        }
        return spans;
    }

    [Fact]
    public void AnMcidOrderThatInterleavesColumnsIsRejected()
    {
        var spans = TwoColumnPage();
        // Content-stream order: L0 R0 L1 R1 … — one crossing per line.
        var zigzag = Enumerable.Range(0, 20).ToList();

        Assert.True(OxStructureOrder.McidOrderZigzagsColumns(spans, zigzag));

        // The fallback is the geometric XY-cut, which reads column-major.
        var ordered = OxStructureOrder.Apply(spans, zigzag);
        Assert.Equal(
            new[] { "L0", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9" },
            ordered.Take(10).Select(s => s.Text));
    }

    [Fact]
    public void AColumnRespectingMcidOrderIsKept()
    {
        var spans = TwoColumnPage();
        // Left column fully, then the right: a single crossing.
        var columnMajor = Enumerable.Range(0, 10).Select(i => i * 2)
            .Concat(Enumerable.Range(0, 10).Select(i => i * 2 + 1)).ToList();

        Assert.False(OxStructureOrder.McidOrderZigzagsColumns(spans, columnMajor));
    }

    [Fact]
    public void ASingleColumnPageNeverZigzags()
    {
        var spans = new List<OxTextSpan>();
        for (int line = 0; line < 20; line++)
            spans.Add(Span($"L{line}", 50.0f, 700.0f - line * 14.0f, mcid: line, width: 40.0f));

        // The X extent is under the 50pt floor, so the column test never engages.
        Assert.False(OxStructureOrder.McidOrderZigzagsColumns(spans, Enumerable.Range(0, 20).ToList()));
    }

    [Fact]
    public void FewerThanTenTaggedSpansSkipTheZigzagCheck()
    {
        var spans = new List<OxTextSpan>();
        for (int i = 0; i < 9; i++)
            spans.Add(Span($"s{i}", i % 2 == 0 ? 50.0f : 400.0f, 700.0f - i * 14.0f, mcid: i));

        Assert.False(OxStructureOrder.McidOrderZigzagsColumns(spans, Enumerable.Range(0, 9).ToList()));
    }

    /// <summary>
    /// A tagged RTL form whose label and value cells sit either side of a ruled grid. Read
    /// geometrically, the two are separate words in separate cells and the grid reads as a
    /// 10x4 table; read in structure order they are reading-order-adjacent, so
    /// `merge_adjacent_words` fuses each label with its value into one word whose box spans
    /// the whole row — leaving three of the four columns empty, which `is_valid_table`
    /// rejects. Upstream emits no table here.
    /// </summary>
    [Fact]
    public void ATaggedRtlFormYieldsNoSpatialTable()
    {
        string? path = FindFixture("vendored/docling/pdf/right_to_left_03.pdf");
        if (path is null) return;

        var result = new Extractor().Extract(
            ExtractInput.FromUri(path), new ExtractionConfig { OutputFormat = OutputFormat.Plain });

        Assert.Empty(result.Results[0].Tables);
    }

    private static string? FindFixture(string relative) =>
        new[]
        {
            Path.Combine("/workspace/test_documents", relative),
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents", relative),
        }.FirstOrDefault(File.Exists);
}
