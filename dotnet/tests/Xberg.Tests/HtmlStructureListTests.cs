using Xberg.Internal.Email;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// List and table nesting in the HTML <see cref="DocumentStructure"/> walker, ported from the
/// Rust <c>extraction::html::structure</c> tests. Covers upstream tasks #719, #721, #727 and #728
/// (<c>fix(html): close the open list item before descending into a sublist</c>,
/// <c>… return to the outer list item after a nested list closes</c>,
/// <c>… keep list-item annotations and nest sublists under their item</c>) and
/// <c>fix(html): bound colspan and rowspan before sizing the occupancy grid</c>.
/// </summary>
public sealed class HtmlStructureListTests
{
    private static List<int> ListNodeIndices(DocumentStructure doc) =>
        doc.Nodes.Select((node, i) => (node, i))
           .Where(t => t.node.Content.Which == NodeContent.Tag.List)
           .Select(t => t.i).ToList();

    private static List<string> ListItemTexts(DocumentStructure doc, int listIdx) =>
        doc.Nodes[listIdx].Children
           .Select(child => doc.Nodes[(int)child])
           .Where(n => n.Content.Which == NodeContent.Tag.ListItem)
           .Select(n => n.Content.Text ?? "").ToList();

    private static List<string> ParagraphTexts(DocumentStructure doc) =>
        doc.Nodes.Where(n => n.Content.Which == NodeContent.Tag.Paragraph)
           .Select(n => n.Content.Text ?? "").ToList();

    /// <summary>
    /// Task #719: a <c>&lt;ul&gt;</c>/<c>&lt;ol&gt;</c> start tag used to flush only the pending
    /// paragraph buffer, not the pending list-item buffer. When a nested list opened while the
    /// parent <c>&lt;li&gt;</c> still had unflushed text, that text was later flushed against the
    /// freshly-pushed <em>inner</em> list — misattributing the parent item one level too deep and
    /// leaving the outermost list empty.
    /// </summary>
    [Fact]
    public void ANestedListItemAttachesToItsOwnListLevel()
    {
        var doc = HtmlStructure.Build("<ul><li>L1<ul><li>L2<ul><li>L3</li></ul></li></ul></li></ul>");

        var lists = ListNodeIndices(doc);
        Assert.Equal(3, lists.Count);
        foreach (var (listIdx, expected) in lists.Zip(new[] { "L1", "L2", "L3" }))
            Assert.Equal(new[] { expected }, ListItemTexts(doc, listIdx));
    }

    /// <summary>
    /// Task #721: content resuming in the outer <c>&lt;li&gt;</c> after a sublist closes must stay
    /// list-item content. <c>_inListItem</c> is a single bool, so the inner list's start and end
    /// handlers both cleared it while the outer item was still open; the trailing text then missed
    /// the list-item branch and landed in the paragraph buffer.
    /// </summary>
    [Fact]
    public void TextAfterASublistReturnsToTheOuterListItem()
    {
        var doc = HtmlStructure.Build("<ul><li>before text<ul><li>child</li></ul>after text</li></ul>");

        var lists = ListNodeIndices(doc);
        Assert.Equal(2, lists.Count);
        Assert.Equal(new[] { "before text", "after text" }, ListItemTexts(doc, lists[0]));
        Assert.Equal(new[] { "child" }, ListItemTexts(doc, lists[1]));
        Assert.Empty(ParagraphTexts(doc));
    }

    /// <summary>
    /// Task #721, three levels deep: each trailing run rejoins the level whose item is still open,
    /// not the level it was nested under. Unfixed, both trailing runs landed in the same paragraph
    /// buffer and were emitted as one concatenated <c>Paragraph</c>.
    /// </summary>
    [Fact]
    public void TrailingTextAfterASublistRejoinsItsOwnLevel()
    {
        var doc = HtmlStructure.Build(
            "<ol><li>L1<ol><li>L2<ol><li>L3</li></ol>after L2</li></ol>after L1</li></ol>");

        var lists = ListNodeIndices(doc);
        Assert.Equal(3, lists.Count);
        Assert.All(lists, i => Assert.True(doc.Nodes[i].Content.Ordered));
        Assert.Equal(new[] { "L1", "after L1" }, ListItemTexts(doc, lists[0]));
        Assert.Equal(new[] { "L2", "after L2" }, ListItemTexts(doc, lists[1]));
        Assert.Equal(new[] { "L3" }, ListItemTexts(doc, lists[2]));
        Assert.Empty(ParagraphTexts(doc));
    }

    /// <summary>
    /// Task #728: a sublist is a child of the <c>&lt;li&gt;</c> it is written inside, so a consumer
    /// walking the tree renders it before the item's trailing text rather than after the whole
    /// outer list.
    /// </summary>
    [Fact]
    public void ASublistIsParentedUnderTheItemItIsWrittenIn()
    {
        var doc = HtmlStructure.Build("<ul><li>parent<ul><li>child</li></ul></li></ul>");

        var lists = ListNodeIndices(doc);
        Assert.Equal(2, lists.Count);
        int parentItem = doc.Nodes[lists[0]].Children.Single(c =>
            doc.Nodes[(int)c].Content.Which == NodeContent.Tag.ListItem
            && doc.Nodes[(int)c].Content.Text == "parent") is var idx ? (int)idx : -1;
        Assert.Equal((uint)parentItem, doc.Nodes[lists[1]].Parent);
    }

    /// <summary>
    /// Task #728, fallback shape: <c>&lt;li&gt;&lt;ul&gt;…</c> has no item text and so no
    /// <c>ListItem</c> node to hang the sublist on. It falls back to the enclosing <c>List</c>
    /// rather than minting an empty item.
    /// </summary>
    [Fact]
    public void ASublistWithNoItemTextFallsBackToTheEnclosingList()
    {
        var doc = HtmlStructure.Build("<ul><li><ul><li>child</li></ul></li></ul>");

        var lists = ListNodeIndices(doc);
        Assert.Equal(2, lists.Count);
        Assert.Equal((uint)lists[0], doc.Nodes[lists[1]].Parent);
        Assert.DoesNotContain(doc.Nodes, n => n.Content.Which == NodeContent.Tag.ListItem
                                              && string.IsNullOrEmpty(n.Content.Text));
    }

    /// <summary>
    /// Task #727: the item owns the annotations buffered while it was the live context, and the
    /// inline stack is cleared with it so a span whose closing tag never arrives cannot annotate an
    /// unrelated node at meaningless offsets.
    /// </summary>
    [Fact]
    public void AListItemKeepsItsOwnInlineAnnotations()
    {
        var doc = HtmlStructure.Build("<ul><li>plain <b>bold</b><ul><li>child</li></ul></li></ul>");

        var lists = ListNodeIndices(doc);
        var item = doc.Nodes[(int)doc.Nodes[lists[0]].Children[0]];
        Assert.Equal("plain bold", item.Content.Text);
        var annotation = Assert.Single(item.Annotations);
        Assert.Equal("bold", item.Content.Text![(int)annotation.Start..(int)annotation.End]);
    }

    /// <summary>
    /// A hostile <c>colspan</c> must not size the occupancy grid. The HTML Living Standard's own
    /// ceiling is the clamp, so the table still extracts rather than allocating gigabytes.
    /// </summary>
    [Fact]
    public void AnOutOfRangeColspanIsClampedToTheHtmlSpecCeiling()
    {
        var doc = HtmlStructure.Build(
            "<table><tr><td colspan=\"4294967295\">wide</td></tr><tr><td>a</td><td>b</td></tr></table>");

        var grid = Assert.Single(doc.Nodes.Where(n => n.Content.Which == NodeContent.Tag.Table)).Content.Grid!;
        Assert.Equal("wide", grid.Cells.Single(c => c.Row == 0).Content);
        Assert.Equal(1000u, grid.Cols);
    }

    /// <summary>
    /// A table nested inside a cell is flattened into that cell rather than replacing the enclosing
    /// table, so the outer table's own rows survive.
    /// </summary>
    [Fact]
    public void ANestedTableIsFlattenedIntoItsEnclosingCell()
    {
        var doc = HtmlStructure.Build(
            "<table><tr><td>outer<table><tr><td>inner</td></tr></table></td><td>next</td></tr></table>");

        var grid = Assert.Single(doc.Nodes.Where(n => n.Content.Which == NodeContent.Tag.Table)).Content.Grid!;
        var row = grid.Cells.Where(c => c.Row == 0).OrderBy(c => c.Col).ToList();
        Assert.Equal(2, row.Count);
        Assert.Contains("outer", row[0].Content);
        Assert.Contains("inner", row[0].Content);
        Assert.Equal("next", row[1].Content);
    }
}
