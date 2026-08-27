using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The reStructuredText extractor, ported from Rust <c>extractors/rst.rs</c>.
/// </summary>
public class RstExtractorTests
{
    private static InternalDocument Parse(string source) =>
        new RstExtractor().Extract(Encoding.UTF8.GetBytes(source), "text/x-rst", new ExtractionConfig());

    private static List<string> Paragraphs(InternalDocument doc) =>
        doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();

    [Fact]
    public void AnUnhandledDirectivesBodyIsStillDocumentText()
    {
        // The parser has no handler for `automodule`, but its option lines are content and
        // dropping them silently loses part of the document.
        var doc = Parse("""
            .. automodule:: sympy.integrals.meijerint
               :members:
               :private-members:
            """);
        Assert.Contains(":members: :private-members:", Paragraphs(doc));
    }

    [Fact]
    public void ACommentsBodyIsNotDocumentText()
    {
        // A comment and an unhandled directive look alike; they are told apart by shape, because
        // a directive's name is a single word immediately followed by "::".
        var doc = Parse("""
            .. this is just a comment
               with a continuation line

            Real text.
            """);
        Assert.Equal(new[] { "Real text." }, Paragraphs(doc));
    }

    [Fact]
    public void ADirectiveBodyEndsAtTheFirstBlankLineAfterItStarts()
    {
        var doc = Parse("""
            .. note:: heading

               Body of the note.

            A separate paragraph.
            """);
        Assert.Contains("A separate paragraph.", Paragraphs(doc));
    }

    [Fact]
    public void EachTableIsEmittedOnce()
    {
        // Tables are parsed in place while the document is built. A second pass raw-pushing them
        // again produced an unreferenced duplicate of every table in the document.
        var doc = Parse("""
            ======  ======
            Name    Value
            ======  ======
            alpha   1
            beta    2
            ======  ======
            """);
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Name", "Value" }, table.Cells[0]);
    }

    [Fact]
    public void AGridTableIsEmittedOnce()
    {
        var doc = Parse("""
            +--------+-------+
            | Name   | Value |
            +========+=======+
            | alpha  | 1     |
            +--------+-------+
            """);
        Assert.Single(doc.Tables);
    }

    [Fact]
    public void SectionUnderlinesBecomeHeadings()
    {
        var doc = Parse("""
            Title
            =====

            Section
            -------

            Text.
            """);
        var headings = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading).ToList();
        Assert.Equal(2, headings.Count);
        Assert.Equal("Title", headings[0].Text);
        Assert.Equal("Section", headings[1].Text);
        Assert.True(headings[0].Kind.Level < headings[1].Kind.Level);
    }

    /// <summary>
    /// A `.. figure::` is an image plus a caption. Its option block (`:target:`, `:align:`,
    /// `:scale:`) is directive syntax, not text — without a handler it fell through to the
    /// generic directive path and every option line was emitted as a paragraph.
    /// </summary>
    [Fact]
    public void AFigureEmitsItsImageAndCaptionAndNotItsOptions()
    {
        var doc = Parse("Intro\n\n.. figure:: images/plot.png\n   :target: plot.html\n   :align: center\n"
                        + "   :scale: 50%\n\n   The caption.\n\nAfter\n");
        var paras = Paragraphs(doc);
        Assert.Contains("[image: images/plot.png]", paras);
        Assert.Contains("The caption.", paras);
        Assert.DoesNotContain(paras, p => p.Contains(":target:") || p.Contains(":scale:"));
        Assert.Contains("images/plot.png", doc.Uris.Select(u => u.Url));
    }

    /// <summary>
    /// A `.. list-table::` is a table: each `* -` opens a row and each nested `-` adds a cell.
    /// With no handler its rows read as a bullet list carrying the option block with them.
    /// </summary>
    [Fact]
    public void AListTableBecomesATable()
    {
        var doc = Parse("Intro\n\n.. list-table::\n   :class: borderless\n   :width: 100%\n\n"
                        + "   * - Name\n     - Age\n   * - Alice\n     - 30\n\nAfter\n");
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Name", "Age" }, table.Cells[0]);
        Assert.Equal(new[] { "Alice", "30" }, table.Cells[1]);
        Assert.DoesNotContain(Paragraphs(doc), p => p.Contains(":class:"));
    }

    /// <summary>
    /// A `.. csv-table::` reads its `:header:` option as the first row, and quotes protect a
    /// comma inside a field.
    /// </summary>
    [Fact]
    public void ACsvTableReadsItsHeaderOptionAndQuotedFields()
    {
        var doc = Parse("Intro\n\n.. csv-table::\n   :header: \"Name\", \"Note\"\n\n"
                        + "   \"Alice\", \"one, two\"\n\nAfter\n");
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Name", "Note" }, table.Cells[0]);
        Assert.Equal(new[] { "Alice", "one, two" }, table.Cells[1]);
    }

    /// <summary>
    /// Upstream <c>fix(pptx,rst): … guard a cross-buffer slice</c>. A simple table's column
    /// boundaries are byte offsets counted against the ASCII (<c>=</c>/space only) separator line,
    /// then applied verbatim to each data row. A row with a multi-byte character straddling a
    /// boundary puts that boundary mid-codepoint — a panic in Rust, mojibake here — so the
    /// boundary is snapped inward instead.
    /// </summary>
    [Fact]
    public void ASimpleTableColumnBoundaryInsideAMultibyteCharacterIsSnappedInward()
    {
        // "éé" is four bytes, so the column boundary at byte 3 lands inside the second one.
        var doc = Parse("Intro.\n\n===  ===\néé  yyy\n===  ===\n\nOutro.\n");

        // The separator's byte offsets do not line up with a row that is wider in bytes than it is
        // in characters, so each column is the snapped slice: column one ends at byte 2, inside
        // "éé", and column two starts at byte 5. What matters is that neither cell carries a
        // replacement character from a codepoint cut in half.
        var table = Assert.Single(doc.Tables);
        var row = table.Cells[0];
        Assert.Equal(2, row.Count);
        Assert.All(row, cell => Assert.DoesNotContain('\uFFFD', cell));
        Assert.Equal("é", row[0]);
        Assert.Equal("yy", row[1]);
    }
}
