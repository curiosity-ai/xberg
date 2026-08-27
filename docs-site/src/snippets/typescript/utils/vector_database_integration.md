```typescript title="TypeScript"
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  chunking: {
    maxCharacters: 512,
    overlap: 50,
    embedding: {
      model: { type: "preset", name: "balanced" },
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const [result] = output.results ?? [];

if (result?.chunks) {
  for (const chunk of result.chunks) {
    console.log(`Chunk: ${chunk.content.slice(0, 100)}...`);
    if (chunk.embedding) {
      console.log(`Embedding dims: ${chunk.embedding.length}`);
    }
  }
}
```
