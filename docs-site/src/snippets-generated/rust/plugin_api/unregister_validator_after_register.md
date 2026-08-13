---
id: fixture_rust_unregister_validator_after_register
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

unregister_validator

```rust title="Rust"
use xberg::unregister_validator;

fn main() {
    let name = r#"test-validator"#;
    let _ = unregister_validator(name);
}

```
