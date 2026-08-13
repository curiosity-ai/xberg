---
id: fixture_rust_post_processors_list
language: rust
target: rust
level: typecheck
requires: []
side_effect: safe
---

List all registered post-processors

```rust title="Rust"
use xberg::list_post_processors;

fn main() {
    let _ = list_post_processors();
}

```
