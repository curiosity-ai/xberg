```typescript title="TypeScript"
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  chunking: {
    maxCharacters: 500,
    overlap: 50,
    embedding: {
      model: { type: "preset", name: "balanced" },
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "research_paper.pdf" }, config);
const [result] = output.results ?? [];

if (result?.chunks) {
  for (const chunk of result.chunks) {
    console.log(`Chunk ${chunk.metadata.chunkIndex + 1}/${chunk.metadata.totalChunks}`);
    console.log(`Position: ${chunk.metadata.byteStart}-${chunk.metadata.byteEnd}`);
    console.log(`Content: ${chunk.content.slice(0, 100)}...`);
    if (chunk.embedding) {
      console.log(`Embedding: ${chunk.embedding.length} dimensions`);
    }
  }
}
```
