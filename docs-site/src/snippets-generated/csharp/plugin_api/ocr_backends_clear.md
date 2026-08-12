---
id: fixture_csharp_ocr_backends_clear
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```csharp title="C#"
using Xberg;

XbergConverter.ClearOcrBackends();

```
