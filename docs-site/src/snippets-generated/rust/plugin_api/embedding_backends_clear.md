---
id: fixture_rust_embedding_backends_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all embedding backends and verify list is empty

```rust title="Rust"
use xberg::clear_embedding_backends;

fn main() {
    let _ = clear_embedding_backends();
}

```
