---
language: typescript
target: wasm
---

```typescript title="WASM"
import init, { extract } from "@xberg-io/xberg-wasm";

await init();

const config = {
  chunking: {
    max_characters: 1000,
    overlap: 100,
  },
};

const buffer = await fetch("document.pdf").then((response) => response.arrayBuffer());
const bytes = new Uint8Array(buffer);
const result = await extract({ kind: "bytes", bytes, mimeType: "application/pdf" }, config);

result.results[0].chunks?.forEach((chunk, idx) => {
  console.log(`Chunk ${idx}: ${chunk.content.substring(0, 50)}...`);
  console.log(`Tokens: ${chunk.metadata?.tokenCount}`);
});
```

```typescript title="WASM - Markdown with Heading Context"
import init, { extract } from "@xberg-io/xberg-wasm";

await init();

const config = {
  chunking: {
    chunker_type: "markdown",
    max_characters: 2000,
    // Note: Token-based sizing is not available in WASM builds.
    // Use character-based sizing instead.
  },
};

const buffer = await fetch("document.md").then((response) => response.arrayBuffer());
const bytes = new Uint8Array(buffer);
const result = await extract({ kind: "bytes", bytes, mimeType: "text/markdown" }, config);

result.results[0].chunks?.forEach((chunk, idx) => {
  console.log(`Chunk ${idx}: ${chunk.content.substring(0, 50)}...`);

  if (chunk.metadata?.headingContext?.headings) {
    console.log("Headings:");
    chunk.metadata.headingContext.headings.forEach((h: any) => {
      console.log(`  Level ${h.level}: ${h.text}`);
    });
  }
});
```
