```typescript title="TypeScript"
import { ExtractInputKind, RedactionStrategy, extract } from "@xberg-io/xberg";

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "contract.pdf",
}, {
    redaction: {
        strategy: RedactionStrategy.TokenReplace,
        customTerms: [
            { label: "Project", value: "Project Polaris", caseSensitive: false },
            { label: "Employee", value: "EMP-7421", caseSensitive: true },
        ],
        customPatterns: [
            { label: "InternalId", pattern: "INT-\\d{6}", caseSensitive: false },
        ],
    },
});
```
