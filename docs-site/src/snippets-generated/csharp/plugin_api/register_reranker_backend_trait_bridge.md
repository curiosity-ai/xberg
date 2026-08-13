---
id: fixture_csharp_register_reranker_backend_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterRerankerBackend(RerankerBackendBridge.Register(new TestStub_RegisterRerankerBackendTraitBridge()));

```
