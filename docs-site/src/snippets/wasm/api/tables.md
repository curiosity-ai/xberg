---
language: typescript
target: wasm
---

```typescript title="WASM"
import init, { extract } from "@xberg-io/xberg-wasm";

await init();

const fileInput = document.getElementById("file") as HTMLInputElement;
const file = fileInput.files?.[0];

if (file) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const result = await extract({ kind: "bytes", bytes, mimeType: file.type || "application/pdf" }, undefined);

  result.results[0].tables?.forEach((table) => {
    console.log(`Table with ${table.cells?.length ?? 0} rows`);
    if (table.markdown) {
      console.log(table.markdown);
    }
    table.cells?.forEach((row: string[]) => console.log(row.join(" | ")));
  });
}
```
