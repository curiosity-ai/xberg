```csharp title="C#"
using Xberg;
using System;

PostProcessorRegistry.Clear();
ValidatorRegistry.Clear();
OcrBackendRegistry.Clear();
DocumentExtractorRegistry.Clear();

Console.WriteLine("All plugins cleared");
```
