---
id: fixture_wasm_ocr_backends_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```typescript title="WebAssembly"
import { clearOcrBackends } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearOcrBackends();
}

void main();

```
