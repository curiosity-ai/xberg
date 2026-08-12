---
id: fixture_rust_renderers_clear
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

Clear all renderers and verify list is empty

```rust title="Rust"
use xberg::clear_renderers;

fn main() {
    let _ = clear_renderers();
}

```
