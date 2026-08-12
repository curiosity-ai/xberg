```csharp title="C#"
using Xberg;
using System;
using System.Collections.Generic;

var extractor = new CustomExtractor();
DocumentExtractorRegistry.RegisterDocumentExtractor(extractor);
Console.WriteLine("Extractor registered");

public class CustomExtractor : IDocumentExtractor
{
    public string Name => "custom";
    public string Version => "1.0.0";
    public int Priority => 50;
    public List<string> SupportedMimeTypes => new() { "application/x-custom" };

    public void Initialize() { }
    public void Shutdown() { }

    public bool CanHandle(string path, string mimeType) => mimeType == "application/x-custom";

    public ExtractedDocument Extract(ExtractInput input, ExtractionConfig config)
    {
        return new ExtractedDocument
        {
            Content = "Extracted content",
            MimeType = "application/x-custom",
            Metadata = new Metadata(),
        };
    }
}
```
