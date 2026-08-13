---
id: fixture_node_renderers_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all renderers and verify list is empty

```typescript title="TypeScript"
import { clearRenderers } from "@xberg-io/xberg";
function main() {
  const result = clearRenderers();
}

void main();

```
