```typescript title="TypeScript"
import { ExtractInputKind, extract } from "@xberg-io/xberg";

const config = {
  pdfOptions: {
    extractMetadata: true,
    hierarchy: {
      enabled: true,
      kClusters: 6,
      includeBbox: true,
      ocrCoverageThreshold: 0.8,
    },
  },
};

const output = await extract({ kind: ExtractInputKind.Uri, uri: "document.pdf" }, config);
const result = output.results?.[0];
if (result?.pages) {
  result.pages.forEach((page) => {
    console.log(`Page ${page.pageNumber}:`);
    console.log(`  Content: ${page.content.substring(0, 100)}...`);
  });
}
```
