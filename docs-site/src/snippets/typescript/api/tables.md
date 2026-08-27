```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const output = await extract({
  kind: ExtractInputKind.Uri,
  uri: "document.pdf",
});

const [first] = output.results ?? [];
first?.tables?.forEach((table) => {
  console.log(`Table with ${table.cells?.length ?? 0} rows`);
  console.log(table.markdown);
  table.cells?.forEach((row) => console.log(row.join(" | ")));
});
```
