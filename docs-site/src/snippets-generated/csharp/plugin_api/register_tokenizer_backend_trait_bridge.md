---
id: fixture_csharp_register_tokenizer_backend_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterTokenizerBackend(TokenizerBackendBridge.Register(new TestStub_RegisterTokenizerBackendTraitBridge()));

```
