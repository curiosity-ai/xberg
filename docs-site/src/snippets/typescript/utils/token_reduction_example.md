```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  tokenReduction: {
    level: "Moderate",
    preserveImportantWords: true,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "verbose_document.pdf" }, config);
const [result] = output.results ?? [];
console.log(`Content length: ${result?.content?.length ?? 0}`);
console.log(`Metadata: ${JSON.stringify(result?.metadata)}`);
```
