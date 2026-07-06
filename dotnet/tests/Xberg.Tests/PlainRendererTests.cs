using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>Ported from Rust `rendering/plain.rs` tests.</summary>
public class PlainRendererTests
{
    [Fact]
    public void Title()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushTitle("My Document", null, null);
        Assert.Equal("My Document", PlainRenderer.Render(b.Build()));
    }

    [Fact]
    public void HeadingIndentedByDepth()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushHeading(2, "Section", null, null);
        Assert.Equal("  Section", PlainRenderer.Render(b.Build()));
    }

    [Fact]
    public void Paragraph()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushParagraph("Hello world.", new(), null, null);
        Assert.Equal("Hello world.", PlainRenderer.Render(b.Build()));
    }

    [Fact]
    public void ListItems()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(false);
        b.PushListItem("Alpha", false, new(), null, null);
        b.PushListItem("Beta", false, new(), null, null);
        b.EndList();
        var outStr = PlainRenderer.Render(b.Build());
        Assert.Contains("Alpha\n", outStr);
        Assert.Contains("Beta", outStr);
    }

    [Fact]
    public void Table()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushTableFromCells(new List<List<string>>
        {
            new() { "Name", "Age" },
            new() { "Alice", "30" },
        }, null, null);
        var outStr = PlainRenderer.Render(b.Build());
        Assert.Contains("Name Age", outStr);
        Assert.Contains("Alice 30", outStr);
    }

    [Fact]
    public void StripsAnnotations()
    {
        var b = new InternalDocumentBuilder("test");
        var ann = new List<TextAnnotation> { new() { Start = 0, End = 5, Kind = AnnotationKind.Bold } };
        b.PushParagraph("Hello world", ann, null, null);
        Assert.Equal("Hello world", PlainRenderer.Render(b.Build()));
    }

    [Fact]
    public void EmptyDocument()
    {
        var b = new InternalDocumentBuilder("test");
        Assert.Equal("", PlainRenderer.Render(b.Build()));
    }

    [Fact]
    public void FootnoteDefinitionsAtEnd()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushParagraph("Main text", new(), null, null);
        b.PushFootnoteRef("1", "fn1", null);
        var def = b.PushFootnoteDefinition("A note.", "fn1", null);
        b.SetLayer(def, ContentLayer.Footnote);
        var outStr = PlainRenderer.Render(b.Build());
        Assert.Contains("Main text", outStr);
        Assert.Contains("A note.", outStr);
    }
}
