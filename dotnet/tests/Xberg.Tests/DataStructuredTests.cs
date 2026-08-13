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
    public void JsonArray_ScalarsBecomeListItems()
    {
        // A top-level array gets per-item structure rather than an opaque code block.
        string plain = Plain("[1, 2, 3]", "application/json");
        Assert.Equal("1\n2\n3", plain);
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
        Assert.Equal("2.0", Plain("[2.0]", "application/json"));
        Assert.Equal("1000.0", Plain("[1.0e3]", "application/json"));
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
    public void Jsonl_ProducesPerRecordStructureAndSkipsBlankLines()
    {
        // Each record becomes its own "Item N" section; blank lines contribute nothing.
        string plain = Plain("{\"a\": 1}\n\n\n{\"b\": 2}\n", "application/x-ndjson");
        Assert.Equal("Item 1\na: 1\n\nItem 2\nb: 2", plain);
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
        // YAML renders structurally now: a nested mapping becomes a heading, scalars become
        // "key: value" paragraphs. No code-block fallback.
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Code);
        Assert.Equal("title: Sample\n\ndatabase\nname: testdb\n\nport: 8080", PlainRender(doc));
        Assert.Equal("yaml", doc.Metadata.Additional["data_format"].GetString());
        Assert.True(doc.Metadata.Additional.ContainsKey("title"));
        Assert.True(doc.Metadata.Additional.ContainsKey("database.name"));
    }

    [Fact]
    public void Toml_TablesAndDottedKeys()
    {
        string src = "[package]\nname = \"xberg\"\nversion = \"1.0\"\ncount = 3\n";
        var doc = Extract(src, "application/toml");
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.Code);
        Assert.Equal("package\nname: xberg\n\nversion: 1.0\n\ncount: 3", PlainRender(doc));
        Assert.Equal("toml", doc.Metadata.Additional["data_format"].GetString());
        Assert.True(doc.Metadata.Additional.ContainsKey("package.name"));
        Assert.True(doc.Metadata.Additional.ContainsKey("package.version"));
        Assert.False(doc.Metadata.Additional.ContainsKey("package.count"));
    }

    [Theory]
    // serde_json's default (non-float_roundtrip) fast-path parser double-rounds: it scales a
    // u64 significand by an exact power of ten, so many-digit inputs land 1 ULP off the
    // correctly-rounded double and then print shorter. These pairs come from docling goldens.
    [InlineData("498.92708999999996", "498.92709")]
    [InlineData("252.05723999999998", "252.05724")]
    [InlineData("478.30521000000005", "478.3052100000001")]
    [InlineData("126.95307000000003", "126.95307000000004")]
    [InlineData("61.569008000000004", "61.569008")]
    [InlineData("159.48581000000001", "159.48581")]
    // Plain / already-shortest values must round-trip unchanged.
    [InlineData("96.301003", "96.301003")]
    [InlineData("1.5", "1.5")]
    [InlineData("0.0", "0.0")]
    [InlineData("-2.25", "-2.25")]
    public void SerdeFloatFormatting_MatchesSerdeJson(string input, string expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(input);
        Assert.Equal(expected, SerdeJson.Compact(doc.RootElement));
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
