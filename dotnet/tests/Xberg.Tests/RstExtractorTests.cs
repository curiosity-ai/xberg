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
}
