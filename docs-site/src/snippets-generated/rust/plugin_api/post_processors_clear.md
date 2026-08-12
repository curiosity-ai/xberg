---
id: fixture_rust_post_processors_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all post-processors and verify list is empty

```rust title="Rust"
use xberg::clear_post_processors;

fn main() {
    let _ = clear_post_processors();
}

```
