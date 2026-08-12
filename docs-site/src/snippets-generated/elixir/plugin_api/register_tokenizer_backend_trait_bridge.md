---
id: fixture_elixir_register_tokenizer_backend_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```elixir title="Elixir"
{:ok, registertokenizerbackendtraitbridge_pid} = E2e.TestStubs.TestStubRegisterTokenizerBackendTraitBridgeGenServer.start_link(nil)

Xberg.register_tokenizer_backend(registertokenizerbackendtraitbridge_pid, "test-tokenizer-backend")

```
