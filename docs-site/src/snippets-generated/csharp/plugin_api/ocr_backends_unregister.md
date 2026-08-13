---
id: fixture_csharp_ocr_backends_unregister
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```csharp title="C#"
using Xberg;

XbergConverter.UnregisterOcrBackend("nonexistent-backend-xyz");

```
