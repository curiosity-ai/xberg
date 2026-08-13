---
id: fixture_node_ocr_backends_unregister
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```typescript title="TypeScript"
import { unregisterOcrBackend } from "@xberg-io/xberg";
function main() {
  const result = unregisterOcrBackend("nonexistent-backend-xyz");
}

void main();

```
