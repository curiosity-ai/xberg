---
id: fixture_rust_list_reranker_backends
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered reranker backends

```rust title="Rust"
use xberg::list_reranker_backends;

fn main() {
    let _ = list_reranker_backends();
}

```
