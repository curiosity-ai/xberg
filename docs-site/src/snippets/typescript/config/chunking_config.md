```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    maxCharacters: 1000,
    overlap: 200,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
console.log(`Total chunks: ${output.results?.[0]?.chunks?.length ?? 0}`);
```

```typescript title="TypeScript - Markdown with Heading Context"
import { ChunkerType, ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const config: ExtractionConfig = {
  chunking: {
    chunkerType: ChunkerType.Markdown,
    maxCharacters: 500,
    overlap: 50,
    sizing: { type: "tokenizer", model: "Xenova/gpt-4o", cacheDir: "~/.cache/xberg/tokenizers" },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.md" }, config);
const [first] = output.results ?? [];
for (const chunk of first?.chunks ?? []) {
  const headings = chunk.metadata?.headingContext?.headings ?? [];
  for (const heading of headings) {
    console.log(`Heading L${heading.level}: ${heading.text}`);
  }
  console.log(`Content: ${chunk.content.slice(0, 100)}...`);
}
```

```typescript title="TypeScript - Semantic"
import { ChunkerType, ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    chunkerType: ChunkerType.Semantic,
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const [first] = output.results ?? [];
for (const chunk of first?.chunks ?? []) {
  console.log(`Content: ${chunk.content.slice(0, 100)}...`);
}
```
