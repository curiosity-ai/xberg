```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    maxCharacters: 1500,
    overlap: 200,
    embedding: {
      model: { type: "preset", name: "quality" },
    },
  },
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
console.log(`Chunks created: ${output.results[0].chunks?.length ?? 0}`);
```
