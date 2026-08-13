```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "paddle-ocr",
    language: ["en"],
    // modelTier: 'server', // for max accuracy
  },
};

const output = await extract({ kind: "uri", uri: "scanned.pdf" }, config);
console.log(output.results[0].content);
```
