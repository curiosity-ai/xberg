---
id: fixture_wasm_ocr_backends_unregister
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```typescript title="WebAssembly"
import { unregisterOcrBackend } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterOcrBackend("nonexistent-backend-xyz");
}

void main();

```
