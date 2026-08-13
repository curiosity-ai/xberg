---
id: fixture_wasm_unregister_reranker_backend
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

unregister_reranker_backend

```typescript title="WebAssembly"
import { unregisterRerankerBackend } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterRerankerBackend("test-reranker-backend");
}

void main();

```
