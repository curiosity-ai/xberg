---
id: fixture_csharp_register_embedding_backend_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterEmbeddingBackend(EmbeddingBackendBridge.Register(new TestStub_RegisterEmbeddingBackendTraitBridge()));

```
