---
id: fixture_node_unregister_embedding_backend_after_register
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

unregister_embedding_backend

```typescript title="TypeScript"
import { unregisterEmbeddingBackend } from "@xberg-io/xberg";
function main() {
  const result = unregisterEmbeddingBackend("test-embedding-backend");
}

void main();

```
