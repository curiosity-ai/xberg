---
id: fixture_go_unregister_reranker_backend
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

unregister_reranker_backend

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterRerankerBackend(`test-reranker-backend`)
	if err != nil {
		panic(err)
	}
}
```
