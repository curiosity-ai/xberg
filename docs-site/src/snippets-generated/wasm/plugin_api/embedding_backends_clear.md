---
id: fixture_wasm_embedding_backends_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all embedding backends and verify list is empty

```typescript title="WebAssembly"
import { clearEmbeddingBackends } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearEmbeddingBackends();
}

void main();

```
