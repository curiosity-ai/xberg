```typescript title="TypeScript"
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  chunking: {
    maxCharacters: 1024,
    overlap: 100,
    embedding: {
      model: { type: "preset", name: "balanced" },
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
console.log(`Chunks: ${output.results?.[0]?.chunks?.length ?? 0}`);
```
