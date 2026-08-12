```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  ocr: {
    backend: "tesseract",
  },
  forceOcr: true,
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
console.log(output.results[0].content);
```
