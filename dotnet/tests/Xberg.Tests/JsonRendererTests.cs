using System.Text.Json;
using Xberg.Rendering;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>Ported from the `#[cfg(test)]` tests in Rust `rendering/json.rs`.</summary>
public class JsonRendererTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EmptyDocument()
    {
        var b = new InternalDocumentBuilder("test");
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        Assert.False(parsed.TryGetProperty("title", out var t) && t.ValueKind != JsonValueKind.Null);
        Assert.Equal(0, parsed.GetProperty("body").GetArrayLength());
    }

    [Fact]
    public void SingleParagraph()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushParagraph("Hello world", new(), null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        Assert.Equal(1, parsed.GetProperty("body").GetArrayLength());
        Assert.Equal("paragraph", parsed.GetProperty("body")[0].GetProperty("type").GetString());
        Assert.Equal("Hello world", parsed.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void HeadingCreatesSection()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushHeading(1, "Chapter 1", null, null);
        b.PushParagraph("Chapter content", new(), null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        Assert.Equal(1, parsed.GetProperty("body").GetArrayLength());
        var section = parsed.GetProperty("body")[0];
        Assert.Equal("section", section.GetProperty("type").GetString());
        Assert.Equal("Chapter 1", section.GetProperty("heading").GetString());
        Assert.Equal(1, section.GetProperty("level").GetInt32());
        Assert.Equal("paragraph", section.GetProperty("body")[0].GetProperty("type").GetString());
        Assert.Equal("Chapter content", section.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void NestedSections()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushHeading(1, "Chapter 1", null, null);
        b.PushParagraph("Intro", new(), null, null);
        b.PushHeading(2, "Section 1.1", null, null);
        b.PushParagraph("Sub content", new(), null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var section = parsed.GetProperty("body")[0];
        Assert.Equal("section", section.GetProperty("type").GetString());
        Assert.Equal("Chapter 1", section.GetProperty("heading").GetString());
        Assert.Equal(1, section.GetProperty("level").GetInt32());
        Assert.Equal(2, section.GetProperty("body").GetArrayLength());
        Assert.Equal("paragraph", section.GetProperty("body")[0].GetProperty("type").GetString());
        var sub = section.GetProperty("body")[1];
        Assert.Equal("section", sub.GetProperty("type").GetString());
        Assert.Equal("Section 1.1", sub.GetProperty("heading").GetString());
        Assert.Equal(2, sub.GetProperty("level").GetInt32());
        Assert.Equal("Sub content", sub.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TableInJson()
    {
        var b = new InternalDocumentBuilder("test");
        var cells = new List<List<string>>
        {
            new() { "A", "B" },
            new() { "1", "2" },
            new() { "3", "4" },
        };
        b.PushTableFromCells(cells, null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var table = parsed.GetProperty("body")[0];
        Assert.Equal("table", table.GetProperty("type").GetString());
        Assert.Equal("[\"A\",\"B\"]", table.GetProperty("headers").GetRawText());
        Assert.Equal("[[\"1\",\"2\"],[\"3\",\"4\"]]", table.GetProperty("rows").GetRawText());
    }

    [Fact]
    public void CodeBlock()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushCode("print('hello')", "python", null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var code = parsed.GetProperty("body")[0];
        Assert.Equal("code", code.GetProperty("type").GetString());
        Assert.Equal("print('hello')", code.GetProperty("text").GetString());
        Assert.Equal("python", code.GetProperty("language").GetString());
    }

    [Fact]
    public void CodeBlockNoLanguage()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushCode("some code", null, null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var code = parsed.GetProperty("body")[0];
        Assert.Equal("code", code.GetProperty("type").GetString());
        Assert.Equal("some code", code.GetProperty("text").GetString());
        Assert.False(code.TryGetProperty("language", out _));
    }

    [Fact]
    public void UnorderedList()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(false);
        b.PushListItem("Item 1", false, new(), null, null);
        b.PushListItem("Item 2", false, new(), null, null);
        b.EndList();
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var list = parsed.GetProperty("body")[0];
        Assert.Equal("list", list.GetProperty("type").GetString());
        Assert.False(list.GetProperty("ordered").GetBoolean());
        Assert.Equal("[\"Item 1\",\"Item 2\"]", list.GetProperty("items").GetRawText());
    }

    [Fact]
    public void OrderedList()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushList(true);
        b.PushListItem("First", true, new(), null, null);
        b.PushListItem("Second", true, new(), null, null);
        b.EndList();
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var list = parsed.GetProperty("body")[0];
        Assert.Equal("list", list.GetProperty("type").GetString());
        Assert.True(list.GetProperty("ordered").GetBoolean());
        Assert.Equal("[\"First\",\"Second\"]", list.GetProperty("items").GetRawText());
    }

    [Fact]
    public void Formula()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushFormula("E = mc^2", null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var formula = parsed.GetProperty("body")[0];
        Assert.Equal("formula", formula.GetProperty("type").GetString());
        Assert.Equal("E = mc^2", formula.GetProperty("text").GetString());
    }

    [Fact]
    public void TitleFromTitleElement()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushTitle("My Document", null, null);
        b.PushParagraph("Content", new(), null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        Assert.Equal("My Document", parsed.GetProperty("title").GetString());
        Assert.Equal(1, parsed.GetProperty("body").GetArrayLength());
        Assert.Equal("paragraph", parsed.GetProperty("body")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Blockquote()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushQuoteStart();
        b.PushParagraph("Quoted text", new(), null, null);
        b.PushQuoteEnd();
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        var bq = parsed.GetProperty("body")[0];
        Assert.Equal("blockquote", bq.GetProperty("type").GetString());
        Assert.Equal("paragraph", bq.GetProperty("body")[0].GetProperty("type").GetString());
        Assert.Equal("Quoted text", bq.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void SiblingSections()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushHeading(1, "Chapter 1", null, null);
        b.PushParagraph("Content 1", new(), null, null);
        b.PushHeading(1, "Chapter 2", null, null);
        b.PushParagraph("Content 2", new(), null, null);
        var parsed = Parse(JsonRenderer.Render(b.Build()));
        Assert.Equal(2, parsed.GetProperty("body").GetArrayLength());
        Assert.Equal("Chapter 1", parsed.GetProperty("body")[0].GetProperty("heading").GetString());
        Assert.Equal("Chapter 2", parsed.GetProperty("body")[1].GetProperty("heading").GetString());
    }

    [Fact]
    public void ValidJsonOutput()
    {
        var b = new InternalDocumentBuilder("test");
        b.PushTitle("Test", null, null);
        b.PushHeading(1, "H1", null, null);
        b.PushParagraph("Para", new(), null, null);
        b.PushHeading(2, "H2", null, null);
        b.PushCode("code", "rust", null, null);
        b.PushFormula("x^2", null, null);
        var json = JsonRenderer.Render(b.Build());
        var ex = Record.Exception(() => JsonDocument.Parse(json));
        Assert.Null(ex);
    }
}
