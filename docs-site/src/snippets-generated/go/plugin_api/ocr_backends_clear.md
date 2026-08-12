---
id: fixture_go_ocr_backends_clear
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.ClearOcrBackends()
	if err != nil {
		panic(err)
	}
}
```
