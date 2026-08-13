```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "tesseract",
  },
  pdfOptions: {
    extractImages: true,
  },
};

const output = await extract({ kind: "uri", uri: "scanned.pdf" }, config);
console.log(output.results[0].content);
```
