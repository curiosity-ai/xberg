```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  enableQualityProcessing: true,
};

const output = await extract({ kind: "uri", uri: "scanned_document.pdf" }, config);
const result = output.results[0];
console.log(`Content length: ${result.content.length} characters`);
console.log(`Metadata: ${JSON.stringify(result.metadata)}`);
```
