```go title="Go"
package main

import (
	"fmt"
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	input := xberg.ExtractInputFromURI("document.pdf")
	result, err := xberg.Extract(*input, xberg.ExtractionConfig{})
	if err != nil {
		log.Fatalf("extract failed: %v", err)
	}

	fmt.Println("Content:")
	fmt.Println(result.Results[0].Content)

	fmt.Println("\nMetadata:")
	if meta := result.Results[0].Metadata; meta != nil {
		if meta.Title != nil {
			fmt.Printf("Title: %s\n", *meta.Title)
		}
		if len(meta.Authors) > 0 {
			fmt.Printf("Author: %s\n", meta.Authors[0])
		}
	}

	fmt.Printf("\nTables found: %d\n", len(result.Results[0].Tables))
	fmt.Printf("Images found: %d\n", len(result.Results[0].Images))
}
```
