```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "tesseract",
    language: ["eng"],
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "scanned.pdf" }, config);
console.log(output.results?.[0]?.content);
```
