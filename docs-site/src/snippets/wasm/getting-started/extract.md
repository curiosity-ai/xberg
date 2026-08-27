---
language: typescript
target: wasm
---

```typescript title="Wasm"
import init, { WasmExtractInputKind, extract } from "@xberg-io/xberg-wasm";

await init();

const buffer = await fetch("/document.pdf").then((response) => response.arrayBuffer());
const bytes = new Uint8Array(buffer);

const output = await extract({
  kind: "bytes",
  bytes,
  mimeType: "application/pdf",
  filename: "document.pdf",
}, undefined);

console.log(output.results[0].content);
```
