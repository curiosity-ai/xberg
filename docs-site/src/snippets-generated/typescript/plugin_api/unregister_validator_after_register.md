---
id: fixture_node_unregister_validator_after_register
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

unregister_validator

```typescript title="TypeScript"
import { unregisterValidator } from "@xberg-io/xberg";
function main() {
  const result = unregisterValidator("test-validator");
}

void main();

```
