---
id: fixture_go_unregister_tokenizer_backend_after_register
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

unregister_tokenizer_backend

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterTokenizerBackend(`test-tokenizer-backend`)
	if err != nil {
		panic(err)
	}
}
```
