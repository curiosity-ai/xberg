---
id: fixture_go_post_processors_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearPostProcessors()
	if err != nil {
		panic(err)
	}
}
```
