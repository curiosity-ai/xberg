using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Tests for <see cref="StructuredExtractor"/> (JSON / JSONL / YAML / TOML).
/// Ports the Rust `extraction/structured.rs` unit tests plus C#-specific serialization checks.
/// </summary>
public class DataStructuredTests
{
    private static InternalDocument Extract(string text, string mime) =>
        new StructuredExtractor().Extract(Encoding.UTF8.GetBytes(text), mime, new ExtractionConfig());

    private static string Plain(string text, string mime) => PlainRender(Extract(text, mime));
    private static string PlainRender(InternalDocument doc) => Xberg.Rendering.PlainRenderer.Render(doc);

    [Fact]
    public void JsonArray_ProducesPrettyCodeBlock()
    {
        // Top-level array -> code-block fallback with serde-pretty content.
        string plain = Plain("[1, 2, 3]", "application/json");
        Assert.Equal("[\n  1,\n  2,\n  3\n]", plain);
    }

    [Fact]
    public void JsonObject_TopLevelKeysBecomeStructure()
    {
        var doc = Extract("{\"title\": \"Hello\", \"count\": 42}", "application/json");
        string plain = PlainRender(doc);
        Assert.Contains("title: Hello", plain);
        Assert.Contains("count: 42", plain);
    }

    [Fact]
    public void JsonFloat_FormattedLikeSerde()
    {
        // serde/ryu keeps a mandatory decimal point and normalizes exponents.
        Assert.Equal("[\n  2.0\n]", Plain("[2.0]", "application/json"));
        Assert.Equal("[\n  1000.0\n]", Plain("[1.0e3]", "application/json"));
    }

    [Fact]
    public void Json_TextFieldsExtractedToMetadata()
    {
        var doc = Extract("{\"name\": \"John\", \"age\": 30, \"offset\": 5}", "application/json");
        Assert.True(doc.Metadata.Additional.ContainsKey("name"));
        Assert.False(doc.Metadata.Additional.ContainsKey("age"));   // "age" not a keyword
        Assert.Equal("json", doc.Metadata.Additional["data_format"].GetString());
        Assert.Equal(1, doc.Metadata.Additional["field_count"].GetInt32());
    }

    [Fact]
    public void Jsonl_ProducesArrayAndSkipsBlankLines()
    {
        string plain = Plain("{\"a\": 1}\n\n\n{\"b\": 2}\n", "application/x-ndjson");
        Assert.Contains("\"a\": 1", plain);
        Assert.Contains("\"b\": 2", plain);
    }

    [Fact]
    public void Jsonl_EmptyInputIsEmptyArray()
    {
        Assert.Equal("[]", Plain("", "application/jsonl"));
    }

    [Fact]
    public void Yaml_ContentIsVerbatimAndTextFieldsDetected()
    {
        string src = "title: Sample\ndatabase:\n  name: testdb\n  port: 8080\n";
        var doc = Extract(src, "application/x-yaml");
        // Verbatim content is preserved as a single code element (the plain renderer trims the tail).
        Assert.Equal(src, doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Code).Text);
        Assert.Equal("yaml", doc.Metadata.Additional["data_format"].GetString());
        Assert.True(doc.Metadata.Additional.ContainsKey("title"));
        Assert.True(doc.Metadata.Additional.ContainsKey("database.name"));
    }

    [Fact]
    public void Toml_TablesAndDottedKeys()
    {
        string src = "[package]\nname = \"xberg\"\nversion = \"1.0\"\ncount = 3\n";
        var doc = Extract(src, "application/toml");
        Assert.Equal(src, doc.Elements.Single(e => e.Kind.Tag == ElementKindTag.Code).Text);
        Assert.Equal("toml", doc.Metadata.Additional["data_format"].GetString());
        Assert.True(doc.Metadata.Additional.ContainsKey("package.name"));
        Assert.True(doc.Metadata.Additional.ContainsKey("package.version"));
        Assert.False(doc.Metadata.Additional.ContainsKey("package.count"));
    }

    [Fact]
    public void SupportedMimeTypes_MatchRust()
    {
        var mimes = new StructuredExtractor().SupportedMimeTypes.ToList();
        Assert.Equal(12, mimes.Count);
        Assert.Contains("application/json", mimes);
        Assert.Contains("application/toml", mimes);
        Assert.Contains("application/x-ndjson", mimes);
    }
}
