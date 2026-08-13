---
id: fixture_go_clear_reranker_backends
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearRerankerBackends()
	if err != nil {
		panic(err)
	}
}
```
