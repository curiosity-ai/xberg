---
id: fixture_wasm_register_tokenizer_backend_trait_bridge
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```typescript title="WebAssembly"
import { registerTokenizerBackend } from "@xberg-io/xberg-wasm";
function main() {
  class _TestStub_register_tokenizer_backend_trait_bridge {
  name(): string { return "test-tokenizer-backend"; }
  countTokens(_p0?: any): number { return 3; }
  async dispose(): Promise<void> { return undefined; }
}

  const result = registerTokenizerBackend(new _TestStub_register_tokenizer_backend_trait_bridge());
}

void main();

```
