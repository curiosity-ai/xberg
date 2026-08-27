---
language: typescript
target: wasm
---

```typescript title="Wasm"
import init, { WasmExtractInput, extractBatch } from "@xberg-io/xberg-wasm";

interface DocumentJob {
  name: string;
  bytes: Uint8Array;
  mimeType: string;
}

async function _processBatch(documents: DocumentJob[], concurrency: number = 3) {
  await init();

  const results: Record<string, string> = {};

  for (let index = 0; index < documents.length; index += concurrency) {
    const batch = documents.slice(index, index + concurrency);
    const output = await extractBatch(
      batch.map((doc) => WasmExtractInput.fromBytes(doc.bytes, doc.mimeType, doc.name)),
      undefined,
    );

    output.results.forEach((result, resultIndex) => {
      results[batch[resultIndex].name] = result.content ?? "";
    });
  }
  return results;
}
```
