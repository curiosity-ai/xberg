```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  enableQualityProcessing: true,
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
console.log(output.results?.[0]?.content);
```
