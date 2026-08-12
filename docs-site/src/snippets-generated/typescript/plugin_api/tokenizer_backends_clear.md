---
id: fixture_node_tokenizer_backends_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all tokenizer backends and verify list is empty

```typescript title="TypeScript"
import { clearTokenizerBackends } from "@xberg-io/xberg";
function main() {
  const result = clearTokenizerBackends();
}

void main();

```
