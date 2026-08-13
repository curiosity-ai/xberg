---
id: fixture_rust_unregister_tokenizer_backend_after_register
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

unregister_tokenizer_backend

```rust title="Rust"
use xberg::unregister_tokenizer_backend;

fn main() {
    let name = r#"test-tokenizer-backend"#;
    let _ = unregister_tokenizer_backend(name);
}

```
