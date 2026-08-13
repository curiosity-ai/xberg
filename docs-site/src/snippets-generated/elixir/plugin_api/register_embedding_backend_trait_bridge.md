---
id: fixture_elixir_register_embedding_backend_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```elixir title="Elixir"
{:ok, registerembeddingbackendtraitbridge_pid} = E2e.TestStubs.TestStubRegisterEmbeddingBackendTraitBridgeGenServer.start_link(nil)

Xberg.register_embedding_backend(registerembeddingbackendtraitbridge_pid, "test-embedding-backend")

```
