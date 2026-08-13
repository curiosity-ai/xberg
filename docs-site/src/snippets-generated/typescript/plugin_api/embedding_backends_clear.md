---
id: fixture_node_embedding_backends_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all embedding backends and verify list is empty

```typescript title="TypeScript"
import { clearEmbeddingBackends } from "@xberg-io/xberg";
function main() {
  const result = clearEmbeddingBackends();
}

void main();

```
