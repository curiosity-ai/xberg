```csharp title="C#"
using Xberg;
using Xunit;

public class CustomExtractorTests
{
    [Fact]
    public void TestCustomExtractor()
    {
        var extractor = new CustomJsonExtractor();
        var jsonData = System.Text.Encoding.UTF8.GetBytes(@"{""message"": ""Hello, world!""}");
        var input = ExtractInput.FromBytes(jsonData, "application/json", null);
        var config = ExtractionConfig.Default();

        var result = extractor.Extract(input, config);

        Assert.Contains("Hello, world!", result.Content);
        Assert.Equal("application/json", result.MimeType);
    }
}
```
