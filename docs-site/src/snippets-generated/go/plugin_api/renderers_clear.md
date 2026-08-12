---
id: fixture_go_renderers_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all renderers and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearRenderers()
	if err != nil {
		panic(err)
	}
}
```
