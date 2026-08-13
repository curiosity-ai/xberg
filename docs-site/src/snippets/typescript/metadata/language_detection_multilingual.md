```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  languageDetection: {
    enabled: true,
    minConfidence: 0.8,
    detectMultiple: true,
  },
};

const output = await extract({ kind: "uri", uri: "multilingual_document.pdf" }, config);
const result = output.results[0];
if (result.detectedLanguages) {
  console.log(`Detected languages: ${result.detectedLanguages.join(", ")}`);
}
```
