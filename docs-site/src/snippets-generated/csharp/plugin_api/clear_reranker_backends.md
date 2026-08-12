---
id: fixture_csharp_clear_reranker_backends
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```csharp title="C#"
using Xberg;

XbergConverter.ClearRerankerBackends();

```
