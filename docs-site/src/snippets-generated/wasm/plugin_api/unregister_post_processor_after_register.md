---
id: fixture_wasm_unregister_post_processor_after_register
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

unregister_post_processor

```typescript title="WebAssembly"
import { unregisterPostProcessor } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterPostProcessor("test-processor");
}

void main();

```
