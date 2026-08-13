---
id: fixture_wasm_extract_batch_bytes_size_cap
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

extract_batch: archive size cap triggers error

```typescript title="WebAssembly"
import { WasmExtractionConfig, extractBatch } from "@xberg-io/xberg-wasm";
async function main() {
  const config: WasmExtractionConfig = (() => { const _u0 = WasmExtractionConfig.default(); _u0.securityLimits = (() => { const _u1 = WasmSecurityLimits.default(); _u1.maxContentSize = 1; return _u1; })(); return _u0; })();
  try {
    await extractBatch([{ bytes: "test_documents/text/fake_text.txt", kind: "bytes", mimeType: "text/plain" }], config);
  } catch (error) {
    console.error("Call failed as expected:", error);
    return;
  }
  throw new Error("expected call to fail");
}

void main();

```
