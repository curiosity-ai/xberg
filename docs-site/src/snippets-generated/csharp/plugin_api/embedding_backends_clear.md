---
id: fixture_csharp_embedding_backends_clear
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

Clear all embedding backends and verify list is empty

```csharp title="C#"
using Xberg;

XbergConverter.ClearEmbeddingBackends();

```
