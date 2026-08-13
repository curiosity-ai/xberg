---
id: fixture_node_register_tokenizer_backend_trait_bridge
language: typescript
target: node
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```typescript title="TypeScript"
import { registerTokenizerBackend } from "@xberg-io/xberg";
function main() {
  class _TestStub_register_tokenizer_backend_trait_bridge {
  name(): string { return "test-tokenizer-backend"; }
  countTokens(_p0?: any): number { return 3; }
  async dispose(): Promise<void> { return undefined; }
}

  const _bridge_backend = new _TestStub_register_tokenizer_backend_trait_bridge();
  const result = registerTokenizerBackend(_bridge_backend);
}

void main();

```
