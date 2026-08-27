```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  forceOcr: true,
  ocr: {
    backend: "vlm",
    vlmConfig: {
      model: "openai/gpt-4o-mini",
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "scan.pdf" }, config);
console.log(output.results?.[0]?.content);
```
