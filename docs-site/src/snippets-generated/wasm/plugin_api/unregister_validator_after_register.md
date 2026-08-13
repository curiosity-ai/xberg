---
id: fixture_wasm_unregister_validator_after_register
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

unregister_validator

```typescript title="WebAssembly"
import { unregisterValidator } from "@xberg-io/xberg-wasm";
function main() {
  const result = unregisterValidator("test-validator");
}

void main();

```
