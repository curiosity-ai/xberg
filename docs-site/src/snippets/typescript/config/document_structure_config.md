```typescript title="Document Structure Config (TypeScript)"
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  includeDocumentStructure: true,
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const [result] = output.results ?? [];

if (result?.document) {
  for (const node of result.document.nodes ?? []) {
    console.log(`[${node.content.node_type}] ${"text" in node.content ? node.content.text : ""}`);
  }
}
```
