```typescript title="TypeScript"
import { extract, type ExtractionConfig } from "@xberg-io/xberg";

// Note: the Node binding has no config-file discovery helper. Build the
// config object directly (or load `xberg.toml`/`xberg.yaml`/`xberg.json`
// yourself and parse it) and pass it to `extract`.
const config: ExtractionConfig = {
  useCache: true,
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
console.log(output.results[0].content);
```
