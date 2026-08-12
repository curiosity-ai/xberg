---
id: fixture_wasm_clear_reranker_backends
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```typescript title="WebAssembly"
import { clearRerankerBackends } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearRerankerBackends();
}

void main();

```
