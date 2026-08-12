---
id: fixture_rust_renderers_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered renderers

```rust title="Rust"
use xberg::list_renderers;

fn main() {
    let _ = list_renderers();
}

```
