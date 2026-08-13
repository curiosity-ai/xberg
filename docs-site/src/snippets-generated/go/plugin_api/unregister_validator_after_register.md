---
id: fixture_go_unregister_validator_after_register
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

unregister_validator

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterValidator(`test-validator`)
	if err != nil {
		panic(err)
	}
}
```
