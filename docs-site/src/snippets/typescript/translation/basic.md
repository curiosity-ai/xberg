```typescript title="TypeScript"
import { ExtractInputKind, extract } from '@xberg-io/xberg';

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "contract.pdf",
}, {
    translation: {
        targetLang: "de",
        preserveMarkup: false,
        llm: { model: "openai/gpt-4o-mini" },
    },
});
const [first] = output.results ?? [];
if (first?.translation) {
    console.log(first.translation.content);
}
```
