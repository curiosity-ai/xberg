using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Internal.DocTags;
using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The Docling DocTags parser and renderer, ported from Rust <c>extraction/doctags.rs</c> and
/// <c>rendering/doctags.rs</c>.
/// </summary>
public class DocTagsTests
{
    private static InternalDocument Parse(string stream) =>
        new DocTagsExtractor().Extract(
            Encoding.UTF8.GetBytes(stream), DocTagsMime.MimeType, new ExtractionConfig());

    private static string Render(InternalDocument doc) => DocTagsRenderer.Render(doc);

    private static string RoundTrip(string stream) => Render(Parse(stream));

    /// <summary>
    /// The format has no escaping, and real Docling output carries a literal <c>&lt;</c> inside
    /// prose — a caption in the vendored corpus discusses <c>' &lt; td &gt; '</c>. Scanning to
    /// the next <c>&gt;</c> would swallow the <c>&lt;/caption&gt;</c> that follows.
    /// </summary>
    [Fact]
    public void AnUnknownAngleRunStaysContent()
    {
        var tokens = DocTagsVocabulary.Tokenize(
            "<doctag><caption>cells (' < td > ', ' < ')</caption></doctag>");
        Assert.Contains(tokens, t => t.Kind == DocTagKind.Close && t.Value == "caption");
        Assert.Contains(tokens, t => t.Kind == DocTagKind.Close && t.Value == "doctag");
    }

    [Fact]
    public void TextAndHeadingsSurviveARoundTrip()
    {
        const string stream =
            "<doctag><title>Report</title>\n<section_header_level_1>Intro</section_header_level_1>\n"
            + "<text>Body prose.</text>\n</doctag>";
        Assert.Equal(stream, RoundTrip(stream));
    }

    /// <summary>
    /// A stray close that does not name the innermost open tag belongs to some other element. A
    /// name-blind depth counter would let it close the wrong element early, truncating content.
    /// </summary>
    [Fact]
    public void AStrayCloseDoesNotEndTheWrongElement()
    {
        var doc = Parse("<doctag><text>before</text><otsl><fcel>a</text><nl></otsl><text>after</text></doctag>");
        var paragraphs = doc.Elements
            .Where(e => e.Kind.Tag == ElementKindTag.Paragraph)
            .Select(e => e.Text).ToList();
        Assert.Contains("before", paragraphs);
        Assert.Contains("after", paragraphs);
    }

    /// <summary>
    /// Location tokens are page-relative on a 0–500 grid and the original page size is not
    /// recoverable, so pages are rebuilt as grid squares — which is exactly what makes a re-emit
    /// reproduce the original tokens.
    /// </summary>
    [Fact]
    public void LocationTokensSurviveARoundTripUnchanged()
    {
        const string stream = "<doctag><text><loc_10><loc_20><loc_490><loc_60>Positioned.</text>\n</doctag>";
        Assert.Equal(stream, RoundTrip(stream));
    }

    [Fact]
    public void ACorruptLocationTokenIsRejectedRatherThanClamped()
    {
        // A `<loc_nan>` or an out-of-range value must not silently become a plausible box.
        var doc = Parse("<doctag><text><loc_10><loc_20><loc_9999><loc_60>Text.</text></doctag>");
        var para = Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph);
        Assert.Null(para.Bbox);
    }

    /// <summary>
    /// OTSL merge tokens repeat the content they continue: <c>lcel</c> the cell to its left,
    /// <c>ucel</c> the one above, <c>xcel</c> either. The flat grid has nowhere to record a span.
    /// </summary>
    [Fact]
    public void OtslMergeTokensRepeatTheCellTheyContinue()
    {
        var doc = Parse("<doctag><otsl><ched>A<ched>B<nl><fcel>1<lcel><nl><ucel><fcel>2<nl></otsl></doctag>");
        var table = Assert.Single(doc.Tables);
        Assert.Equal(new[] { "A", "B" }, table.Cells[0]);
        Assert.Equal(new[] { "1", "1" }, table.Cells[1]);   // lcel repeats its left neighbour
        Assert.Equal(new[] { "1", "2" }, table.Cells[2]);   // ucel repeats the cell above
    }

    /// <summary>
    /// Docling emits table regions it found no cells in. There is no table there, and an
    /// invented empty one would not survive a re-emit, so it is dropped — but its caption still
    /// carries text and stays as an ordinary element.
    /// </summary>
    [Fact]
    public void AnEmptyTableIsDroppedButItsCaptionIsKept()
    {
        var doc = Parse("<doctag><otsl><caption>Table 1: nothing here</caption></otsl></doctag>");
        Assert.Empty(doc.Tables);
        Assert.Contains(doc.Elements, e => e.Text == "Table 1: nothing here");
    }

    [Fact]
    public void ACaptionNestsInsideTheElementItDescribes()
    {
        const string stream = "<doctag><picture><caption>Figure 1</caption></picture>\n</doctag>";
        Assert.Equal(stream, RoundTrip(stream));
    }

    [Fact]
    public void ACodeBlockKeepsItsLanguageTokenAndFallsBackToUnknown()
    {
        Assert.Equal("<doctag><code><_rust_>fn main() {}</code>\n</doctag>",
                     RoundTrip("<doctag><code><_rust_>fn main() {}</code></doctag>"));
        Assert.Equal("<doctag><code><_unknown_>plain</code>\n</doctag>",
                     RoundTrip("<doctag><code><_unknown_>plain</code></doctag>"));
    }

    /// <summary>
    /// A content layer chooses the tag: a footnote round-trips as <c>&lt;footnote&gt;</c>, a
    /// header as <c>&lt;page_header&gt;</c>.
    /// </summary>
    [Fact]
    public void ContentLayersChooseTheirTag()
    {
        const string stream = "<doctag><page_header>Running head</page_header>\n"
                              + "<page_footer>Page 1</page_footer>\n"
                              + "<footnote>A note.</footnote>\n</doctag>";
        Assert.Equal(stream, RoundTrip(stream));
    }

    [Fact]
    public void CheckboxesBecomeMarkedText()
    {
        var doc = Parse("<doctag><checkbox_selected>Done</checkbox_selected>"
                        + "<checkbox_unselected>Todo</checkbox_unselected></doctag>");
        var texts = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph)
                       .Select(e => e.Text).ToList();
        Assert.Equal(new[] { "[x] Done", "[ ] Todo" }, texts);
    }

    /// <summary>
    /// A bare list item opens an implicit wrapper, and one whose ordering differs from the open
    /// wrapper closes it and opens the right kind rather than being absorbed into the wrong one.
    /// </summary>
    [Fact]
    public void ABareListItemOpensAnImplicitWrapper()
    {
        var doc = new InternalDocument("test");
        var builder = new InternalDocumentBuilder("test");
        builder.PushListItem("one", false, new List<TextAnnotation>(), null, null);
        builder.PushListItem("two", false, new List<TextAnnotation>(), null, null);
        builder.PushParagraph("after", new List<TextAnnotation>(), null, null);

        string rendered = Render(builder.Build());
        Assert.Contains("<unordered_list><list_item>one</list_item>\n<list_item>two</list_item>\n"
                        + "</unordered_list>\n<text>after</text>", rendered);
    }

    /// <summary>
    /// A truncated stream — or a plain-text file misrouted here — may carry no recognised tags at
    /// all. Dropping that text would turn a malformed input into an empty extraction that claims
    /// to have succeeded.
    /// </summary>
    [Fact]
    public void TextOutsideAnyTagIsKeptAndWarnedAbout()
    {
        var doc = Parse("just some prose with no tags");
        Assert.Contains(doc.Elements, e => e.Text == "just some prose with no tags");
        Assert.Contains(doc.ProcessingWarnings, w => w.Source == "doctags");
    }

    [Fact]
    public void WhitespaceBetweenTagsIsNotContent()
    {
        var doc = Parse("<doctag>\n<text>a</text>\n<text>b</text>\n</doctag>");
        Assert.Equal(2, doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.Paragraph));
        Assert.Empty(doc.ProcessingWarnings);
    }

    [Fact]
    public void PageBreaksAdvanceThePageAndAreCounted()
    {
        var doc = Parse("<doctag><text>one</text><page_break><text>two</text></doctag>");
        Assert.Equal(2u, doc.Metadata.Pages!.TotalCount);
        var paragraphs = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).ToList();
        Assert.Equal(1u, paragraphs[0].Page);
        Assert.Equal(2u, paragraphs[1].Page);
    }

    [Fact]
    public void TheOutputFormatResolvesToTheFirstClassVariant()
    {
        Assert.Equal(OutputFormat.DocTags, OutputFormat.FromString("doctags"));
        Assert.Equal("doctags", OutputFormat.DocTags.ToString());
    }

    /// <summary>
    /// Routing is by extension, as upstream has it. The corpus names its streams
    /// <c>*.doctags.txt</c>, which resolves as plain text — which is why none of them reaches
    /// this extractor.
    /// </summary>
    [Fact]
    public void RoutingIsByExtensionOnly()
    {
        Assert.Equal(DocTagsMime.MimeType, Mime.DetectMimeType("out.doctags", checkExists: false));
        Assert.Equal("text/plain", Mime.DetectMimeType("out.doctags.txt", checkExists: false));
    }
}
