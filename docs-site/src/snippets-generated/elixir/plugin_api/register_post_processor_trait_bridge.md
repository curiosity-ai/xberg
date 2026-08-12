---
id: fixture_elixir_register_post_processor_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```elixir title="Elixir"
{:ok, registerpostprocessortraitbridge_pid} = E2e.TestStubs.TestStubRegisterPostProcessorTraitBridgeGenServer.start_link(nil)

Xberg.register_post_processor(registerpostprocessortraitbridge_pid, "test-processor")

```
