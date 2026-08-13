---
id: fixture_elixir_register_validator_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```elixir title="Elixir"
{:ok, registervalidatortraitbridge_pid} = E2e.TestStubs.TestStubRegisterValidatorTraitBridgeGenServer.start_link(nil)

Xberg.register_validator(registervalidatortraitbridge_pid, "test-validator")

```
