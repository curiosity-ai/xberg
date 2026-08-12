---
id: fixture_rust_validators_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered validators

```rust title="Rust"
use xberg::list_validators;

fn main() {
    let _ = list_validators();
}

```
