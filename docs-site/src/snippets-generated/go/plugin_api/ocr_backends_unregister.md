---
id: fixture_go_ocr_backends_unregister
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```go title="Go"
package main

import (
	xberg "xberg"
)

func main() {
	err := xberg.UnregisterOcrBackend(`nonexistent-backend-xyz`)
	if err != nil {
		panic(err)
	}
}
```
