---
id: fixture_go_register_tokenizer_backend_trait_bridge
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```go title="Go"
package main

import (
	xberg "xberg"
)

type testStub_register_tokenizer_backend_trait_bridge struct{}

func (testStub_register_tokenizer_backend_trait_bridge) CountTokens(text string) uint { return 3 }
func (testStub_register_tokenizer_backend_trait_bridge) Name() string { return "test-tokenizer-backend" }
func (testStub_register_tokenizer_backend_trait_bridge) Version() string { return "" }
func (testStub_register_tokenizer_backend_trait_bridge) Initialize() error { return nil }
func (testStub_register_tokenizer_backend_trait_bridge) Shutdown() error { return nil }
func (testStub_register_tokenizer_backend_trait_bridge) Description() string { return "" }
func (testStub_register_tokenizer_backend_trait_bridge) Author() string { return "" }

func main() {
	err := xberg.RegisterTokenizerBackend(testStub_register_tokenizer_backend_trait_bridge{})
	if err != nil {
		panic(err)
	}
}
```
