using Xberg.Core;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

public class InternalDocumentTests
{
    [Fact]
    public void ElementIdDeterministicAndFormatted()
    {
        var id1 = InternalElementId.Generate("heading", "Introduction", 1, 0);
        var id2 = InternalElementId.Generate("heading", "Introduction", 1, 0);
        Assert.Equal(id1, id2);
        Assert.StartsWith("ie-", id1.AsString());
        Assert.Equal(3 + 12, id1.AsString().Length);
    }

    [Fact]
    public void ElementIdDiffersByIndex()
    {
        var id1 = InternalElementId.Generate("paragraph", "Same text", 1, 0);
        var id2 = InternalElementId.Generate("paragraph", "Same text", 1, 1);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DiscriminantMatchesRust()
    {
        Assert.Equal("title", ElementKind.Title.Discriminant());
        Assert.Equal("heading", ElementKind.Heading(2).Discriminant());
        Assert.Equal("list_start", ElementKind.ListStart(true).Discriminant());
    }

    [Fact]
    public void BuilderHeadingDepthFromLevel()
    {
        var b = new InternalDocumentBuilder("markdown");
        b.PushHeading(1, "H1", null, null);
        b.PushHeading(2, "H2", null, null);
        b.PushHeading(3, "H3", null, null);
        var doc = b.Build();
        Assert.Equal(0, doc.Elements[0].Depth);
        Assert.Equal(1, doc.Elements[1].Depth);
        Assert.Equal(2, doc.Elements[2].Depth);
        Assert.Equal("introduction", InternalDocumentBuilder.Slugify("Introduction"));
    }

    [Fact]
    public void SlugifyMatchesRust()
    {
        Assert.Equal("hello-world", InternalDocumentBuilder.Slugify("Hello World"));
        Assert.Equal("what-s-new", InternalDocumentBuilder.Slugify("What's New?"));
        Assert.Equal("section-3-1", InternalDocumentBuilder.Slugify("Section 3.1"));
        Assert.Equal("", InternalDocumentBuilder.Slugify(""));
    }

    [Fact]
    public void InternalDocumentRoundTripsThroughJson()
    {
        var doc = new InternalDocument("pdf") { MimeType = "application/pdf" };
        doc.PushElement(InternalElement.TextElement(ElementKind.Title, "Test Document", 0));
        doc.PushElement(InternalElement.TextElement(ElementKind.Heading(2), "Introduction", 1));
        doc.PushElement(InternalElement.TextElement(ElementKind.ListItem(true), "First item", 2));
        doc.PushElement(InternalElement.TextElement(ElementKind.Image(0), "", 0));
        doc.PushRelationship(new Relationship
        {
            Source = 0,
            Target = RelationshipTarget.FromKey("introduction"),
            Kind = RelationshipKind.CrossReference,
        });

        string json = Json.Serialize(doc);
        var restored = Json.Deserialize<InternalDocument>(json)!;

        Assert.Equal(doc.Elements.Count, restored.Elements.Count);
        Assert.Equal(ElementKind.Title, restored.Elements[0].Kind);
        Assert.Equal(ElementKind.Heading(2), restored.Elements[1].Kind);
        Assert.Equal(ElementKind.ListItem(true), restored.Elements[2].Kind);
        Assert.Equal(ElementKind.Image(0), restored.Elements[3].Kind);
        Assert.Equal(doc.Elements[0].Id, restored.Elements[0].Id);
        Assert.Equal("introduction", restored.Relationships[0].Target.Key);
    }

    [Fact]
    public void EndToEndPlainTextExtraction()
    {
        var extractor = new Extractor();
        var input = ExtractInput.FromBytes(
            System.Text.Encoding.UTF8.GetBytes("Hello, World!\n\nThis is a test."),
            "text/plain");
        var result = extractor.Extract(input, new ExtractionConfig());
        Assert.Single(result.Results);
        var extracted = result.Results[0];
        Assert.Equal("text/plain", extracted.MimeType);
        Assert.Contains("Hello, World!", extracted.Content);
        Assert.Contains("This is a test.", extracted.Content);
    }

    [Fact]
    public void EndToEndJsonOutputFormat()
    {
        var extractor = new Extractor();
        var input = ExtractInput.FromBytes(
            System.Text.Encoding.UTF8.GetBytes("A paragraph."),
            "text/plain");
        var result = extractor.Extract(input, new ExtractionConfig { OutputFormat = OutputFormat.Json });
        var content = result.Results[0].Content;
        using var parsed = System.Text.Json.JsonDocument.Parse(content);
        Assert.Equal("paragraph", parsed.RootElement.GetProperty("body")[0].GetProperty("type").GetString());
    }
}
