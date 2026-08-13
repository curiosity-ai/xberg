---
id: fixture_node_error_empty_mime
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

Show how an empty MIME type is rejected consistently.

```typescript title="TypeScript"
import { ExtractInput, ExtractInputKind, extract } from "@xberg-io/xberg";
async function main() {
  const input: ExtractInput = { bytes: await (await import("node:fs/promises")).readFile("test_documents/text/plain.txt"), config: {  }, filename: "plain.txt", kind: ExtractInputKind.Bytes, mimeType: "" };
  try {
    await extract(input, undefined);
  } catch (error) {
    console.error("Call failed as expected:", error);
    return;
  }
  throw new Error("expected call to fail");
}

void main();

```
