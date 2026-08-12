---
id: fixture_node_clear_reranker_backends
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```typescript title="TypeScript"
import { clearRerankerBackends } from "@xberg-io/xberg";
function main() {
  const result = clearRerankerBackends();
}

void main();

```
