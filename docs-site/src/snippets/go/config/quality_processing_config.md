```go title="Go"
package main

import (
	"fmt"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	config := &xberg.ExtractionConfig{
		EnableQualityProcessing: true,  // Default
	}

	fmt.Printf("Quality processing enabled: %v\n", config.EnableQualityProcessing)
}
```
