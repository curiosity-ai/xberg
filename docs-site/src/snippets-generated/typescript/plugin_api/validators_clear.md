---
id: fixture_node_validators_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all validators and verify list is empty

```typescript title="TypeScript"
import { clearValidators } from "@xberg-io/xberg";
function main() {
  const result = clearValidators();
}

void main();

```
