```csharp title="C#"
using Xberg;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

public class MyExtractorPlugin : IDocumentExtractor
{
    private readonly ILogger _logger;

    public MyExtractorPlugin(ILogger logger)
    {
        _logger = logger;
    }

    public string Name => "my-plugin";
    public string Version => "1.0.0";
    public int Priority => 50;
    public List<string> SupportedMimeTypes => new() { "text/plain" };

    public void Initialize()
    {
        _logger.LogInformation($"Initializing plugin: {Name}");
    }

    public void Shutdown()
    {
        _logger.LogInformation($"Shutting down plugin: {Name}");
    }

    public bool CanHandle(string path, string mimeType) => mimeType == "text/plain";

    public ExtractedDocument Extract(ExtractInput input, ExtractionConfig config)
    {
        _logger.LogInformation($"Extracting {input.MimeType} ({input.Bytes?.Length ?? 0} bytes)");
        var content = input.Bytes is null ? "" : System.Text.Encoding.UTF8.GetString(input.Bytes);
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning("Extraction resulted in empty content");
        }
        return new ExtractedDocument
        {
            Content = content,
            MimeType = input.MimeType ?? "text/plain",
            Metadata = new Metadata(),
        };
    }
}
```
