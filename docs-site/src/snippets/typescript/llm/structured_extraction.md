```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  structuredExtraction: {
    schema: {
      type: "object",
      properties: {
        title: { type: "string" },
        authors: { type: "array", items: { type: "string" } },
        date: { type: "string" },
      },
      required: ["title", "authors", "date"],
      additionalProperties: false,
    },
    schemaName: "paper_metadata",
    llm: {
      model: "openai/gpt-4o-mini",
    },
    strict: true,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "paper.pdf" }, config);
console.log(output.results?.[0]?.structuredOutput);
```
