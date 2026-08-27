```go title="Element-Based Output (Go)"
package main

import (
    "fmt"
    "github.com/xberg-io/xberg/packages/go"
)

func main() {
    // Configure element-based output
    resultFormat := xberg.ResultFormatElementBased
    cfg := xberg.ExtractionConfig{
        ResultFormat: &resultFormat,
    }

    // Extract document
    input := xberg.ExtractInputFromURI("document.pdf")
    result, err := xberg.Extract(*input, cfg)
    if err != nil {
        panic(err)
    }

    // Access elements
    for _, element := range result.Results[0].Elements {
        fmt.Printf("Type: %s\n", element.ElementType)

        text := element.Text
        if len(text) > 100 {
            text = text[:100]
        }
        fmt.Printf("Text: %s\n", text)

        if element.Metadata.PageNumber != nil {
            fmt.Printf("Page: %d\n", *element.Metadata.PageNumber)
        }

        if element.Metadata.Coordinates != nil {
            coords := element.Metadata.Coordinates
            fmt.Printf("Coords: (%f, %f) - (%f, %f)\n",
                coords.X0, coords.Y1, coords.X1, coords.Y0)
        }

        fmt.Println("---")
    }

    // Filter by element type
    var titles []xberg.Element
    for _, element := range result.Results[0].Elements {
        if element.ElementType == "title" {
            titles = append(titles, element)
        }
    }

    for _, title := range titles {
        level, ok := title.Metadata.Additional["level"]
        if !ok {
            level = "unknown"
        }
        fmt.Printf("[%s] %s\n", level, title.Text)
    }
}
```
