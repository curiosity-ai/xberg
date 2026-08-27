```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  enableQualityProcessing: true,
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "scanned_document.pdf" }, config);
const [result] = output.results ?? [];
console.log(`Content length: ${result?.content?.length ?? 0} characters`);
console.log(`Metadata: ${JSON.stringify(result?.metadata)}`);
```
