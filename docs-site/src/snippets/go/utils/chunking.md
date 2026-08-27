```go title="Go"
package main

import (
	"fmt"
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	maxChars := uint(1000)
	overlap := uint(200)
	cfg := xberg.ExtractionConfig{
		Chunking: &xberg.ChunkingConfig{
			MaxCharacters: &maxChars,
			Overlap:       &overlap,
		},
	}

	input := xberg.ExtractInputFromURI("document.pdf")
	result, err := xberg.Extract(*input, cfg)
	if err != nil {
		log.Fatalf("extract failed: %v", err)
	}

	for i, chunk := range result.Results[0].Chunks {
		// Byte offsets (UTF-8 valid boundaries) into the original document text.
		fmt.Printf("Chunk %d/%d (%d-%d)\n", i+1, chunk.Metadata.TotalChunks, chunk.Metadata.ByteStart, chunk.Metadata.ByteEnd)
		fmt.Printf("%s...\n", chunk.Content[:min(len(chunk.Content), 100)])
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
```
