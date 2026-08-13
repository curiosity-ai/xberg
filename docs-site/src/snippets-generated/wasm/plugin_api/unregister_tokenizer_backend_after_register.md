---
id: fixture_wasm_unregister_tokenizer_backend_after_register
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

unregister_tokenizer_backend

```typescript title="WebAssembly"
import { unregisterTokenizerBackend } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterTokenizerBackend("test-tokenizer-backend");
}

void main();

```
