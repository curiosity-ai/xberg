---
id: fixture_rust_unregister_embedding_backend_after_register
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

unregister_embedding_backend

```rust title="Rust"
use xberg::unregister_embedding_backend;

fn main() {
    let name = r#"test-embedding-backend"#;
    let _ = unregister_embedding_backend(name);
}

```
