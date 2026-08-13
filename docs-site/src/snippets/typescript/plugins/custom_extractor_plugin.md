```typescript title="TypeScript"
import {
  listDocumentExtractors,
  registerDocumentExtractor,
  unregisterDocumentExtractor,
  clearDocumentExtractors,
  type DocumentExtractor,
  type ExtractedDocument,
} from "@xberg-io/xberg";

// Custom document extractors are supported: implement `DocumentExtractor`
// and register it. See `plugin_extractor.md` for a complete example.
const customExtractor: DocumentExtractor = {
  name: () => "custom-text-extractor",
  supportedMimeTypes: () => ["text/x-custom"],
  priority: () => 60,
  async extract(): Promise<ExtractedDocument> {
    return { content: "custom extraction result", mimeType: "text/x-custom" };
  },
};
registerDocumentExtractor(customExtractor);

// List all registered document extractors
const extractors = listDocumentExtractors();
console.log("Available extractors:", extractors);

// Unregister a specific extractor (use with caution)
unregisterDocumentExtractor("custom-text-extractor");

// Clear all extractors (use with extreme caution)
// clearDocumentExtractors();
```
