---
id: fixture_rust_clear_reranker_backends
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all reranker backends and verify list is empty

```rust title="Rust"
use xberg::clear_reranker_backends;

fn main() {
    let _ = clear_reranker_backends();
}

```
