```csharp title="C#"
using Xberg;

public class QualityValidator : IValidator
{
    public string Name => "quality-validator";
    public string Version => "1.0.0";
    public int Priority => 100;

    public void Validate(ExtractedDocument result, ExtractionConfig config)
    {
        var score = result.QualityScore ?? 0.0;

        if (score < 0.5)
            throw new ValidationException($"Quality score too low: {score:F2}");
    }

    public bool ShouldValidate(ExtractedDocument result, ExtractionConfig config) => result.QualityScore.HasValue;
    public void Initialize() { }
    public void Shutdown() { }
}

class Program
{
    static void Main()
    {
        var validator = new QualityValidator();
        ValidatorRegistry.RegisterValidator(validator);
    }
}
```
