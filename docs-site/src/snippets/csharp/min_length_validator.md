```csharp title="C#"
using Xberg;

var validator = new MinLengthValidator(minLength: 100);
ValidatorRegistry.RegisterValidator(validator);

public class MinLengthValidator : IValidator
{
    private readonly int _minLength;

    public MinLengthValidator(int minLength = 100)
    {
        _minLength = minLength;
    }

    public string Name => "min_length_validator";
    public string Version => "1.0.0";
    public int Priority => 100;

    public void Validate(ExtractedDocument result, ExtractionConfig config)
    {
        var contentLength = result.Content.Length;
        if (contentLength < _minLength)
            throw new ValidationException($"Content too short: {contentLength}");
    }

    public bool ShouldValidate(ExtractedDocument result, ExtractionConfig config) => true;
    public void Initialize() { }
    public void Shutdown() { }
}
```
