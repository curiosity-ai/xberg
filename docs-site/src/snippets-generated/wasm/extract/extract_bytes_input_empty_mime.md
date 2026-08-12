---
id: fixture_wasm_extract_bytes_input_empty_mime
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

extract bytes input with empty MIME type

```typescript title="WebAssembly"
import { ExtractInput, ExtractInputKind, extract } from "@xberg-io/xberg-wasm";
async function main() {
  const input: WasmExtractInput = await (async () => { const _u0 = WasmExtractInput.default(); _u0.bytes = await (await import("node:fs/promises")).readFile("test_documents/text/plain.txt"); _u0.config = await (async () => { const _u1 = WasmFileExtractionConfig.default(); return _u1; })(); _u0.filename = "plain.txt"; _u0.kind = ExtractInputKind.Bytes; _u0.mimeType = ""; return _u0; })();
  try {
    await extract(input, {  });
  } catch (error) {
    console.error("Call failed as expected:", error);
    return;
  }
  throw new Error("expected call to fail");
}

void main();

```
