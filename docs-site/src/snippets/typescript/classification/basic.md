```typescript title="TypeScript"
import { ExtractInputKind, extract } from '@xberg-io/xberg';

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "packet.pdf",
}, {
    pageClassification: {
        labels: ["invoice", "contract", "id_document", "receipt"],
        multiLabel: false,
        llm: { model: "openai/gpt-4o-mini" },
    },
});

const [first] = output.results ?? [];
for (const page of first?.pageClassifications ?? []) {
    console.log(`page ${page.pageNumber}: ${page.labels[0]?.label}`);
}
```
