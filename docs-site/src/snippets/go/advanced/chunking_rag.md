```go title="Go"
package main

import (
	"fmt"
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	maxChars := uint(500)
	overlap := uint(50)
	normalize := true
	batchSize := uint(16)

	cfg := xberg.ExtractionConfig{
		Chunking: &xberg.ChunkingConfig{
			MaxCharacters: &maxChars,
			Overlap:       &overlap,
			Embedding: &xberg.EmbeddingConfig{
				Model:     xberg.EmbeddingModelTypePreset{Name: "quality"},
				Normalize: &normalize,
				BatchSize: &batchSize,
			},
		},
	}

	input := xberg.ExtractInputFromURI("research_paper.pdf")
	result, err := xberg.Extract(*input, cfg)
	if err != nil {
		log.Fatalf("RAG extraction failed: %v", err)
	}

	chunks := result.Results[0].Chunks
	fmt.Printf("Found %d chunks for RAG pipeline\n", len(chunks))

	for i := 0; i < len(chunks) && i < 3; i++ {
		chunk := chunks[i]
		content := chunk.Content
		if len(content) > 80 {
			content = content[:80]
		}
		fmt.Printf("Chunk %d: %s...\n", i, content)
	}
}
```
