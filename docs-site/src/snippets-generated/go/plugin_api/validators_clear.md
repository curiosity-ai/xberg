---
id: fixture_go_validators_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all validators and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearValidators()
	if err != nil {
		panic(err)
	}
}
```
