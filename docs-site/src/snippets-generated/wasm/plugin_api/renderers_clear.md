---
id: fixture_wasm_renderers_clear
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

Clear all renderers and verify list is empty

```typescript title="WebAssembly"
import { clearRenderers } from "@xberg-io/xberg-wasm";
function main() {
  const result = clearRenderers();
}

void main();

```
