---
id: fixture_node_post_processors_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```typescript title="TypeScript"
import { clearPostProcessors } from "@xberg-io/xberg";
function main() {
  const result = clearPostProcessors();
}

void main();

```
