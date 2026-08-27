```typescript title="TypeScript"
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  chunking: {
    maxCharacters: 1500,
    overlap: 200,
    embedding: {
      model: { type: "preset", name: "quality" },
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
console.log(`Chunks created: ${output.results?.[0]?.chunks?.length ?? 0}`);
```
