---
id: fixture_csharp_register_ocr_backend_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterOcrBackend(OcrBackendBridge.Register(new TestStub_RegisterOcrBackendTraitBridge()));

```
