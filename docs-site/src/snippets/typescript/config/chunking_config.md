```typescript title="TypeScript"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    maxCharacters: 1000,
    overlap: 200,
  },
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
console.log(`Total chunks: ${output.results[0].chunks?.length ?? 0}`);
```

```typescript title="TypeScript - Markdown with Heading Context"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    chunkerType: "markdown",
    maxCharacters: 500,
    overlap: 50,
    sizing: { type: "tokenizer", model: "Xenova/gpt-4o", cacheDir: "~/.cache/xberg/tokenizers" },
  },
};

const output = await extract({ kind: "uri", uri: "document.md" }, config);
for (const chunk of output.results[0].chunks ?? []) {
  const headings = chunk.metadata?.headingContext?.headings ?? [];
  for (const heading of headings) {
    console.log(`Heading L${heading.level}: ${heading.text}`);
  }
  console.log(`Content: ${chunk.content.slice(0, 100)}...`);
}
```

```typescript title="TypeScript - Semantic"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    chunkerType: "semantic",
  },
};

const output = await extract({ kind: "uri", uri: "document.pdf" }, config);
for (const chunk of output.results[0].chunks ?? []) {
  console.log(`Content: ${chunk.content.slice(0, 100)}...`);
}
```

```typescript title="TypeScript - Prepend Heading Context"
import { extract } from "@xberg-io/xberg";

const config = {
  chunking: {
    chunkerType: "markdown",
    maxCharacters: 500,
    overlap: 50,
    prependHeadingContext: true,
  },
};

const output = await extract({ kind: "uri", uri: "document.md" }, config);
for (const chunk of output.results[0].chunks ?? []) {
  // Each chunk's content is prefixed with its heading breadcrumb
  console.log(`Content: ${chunk.content.slice(0, 100)}...`);
}
```
