---
id: fixture_wasm_post_processors_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```typescript title="WebAssembly"
import { clearPostProcessors } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearPostProcessors();
}

void main();

```
