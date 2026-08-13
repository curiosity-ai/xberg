```csharp title="C#"
using Xberg;
using System;

var processor = new PdfMetadataExtractor();
PostProcessorRegistry.RegisterPostProcessor(processor);

public class PdfMetadataExtractor : IPostProcessor
{
    private int _processedCount = 0;

    public string Name => "pdf_metadata_extractor";
    public string Version => "1.0.0";
    public int Priority => 50;
    public ProcessingStage ProcessingStage => ProcessingStage.Early;

    public bool ShouldProcess(ExtractedDocument result, ExtractionConfig config)
        => result.MimeType == "application/pdf";

    public ulong EstimatedDurationMs(ExtractedDocument result) => 1;

    public void Process(ExtractedDocument result, ExtractionConfig config)
    {
        _processedCount++;
    }

    public void Initialize()
    {
        Console.WriteLine("PDF metadata extractor initialized");
    }

    public void Shutdown()
    {
        Console.WriteLine($"Processed {_processedCount} PDFs");
    }
}
```
