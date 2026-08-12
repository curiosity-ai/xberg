---
id: fixture_wasm_register_validator_trait_bridge
language: typescript
target: wasm
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```typescript title="WebAssembly"
import { registerValidator } from "@xberg-io/xberg-wasm";
function main() {
  class _TestStub_register_validator_trait_bridge {
  name(): string { return "test-validator"; }
  async validate(_p0?: any, _p1?: any): Promise<void> { return undefined; }
  shouldValidate(_p0?: any, _p1?: any): boolean { return false; }
  priority(): number { return 1; }
  async dispose(): Promise<void> { return undefined; }
}

  const result = registerValidator(new _TestStub_register_validator_trait_bridge());
}

void main();

```
