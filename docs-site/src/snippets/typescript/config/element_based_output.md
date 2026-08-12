```typescript title="Element-Based Output (TypeScript)"
import { extract, ElementType, type ExtractionConfig } from "@xberg-io/xberg";

// Configure element-based output
const config: ExtractionConfig = {
  resultFormat: "element_based",
};

// Extract document
const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
const result = output.results[0];

// Access elements
for (const element of result.elements ?? []) {
  console.log(`Type: ${element.elementType}`);
  console.log(`Text: ${element.text.slice(0, 100)}`);

  if (element.metadata.pageNumber) {
    console.log(`Page: ${element.metadata.pageNumber}`);
  }

  if (element.metadata.coordinates) {
    const coords = element.metadata.coordinates;
    console.log(`Coords: (${coords.x0}, ${coords.y0}) - (${coords.x1}, ${coords.y1})`);
  }

  console.log("---");
}

// Filter by element type
const titles = (result.elements ?? []).filter((e) => e.elementType === ElementType.Title);
for (const title of titles) {
  const level = title.metadata.additional?.level || "unknown";
  console.log(`[${level}] ${title.text}`);
}
```
