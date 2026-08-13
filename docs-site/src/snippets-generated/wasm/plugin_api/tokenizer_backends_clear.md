---
id: fixture_wasm_tokenizer_backends_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all tokenizer backends and verify list is empty

```typescript title="WebAssembly"
import { clearTokenizerBackends } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearTokenizerBackends();
}

void main();

```
