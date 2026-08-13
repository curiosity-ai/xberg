---
id: fixture_wasm_register_embedding_backend_trait_bridge
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```typescript title="WebAssembly"
import { registerEmbeddingBackend } from "@xberg-io/xberg-wasm";
function main() {
  class _TestStub_register_embedding_backend_trait_bridge {
  name(): string { return "test-embedding-backend"; }
  dimensions(): number { return 768; }
  async embed(_p0?: any): Promise<string> { return []; }
  async dispose(): Promise<void> { return undefined; }
}

  const result = registerEmbeddingBackend(new _TestStub_register_embedding_backend_trait_bridge());
}

void main();

```
