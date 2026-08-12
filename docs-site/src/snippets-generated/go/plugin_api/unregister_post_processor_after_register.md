---
id: fixture_go_unregister_post_processor_after_register
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

unregister_post_processor

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterPostProcessor(`test-processor`)
	if err != nil {
		panic(err)
	}
}
```
