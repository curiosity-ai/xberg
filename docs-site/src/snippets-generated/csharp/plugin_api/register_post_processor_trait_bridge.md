---
id: fixture_csharp_register_post_processor_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterPostProcessor(PostProcessorBridge.Register(new TestStub_RegisterPostProcessorTraitBridge()));

```
