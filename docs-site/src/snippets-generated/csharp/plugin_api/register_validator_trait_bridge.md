---
id: fixture_csharp_register_validator_trait_bridge
language: csharp
target: csharp
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```csharp title="C#"
using Xberg;

XbergConverter.RegisterValidator(ValidatorBridge.Register(new TestStub_RegisterValidatorTraitBridge()));

```
