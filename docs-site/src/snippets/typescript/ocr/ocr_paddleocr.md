```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "paddle-ocr",
    language: ["en"],
    // modelTier: 'server', // for max accuracy
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "scanned.pdf" }, config);
console.log(output.results?.[0]?.content);
```
