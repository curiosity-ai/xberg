---
id: fixture_rust_unregister_reranker_backend
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

unregister_reranker_backend

```rust title="Rust"
use xberg::unregister_reranker_backend;

fn main() {
    let name = r#"test-reranker-backend"#;
    let _ = unregister_reranker_backend(name);
}

```
