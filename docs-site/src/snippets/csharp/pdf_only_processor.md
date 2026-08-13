```csharp title="C#"
using Xberg;

public class PdfOnlyProcessor : IPostProcessor
{
    public string Name => "pdf-only-processor";
    public string Version => "1.0.0";
    public int Priority => 50;
    public ProcessingStage ProcessingStage => ProcessingStage.Middle;

    public void Initialize() { }
    public void Shutdown() { }

    public ulong EstimatedDurationMs(ExtractedDocument result) => 1;

    public void Process(ExtractedDocument result, ExtractionConfig config) { }

    public bool ShouldProcess(ExtractedDocument result, ExtractionConfig config)
        => result.MimeType == "application/pdf";
}

class Program
{
    static void Main()
    {
        var processor = new PdfOnlyProcessor();
        PostProcessorRegistry.RegisterPostProcessor(processor);
    }
}
```
