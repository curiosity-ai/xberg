---
id: fixture_wasm_unregister_embedding_backend_after_register
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

unregister_embedding_backend

```typescript title="WebAssembly"
import { unregisterEmbeddingBackend } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterEmbeddingBackend("test-embedding-backend");
}

void main();

```
