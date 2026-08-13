---
id: fixture_elixir_register_reranker_backend_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```elixir title="Elixir"
{:ok, registerrerankerbackendtraitbridge_pid} = E2e.TestStubs.TestStubRegisterRerankerBackendTraitBridgeGenServer.start_link(nil)

Xberg.register_reranker_backend(registerrerankerbackendtraitbridge_pid, "test-reranker-backend")

```
