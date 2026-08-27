using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// FictionBook structure that the port previously dropped or mislabelled.
/// </summary>
public class FictionBookExtractorTests
{
    private static InternalDocument Parse(string body) =>
        new FictionBookExtractor().Extract(
            Encoding.UTF8.GetBytes($"""
                <?xml version="1.0" encoding="utf-8"?>
                <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"
                             xmlns:l="http://www.w3.org/1999/xlink">
                {body}
                </FictionBook>
                """),
            "application/x-fictionbook+xml", new ExtractionConfig());

    [Fact]
    public void ATableIsEmittedWhereTheDocumentPutIt()
    {
        // A separate pass raw-pushed tables, recording the data without a matching element, so no
        // renderer emitted them and every table's text was missing from the document.
        var doc = Parse("""
            <body><section>
              <table>
                <tr><th>Name</th><th>Age</th></tr>
                <tr><td>Ada</td><td>36</td></tr>
              </table>
            </section></body>
            """);
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "Name", "Age" }, table.Cells[0]);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Table);
    }

    [Fact]
    public void AnEntityReferenceStaysInsideTheTextAroundIt()
    {
        // Splitting the run at the reference showed the whitespace rules edges that were never
        // in the source, so "2 > 1" came out as "2>  1".
        var doc = Parse("<body><section><p>This is not a quote: 2\n&gt; 1.</p></section></body>");
        Assert.Contains("2 > 1.", string.Join("\n", doc.Elements.Select(e => e.Text)));
    }

    [Fact]
    public void AFootnoteReferenceIsItsOwnElement()
    {
        // A reference is an element, not a marker in the prose: what precedes it is a paragraph,
        // the reference is a reference, and what follows is another paragraph.
        var doc = Parse("""
            <body><section><p>A claim<a l:href="#n1" type="note">[1]</a> and more text.</p></section></body>
            """);

        var refs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.FootnoteRef).ToList();
        Assert.Single(refs);
        // The paragraph's own end trims; only a split mid-paragraph keeps the space that
        // separated the text from the marker.
        var paragraphs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Equal(new[] { "A claim", "and more text." }, paragraphs);
    }

    [Fact]
    public void ANotesSectionIsKeyedByItsOwnId()
    {
        // The id is what the body's reference names, so it is the key that pairs the two.
        // Numbering definitions independently made every reference point at the wrong note.
        var doc = Parse("""
            <body><section><p>A claim<a l:href="#n2" type="note">[1]</a>.</p></section></body>
            <body name="notes">
              <section id="n2"><p>The note text.</p></section>
            </body>
            """);

        var definition = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.FootnoteDefinition));
        var reference = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.FootnoteRef));
        Assert.Equal("n2", reference.Anchor);
        Assert.Equal("n2", definition.Anchor);
    }

    /// <summary>
    /// Upstream #859: whitespace normalization trims the trailing space off a text run, so the
    /// joining space re-inserted between two runs lands exactly where a still-open inline element
    /// recorded its annotation start. Left unbumped, the span swallows that space.
    /// </summary>
    [Fact]
    public void AJoiningSpaceStaysOutsideAnInlineAnnotationSpan()
    {
        var doc = Parse("""
            <body><section><p>Some <code>code</code></p></section></body>
            """);

        var paragraph = Assert.Single(doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph));
        Assert.Equal("Some code", paragraph.Text);

        // The <code> element opened at offset 4, where "Some" ends; the joining space then took
        // that offset, so the annotation has to start one byte later or it covers " code".
        var annotation = Assert.Single(paragraph.Annotations);
        Assert.Equal("code", paragraph.Text[(int)annotation.Start..(int)annotation.End]);
    }
}
