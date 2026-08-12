---
id: fixture_wasm_validators_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all validators and verify list is empty

```typescript title="WebAssembly"
import { clearValidators } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearValidators();
}

void main();

```
