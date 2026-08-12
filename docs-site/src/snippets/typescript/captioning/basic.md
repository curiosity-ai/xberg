```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const output = await extract({
    kind: "uri",
    uri: "report.pdf",
}, {
    captioning: {
        llm: { model: "openai/gpt-4o-mini" },
        minImageArea: 1000,
    },
});

for (const image of output.results[0].images ?? []) {
    if (image.caption) {
        console.log(image.caption);
    }
}
```
