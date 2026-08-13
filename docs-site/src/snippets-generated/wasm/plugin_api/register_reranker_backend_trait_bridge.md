---
id: fixture_wasm_register_reranker_backend_trait_bridge
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

register_reranker_backend: trait bridge

```typescript title="WebAssembly"
import { registerRerankerBackend } from "@xberg-io/xberg-wasm";
function main() {
  class _TestStub_register_reranker_backend_trait_bridge {
  name(): string { return "test-reranker-backend"; }
  async rerank(_p0?: any, _p1?: any): Promise<string> { return []; }
  async dispose(): Promise<void> { return undefined; }
}

  const result = registerRerankerBackend(new _TestStub_register_reranker_backend_trait_bridge());
}

void main();

```
