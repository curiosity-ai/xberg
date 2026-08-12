```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  tokenReduction: {
    level: "Moderate",
    preserveImportantWords: true,
  },
};

const output = await extract({ kind: "uri", uri: "verbose_document.pdf" }, config);
const result = output.results[0];
console.log(`Content length: ${result.content.length}`);
console.log(`Metadata: ${JSON.stringify(result.metadata)}`);
```
