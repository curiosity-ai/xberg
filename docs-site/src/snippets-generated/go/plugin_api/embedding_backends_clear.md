---
id: fixture_go_embedding_backends_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all embedding backends and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearEmbeddingBackends()
	if err != nil {
		panic(err)
	}
}
```
