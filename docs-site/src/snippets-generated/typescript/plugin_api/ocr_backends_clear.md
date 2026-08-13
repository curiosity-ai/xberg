---
id: fixture_node_ocr_backends_clear
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Clear all OCR backends and verify list is empty

```typescript title="TypeScript"
import { clearOcrBackends } from "@xberg-io/xberg";
function main() {
  const result = clearOcrBackends();
}

void main();

```
