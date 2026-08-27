---
language: typescript
target: wasm
---

```typescript title="WASM"
import init, { extract } from "@xberg-io/xberg-wasm";

await init();

const config = {
  use_cache: true,
  enable_quality_processing: true,
  ocr: {
    backend: "tesseract",
    language: ["eng"],
  },
};

const buffer = await fetch("document.pdf").then((response) => response.arrayBuffer());
const bytes = new Uint8Array(buffer);
const result = await extract({ kind: "bytes", bytes, mimeType: "application/pdf" }, config);
console.log(result.results[0].content);
```
