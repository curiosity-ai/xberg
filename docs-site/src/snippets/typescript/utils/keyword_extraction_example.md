```typescript title="TypeScript"
import { ExtractInputKind, KeywordAlgorithm, extract } from "@xberg-io/xberg";

const config = {
  keywords: {
    algorithm: KeywordAlgorithm.Yake,
    maxKeywords: 10,
    minScore: 0.3,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "research_paper.pdf" }, config);
const result = output.results![0];
console.log(`Content length: ${result.content?.length ?? 0}`);
console.log(`Metadata: ${JSON.stringify(result.metadata)}`);
```
