---
id: fixture_go_register_embedding_backend_trait_bridge
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```go title="Go"
package main

import (
	xberg "xberg"
)

type testStub_register_embedding_backend_trait_bridge struct{}

func (testStub_register_embedding_backend_trait_bridge) Dimensions() uint { return 768 }
func (testStub_register_embedding_backend_trait_bridge) Embed(texts []string) ([][]float32, error) { return nil, nil }
func (testStub_register_embedding_backend_trait_bridge) Name() string { return "test-embedding-backend" }
func (testStub_register_embedding_backend_trait_bridge) Version() string { return "" }
func (testStub_register_embedding_backend_trait_bridge) Initialize() error { return nil }
func (testStub_register_embedding_backend_trait_bridge) Shutdown() error { return nil }
func (testStub_register_embedding_backend_trait_bridge) Description() string { return "" }
func (testStub_register_embedding_backend_trait_bridge) Author() string { return "" }

func main() {
	err := xberg.RegisterEmbeddingBackend(testStub_register_embedding_backend_trait_bridge{})
	if err != nil {
		panic(err)
	}
}
```
