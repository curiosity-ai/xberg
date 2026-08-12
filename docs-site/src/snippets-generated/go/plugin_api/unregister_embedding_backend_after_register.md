---
id: fixture_go_unregister_embedding_backend_after_register
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

unregister_embedding_backend

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterEmbeddingBackend(`test-embedding-backend`)
	if err != nil {
		panic(err)
	}
}
```
