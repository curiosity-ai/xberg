```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    maxCharacters: 1024,
    overlap: 100,
    embedding: {
      model: { type: "preset", name: "balanced" },
    },
  },
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
console.log(`Chunks: ${output.results[0].chunks?.length ?? 0}`);
```
