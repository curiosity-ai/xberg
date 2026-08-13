---
id: fixture_node_unregister_tokenizer_backend_after_register
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

unregister_tokenizer_backend

```typescript title="TypeScript"
import { unregisterTokenizerBackend } from "@xberg-io/xberg";
function main() {
  const result = unregisterTokenizerBackend("test-tokenizer-backend");
}

void main();

```
