```go title="Go"
package main

import (
	"fmt"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	maxChars := uint(512)
	overlap := uint(50)
	normalize := true
	batchSize := uint(32)
	showProgress := false

	cfg := xberg.ExtractionConfig{
		Chunking: &xberg.ChunkingConfig{
			MaxCharacters: &maxChars,
			Overlap:       &overlap,
			Embedding: &xberg.EmbeddingConfig{
				Model:                xberg.EmbeddingModelTypePreset{Name: "balanced"},
				Normalize:            &normalize,
				BatchSize:            &batchSize,
				ShowDownloadProgress: showProgress,
			},
		},
	}

	input := xberg.ExtractInputFromURI("document.pdf")
	result, err := xberg.Extract(*input, cfg)
	if err != nil {
		fmt.Printf("Error: %v\n", err)
		return
	}

	for index, chunk := range result.Results[0].Chunks {
		chunkID := fmt.Sprintf("doc_chunk_%d", index)
		content := chunk.Content
		if len(content) > 50 {
			content = content[:50]
		}
		fmt.Printf("Chunk %s: %s\n", chunkID, content)

		if chunk.Embedding != nil && len(chunk.Embedding) > 0 {
			fmt.Printf("  Embedding dimensions: %d\n", len(chunk.Embedding))
		}
	}
}
```
