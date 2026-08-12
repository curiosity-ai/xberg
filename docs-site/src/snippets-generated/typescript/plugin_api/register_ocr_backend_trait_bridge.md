---
id: fixture_node_register_ocr_backend_trait_bridge
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```typescript title="TypeScript"
import { registerOcrBackend } from "@xberg-io/xberg";
function main() {
  class _TestStub_register_ocr_backend_trait_bridge {
  name(): string { return "test-backend"; }
  async processImage(_p0?: any, _p1?: any): Promise<string> { return "{}"; }
  async processImageFile(_p0?: any, _p1?: any): Promise<string> { return "{}"; }
  supportsLanguage(_p0?: any): boolean { return false; }
  backendType(): string { return "{}"; }
  supportedLanguages(): string { return []; }
  supportsTableDetection(): boolean { return false; }
  supportsDocumentProcessing(): boolean { return false; }
  emitsStructuredMarkdown(): boolean { return false; }
  async processDocument(_p0?: any, _p1?: any): Promise<string> { return "{}"; }
  async dispose(): Promise<void> { return undefined; }
}

  const _bridge_backend = new _TestStub_register_ocr_backend_trait_bridge();
  const result = registerOcrBackend(_bridge_backend);
}

void main();

```
