```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "report.pdf",
}, {
    captioning: {
        llm: { model: "openai/gpt-4o-mini" },
        minImageArea: 1000,
    },
});

const [first] = output.results ?? [];
for (const image of first?.images ?? []) {
    if (image.caption) {
        console.log(image.caption);
    }
}
```
