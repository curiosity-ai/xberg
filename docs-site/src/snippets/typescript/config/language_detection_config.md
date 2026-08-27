```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  languageDetection: {
    enabled: true,
    minConfidence: 0.8,
    detectMultiple: false,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const result = output.results?.[0];
if (result?.detectedLanguages) {
  console.log(`Detected languages: ${result.detectedLanguages.join(", ")}`);
}
```
