---
id: fixture_node_extract_batch_bytes_size_cap
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

extract_batch: archive size cap triggers error

```typescript title="TypeScript"
import { ExtractionConfig, extractBatch } from "@xberg-io/xberg";
async function main() {
  const config: ExtractionConfig = { securityLimits: { maxContentSize: 1 } };
  try {
    await extractBatch([{ bytes: "test_documents/text/fake_text.txt", kind: "bytes", mimeType: "text/plain" }], config);
  } catch (error) {
    console.error("Call failed as expected:", error);
    return;
  }
  throw new Error("expected call to fail");
}

void main();

```
