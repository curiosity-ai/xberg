---
id: fixture_node_register_embedding_backend_trait_bridge
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

register_embedding_backend: trait bridge

```typescript title="TypeScript"
import { registerEmbeddingBackend } from "@xberg-io/xberg";
function main() {
  class _TestStub_register_embedding_backend_trait_bridge {
  name(): string { return "test-embedding-backend"; }
  dimensions(): number { return 768; }
  async embed(_p0?: any): Promise<string> { return []; }
  async dispose(): Promise<void> { return undefined; }
}

  const _bridge_backend = new _TestStub_register_embedding_backend_trait_bridge();
  const result = registerEmbeddingBackend(_bridge_backend);
}

void main();

```
