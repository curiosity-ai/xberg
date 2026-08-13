```csharp title="C#"
using Xberg;
using System.Collections.Generic;

var names = new List<string>
{
    "custom-json-extractor",
    "word_count",
    "cloud-ocr",
    "min_length_validator"
};

DocumentExtractorRegistry.Unregister(names[0]);
PostProcessorRegistry.Unregister(names[1]);
OcrBackendRegistry.Unregister(names[2]);
ValidatorRegistry.Unregister(names[3]);
```
