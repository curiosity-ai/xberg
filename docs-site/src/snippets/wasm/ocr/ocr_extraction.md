---
language: typescript
target: wasm
---

```typescript title="WASM (Browser)"
import init, { extract } from "@xberg-io/xberg-wasm";

await init();

const fileInput = document.getElementById("file") as HTMLInputElement;
const file = fileInput.files?.[0];

if (file) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const result = await extract(
    { kind: "bytes", bytes, mimeType: file.type },
    {
      ocr: {
        enabled: true,
        backend: "tesseract",
        language: ["eng"],
      },
    },
  );
  console.log(result.results[0].content);
}
```

```typescript title="WASM (Node.js / Deno / Bun)"
import init, { extract } from "@xberg-io/xberg-wasm";

// Outside the browser the default `fetch`-based init cannot read a `file://`
// URL: pass the `xberg_wasm_bg.wasm` bytes yourself, either as
// `init({ module_or_path: bytes })` or via the synchronous `initSync({ module: bytes })`.
await init();

const result = await extract(
  { kind: "uri", uri: "./scanned_document.png" },
  {
    ocr: {
      enabled: true,
      backend: "tesseract",
      language: ["eng"],
    },
  },
);
console.log(result.results[0].content);
```
