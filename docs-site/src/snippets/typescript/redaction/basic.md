```typescript title="TypeScript"
import { ExtractInputKind, PiiCategory, RedactionStrategy, extract } from '@xberg-io/xberg';

const output = await extract({
    kind: ExtractInputKind.Uri,
    uri: "contract.pdf",
}, {
    redaction: {
        categories: [PiiCategory.Email, PiiCategory.Phone, PiiCategory.Ssn, PiiCategory.CreditCard, PiiCategory.Iban],
        strategy: RedactionStrategy.Mask,
    },
});
const [result] = output.results ?? [];
console.log(result?.content);
console.log(`Redacted ${result?.redactionReport?.totalRedacted ?? 0} spans`);
```
