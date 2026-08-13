```csharp title="C#"
using Xberg;
using System;

var extractors = XbergConverter.ListDocumentExtractors();
var processors = XbergConverter.ListPostProcessors();
var ocrBackends = XbergConverter.ListOcrBackends();
var validators = XbergConverter.ListValidators();

Console.WriteLine($"Extractors: {string.Join(", ", extractors)}");
Console.WriteLine($"Processors: {string.Join(", ", processors)}");
Console.WriteLine($"OCR backends: {string.Join(", ", ocrBackends)}");
Console.WriteLine($"Validators: {string.Join(", ", validators)}");
```
