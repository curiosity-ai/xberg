---
id: fixture_go_register_reranker_backend_trait_bridge
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```go title="Go"
package main

import (
	xberg "xberg"
)

type testStub_register_reranker_backend_trait_bridge struct{}

func (testStub_register_reranker_backend_trait_bridge) Rerank(query string, documents []string) ([]float32, error) { return nil, nil }
func (testStub_register_reranker_backend_trait_bridge) Name() string { return "test-reranker-backend" }
func (testStub_register_reranker_backend_trait_bridge) Version() string { return "" }
func (testStub_register_reranker_backend_trait_bridge) Initialize() error { return nil }
func (testStub_register_reranker_backend_trait_bridge) Shutdown() error { return nil }
func (testStub_register_reranker_backend_trait_bridge) Description() string { return "" }
func (testStub_register_reranker_backend_trait_bridge) Author() string { return "" }

func main() {
	err := xberg.RegisterRerankerBackend(testStub_register_reranker_backend_trait_bridge{})
	if err != nil {
		panic(err)
	}
}
```
