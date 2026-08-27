```typescript title="TypeScript"
import { ExtractInputKind, KeywordAlgorithm, extract } from "@xberg-io/xberg";

const config = {
  keywords: {
    algorithm: KeywordAlgorithm.Yake,
    maxKeywords: 10,
    minScore: 0.3,
    language: "en",
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const result = output.results![0];
console.log(`Content: ${result.content}`);
```
