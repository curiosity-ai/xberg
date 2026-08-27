```go title="Go"
package main

import (
	"fmt"
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

func main() {
	kind := xberg.ExtractInputKindURI
	uri := "document.pdf"

	output, err := xberg.Extract(
		xberg.ExtractInput{Kind: &kind, URI: &uri},
		xberg.ExtractionConfig{},
	)
	if err != nil {
		log.Fatal(err)
	}

	fmt.Println(output.Results[0].Content)
}
```
