```typescript title="TypeScript"
import { ExtractInputKind, NerBackendKind, extract } from "@xberg-io/xberg";

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "contract.pdf",
}, {
    ner: {
        backend: NerBackendKind.Llm,
        llm: { model: "openai/gpt-4o-mini" },
        customLabels: ["Treatment", "Vessel", "Product"],
    },
});
```
