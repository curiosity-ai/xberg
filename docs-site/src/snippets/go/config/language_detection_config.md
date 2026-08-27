```go title="Go"
package main

import (
	"fmt"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	enabled := true
	minConfidence := 0.8
	config := &xberg.ExtractionConfig{
		LanguageDetection: &xberg.LanguageDetectionConfig{
			Enabled:        &enabled,
			MinConfidence:  &minConfidence,
			DetectMultiple: false,
		},
	}

	fmt.Printf("Language detection enabled: %v\n", *config.LanguageDetection.Enabled)
	fmt.Printf("Min confidence: %f\n", *config.LanguageDetection.MinConfidence)
}
```
