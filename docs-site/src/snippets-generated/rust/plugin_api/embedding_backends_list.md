---
id: fixture_rust_embedding_backends_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered embedding backends

```rust title="Rust"
use xberg::list_embedding_backends;

fn main() {
    let _ = list_embedding_backends();
}

```
