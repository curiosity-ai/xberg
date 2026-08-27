using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Renderer fixes brought in with the upstream sync: <c>fix(render): strip duplicated list markers
/// and guard columnless tables</c>, the char-boundary clamp in
/// <c>rendering::common::render_annotated_text_with_plain</c>, and the plain renderer's fallback
/// for an image the extractor could not resolve.
/// </summary>
public sealed class RenderMarkerAndSpanTests
{
    /// <summary>
    /// Structure detection reads a numbered marker as part of the line's own text, so an item's
    /// text still carries "1. " even though the element is correctly flagged as an ordered list
    /// item. Left in place, the renderer emits its own marker followed by the untouched label, and
    /// the CommonMark writer backslash-escapes the second period — "1. 1\. Land Use…".
    /// </summary>
    [Fact]
    public void AnOrderedItemDropsTheNumeralItsOwnTextAlreadyCarries()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(true);
        b.PushListItem("1. Land Use: Live/Work Townhomes", true, new(), null, null);
        b.EndList();

        string markdown = MarkdownRenderer.Render(b.Build());

        Assert.Contains("1. Land Use: Live/Work Townhomes", markdown);
        Assert.DoesNotContain("1\\.", markdown);
    }

    [Theory]
    [InlineData("- Alpha", "Alpha")]
    [InlineData("• Alpha", "Alpha")]
    [InlineData("2) Alpha", "Alpha")]
    public void ARedundantBulletOrDelimitedNumeralIsStripped(string itemText, string expected)
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(false);
        b.PushListItem(itemText, false, new(), null, null);
        b.EndList();

        Assert.Contains("- " + expected, MarkdownRenderer.Render(b.Build()));
    }

    /// <summary>A numeral with no delimiter, or a delimiter with no space, is the item's own text.</summary>
    [Theory]
    [InlineData("2024 was a good year")]
    [InlineData("1.5x throughput")]
    public void TextThatOnlyLooksLikeAMarkerIsLeftAlone(string itemText)
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(false);
        b.PushListItem(itemText, false, new(), null, null);
        b.EndList();

        Assert.Contains(itemText, MarkdownRenderer.Render(b.Build()));
    }

    /// <summary>
    /// Stripping the marker shifts every annotation offset with it, so a span still covers the same
    /// words it did before.
    /// </summary>
    [Fact]
    public void StrippingAMarkerShiftsTheAnnotationsWithIt()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(true);
        // "1. Land Use" — bold covers "Land" at bytes 3..7 of the unstripped text.
        b.PushListItem("1. Land Use", true, new()
        {
            new TextAnnotation { Start = 3, End = 7, Kind = AnnotationKind.Bold },
        }, null, null);
        b.EndList();

        Assert.Contains("**Land** Use", MarkdownRenderer.Render(b.Build()));
    }

    /// <summary>
    /// A grid whose every row is empty has no columns to align; a table node with zero columns
    /// renders as a bare row of pipes. The renderer falls back to the extractor's pre-rendered
    /// markdown instead.
    /// </summary>
    [Fact]
    public void AColumnlessGridFallsBackToThePreRenderedMarkdown()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushTable(new Table { Cells = new() { new List<string>() }, Markdown = "| fallback |" }, null, null);

        string markdown = MarkdownRenderer.Render(b.Build());

        Assert.Contains("fallback", markdown);
        Assert.DoesNotContain("| --- |", markdown);
    }

    /// <summary>
    /// Annotation offsets can come from any extractor and are not guaranteed to land on a codepoint
    /// boundary. "é" occupies bytes 0..2, so start=1 cuts inside it; rounding up to 2 bolds only
    /// the complete characters instead of slicing a codepoint in half.
    /// </summary>
    [Fact]
    public void AMidCodepointAnnotationOffsetIsClampedToTheCharBoundary()
    {
        string result = RenderCommon.RenderAnnotatedText("éab",
            new[] { new TextAnnotation { Start = 1, End = 3, Kind = AnnotationKind.Bold } },
            (span, _) => $"[B:{span}]");

        Assert.Equal("é[B:a]b", result);
    }

    /// <summary>An annotation that collapses to nothing after clamping is dropped, not emitted.</summary>
    [Fact]
    public void AnAnnotationThatClampsToEmptyIsDropped()
    {
        const string text = "é world";
        string result = RenderCommon.RenderAnnotatedText(text,
            new[] { new TextAnnotation { Start = 1, End = 1, Kind = AnnotationKind.Bold } },
            (span, _) => $"[B:{span}]");

        Assert.Equal(text, result);
    }

    /// <summary>
    /// An image the extractor could not resolve — a missing archive member — still carries its alt
    /// text or caption, and the plain renderer keeps it rather than emitting nothing.
    /// </summary>
    [Fact]
    public void AnUnresolvedImageStillRendersItsAltText()
    {
        var doc = new InternalDocument { SourceFormat = "test" };
        doc.Elements.Add(new InternalElement
        {
            Kind = ElementKind.Image(0),
            Text = "A missing diagram",
            Annotations = new(),
        });

        Assert.Contains("[Image: A missing diagram]", PlainRenderer.Render(doc));
    }
}
