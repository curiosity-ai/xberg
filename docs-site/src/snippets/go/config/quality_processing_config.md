```go title="Go"
package main

import (
	"fmt"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	enableQualityProcessing := true // Default
	config := &xberg.ExtractionConfig{
		EnableQualityProcessing: &enableQualityProcessing,
	}

	fmt.Printf("Quality processing enabled: %v\n", *config.EnableQualityProcessing)
}
```
