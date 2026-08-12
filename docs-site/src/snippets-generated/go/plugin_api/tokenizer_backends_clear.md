---
id: fixture_go_tokenizer_backends_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all tokenizer backends and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearTokenizerBackends()
	if err != nil {
		panic(err)
	}
}
```
