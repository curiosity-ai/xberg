---
id: fixture_wasm_register_post_processor_trait_bridge
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```typescript title="WebAssembly"
import { registerPostProcessor } from "@xberg-io/xberg-wasm";
function main() {
  class _TestStub_register_post_processor_trait_bridge {
  name(): string { return "test-processor"; }
  async process(_p0?: any, _p1?: any): Promise<void> { return undefined; }
  processingStage(): string { return "{}"; }
  shouldProcess(_p0?: any, _p1?: any): boolean { return false; }
  estimatedDurationMs(_p0?: any): number { return 1; }
  priority(): number { return 1; }
  async dispose(): Promise<void> { return undefined; }
}

  const result = registerPostProcessor(new _TestStub_register_post_processor_trait_bridge());
}

void main();

```
