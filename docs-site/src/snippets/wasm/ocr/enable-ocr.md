---
language: typescript
target: wasm
---

```typescript title="Wasm"
import init, { WasmExtractInputKind, extract } from "@xberg-io/xberg-wasm";

async function extractWithOcr() {
  await init();

  const buffer = await fetch("scanned-page.png").then((response) => response.arrayBuffer());
  const bytes = new Uint8Array(buffer);

  // OCR is turned on per extraction through the `ocr` config block. There is no
  // separate global "enable OCR" call — the backend is selected by name here.
  const output = await extract(
    {
      kind: "bytes",
      bytes,
      mimeType: "image/png",
      filename: "scanned-page.png",
    },
    {
      ocr: {
        enabled: true,
        backend: "tesseract",
        language: ["eng"],
      },
    },
  );

  console.log("Extracted text:");
  console.log(output.results[0].content);
}

extractWithOcr().catch(console.error);
```
