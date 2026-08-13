---
id: fixture_elixir_register_ocr_backend_trait_bridge
language: elixir
target: elixir
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```elixir title="Elixir"
{:ok, registerocrbackendtraitbridge_pid} = E2e.TestStubs.TestStubRegisterOcrBackendTraitBridgeGenServer.start_link(nil)

Xberg.register_ocr_backend(registerocrbackendtraitbridge_pid, "test-backend")

```
