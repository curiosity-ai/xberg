---
id: fixture_rust_validators_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all validators and verify list is empty

```rust title="Rust"
use xberg::clear_validators;

fn main() {
    let _ = clear_validators();
}

```
