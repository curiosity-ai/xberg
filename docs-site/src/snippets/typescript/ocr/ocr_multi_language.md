```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "tesseract",
    language: ["eng", "deu", "fra"],
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "multilingual.pdf" }, config);
console.log(output.results?.[0]?.content);
```
