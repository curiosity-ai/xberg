```go title="Go"
package main

import (
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

// jsonExtractor is a minimal custom xberg.DocumentExtractor for JSON documents.
type jsonExtractor struct{}

func (jsonExtractor) Name() string    { return "custom-json-extractor" }
func (jsonExtractor) Version() string { return "1.0.0" }
func (jsonExtractor) Initialize() error { return nil }
func (jsonExtractor) Shutdown() error   { return nil }
func (jsonExtractor) Priority() int32   { return 50 }

func (jsonExtractor) CanHandle(_path string, mimeType string) bool {
	return mimeType == "application/json"
}

func (jsonExtractor) Extract(input xberg.ExtractInput, config xberg.ExtractionConfig) (xberg.ExtractedDocument, error) {
	return xberg.ExtractedDocument{}, nil
}

func (jsonExtractor) SupportedMimeTypes() []string {
	return []string{"application/json"}
}

func main() {
	// Register custom extractor
	if err := xberg.RegisterDocumentExtractor(jsonExtractor{}); err != nil {
		log.Fatalf("register extractor failed: %v", err)
	}

	input := xberg.ExtractInputFromURI("document.json")
	result, err := xberg.Extract(*input, xberg.ExtractionConfig{})
	if err != nil {
		log.Fatalf("extract failed: %v", err)
	}
	log.Printf("Extracted content length: %d", len(result.Results[0].Content))
}
```
